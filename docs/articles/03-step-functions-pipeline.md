# Serverless Document Pipelines with AWS Step Functions (Part 3)

*Why I chose Step Functions over SQS + Lambda — and what the execution history is actually worth.*

---

Every async processing pipeline starts the same way: a file lands somewhere, something needs to happen to it in multiple stages, and you need it to be reliable. The obvious architecture is SQS queues chained between Lambda functions. It's battle-tested, it scales, and you've probably built it before.

I deliberately chose not to use it here.

Sift's document processing pipeline has six stages: extract text, chunk it, generate embeddings in parallel, extract metadata with an LLM, and mark the document ready (or failed). I implemented all of it as a Step Functions Express Workflow. This post covers why, how the state machine is structured, and what the Map state for parallel embedding actually does.

---

## The Trigger: S3 → EventBridge → Step Functions

When a user uploads a document, the browser sends it directly to S3 via a presigned URL. The API never sees the file content — it just issues the URL and records the pending document in the database. From there, the pipeline starts automatically.

The trigger chain has two hops.

**Hop 1: S3 to EventBridge.** The uploads bucket has EventBridge notifications enabled:

```yaml
UploadsBucket:
  Type: AWS::S3::Bucket
  Properties:
    NotificationConfiguration:
      EventBridgeConfiguration:
        EventBridgeEnabled: true
```

That one flag makes the bucket publish `Object Created` events to the default EventBridge bus automatically, for every object upload. No SNS topic, no S3 notification configuration specifying ARNs.

**Hop 2: EventBridge to Step Functions.** An EventBridge rule matches those events and triggers the state machine:

```yaml
S3UploadRule:
  Type: AWS::Events::Rule
  Properties:
    EventPattern:
      source: [aws.s3]
      detail-type: [Object Created]
      detail:
        bucket:
          name: [!Ref UploadsBucket]
    Targets:
      - Id: TriggerPipeline
        Arn: !Ref PipelineStateMachine
        RoleArn: !GetAtt EventBridgeToSfnRole.Arn
        InputTransformer:
          InputPathsMap:
            key:    "$.detail.object.key"
            bucket: "$.detail.bucket.name"
          InputTemplate: '{"s3Key": "<key>", "bucketName": "<bucket>"}'
```

The `InputTransformer` is doing something important: it reshapes the raw S3 event (which has a lot of noise — checksums, ETags, content type) into a clean minimal payload before Step Functions even sees it. The state machine starts with just `{ s3Key, bucketName }`.

**Why EventBridge instead of S3 → Lambda directly?**

S3 supports direct Lambda triggers. The reason to go through EventBridge anyway is decoupling: the Step Functions ARN isn't embedded in the S3 bucket configuration. If I wanted to add a second consumer — say, a Lambda that indexes the filename for search — I'd add another EventBridge rule target, not modify the S3 bucket. The bucket doesn't know what listens to its events.

---

## The State Machine

The entire pipeline is defined as YAML inside the SAM template. Here's the full structure:

