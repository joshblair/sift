# Building a Multi-Tenant AI Document Platform on AWS (Part 1: Architecture)

*How I designed a production-ready RAG system from scratch using AWS-native services — and kept the monthly bill under $20.*

---

Every senior full-stack role I see right now lists "AI experience" somewhere in the requirements. The problem is that most AI portfolio projects look the same: a Next.js frontend, a call to the OpenAI API, maybe a LangChain wrapper. They're fine demos, but they don't show anything about how you'd actually build and operate a system at scale.

I wanted to build something that would hold up in a technical interview with an AWS architect — not just "I built a chatbot." So I built **Sift**: a multi-tenant RAG (Retrieval-Augmented Generation) document platform, fully serverless, built on AWS-native services, with real data isolation between tenants, an async document processing pipeline, and a total monthly cost of about $10–15 in a live demo environment.

This is the first post in a six-part series. Here I'll walk through the overall architecture and explain why I made each service choice. Future posts will dig into specific areas: auth and multi-tenancy, the Step Functions pipeline, the RAG implementation, the React frontend, and the CI/CD pipeline.

---

## What Sift Does

Users upload documents — PDF, DOCX, CSV, or plain text. Sift processes them asynchronously through a six-stage pipeline, generating text embeddings and storing them in a vector database. Users then ask natural language questions, and Sift retrieves the most relevant chunks from their documents and feeds them to a Claude model to generate a grounded answer with numbered citations linking back to the source text.

It's a legitimate enterprise use case: internal knowledge bases, contract review, research assistants. The architecture reflects that — it's not a demo that would fall apart the moment a second organization tried to use it.

---

## The Full Architecture

Here's how data flows through the system:

```
┌──────────────────────────────────────────────────────────────────┐
│                         Browser (React SPA)                      │
└──────────┬───────────────────────────────────────────────────────┘
           │ Auth: JWT (Cognito)
           ▼
┌──────────────────────┐     ┌─────────────────────────────────────┐
│  CloudFront + S3     │     │       Amazon Cognito                │
│  (Static hosting)    │     │  User Pool + Pre-Token Lambda       │
└──────────────────────┘     └─────────────────────────────────────┘
                                         │ JWT with custom tenantId claim
           ┌─────────────────────────────▼
           │        API Gateway HTTP API
           │        (Cognito JWT Authorizer)
           └────────┬────────────────────────────┐
                    │                            │
        ┌───────────▼──────────┐   ┌─────────────▼──────────────┐
        │  DocumentsFunction   │   │       ChatFunction         │
        │  (.NET 8 Lambda)     │   │      (.NET 8 Lambda)       │
        │  GET/POST/DELETE     │   │  Embed → pgvector → Claude  │
        └───────────┬──────────┘   └─────────────┬──────────────┘
                    │                             │
           S3 Presigned URL               Bedrock (Titan Embed v2
           (Direct browser upload)         + Claude Haiku 4.5)
                    │
           ┌────────▼────────┐
           │    S3 Bucket    │
           │ (Document store)│
           └────────┬────────┘
                    │ EventBridge (Object Created)
                    ▼
        ┌───────────────────────────────────────┐
        │    Step Functions Express Workflow    │
        │                                       │
        │  1. ExtractText  (Python Lambda)      │
        │  2. ChunkText    (Python Lambda)      │
        │  3. GenerateEmbeddings (Map State)    │
        │     └── EmbedChunk × N (parallel)    │
        │  4. ExtractMetadata  (Python Lambda)  │
        │  5. MarkReady    (Python Lambda)      │
        │     MarkFailed   (Python Lambda)      │
        └───────────┬───────────────────────────┘
                    │
                    ▼
        ┌───────────────────────────────────────┐
        │  Aurora PostgreSQL Serverless v2      │
        │  pgvector (cosine similarity)         │
        │  Row-Level Security (per-tenant RLS)  │
        └───────────────────────────────────────┘
```

Let's go through each choice.

---

## Why Each Service?

### Aurora Serverless v2 with pgvector

The go-to recommendation for vector storage right now is a managed vector database — Pinecone, Weaviate, OpenSearch with k-NN. I deliberately chose not to use any of them.

