# RAG and Vector Search with pgvector and Amazon Bedrock (Part 4)

*How to build retrieval-augmented generation that actually cites its sources — without a vector database subscription.*

---

Most RAG tutorials reach for Pinecone, Chroma, or Weaviate as the vector store. Those are all fine services, but they add another cost line, another auth boundary, and a dependency you don't control. If you're already running Postgres — and for multi-tenant SaaS, you should be — the pgvector extension gives you vector similarity search inside your existing database, protected by the same Row-Level Security policies you already have.

This post covers the full query path in Sift: how a user's question becomes a vector, how pgvector finds the closest document chunks, and how Claude turns those chunks into a cited answer.

---

## What RAG Actually Does

The core idea is simple. At query time:

1. Embed the user's question with the same model used to embed the documents
2. Find the document chunks whose embeddings are closest to the question embedding
3. Send those chunks to an LLM, tell it to answer the question using only that context
4. Return the answer with numbered citations linking back to the source text

That's it. The sophistication is in the details of each step.

---

## Embeddings with Bedrock Titan Embed v2

Both the pipeline (at ingest time) and the chat handler (at query time) use the same embedding model: `amazon.titan-embed-text-v2:0`. Using the same model for both sides of the search is a hard requirement — embeddings from different models live in incompatible vector spaces.

The Python implementation in the pipeline's shared module:

```python
EMBED_MODEL_ID = "amazon.titan-embed-text-v2:0"

def embed(text: str) -> list[float]:
    payload = json.dumps({"inputText": text, "dimensions": 1024, "normalize": True})
    response = _get_client().invoke_model(
        modelId=EMBED_MODEL_ID,
        contentType="application/json",
        accept="application/json",
        body=payload,
    )
    return json.loads(response["body"].read())["embedding"]
```

Two parameters worth noting.

**`dimensions: 1024`** — Titan Embed v2 supports multiple output sizes (256, 512, or 1024 dimensions). Fewer dimensions mean smaller storage and faster search at the cost of some precision. 1024 is the maximum and gives the best retrieval quality; for a demo at this scale, there's no reason to trade it away.

**`normalize: True`** — this asks Bedrock to return a unit-length vector. Normalized embeddings mean cosine similarity is equivalent to dot product. pgvector can compute dot products slightly faster than cosine distance, and it simplifies reasoning about scores. More importantly, it means you don't have to normalize manually — if you skip it and your embeddings have different magnitudes, your similarity scores will be skewed by vector length rather than semantic meaning.

Authentication is IAM. The Lambda execution role has `bedrock:InvokeModel` permission via its attached policy — no API keys, no secrets to rotate.

---

## Schema: Storing Vectors in Postgres