```yaml
PipelineStateMachine:
  Type: AWS::Serverless::StateMachine
  Properties:
    Name: !Sub sift-pipeline-${Env}
    Type: EXPRESS
    Definition:
      Comment: Sift document ingestion pipeline
      StartAt: ExtractText
      States:
        ExtractText:
          Type: Task
          Resource: !GetAtt ExtractTextFunction.Arn
          Retry:
            - ErrorEquals: [States.TaskFailed]
              IntervalSeconds: 2
              MaxAttempts: 2
          Catch:
            - ErrorEquals: [States.ALL]
              ResultPath: $.error
              Next: MarkFailed
          Next: ChunkText

        ChunkText:
          Type: Task
          Resource: !GetAtt ChunkTextFunction.Arn
          Catch:
            - ErrorEquals: [States.ALL]
              ResultPath: $.error
              Next: MarkFailed
          Next: GenerateEmbeddings

        GenerateEmbeddings:
          Type: Map
          ItemsPath: $.chunks
          Parameters:
            documentId.$: $.documentId
            tenantId.$:   $.tenantId
            index.$:      $$.Map.Item.Value.index
            content.$:    $$.Map.Item.Value.content
          MaxConcurrency: 5
          ResultPath: $.embeddingResults
          Iterator:
            StartAt: EmbedChunk
            States:
              EmbedChunk:
                Type: Task
                Resource: !GetAtt EmbedChunkFunction.Arn
                Retry:
                  - ErrorEquals: [States.TaskFailed]
                    IntervalSeconds: 2
                    MaxAttempts: 3
                End: true
          Catch:
            - ErrorEquals: [States.ALL]
              ResultPath: $.error
              Next: MarkFailed
          Next: ExtractMetadata

        ExtractMetadata:
          Type: Task
          Resource: !GetAtt ExtractMetadataFunction.Arn
          Catch:
            - ErrorEquals: [States.ALL]
              ResultPath: $.error
              Next: MarkFailed
          Next: MarkReady

        MarkReady:
          Type: Task
          Resource: !GetAtt MarkReadyFunction.Arn
          End: true

        MarkFailed:
          Type: Task
          Resource: !GetAtt MarkFailedFunction.Arn
          End: true
```

Let's walk through each stage.

---

## Stage 1: ExtractText

The first Lambda gets `{ s3Key, bucketName }` and has two jobs: parse the tenant and document IDs from the key, and extract plain text from whatever file type was uploaded.

The S3 key format is `{tenantId}/{documentId}/{filename}` — the same prefix structure used for tenant isolation in S3 (covered in Part 2). Parsing it is a single split:

```python
parts       = s3_key.split("/", 2)
tenant_id   = parts[0]
document_id = parts[1]
filename    = parts[2]
```

Text extraction is dispatched on file extension:

```python
ext = filename.rsplit(".", 1)[-1].lower()
if ext == "pdf":
    text, page_count = _extract_pdf(content)
elif ext == "docx":
    text, page_count = _extract_docx(content)
elif ext == "csv":
    text, page_count = _extract_csv(content)
elif ext == "txt":
    text       = content.decode("utf-8", errors="replace")
    page_count = 1
else:
    raise ValueError(f"Unsupported file extension: {ext}")
```

For PDFs, `pdfplumber` handles multi-page extraction and tracks the page count. For DOCX, `python-docx` walks the paragraph list. For CSV, `pandas` converts the dataframe to a string representation with column names in the header — not ideal for prose, but searchable and embeddable. The page count flows downstream to the `documents` table and surfaces in the UI.

The Lambda also sets the document status to `processing` before returning. This tells the frontend's polling logic that the pipeline is running and to keep checking.

The return value passes everything forward:

```python
return {
    **event,
    "tenantId":   tenant_id,
    "documentId": document_id,
    "filename":   filename,
    "text":       text,
    "pageCount":  page_count,
}
```

---

## Stage 2: ChunkText

This stage splits the extracted text into overlapping windows. The constants:

```python
CHUNK_SIZE    = 512   # tokens, approximated as characters / 4
CHUNK_OVERLAP = 64    # tokens of overlap between adjacent chunks
CHARS_PER_TOKEN = 4
```

The overlap is the important detail. If a chunk boundary lands in the middle of a sentence that contains the answer to a user's question, a chunk with no overlap might return two fragments — each with half the context — neither of which scores well in similarity search. With 64-token overlap, adjacent chunks share a paragraph's worth of text, so the answer has a better chance of appearing intact in at least one chunk.

The chunker is a sliding-window algorithm that splits on word boundaries:

```python
def _chunk(text: str, size: int, overlap: int) -> list[str]:
    words  = text.split()
    chunks = []
    buf    = []
    buf_len = 0

    for word in words:
        word_len = len(word) + 1
        if buf_len + word_len > size and buf:
            chunks.append(" ".join(buf))
            # Roll back by overlap characters
            rolled, rolled_len = [], 0
            for w in reversed(buf):
                if rolled_len + len(w) + 1 > overlap:
                    break
                rolled.insert(0, w)
                rolled_len += len(w) + 1
            buf     = rolled
            buf_len = rolled_len
        buf.append(word)
        buf_len += word_len

    if buf:
        chunks.append(" ".join(buf))
    return chunks
```