Sift already needs a relational database for its operational data: tenants, users, documents, processing status. Aurora PostgreSQL Serverless v2 handles all of that *and* the vector search with the pgvector extension. That's one less service to operate, one less connection to secure, one less cost line item.

pgvector supports cosine similarity search, IvfFlat indexes for approximate nearest-neighbor search at scale, and runs directly in the Postgres query planner — which means I can join vector search results with relational data in a single query. When a user asks a question, I can filter to only that tenant's documents *before* the similarity search, not after. That's a meaningful security and efficiency win.

The Serverless v2 auto-pause feature means the cluster scales to zero ACUs (Aurora Capacity Units) during idle periods. For a portfolio demo that doesn't have 24/7 traffic, that alone cuts the database cost from ~$50–70/month for a minimum-size provisioned Aurora instance to roughly $7/month in actual usage.

The tradeoff: the first query after an idle period has a cold-start delay while the cluster resumes — typically 5–15 seconds. That's acceptable for a demo, and for production you'd set a non-zero minimum ACU to keep it warm.

### Step Functions Express Workflows

When I first sketched the pipeline, the obvious choice was SQS queues between Lambda stages — it's the standard event-driven pattern and I've used it plenty of times. I chose Step Functions Express instead, and it was the right call for this project.

The visibility argument is simple: when I open the AWS console and click into a Step Functions execution, I can see exactly which stage a document is in, what the input and output were at each step, and precisely where it failed if something went wrong. With SQS, you're inferring pipeline state from DLQ message counts and CloudWatch metrics. That's fine in production where you've built dashboards for it — it's friction in a portfolio project where the goal is demonstrating the architecture clearly.

Step Functions also handles retries and error catching declaratively in the state machine definition. Instead of writing retry logic in each Lambda, I configure it once in the YAML:

```yaml
Retry:
  - ErrorEquals: [States.ALL]
    IntervalSeconds: 2
    MaxAttempts: 3
    BackoffRate: 2
```

Transient errors — throttling, network blips — get retried automatically. Unrecoverable failures route to the `MarkFailed` state, which updates the document status and preserves the error for display in the UI.

Express Workflows (vs. Standard Workflows) are priced per execution and duration rather than per state transition. For a document pipeline that completes in under 5 minutes, this is significantly cheaper. The tradeoff is that Express Workflows have a maximum duration of 5 minutes and don't support activities or sync patterns that Standard Workflows do — neither matters here.

### Amazon Bedrock

I used Bedrock for both embedding generation (Titan Embed v2, 1024 dimensions) and the chat completion (Claude Haiku 4.5).

The straightforward reason is that it keeps everything inside the AWS trust boundary. No data leaves my VPC to a third-party API. Authentication is IAM, not an API key stored in a secret. There's no separate vendor account to manage, no risk of data being used for model training, and the latency is lower because it's in-region.

The more practical reason: on an AWS-focused résumé, "I used Bedrock" is worth more than "I used OpenAI." It demonstrates familiarity with Bedrock's APIs, model catalog, and IAM integration patterns that you'd actually use in enterprise AWS environments.

Titan Embed v2 produces 1024-dimensional vectors with normalized outputs — which means cosine similarity and dot product give equivalent results, simplifying the pgvector query. Claude Haiku 4.5 handles the metadata extraction (generating a document summary and extracting topics from text chunks) and the final chat response generation. Haiku is fast and cheap for the metadata step; the quality is more than adequate for structured extraction tasks.

### Amazon Cognito

Auth is one of those areas where building your own is almost always the wrong call. Cognito gives you a managed user pool with email/password auth, JWT issuance, and a hosted UI for free at demo scale.

The interesting piece is the Pre-Token Generation Lambda trigger. When Cognito issues a JWT, it calls a Lambda function that can add custom claims to the token before it's signed. I use this to inject a `tenantId` claim — the tenant the user belongs to — directly into the token.

That `tenantId` flows downstream: API Gateway validates the JWT and rejects unauthenticated requests; my Lambda functions extract the claim from the validated token context; and the database uses it to enforce Row-Level Security. The tenant identity is cryptographically bound to the token — a user can't forge a different `tenantId` without Cognito's private signing key.

### AWS SAM over CDK

CDK is a better abstraction for large teams and complex infrastructure. SAM is CloudFormation with a thinner layer of syntactic sugar, which means the output is standard CloudFormation YAML that any AWS engineer can read without knowing TypeScript or Python CDK constructs.

