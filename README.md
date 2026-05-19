# Sift — AI Document Intelligence Platform

> Ask questions about your documents. Get answers with citations, backed by your own data.

Sift is a production-architected, multi-tenant RAG (Retrieval-Augmented Generation) platform built entirely on AWS-native services. Upload PDFs, Word docs, CSVs, or text files — Sift extracts, chunks, embeds, and indexes them so you can chat with your document library using natural language.

Built as a portfolio demonstration of full-stack AI/cloud engineering: serverless compute, vector search, streaming pipelines, multi-tenant data isolation, and zero-secret CI/CD.

---

## Live Demo

> **[[https://d3dsn1f23yg4bo.cloudfront.net/]](https://d3dsn1f23yg4bo.cloudfront.net/)** — sign up with any email, start uploading immediately.

The demo environment runs in a shared tenant (Acme Corp). Documents you upload are processed within ~30 seconds and immediately available for chat.

---

## What It Does

### Upload
Drop a PDF, DOCX, CSV, or TXT file into the document library. Sift generates a presigned S3 URL and uploads directly from your browser — the API server never touches your file bytes.

### Process
An event-driven pipeline kicks off automatically on upload:

1. **Extract** — pulls plain text from the file (PDF pages, DOCX paragraphs, CSV rows)
2. **Chunk** — splits text into overlapping windows for context preservation
3. **Embed** — generates 1024-dimensional vector embeddings via Amazon Bedrock Titan Embed v2
4. **Metadata** — Claude Haiku 4.5 writes a one-paragraph summary and extracts key topics
5. **Ready** — document status flips; the UI polls and updates in real time

### Chat
Ask anything in natural language. Sift embeds your question, runs a cosine similarity search against all your document chunks, assembles the top matches as context, and sends them to Claude Haiku 4.5 to generate a grounded answer with numbered citations.

### Manage
The Settings page shows your tenant profile and all users in your organization. Admins can promote or demote members with a single click.

---

## Architecture

```
Browser
  │
  ├─► Cognito (auth)          JWT with custom tenantId claim
  │
  └─► API Gateway HTTP API
        │
        ├─► DocumentsFunction (C# .NET 8 Lambda)
        │     ├─ GET  /documents
        │     ├─ POST /documents/upload-url  → presigned S3 PUT
        │     ├─ GET  /documents/{id}
        │     └─ DELETE /documents/{id}
        │
        ├─► ChatFunction (C# .NET 8 Lambda)
        │     └─ POST /chat  → embed → pgvector search → Claude
        │
        └─► TenantFunction (C# .NET 8 Lambda)
              ├─ POST /tenants/me/sync
              ├─ GET  /tenants/me
              ├─ GET  /tenants/users
              └─ PUT  /tenants/users/{id}/role

S3 (uploads bucket)
  └─► EventBridge (Object Created)
        └─► Step Functions Express — document pipeline
              ├─ ExtractText      (Python Lambda)
              ├─ ChunkText        (Python Lambda)
              ├─ GenerateEmbeddings (Map, MaxConcurrency=5)
              │    └─ EmbedChunk  (Python Lambda × N)
              ├─ ExtractMetadata  (Python Lambda → Claude Haiku 4.5)
              ├─ MarkReady        (Python Lambda)
              └─ MarkFailed       (Python Lambda, catch-all)

Aurora PostgreSQL Serverless v2
  ├─ pgvector extension (cosine similarity search)
  └─ Row-Level Security (per-tenant data isolation)
```

### Key Design Decisions

**Why C# for the API?** .NET 8 Lambda cold starts are competitive with Python for I/O-bound workloads, and the type system catches an entire class of bugs before deployment. The three API functions share a single compiled binary — SAM routes handler classes within it.

**Why Python for the pipeline?** The document processing ecosystem (pdfplumber, python-docx, pandas) is Python-native. A shared Lambda Layer keeps dependencies DRY across the five pipeline functions.

**Why Step Functions over SQS?** Built-in retry logic, fan-out with the Map state, visual execution graphs, and structured error routing to a catch-all MarkFailed state — with zero orchestration code.

**Why Aurora Serverless v2 over a dedicated vector DB?** pgvector on Aurora gives you SQL joins, ACID transactions, Row-Level Security, and vector similarity search in one service. For document workloads under a few million chunks, there's no need for a dedicated vector database.

**Why presigned S3 URLs?** Files never pass through the API tier. The browser uploads directly to S3, which keeps Lambda payload limits out of the picture and eliminates unnecessary data transfer costs.

---

## Multi-Tenancy

Three independent isolation layers ensure one tenant can never read another's data:

| Layer | Mechanism |
|---|---|
| **Auth** | Cognito Pre-Token Lambda injects `tenantId` into every JWT |
| **Storage** | S3 keys prefixed `{tenantId}/{docId}/filename` |
| **Database** | Postgres RLS — `SET app.current_tenant_id` before every query; policies enforce `tenant_id = current_setting(...)::UUID` |

---

## Tech Stack

| Layer | Technology |
|---|---|
| Frontend | React 18, TypeScript, Vite, Tailwind CSS v4, AWS Amplify |
| API | C# .NET 8 Lambda (x86_64), API Gateway HTTP API |
| Pipeline | Python 3.12 Lambdas, Step Functions Express, EventBridge |
| Database | Aurora PostgreSQL Serverless v2, pgvector |
| AI / Embeddings | Amazon Bedrock — Titan Embed v2 (1024-dim), Claude Haiku 4.5 |
| Auth | Amazon Cognito User Pool, JWT authorizer, Pre-Token Lambda |
| Storage | Amazon S3 with presigned URLs and EventBridge notifications |
| IaC | AWS SAM (CloudFormation) — 4 stacks (Cognito, DB, API, Frontend) |
| CI/CD | GitHub Actions + OIDC (no stored AWS credentials) |
| Region | us-west-2 |

---

## CI/CD

Every push to `main` triggers a GitHub Actions workflow that:

1. Builds all Lambda artifacts (dotnet publish for C#, pip install for Python)
2. Runs tests (xUnit for C#, pytest for Python)
3. Assumes an AWS IAM role via OIDC — **no `AWS_ACCESS_KEY_ID` or `AWS_SECRET_ACCESS_KEY` stored anywhere**
4. Deploys via `sam deploy` (build artifacts only, not raw source)
5. Syncs the frontend build to S3 and invalidates CloudFront

The OIDC trust is scoped to the specific GitHub repo and branch, so a fork cannot assume the role.

---

## Cost Estimate (us-west-2, light demo usage)

| Service | Config | Est. Monthly |
|---|---|---|
| Aurora Serverless v2 | Min 0 ACU (auto-pause), max 4 ACU | ~$7 |
| Amazon Bedrock | ~500 embed calls + ~100 chat completions/mo | ~$1–3 |
| Lambda | Well within free tier at demo scale | ~$0 |
| API Gateway | Well within free tier at demo scale | ~$0 |
| S3 | A few GB of documents | ~$0 |
| CloudFront + CDN | Free tier | ~$0 |
| Step Functions | EXPRESS type — priced per invocation | ~$0 |
| Secrets Manager | 1 secret | ~$0.40 |
| **Total** | | **~$9–11/mo** |

Aurora auto-pause brings the cluster to 0 ACUs when idle, which accounts for most of the bill. The first request after a pause incurs a ~10-second cold start.

---

## Running Locally (API tests only)

```bash
# Python pipeline tests
PYTHONPATH=backend/pipeline/layers/shared/python \
  pytest backend/pipeline/chunk/tests/ \
         backend/pipeline/extract/tests/ \
         backend/pipeline/embed/tests/ \
         backend/pipeline/metadata/tests/ -v

# C# unit tests (requires .NET 8 SDK)
cd backend && dotnet test

# Frontend typecheck + build
cd frontend && npx tsc --noEmit && npm run build
```

The API and pipeline require live AWS resources (Aurora, Bedrock, S3) and are not designed to run fully offline.

---

## Deployment

Requires: AWS CLI, SAM CLI, Node 20+, .NET 8 SDK, Python 3.12.

```bash
# 1. Bootstrap — creates the OIDC role and SAM artifact bucket (once)
bash scripts/bootstrap.sh

# 2. Cognito stack (once)
sam deploy --template-file infrastructure/template-cognito.yaml \
           --stack-name sift-cognito-dev --capabilities CAPABILITY_IAM

# 3. Database stack — VPC + Aurora (once)
sam deploy --template-file infrastructure/template-database.yaml \
           --stack-name sift-database-dev --capabilities CAPABILITY_IAM \
           --parameter-overrides Env=dev

# 4. Run migrations
python3 scripts/migrate-local.py

# 5. Main stack — Lambda + API + pipeline
sam build && sam deploy --stack-name sift-dev \
           --capabilities CAPABILITY_IAM CAPABILITY_NAMED_IAM \
           --parameter-overrides Env=dev SamBucket=<your-sam-bucket>

# 6. Frontend stack — CloudFront + S3
aws cloudformation deploy \
    --template-file infrastructure/template-frontend.yaml \
    --stack-name sift-frontend-dev

# 7. Build and sync frontend
cd frontend && npm run build
aws s3 sync dist/ s3://<frontend-bucket>/ --delete
aws cloudfront create-invalidation --distribution-id <dist-id> --paths "/*"
```

Copy `frontend/.env.example` → `frontend/.env.local` and fill in the Cognito and API values from the stack outputs.

---

## Article Series

This project is documented in a six-part series covering the engineering decisions in depth:

1. [Architecture Overview](docs/articles/01-architecture.md) — system design and AWS service selection
2. [Multi-Tenant Auth](docs/articles/02-auth-multitenant.md) — Cognito + PostgreSQL Row-Level Security
3. [Document Pipeline](docs/articles/03-step-functions-pipeline.md) — Step Functions Express orchestration
4. [RAG with pgvector](docs/articles/04-rag-pgvector.md) — embeddings, cosine search, and citations
5. [React Frontend](docs/articles/05-react-frontend.md) — document library, chat UI, and polling
6. [Zero-Secret CI/CD](docs/articles/06-cicd.md) — GitHub Actions + OIDC, no stored credentials

---

## Repository Layout

```
backend/
  src/Sift.Api/              C# Lambda functions + shared infrastructure
  src/Sift.Api.Tests/        xUnit + Moq unit tests
  pipeline/
    layers/shared/           Python Lambda Layer (db.py, bedrock.py)
    extract/                 Text extraction (PDF, DOCX, CSV, TXT)
    chunk/                   Sliding-window chunker with overlap
    embed/                   Bedrock Titan Embed v2 → pgvector insert
    metadata/                Claude Haiku — summary + topics
    mark_ready/              Sets document status to ready
    mark_failed/             Catch-all error handler
frontend/
  src/
    api/client.ts            Axios + auto-inject Bearer token
    auth/cognito.ts          Amplify config + ID token helper
    components/              DocumentCard, UploadDropzone, ChatMessage, Layout
    hooks/                   useDocuments (polling), useChat
    pages/                   Documents, Chat, Settings
infrastructure/
  template.yaml              Main stack — API + Lambda + pipeline + Step Functions
  template-cognito.yaml      Cognito User Pool (deploy once)
  template-database.yaml     VPC + Aurora Serverless v2
  template-frontend.yaml     CloudFront + S3
  parameters/                dev.json, prod.json
migrations/
  001_initial_schema.sql     pgvector, tenants, users, documents, chunks, RLS
scripts/
  bootstrap.sh               One-time OIDC role + SAM bucket setup
  migrate-local.py           Run migrations via RDS Data API (no VPN needed)
  smoke-test.sh              End-to-end API verification
docs/articles/               Six-part engineering article series
```
