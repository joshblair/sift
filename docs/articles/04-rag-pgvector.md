# Article 4: RAG with pgvector and Amazon Bedrock

**Tagline:** How to build retrieval-augmented generation that actually cites its sources — without a vector database subscription.

---

## Outline

### Hook
Most RAG tutorials use Pinecone or Chroma as the vector store. Those are fine services, but they add cost, another auth boundary, and a service you don't control. If you're already on Postgres (and for multi-tenant SaaS, you should be), pgvector gives you vector search in the same database as your application data — with the same RLS policies protecting it.

### Part 1: Embeddings with Bedrock Titan Embed v2

**Model choice**
- `amazon.titan-embed-text-v2:0` — 1536 dimensions, available in us-west-2
- No API key management, IAM auth via Lambda execution role
- ~$0.02 per million tokens at demo scale

**Python implementation**
Walk through `shared/bedrock.py`:
```python
payload = json.dumps({"inputText": text, "dimensions": 1536, "normalize": True})
response = client.invoke_model(modelId=EMBED_MODEL_ID, ...)
embedding = json.loads(response['body'].read())['embedding']
```

**Why normalize?** Normalized embeddings make cosine similarity equivalent to dot product, which pgvector can compute faster.

### Part 2: pgvector schema and indexing

**The `vector(1536)` column**
Show the schema from `migrations/001_initial_schema.sql`:
```sql
embedding vector(1536)
```

**IVFFlat index**
```sql
CREATE INDEX ON document_chunks USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);
```
Explain: IVFFlat clusters vectors into `lists` buckets at index build time. Query time probes the nearest buckets (controlled by `ivfflat.probes`). Trade-off: approximate (slightly lower recall) vs. exact scan (100% recall but slow).

**When to build the index**
IVFFlat needs data to exist at index build time — build it after loading the initial embeddings. For a live system, use HNSW instead (no rebuild needed as data grows).

**Passing vectors from Python**
The `::vector` cast approach used in `embed_handler.py`:
```python
vector_literal = "[" + ",".join(str(v) for v in embedding) + "]"
cur.execute("INSERT ... VALUES (%s::vector)", (vector_literal,))
```
Why not the pgvector Python client? Avoids an extra dependency; works on all architectures without compiled extensions.

### Part 3: Similarity search

**The query**
Walk through `ChatService.cs`:
```csharp
cmd.CommandText = """
    SELECT dc.content, d.filename, dc.chunk_index,
           dc.embedding <=> $1::vector AS distance
    FROM document_chunks dc
    JOIN documents d ON d.id = dc.document_id
    ORDER BY distance
    LIMIT 8
    """;
```
The `<=>` operator is cosine distance (0 = identical, 2 = opposite). RLS on `document_chunks` ensures only the current tenant's embeddings are searched.

**Why 8 chunks?** Haiku's context window is 200k tokens but latency grows. 8 chunks × ~512 tokens ≈ 4096 tokens of context. Enough for most questions, fast to generate.

### Part 4: The RAG prompt

Walk through the prompt construction in `ChatService.cs`:
- System message: instruction to cite sources with [1], [2], etc.
- User message: numbered excerpts with filename + chunk index, followed by the question
- Claude Haiku: fast and cheap for this task, returns cited answer

**Citation UX**
The response includes a `citations` array. The React `ChatMessage` component renders them as expandable cards below the answer with the excerpt shown. Show a screenshot.

### Part 5: Limitations and production improvements

- **Chunking strategy**: fixed-size sliding window misses semantic boundaries. Production: use a recursive sentence splitter or semantic chunker
- **Index type**: IVFFlat needs rebuild; HNSW is better for growing datasets
- **Reranking**: add a cross-encoder reranker pass after the vector search to improve precision
- **Streaming**: API Gateway + Lambda doesn't support SSE streaming. Use Lambda Function URLs for real-time streaming responses

---

## Key code references
- `backend/pipeline/embed/embed_handler.py` — embedding + insert
- `backend/pipeline/chunk/chunk_handler.py` — chunking strategy
- `backend/src/Sift.Api/Services/ChatService.cs` — full RAG flow
- `migrations/001_initial_schema.sql` — vector column + IVFFlat index