For portfolio purposes, that's actually the right choice. When I'm walking through this in an interview, I want to point directly at `template.yaml` and explain the `AWS::Serverless::StateMachine` resource, the IAM policy, the event source mapping — without the interviewer needing to know what `aws_cdk.aws_stepfunctions` compiles to under the hood.

---

## Multi-Tenancy: Three Layers of Isolation

This is where a lot of "multi-tenant" demos fall short — they add a `tenantId` column to their tables and filter on it in application code. That works until a bug in one query path leaks cross-tenant data.

Sift uses three independent isolation layers:

**Layer 1 — Cognito custom claim.** The `tenantId` is in the signed JWT. No application code can inject or modify it.

**Layer 2 — S3 key prefix.** Every uploaded document is stored at `{tenantId}/{documentId}/{filename}`. Tenant A can't construct a presigned URL for tenant B's key without knowing the exact key path — and they'd also need to be an IAM principal with S3 access, which they're not.

**Layer 3 — PostgreSQL Row-Level Security.** Before any database query executes, the Lambda sets a session variable:

```sql
SELECT set_config('app.current_tenant_id', $1, false)
```

The RLS policies on every table enforce this automatically at the database level:

```sql
CREATE POLICY tenant_isolation ON documents
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
```

Even if application code contained a bug that omitted a `WHERE tenant_id = ?` clause, the database would still return only the current tenant's rows. The isolation is enforced below the application layer.

One important gotcha: the database application user must not have `BYPASSRLS` privileges. Postgres superusers bypass RLS by default. If your Lambda connects as a superuser, your RLS policies are decoration.

---

## Cost Breakdown

One thing I wanted to demonstrate with this project is that you can run a legitimate, production-architected system for almost nothing at low volume:

| Service | Monthly Cost |
|---|---|
| Aurora Serverless v2 (auto-pause, demo traffic) | ~$7 |
| Amazon Bedrock (embeddings + chat, light usage) | ~$2–5 |
| Lambda, API Gateway, S3, CloudFront | Free tier |
| Cognito (under 50k MAU) | Free tier |
| **Total** | **~$10–15** |

The variable cost scales with Bedrock usage — each embedding call and each chat completion. At real production volume you'd be looking at real Bedrock costs, and you'd want to add CloudFront caching for static assets and tune Lambda memory settings. But the architectural pattern holds: Aurora Serverless v2 auto-pause means you're not paying for compute when nobody is using the system.

---

## AWS Well-Architected Alignment

Since this is a portfolio project for AWS architecture roles, I mapped the design against the six pillars explicitly:

- **Operational Excellence** — SAM + GitHub Actions with OIDC: the entire infrastructure is code, deployments are automated, there are no manual steps
- **Security** — OIDC federation (zero stored credentials), Row-Level Security, Cognito JWT, no Lambda with public S3 access
- **Reliability** — Step Functions retry logic, EventBridge decoupling between S3 upload and pipeline execution, Aurora Multi-AZ in production config
- **Performance Efficiency** — Aurora Serverless v2 scales with demand, Map state in Step Functions parallelizes embedding generation
- **Cost Optimization** — Aurora auto-pause, Bedrock pay-per-token, Lambda and CloudFront at free tier scale
- **Sustainability** — Serverless by default: compute resources allocated only when actively processing

---

## What's Next

This series covers each major subsystem in depth:

- **Part 2:** Multi-tenant auth — how the Cognito Pre-Token Lambda works, the RLS policy setup, and the C# tenant context middleware
- **Part 3:** The Step Functions pipeline — state machine design, the Map state for parallel embedding, error handling
- **Part 4:** RAG and vector search — chunking strategy, pgvector queries, and how citations are generated
- **Part 5:** The React frontend — polling patterns, Amplify auth integration, the upload flow
- **Part 6:** CI/CD with GitHub Actions and OIDC — zero-secret deployments to AWS

The live demo is running at [d3dsn1f23yg4bo.cloudfront.net](https://d3dsn1f23yg4bo.cloudfront.net) — log in with the shared Acme Corp credentials from the README and try uploading a PDF.

The code is at [github.com/joshblair/sift](https://github.com/joshblair/sift).

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
