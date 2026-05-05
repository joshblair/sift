# Article 3: Serverless Document Pipelines with AWS Step Functions

**Tagline:** Why I chose Step Functions over SQS + Lambda, and what the AWS console visualization is worth in a technical interview.

---

## Outline

### Hook
Everyone reaches for SQS + Lambda for async processing. It works, but when something goes wrong you're left staring at CloudWatch logs trying to reconstruct what happened. Step Functions gives you a visual execution history, per-step retry configuration, and structured error handling — for basically no extra cost with Express Workflows.

### Architecture: S3 → EventBridge → Step Functions

**S3 event notification**
Show the `NotificationConfiguration` in the SAM template that sends S3 Object Created events to EventBridge.

**EventBridge rule**
Walk through `S3UploadRule` in `template.yaml`:
- Event pattern matching on bucket name
- `InputTransformer` that reshapes the S3 event into `{ s3Key, bucketName }`

**Why EventBridge over S3 → Lambda directly?**
Decoupling: the Step Functions ARN isn't embedded in the S3 bucket config. You can swap out the pipeline without touching the bucket.

### The state machine walkthrough

Show the YAML state machine definition and walk through each state:

**State 1: ExtractText**
- Downloads from S3, dispatches on file extension
- pdfplumber for PDFs (page count tracking)
- python-docx for DOCX
- pandas for CSV (converts to text representation)
- Retry: 2 attempts on TaskFailed (transient S3 errors)
- Catch: all errors → MarkFailed

**State 2: ChunkText**
- Sliding-window algorithm — show the `_chunk()` function
- `CHUNK_SIZE = 512 tokens`, `CHUNK_OVERLAP = 64 tokens`
- Why overlap? Adjacent chunks share context so semantic search doesn't miss answers that span a chunk boundary
- No external dependencies (stdlib only — fast cold starts)

**State 3: GenerateEmbeddings (Map state)**
- The Map state fans out one Lambda invocation per chunk
- `MaxConcurrency: 5` — prevents Bedrock rate limiting
- `Parameters` block reshapes each array item: show `$$.Map.Item.Value.content`
- `ResultPath: $.embeddingResults` — preserves original input for downstream states
- Each EmbedChunk Lambda: calls Titan Embed v2, inserts to pgvector as `$1::vector`

**State 4: ExtractMetadata**
- Truncates text to 6000 chars to stay within Haiku context window
- Calls Claude Haiku with structured JSON output prompt
- Strips markdown fences from response (show the `_extract` function)
- Writes summary + topics back to documents table

**State 5: MarkReady / MarkFailed**
- Sets status + processed_at (or error_message on failure)
- Document status drives the React UI polling logic

### Express Workflow vs Standard Workflow
- Express: pays per execution-second (cheap for short pipelines), at-least-once, async — right choice here
- Standard: pays per state transition (expensive for Map states), exactly-once — for financial transactions

### Observability
Step Functions console shows every execution with input/output per state. Demo: show a failed execution with the error visible in the MarkFailed state's output. Compare to SQS DLQ debugging.

---

## Key code references
- `infrastructure/template.yaml` — state machine definition YAML
- `backend/pipeline/extract/extract_handler.py` — file type dispatch
- `backend/pipeline/chunk/chunk_handler.py` — sliding window algorithm
- `backend/pipeline/embed/embed_handler.py` — Bedrock + pgvector insert
- `backend/pipeline/metadata/metadata_handler.py` — structured Haiku output