The `document_chunks` table has a `vector(1024)` column — the native pgvector type:

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE document_chunks (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id   UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  tenant_id     UUID NOT NULL,
  chunk_index   INT NOT NULL,
  content       TEXT NOT NULL,
  embedding     vector(1024),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
```

The `(1024)` in the column type is a hard constraint — Postgres will reject inserts with a vector of any other dimension. That's a useful guardrail: if the embedding model changes and the dimension changes with it, the insert fails loudly rather than silently storing mismatched vectors.

### The IVFFlat Index

An exact nearest-neighbor search scans every vector in the table and computes distance to the query vector. For a small dataset that's fine. At tens of millions of chunks it becomes expensive.

IVFFlat (Inverted File Flat) is an approximate nearest-neighbor index. It clusters the vectors into groups (called "lists") at index build time. At query time, it only searches the most promising lists rather than the entire table:

```sql
CREATE INDEX ON document_chunks
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
```

`vector_cosine_ops` tells the index to use cosine distance as its metric, which matches the `<=>` operator in the query. The `lists = 100` parameter controls how many clusters to build — the pgvector docs recommend roughly `sqrt(rows)` as a starting point.

**The IVFFlat gotcha:** the index needs data to exist when it's built. An IVFFlat index built on an empty table is useless. In Sift, the initial migration creates the index after the schema is established, and the seed data runs in the same migration. For a production system where the table grows continuously, HNSW is a better choice — it maintains good search quality as data is inserted without needing a rebuild.

### Inserting Vectors from Python

The `psycopg2` driver doesn't natively understand the pgvector type. Rather than adding the `pgvector` Python package (which requires a compiled extension and adds deploy complexity), the pipeline constructs a Postgres vector literal as a plain string and casts it:

```python
vector_literal = "[" + ",".join(str(v) for v in embedding) + "]"
cur.execute(
    """
    INSERT INTO document_chunks
        (document_id, tenant_id, chunk_index, content, embedding)
    VALUES (%s, %s, %s, %s, %s::vector)
    ON CONFLICT DO NOTHING
    """,
    (document_id, tenant_id, chunk_index, content, vector_literal),
)
```

The `::vector` cast in the SQL converts the string to the native vector type at insert time. This works on any Postgres driver, any Lambda architecture (x86 or ARM), without native extensions. The `ON CONFLICT DO NOTHING` handles at-least-once delivery from the Step Functions Map state — if an EmbedChunk Lambda retries, it won't create duplicate chunks.

---

## Similarity Search

At query time, the C# `ChatService` embeds the user's question and runs the search. The same vector literal approach works from the .NET side:

```csharp
private async Task<List<ChunkResult>> SearchChunksAsync(Guid tenantId, float[] embedding)
{
    var vectorLiteral = $"[{string.Join(",", embedding)}]";

    await using var conn = await db.CreateAsync();
    await TenantContext.SetAsync(conn, tenantId);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        SELECT dc.id, dc.document_id, dc.chunk_index, dc.content,
               d.filename,
               dc.embedding <=> $1::vector AS distance
        FROM document_chunks dc
        JOIN documents d ON d.id = dc.document_id
        ORDER BY distance
        LIMIT $2
        """;
    cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, vectorLiteral);
    cmd.Parameters.AddWithValue(TopK);
    // ...
}
```

The `<=>` operator is pgvector's cosine distance operator. It returns values between 0 and 2 — 0 means identical vectors, 2 means pointing in opposite directions. Ordering by ascending distance gives the most semantically similar chunks first.

Notice that `TenantContext.SetAsync` runs before the query. This sets the Postgres session variable that the RLS policy reads. The similarity search is automatically tenant-scoped — there's no `WHERE tenant_id = $3` in this query, but Postgres applies the policy invisibly. A user from Acme Corp can only find chunks from their own documents, even though the `<=>` distance calculation runs across an index that spans all tenants' data.

**Why 8 chunks?** `TopK = 8` is a constant in `ChatService.cs`. Eight chunks at ~512 tokens each is roughly 4,000 tokens of context — enough to answer most questions without overwhelming the model or the latency budget. The tradeoff is real: more chunks means higher recall (better chance the right information is included) at the cost of slower generation and more noise in the prompt. Eight is a practical default, not a theoretically derived optimum.

---

## The RAG Prompt

With the top 8 chunks retrieved, the service builds the prompt:

```csharp
var context = string.Join("\n\n", chunks.Select((c, i) =>
    $"[{i + 1}] From \"{c.Filename}\" (chunk {c.ChunkIndex}):\n{c.Content}"));

var systemPrompt = """
    You are a helpful document assistant. Answer the user's question using only
    the provided document excerpts. Cite your sources using [1], [2], etc.
    If the answer cannot be found in the excerpts, say so clearly.
    """;

var userMessage = $"Document excerpts:\n{context}\n\nQuestion: {question}";
```

Each chunk gets a numbered label `[1]`, `[2]`, etc., with the filename and chunk index. The system prompt instructs Claude to use those same numbers as inline citations. The model sees something like:

```
[1] From "Q3_Report.pdf" (chunk 4):
Revenue for Q3 was $4.2M, up 18% year-over-year driven by enterprise contracts...

[2] From "Q3_Report.pdf" (chunk 5):
The increase was concentrated in the healthcare vertical, which grew 31%...

Question: What drove the Q3 revenue increase?
```

And responds with an answer that cites `[1]` and `[2]` inline, so the reader knows exactly which passage each claim came from.

The model for this step is Claude Haiku 4.5 — fast and cheap for a task that's mostly about summarizing and organizing provided context rather than knowledge retrieval or reasoning. The `max_tokens: 1024` cap keeps response times predictable.

### Citations as First-Class Data

The response doesn't just return the answer string. The `ChatResponse` model carries a parallel citations array:

```csharp
public class ChatResponse
{
    public string             Answer    { get; set; } = "";
    public List<ChatCitation> Citations { get; set; } = [];
}

public class ChatCitation
{
    public Guid   DocumentId { get; set; }
    public string Filename   { get; set; } = "";
    public string Excerpt    { get; set; } = "";
    public int    ChunkIndex { get; set; }
}
```

Each citation includes the first 200 characters of the chunk's content. The React frontend renders them as expandable cards below the answer — the user can click `[1]` to see the exact excerpt that grounded that part of the response, with the source document and chunk position shown.

This matters for trust. A RAG system that returns confident-sounding answers with no way to verify them is worse than one that shows its work.

---

## Limitations and What Production Would Change

The implementation above works well at demo scale. A few things I'd change for a real production deployment:

**Chunking strategy.** The sliding-window chunker in Part 3 splits on character count, not semantic boundaries. A 512-token window can cut off mid-sentence, mid-table, or mid-list. Better approaches: a recursive sentence splitter that tries to preserve paragraph boundaries, or a semantic chunker that uses an embedding model to detect topic shifts. The trade-off is complexity and ingest latency.

**Index type.** IVFFlat is good for static or slowly-growing datasets, but it degrades as data is inserted after the index is built — you need periodic reindexing. HNSW (Hierarchical Navigable Small World) maintains search quality dynamically as data grows, at the cost of higher memory usage. For a production system with continuous ingestion, HNSW is the right default.

**Reranking.** Vector similarity is a good first filter but not a perfect one. A cross-encoder reranker — a small model that takes (question, chunk) pairs and scores their relevance directly — can significantly improve the precision of the final context window. The typical pattern is: retrieve top 20–50 chunks with vector search, rerank with a cross-encoder, pass the top 8 to the LLM.

**Streaming.** The current API waits for Claude to finish generating the full answer before returning it. For longer answers that can take 3–5 seconds, that's a noticeable pause. Lambda Function URLs support response streaming, which would let the frontend display tokens as they arrive. API Gateway HTTP APIs don't support streaming, so switching to Function URLs for the chat endpoint would be the path there.

---

## What's Next

**Part 5** covers the React frontend: how the upload flow works, the polling pattern that drives the document status cards, and how Amplify's auth integration wires up the Cognito token flow.

The code for this post:
- `backend/shared/bedrock.py` — embedding call, normalize flag
- `migrations/001_initial_schema.sql` — `vector(1024)` column, IVFFlat index
- `backend/pipeline/embed/embed_handler.py` — vector literal insert, `ON CONFLICT DO NOTHING`
- `backend/src/Sift.Api/Services/ChatService.cs` — full query path: embed → search → generate
- `backend/src/Sift.Api/Models/Chat.cs` — response shape with citations

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
