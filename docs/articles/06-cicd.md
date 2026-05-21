---
title: "Zero-Secret CI/CD: GitHub Actions + OIDC on AWS (Part 6)"
published: false
description: "No AWS_ACCESS_KEY_ID in your GitHub secrets. Ever. Here's how OIDC trust works and why it's strictly better."
tags: github, aws, devops, cicd
series: "Building Sift: A Multi-Tenant AI Platform on AWS"
cover_image: https://raw.githubusercontent.com/joshblair/sift/main/docs/diagrams/sift-diagram-architecture.png
---

# Zero-Secret CI/CD: GitHub Actions + OIDC on AWS (Part 6)

*No `AWS_ACCESS_KEY_ID` in your GitHub secrets. Ever. Here's how OIDC trust works and why it's strictly better.*

---

The most common GitHub Actions setup I see in portfolios stores `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` as repository secrets. Those are long-lived credentials tied to an IAM user. One breach of your GitHub account — a compromised OAuth token, a compromised third-party Action, a secret accidentally logged in workflow output — and an attacker has permanent AWS access until someone notices and rotates the keys.

OIDC federation eliminates the stored credentials entirely. GitHub Actions assumes an IAM role using a short-lived signed token. When the job ends, the session expires. There are no keys to rotate because there are no keys.

This post covers how the trust relationship works, how the CI and deploy workflows are structured, and how the frontend gets deployed to CloudFront with correct cache headers.

---

## How GitHub Actions OIDC Works

GitHub operates as an OpenID Connect identity provider. When a workflow job runs with the `id-token: write` permission, GitHub can mint a signed JWT asserting the identity of the running job:

```json
{
  "sub": "repo:joshblair/sift:ref:refs/heads/main",
  "aud": "sts.amazonaws.com",
  "iss": "https://token.actions.githubusercontent.com",
  "repository": "joshblair/sift",
  "ref": "refs/heads/main"
}
```

AWS STS accepts this JWT via `AssumeRoleWithWebIdentity` and issues a short-lived role session — credentials that expire when the job ends, typically within an hour. The exchange only works if AWS has been told to trust GitHub's OIDC provider and the role's trust policy permits the specific repository making the request.

### Setting Up the Trust (Once Per Account)

`scripts/bootstrap.sh` runs once to wire this up. It does three things:

**1. Creates the GitHub OIDC provider in IAM:**

```bash
aws iam create-open-id-connect-provider \
  --url "https://token.actions.githubusercontent.com" \
  --client-id-list "sts.amazonaws.com" \
  --thumbprint-list "6938fd4d98bab03faadb97b34396831e3780aea1"
```

This tells AWS to trust JWTs signed by GitHub's OIDC endpoint. It's a one-time setup for the AWS account — not per-repo.

**2. Creates the IAM deploy role with a scoped trust policy:**

```json
{
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::ACCOUNT_ID:oidc-provider/token.actions.githubusercontent.com" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
      },
      "StringLike": {
        "token.actions.githubusercontent.com:sub": "repo:joshblair/sift:*"
      }
    }
  }]
}
```

The `StringLike` condition on `sub` limits trust to jobs running from the `joshblair/sift` repository. The wildcard `*` allows both branch pushes and pull request checks. For a production setup, you'd tighten this to `repo:org/repo:ref:refs/heads/main` to prevent deploy jobs from running on feature branches.

**3. Prints the values to add as GitHub Actions variables:**

```
Bootstrap complete. Add these as GitHub Actions variables in your repo settings:

  AWS_REGION       = us-west-2
  SAM_BUCKET       = sift-sam-123456789-us-west-2
  DEPLOY_ROLE_ARN  = arn:aws:iam::123456789:role/sift-github-actions-deploy
```

These go in the repository's Variables (not Secrets) — they're not sensitive values. Cognito configuration (`VITE_USER_POOL_ID`, `VITE_USER_POOL_CLIENT_ID`, `VITE_COGNITO_DOMAIN`) is also stored as variables.

### In the Workflow

With the provider and role in place, a single step handles authentication in every job that needs AWS access:

