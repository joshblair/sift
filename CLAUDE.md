# Sift — AI Document Intelligence Platform

Portfolio demo app. Multi-tenant RAG platform on AWS targeting a full-stack AI/AWS engineering role.

## Stack

| Layer | Technology |
|---|---|
| Frontend | React 18 + TypeScript + Vite + Tailwind v4 + Amplify Auth |
| API | C# .NET 8 Lambda (arm64), API Gateway HTTP API, Cognito JWT auth |
| Pipeline | Python 3.12 Lambdas, Step Functions Express, EventBridge |
| Database | Aurora PostgreSQL Serverless v2 (us-west-2), pgvector extension |
| AI | Bedrock Titan Embed v2 (embeddings), Claude Haiku (metadata + chat) |
| Auth | Cognito User Pool with custom `tenantId` claim via Pre-Token Lambda |
| IaC | AWS SAM (CloudFormation YAML) |
| CI/CD | GitHub Actions with OIDC (no stored AWS secrets) |

## Repository layout

```
backend/
  src/Sift.Api/          C# Lambda functions (Documents, Chat, Tenant)
  src/Sift.Api.Tests/    xUnit + Moq tests
  pipeline/
    layers/shared/       Shared Lambda Layer (db.py, bedrock.py)
    extract/             PDF/DOCX/CSV/TXT text extraction
    chunk/               Sliding-window chunker
    embed/               Bedrock Titan Embed v2 + pgvector insert
    metadata/            Claude Haiku summary + topics
    mark_ready/          Final status update
    mark_failed/         Error catch handler
frontend/
  src/
    api/client.ts        Axios + auto-inject Bearer token
    auth/cognito.ts      Amplify config + token helper
    components/          DocumentCard, UploadDropzone, ChatMessage, Layout
    hooks/               useDocuments (polling), useChat
    pages/               Documents, Chat, Settings
infrastructure/
  template.yaml          SAM: API Gateway + C# + Python Lambdas + Step Functions
  template-cognito.yaml  Cognito User Pool (deploy once)
  template-database.yaml VPC + Aurora Serverless v2
  template-frontend.yaml CloudFront + S3 static hosting
  parameters/            dev.json, prod.json
migrations/
  001_initial_schema.sql pgvector, tenants, users, documents, chunks, RLS
scripts/
  bootstrap.sh           One-time: OIDC role + SAM S3 bucket
  seed-db.sql            Demo tenants + pre-processed document records
  smoke-test.sh          End-to-end API test (upload → pipeline → chat)
docs/articles/           6-part article series outlines
```

## Multi-tenancy

Three-layer isolation:
1. **Cognito** — `tenantId` injected into JWT by Pre-Token Generation Lambda
2. **S3** — keys prefixed `{tenantId}/{docId}/filename`
3. **Postgres RLS** — `SET LOCAL app.current_tenant_id = $1` before every query; RLS policies enforce isolation automatically

## Running tests

```bash
# Python (from repo root)
PYTHONPATH=backend/pipeline/layers/shared/python pytest backend/pipeline/chunk/tests/ -v
PYTHONPATH=backend/pipeline/layers/shared/python pytest backend/pipeline/extract/tests/ -v
PYTHONPATH=backend/pipeline/layers/shared/python pytest backend/pipeline/embed/tests/ -v
PYTHONPATH=backend/pipeline/layers/shared/python pytest backend/pipeline/metadata/tests/ -v

# C# (requires .NET 8 SDK)
cd backend && dotnet test

# Frontend typecheck + build
cd frontend && npx tsc --noEmit && npm run build
```

## Deployment order

1. `bash scripts/bootstrap.sh` — create OIDC role + SAM bucket (once)
2. `sam deploy --template-file infrastructure/template-cognito.yaml` — Cognito stack (once)
3. `sam deploy --template-file infrastructure/template-database.yaml` — VPC + Aurora (once)
4. Run `migrations/001_initial_schema.sql` against the Aurora cluster
5. `sam deploy --template-file infrastructure/template.yaml` — main stack (Lambda + API + pipeline)
6. `aws cloudformation deploy --template-file infrastructure/template-frontend.yaml` — CloudFront
7. Build + sync frontend to S3

After deploying, run `scripts/smoke-test.sh` to verify end-to-end.

## Environment variables

Frontend: copy `frontend/.env.example` → `frontend/.env.local`

Lambda (set by SAM template via CloudFormation exports):
- `DB_SECRET_ARN` — Secrets Manager ARN for Aurora credentials
- `DB_HOST` — Aurora cluster endpoint
- `DB_PORT` — 5432
- `DB_NAME` — sift
- `UPLOADS_BUCKET` — S3 bucket name

## Cost (us-west-2, dev)

~$10-15/month: Aurora auto-pause (~$7), Bedrock light usage (~$2-5), everything else free tier.