No external dependencies — pure Python standard library. That means cold starts for this Lambda are essentially free. The output is a list of `{ index, content }` objects that becomes the input to the Map state.

---

## Stage 3: GenerateEmbeddings (The Map State)

This is where Step Functions earns its keep.

Embedding generation is the most time-consuming part of the pipeline. A 20-page PDF might produce 80–100 chunks, each requiring a separate Bedrock API call. Running them sequentially would be slow and wasteful. The Map state fans them out in parallel.

```yaml
GenerateEmbeddings:
  Type: Map
  ItemsPath: $.chunks
  Parameters:
    documentId.$: $.documentId
    tenantId.$:   $.tenantId
    index.$:      $$.Map.Item.Value.index
    content.$:    $$.Map.Item.Value.content
  MaxConcurrency: 5
  ResultPath: $.embeddingResults
```

A few things are happening here.

**`ItemsPath: $.chunks`** tells Step Functions to iterate over the `chunks` array from the previous state's output. Each item becomes one Lambda invocation.

**The `Parameters` block** reshapes each iteration's input. Without it, each EmbedChunk Lambda invocation would receive the full `chunks` array — which it doesn't need, and which would exceed Lambda's payload size at any real document length. Instead, `$$.Map.Item.Value.index` and `$$.Map.Item.Value.content` pull just the current chunk's fields, and `documentId.$` and `tenantId.$` carry the parent context. The `$$` prefix accesses the Step Functions execution context rather than the state input.

**`MaxConcurrency: 5`** caps the parallelism. Bedrock has per-account request rate limits. With 100 chunks and no concurrency cap, all 100 invocations would fire simultaneously and most would get throttled — producing retries, latency, and noise. Five concurrent invocations keeps throughput high while staying well under the throttle threshold.

**`ResultPath: $.embeddingResults`** is subtle. Normally, a Map state replaces the entire state input with its result array. Setting a `ResultPath` instead merges the results into the existing input under a new key. This is important: ExtractMetadata needs the `text`, `tenantId`, `documentId`, and `chunks` fields from earlier stages. Without `ResultPath`, they'd be overwritten.

Each EmbedChunk Lambda invocation does two things:

```python
def handler(event: dict, context) -> dict:
    embedding = embed(content)         # Bedrock Titan Embed v2
    _insert_chunk(document_id, tenant_id, chunk_index, content, embedding)
    return {"documentId": document_id, "tenantId": tenant_id, "index": index, "status": "ok"}
```

The `embed()` call hits Titan Embed v2 (1024 dimensions). The insert uses `ON CONFLICT DO NOTHING` — if the Lambda retries after a partial failure, it won't create duplicate chunks.

The vector gets written as a Postgres vector literal:

```python
vector_literal = "[" + ",".join(str(v) for v in embedding) + "]"
cur.execute(
    "INSERT INTO document_chunks (document_id, tenant_id, chunk_index, content, embedding) "
    "VALUES (%s, %s, %s, %s, %s::vector)",
    (document_id, tenant_id, chunk_index, content, vector_literal),
)
```

---

## Stage 4: ExtractMetadata

Once all chunks are embedded, a final Bedrock call generates a summary and topic list for the document. This surfaces in the UI as the document card's description.

The Lambda sends only the first 6,000 characters of the document text to stay within Claude Haiku's practical context window for this task:

```python
excerpt = text[:6000].strip()
```

The prompt asks for structured JSON output:

```python
system = (
    "You are a document analyst. Given document text, return a JSON object "
    "with exactly two keys: "
    '"summary" (one paragraph, max 200 words) and '
    '"topics" (array of 3-7 short topic strings). '
    "Return only the JSON object, no other text."
)
```