```yaml
permissions:
  id-token: write   # allows the job to request an OIDC token
  contents: read

- uses: aws-actions/configure-aws-credentials@v4
  with:
    role-to-assume: ${{ vars.DEPLOY_ROLE_ARN }}
    aws-region:     ${{ vars.AWS_REGION }}
```

After this step, the job has temporary AWS credentials in its environment — the same `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, and `AWS_SESSION_TOKEN` that the AWS CLI and SDKs look for, but populated automatically for the duration of the job.

---

## CI: Three Parallel Jobs on Every Pull Request

`ci.yml` triggers on pull requests to main. Three jobs run in parallel — each covers a different part of the stack.

### .NET Build and Test

```yaml
- name: Restore
  run: dotnet restore
  working-directory: backend
- name: Build
  run: dotnet build --no-restore --configuration Release
- name: Test
  run: dotnet test --no-build --configuration Release --logger "console;verbosity=normal"
```

Standard `dotnet` pipeline. The `--no-restore` and `--no-build` flags avoid redundant work between steps. Tests run against unit test doubles — no database, no Bedrock — so they complete in a few seconds with no external dependencies.

### Python Pipeline Tests

The six pipeline Lambda functions live in separate directories under `backend/pipeline/`. Each has its own `tests/` subdirectory. The shared utilities (database connection, Bedrock client) live in a Lambda layer at `backend/pipeline/layers/shared/python/`.

In the Lambda execution environment, that layer is mounted at a path Python automatically searches. In CI there's no layer mounting — so the path is added to `PYTHONPATH` instead:

```yaml
- name: Add shared layer to PYTHONPATH
  run: echo "PYTHONPATH=$PYTHONPATH:$GITHUB_WORKSPACE/backend/pipeline/layers/shared/python" >> $GITHUB_ENV
```

Writing to `$GITHUB_ENV` makes the variable available to all subsequent steps in the job — not just the current shell. This is the correct approach; `export` would only persist for the current `run` block.

The test suites then run one per handler:

```yaml
- name: pytest — chunk (no AWS deps)
  run: pytest backend/pipeline/chunk/tests/ -v
- name: pytest — extract
  run: pytest backend/pipeline/extract/tests/ -v
- name: pytest — embed
  run: pytest backend/pipeline/embed/tests/ -v
- name: pytest — metadata
  run: pytest backend/pipeline/metadata/tests/ -v
```

The chunker tests are noted as having no AWS dependencies — that handler is pure Python stdlib, so no mocking needed. The others mock `boto3` calls to Bedrock and S3.

### TypeScript Check, Lint, and Build

```yaml
- name: TypeScript check
  run: npx tsc --noEmit
- name: ESLint
  run: npx eslint src --max-warnings 0
- name: Build (smoke test)
  run: npm run build
  env:
    VITE_API_URL: https://placeholder.execute-api.us-west-2.amazonaws.com/dev
    VITE_USER_POOL_ID: us-west-2_PLACEHOLDER
    VITE_USER_POOL_CLIENT_ID: placeholder
    VITE_COGNITO_DOMAIN: placeholder.auth.us-west-2.amazoncognito.com
```

TypeScript checking and ESLint catch type errors and style issues. The `vite build` step is a smoke test: TypeScript's `tsc --noEmit` checks types but doesn't bundle. Vite's bundler can still fail on import cycles, missing environment variable references, or tree-shaking edge cases that `tsc` doesn't see. Placeholder values satisfy Vite's env var requirements without needing real infrastructure.

`--max-warnings 0` on ESLint means warnings are treated as errors — a warning that gets committed and ignored accumulates into noise. Zero tolerance keeps the lint output meaningful.

---

## Deploy: Two Sequential Jobs on Every Push to Main

`deploy.yml` triggers on pushes to `main`. It has two jobs: `deploy-backend` builds and deploys the infrastructure stacks, then `deploy-frontend` builds the React app with the real API URL and syncs it to S3.

### Job 1: SAM Build and Deploy

After authenticating with OIDC, the job builds the Lambda functions:

```yaml
- name: SAM build
  run: sam build --template-file infrastructure/template.yaml
