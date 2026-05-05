# Article 1: Building a Multi-Tenant AI Document Platform on AWS

**Tagline:** How I designed a production-ready RAG system from scratch using AWS-native services — and kept the monthly bill under $20.

---

## Outline

### Hook
Start with the job description problem: every senior full-stack role now wants "AI experience" but most demo apps are toy chatbots. This project shows how to build something technically credible — multi-tenancy, real pipelines, cost discipline — not just a wrapper around an LLM API.

### What we're building
- Upload PDFs, DOCX, CSV, TXT files
- Async pipeline: extract → chunk → embed → metadata
- RAG chat with citations linking back to source chunks
- True multi-tenancy: each organization's data is fully isolated
- Under $20/month to run at demo scale

### Architecture diagram
Walk through the full diagram from the README:
- React SPA → CloudFront → API Gateway → C# Lambda
- C# Lambda → Aurora PostgreSQL + pgvector + Bedrock
- S3 upload → EventBridge → Step Functions → Python Lambdas → Aurora

### Why each service was chosen
Table from `infrastructure/template.yaml` rationale comments:
- **Aurora Serverless v2 with auto-pause** — scales to zero, pgvector built in, real Postgres (not DynamoDB)
- **Step Functions Express** — makes the pipeline visible in the AWS console; retry/error handling as config not code
- **Bedrock over OpenAI** — no egress to third-party; stays inside the AWS trust boundary; IAM auth
- **Cognito** — managed auth, JWT with custom claims, no auth server to operate
- **SAM over CDK** — YAML CloudFormation is the JD requirement; CDK adds abstraction but hides what interviewers want to see you know

### Multi-tenancy strategy
Three-layer isolation:
1. Cognito custom claim (`tenantId` injected by Pre-Token Lambda)
2. S3 key prefix (`{tenantId}/{docId}/...`)
3. Postgres RLS — show the `CREATE POLICY` SQL and the C# `SET LOCAL app.current_tenant_id` call

### Cost breakdown
Reference the table in `infrastructure/template.yaml`:
- Aurora auto-pause: ~$7/mo
- Bedrock (light usage): ~$2-5/mo
- Everything else: free tier
- Total: ~$10-15/mo

### AWS Well-Architected alignment
Brief callout: operational excellence (SAM + GitHub Actions), security (OIDC, RLS, no stored secrets), reliability (Step Functions retries), performance efficiency (Aurora Serverless v2, arm64 Lambda), cost optimization (auto-pause, free tier services).

### What's next in this series
Preview the remaining 5 articles.

---

## Key code references
- `infrastructure/template-cognito.yaml` — Pre-Token Lambda, User Pool, custom claim
- `infrastructure/template-database.yaml` — Aurora Serverless v2, RLS policies
- `migrations/001_initial_schema.sql` — full schema with `CREATE POLICY` statements
- `backend/src/Sift.Api/Infrastructure/TenantContext.cs` — the SET LOCAL call