LLMs sometimes wrap their JSON output in markdown code fences even when told not to. The handler strips them before parsing:

```python
cleaned = response.strip().removeprefix("```json").removeprefix("```").removesuffix("```").strip()
data    = json.loads(cleaned)
```

The results get written to the `documents` table. The page count and chunk count from earlier stages are also persisted here — they came through in the state machine data, so no extra database reads needed.

---

## Stages 5 and 6: MarkReady and MarkFailed

These terminal states are simple status updates. MarkReady stamps `status = 'ready'` and `processed_at = NOW()`. MarkFailed records the error message (truncated to 1,000 characters) and sets `status = 'failed'`.

Every non-terminal state has a `Catch` block that routes all errors to MarkFailed:

```yaml
Catch:
  - ErrorEquals: [States.ALL]
    ResultPath: $.error
    Next: MarkFailed
```

`ResultPath: $.error` merges the error details into the state data under `$.error` rather than replacing the entire input. That means MarkFailed still receives `documentId` and `tenantId` — it can always look up which document to update, even when the failure happens deep in an unexpected state.

The pipeline status flows back to the React frontend through the `documents` table. The UI polls the document status endpoint every few seconds and updates the card from `uploading` → `processing` → `ready` (or `failed` with the error message shown inline).

---

## Why Express Workflows, Not Standard

Step Functions has two execution types. The choice matters for cost.

**Standard Workflows** charge per state transition — $0.025 per 1,000 transitions. A pipeline with 100 chunks runs the Map state, which means 100 EmbedChunk transitions plus the surrounding states. At scale, that adds up fast.

**Express Workflows** charge per execution and duration — $1.00 per million executions plus $0.00001 per GB-second. For a pipeline that completes in 2–4 minutes, the cost per document is a fraction of a cent.

The tradeoffs Express gives up: maximum 5-minute duration, at-least-once (not exactly-once) execution semantics, and no synchronous execution pattern. None of those matter here — the pipeline completes well under 5 minutes for any realistic document size, and `ON CONFLICT DO NOTHING` in the embed insert makes at-least-once delivery safe.

---

## The Real Argument for Step Functions

None of the above required Step Functions specifically. You could build the same pipeline with SQS queues between Lambda functions. The chunked output goes on a queue; workers pick up items and embed them; another queue signals the metadata stage.

The practical difference shows up when something breaks.

When a document gets stuck in an SQS pipeline, diagnosing it means correlating CloudWatch log groups across multiple Lambda functions, checking DLQ message counts, and reconstructing the sequence of events from timestamps. The document is somewhere in the pipeline, but you're inferring state from indirect evidence.

In Step Functions, you open the console, click the execution, and see this:

```
ExtractText        → SUCCEEDED  (2.3s)
ChunkText          → SUCCEEDED  (0.1s)
GenerateEmbeddings → FAILED
  └─ EmbedChunk[47] → FAILED (attempt 3/3)
       Error: ThrottlingException
       Cause: Rate exceeded for model amazon.titan-embed-text-v2:0
```

The failure is pinpointed: chunk 47, third retry, Bedrock throttle. Every invocation's input and output is stored in the execution history. For a portfolio project where the goal is demonstrating architectural thinking clearly — including to interviewers who might pull up the AWS console during a technical screen — that visibility is genuinely worth something.

---

## What's Next

**Part 4** covers the RAG query path: how a user's question gets embedded, how pgvector finds the closest chunks across potentially thousands of document segments, and how the citation system links each paragraph of Claude's response back to the source text.

The code for this post:
- `infrastructure/template.yaml` — state machine definition, EventBridge rule, InputTransformer
- `backend/pipeline/extract/extract_handler.py` — file type dispatch, S3 key parsing
- `backend/pipeline/chunk/chunk_handler.py` — sliding window chunker
- `backend/pipeline/embed/embed_handler.py` — Bedrock Titan Embed v2, pgvector insert
- `backend/pipeline/metadata/metadata_handler.py` — structured Haiku output, markdown fence stripping

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