```

The build step does the work of compiling or packaging each Lambda function according to the `Metadata.BuildMethod` in the template. The .NET functions use a `Makefile` target that runs `dotnet publish`. The Python pipeline functions are packaged with their dependencies. Because all functions target `x86_64` — matching the `ubuntu-latest` runner — no cross-compilation or Docker containerization is needed.

The deploy step reads from the build output, not the source template:

```yaml
- name: SAM deploy — main stack
  run: |
    sam deploy \
      --no-confirm-changeset \
      --no-fail-on-empty-changeset \
      --s3-bucket ${{ vars.SAM_BUCKET }} \
      --s3-prefix sift-main \
      --stack-name sift-dev \
      --parameter-overrides Env=dev SamBucket=${{ vars.SAM_BUCKET }} \
      --capabilities CAPABILITY_IAM CAPABILITY_NAMED_IAM
```

Notice there's no `--template-file` flag here. When `sam deploy` runs without specifying a template, it reads from `.aws-sam/build/template.yaml` — the processed template that references the compiled Lambda artifacts from the build step. Passing `--template-file infrastructure/template.yaml` would bypass the build and re-package raw source, which would break the .NET functions.

`--no-fail-on-empty-changeset` means the deploy step succeeds even if nothing changed. Without it, a push that only modifies the frontend would fail the backend deploy job with "No changes to deploy."

The frontend hosting stack is a plain CloudFormation template (no SAM transforms), deployed separately:

```yaml
- name: Deploy frontend hosting stack
  run: |
    aws cloudformation deploy \
      --template-file infrastructure/template-frontend.yaml \
      --stack-name sift-frontend-dev \
      --parameter-overrides Env=dev \
      --no-fail-on-empty-changeset
```

The job then captures the stack outputs — API URL, S3 bucket name, CloudFront distribution ID — and writes them to `$GITHUB_OUTPUT` so the next job can read them:

```yaml
- name: Capture stack outputs
  id: outputs
  run: |
    API_URL=$(aws cloudformation describe-stacks \
      --stack-name sift-dev \
      --query "Stacks[0].Outputs[?OutputKey=='ApiUrl'].OutputValue" \
      --output text)
    echo "api-url=$API_URL" >> $GITHUB_OUTPUT
    # ... bucket name and CF distribution ID
```

### Job 2: Frontend Build and Sync

`deploy-frontend` declares `needs: deploy-backend`, which both enforces ordering and makes the first job's outputs available as `needs.deploy-backend.outputs.*`.

```yaml
- name: Build
  run: npm run build
  env:
    VITE_API_URL:             ${{ needs.deploy-backend.outputs.api-url }}
    VITE_USER_POOL_ID:        ${{ vars.VITE_USER_POOL_ID }}
    VITE_USER_POOL_CLIENT_ID: ${{ vars.VITE_USER_POOL_CLIENT_ID }}
    VITE_COGNITO_DOMAIN:      ${{ vars.VITE_COGNITO_DOMAIN }}
```

`VITE_API_URL` is the only value that comes from the previous job's runtime output — it's only known after the SAM stack deploys. Everything else is static configuration stored as repository variables.

The S3 sync uses split cache headers:

```yaml
- name: Sync to S3
  run: |
    aws s3 sync dist/ s3://${{ needs.deploy-backend.outputs.frontend-bucket }} \
      --delete \
      --cache-control "public,max-age=31536000,immutable" \
      --exclude "index.html"
    aws s3 cp dist/index.html s3://$BUCKET/index.html \
      --cache-control "no-cache,no-store,must-revalidate"
```

This is the standard SPA cache strategy. Vite includes a content hash in every asset filename — `main-Dz9a8bK2.js`, `vendor-x4jKLmY8.css`. These filenames change when the content changes. The browser can safely cache them for a year (`max-age=31536000,immutable`); if the file changes, it gets a new URL.

`index.html` doesn't have a hash in its name — it's always `index.html`. It contains `<script src="./assets/main-Dz9a8bK2.js">`, pointing to the current hashed filenames. If `index.html` is cached, the browser never fetches the new asset filenames after a deploy. `no-cache,no-store,must-revalidate` forces the browser to revalidate `index.html` on every navigation — it fetches fresh, reads the new asset filenames, and the rest loads from cache.

Finally, the CloudFront edge caches are invalidated:

```yaml
- name: Invalidate CloudFront
  run: |
    aws cloudfront create-invalidation \
      --distribution-id ${{ needs.deploy-backend.outputs.cloudfront-id }} \
      --paths "/*"
```

Without this, CloudFront would serve the cached previous version of `index.html` from edge locations for up to 24 hours — undermining the `no-cache` header set on the origin.

---

## CloudFront + S3 Hosting

`template-frontend.yaml` configures CloudFront to serve a private S3 bucket using Origin Access Control:

```yaml
FrontendBucket:
  Type: AWS::S3::Bucket
  Properties:
    PublicAccessBlockConfiguration:
      BlockPublicAcls:       true
      IgnorePublicAcls:      true
      BlockPublicPolicy:     true
      RestrictPublicBuckets: true
```

The bucket has all public access blocked. The only way to read objects is through CloudFront.

Origin Access Control (OAC) replaces the older Origin Access Identity (OAI) pattern. OAC uses SigV4 request signing rather than a special IAM principal:

```yaml
OriginAccessControl:
  Type: AWS::CloudFront::OriginAccessControl
  Properties:
    OriginAccessControlConfig:
      OriginAccessControlOriginType: s3
      SigningBehavior:               always
      SigningProtocol:               sigv4
```

The bucket policy grants `s3:GetObject` to CloudFront, scoped to this specific distribution's ARN:

```yaml
Condition:
  StringEquals:
    AWS:SourceArn: !Sub arn:aws:cloudfront::${AWS::AccountId}:distribution/${CloudFrontDistribution}
```

The `AWS:SourceArn` condition means even if someone obtained CloudFront's service principal, they couldn't use it to access this bucket from a different distribution. The permission is tied to the specific CloudFront resource, not just the service.

The distribution handles the SPA routing requirement with custom error responses:

```yaml
CustomErrorResponses:
  - ErrorCode: 403
    ResponseCode: 200
    ResponsePagePath: /index.html
  - ErrorCode: 404
    ResponseCode: 200
    ResponsePagePath: /index.html
```

When a user bookmarks `app.example.com/documents` and navigates directly to it, S3 returns a 403 (no object at that key) or 404. Without this configuration, the user sees an XML error response. With it, CloudFront intercepts that error and serves `index.html` instead — React Router then handles the `/documents` path client-side. The `ErrorCachingMinTTL: 0` on each rule prevents CloudFront from caching the error responses themselves.

---

## The Result

The full pipeline, end to end:

1. Developer opens a pull request → CI runs three parallel jobs (45–90 seconds)
2. Merge to main → Deploy job builds and deploys all infrastructure stacks
3. Frontend build runs with the real API URL from stack outputs
4. `aws s3 sync` with correct cache headers, CloudFront invalidation

The only AWS credentials that exist are the temporary role session credentials inside the running job. There's no `AWS_ACCESS_KEY_ID` in repository secrets, no IAM user to audit, and no credential rotation to schedule. The IAM role trust policy limits which repos can assume it, and the role itself is scoped to exactly the permissions needed to deploy Sift — no more.

---

## The Complete Series

That's all six parts:

| Part | Topic |
|---|---|
| 1 | Architecture overview — service choices and cost breakdown |
| 2 | Multi-tenant auth — Cognito JWT, API Gateway validation, Postgres RLS |
| 3 | Step Functions pipeline — state machine, Map state, Express Workflows |
| 4 | RAG and vector search — pgvector, Titan Embed v2, citations |
| 5 | React frontend — Amplify auth, presigned upload, React Query polling |
| 6 | CI/CD — OIDC federation, SAM build/deploy, CloudFront cache strategy |

The live demo is at [d3dsn1f23yg4bo.cloudfront.net](https://d3dsn1f23yg4bo.cloudfront.net). The code is at [github.com/joshblair/sift](https://github.com/joshblair/sift).

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
