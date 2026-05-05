# Article 6: GitOps on AWS — Zero-Secret CI/CD with GitHub Actions + SAM

**Tagline:** No AWS_ACCESS_KEY_ID in your GitHub secrets. Ever. Here's how OIDC trust works and why it's strictly better.

---

## Outline

### Hook
The most common GitHub Actions setup I see in portfolios stores `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` as repository secrets. Those are long-lived credentials — one breach of your GitHub account and an attacker has your AWS keys indefinitely. OIDC federation lets GitHub Actions assume an IAM role with a short-lived token that expires when the job ends. No keys to rotate, no keys to leak.

### Part 1: How GitHub Actions OIDC works

**The trust relationship**
GitHub acts as an OIDC identity provider. When a workflow job runs, GitHub generates a signed JWT asserting:
- `sub`: `repo:joshblair/sift:ref:refs/heads/main`
- `aud`: `sts.amazonaws.com`

AWS STS accepts this JWT and issues a short-lived role session (default: 1 hour).

**Setting up the trust**
Walk through `scripts/bootstrap.sh`:
1. Create the OIDC provider in IAM (once per AWS account)
2. Create the IAM role with a trust policy scoped to your specific repo

Show the trust policy JSON:
```json
{
  "Condition": {
    "StringLike": {
      "token.actions.githubusercontent.com:sub": "repo:joshblair/sift:*"
    }
  }
}
```

**In the workflow**
```yaml
- uses: aws-actions/configure-aws-credentials@v4
  with:
    role-to-assume: ${{ vars.DEPLOY_ROLE_ARN }}
    aws-region: ${{ vars.AWS_REGION }}
```
The `id-token: write` permission on the job is what allows GitHub to request the OIDC token.

### Part 2: CI workflow

Walk through `.github/workflows/ci.yml`:

**Three parallel jobs:**
- `.NET build & test` — `dotnet restore` → `dotnet build` → `dotnet test`
- `Python pytest` — sets `PYTHONPATH` to include the shared Lambda layer, runs all four test suites
- `TypeScript & lint` — `tsc --noEmit`, ESLint, full `vite build` with placeholder env vars

**The PYTHONPATH trick**
The shared Lambda layer lives at `backend/pipeline/layers/shared/python/`. In CI we just add it to `PYTHONPATH` — no Docker, no layer extraction:
```yaml
echo "PYTHONPATH=$PYTHONPATH:$GITHUB_WORKSPACE/backend/pipeline/layers/shared/python" >> $GITHUB_ENV
```

**Why run `vite build` in CI?**
TypeScript type checking passes but Vite's bundler can still fail on missing environment variables, import cycles, or tree-shaking issues. The build step with placeholder env vars catches these early.

### Part 3: Deploy workflow

Walk through `.github/workflows/deploy.yml`:

**Job 1: deploy-backend**
1. Configure OIDC credentials
2. `sam build --use-container` — builds .NET and Python Lambdas inside Amazon Linux containers. This is essential for `psycopg2-binary` which has native extensions that must compile for the Lambda execution environment.
3. `sam deploy` — idempotent, `--no-fail-on-empty-changeset` so reruns don't fail when nothing changed
4. Deploy `template-frontend.yaml` separately (CloudFormation, not SAM — no Lambda functions)
5. Capture stack outputs as job outputs for the next job

**Job 2: deploy-frontend**
- `needs: deploy-backend` — waits for the API URL to be known
- `npm run build` with `VITE_API_URL` injected from previous job's outputs
- `aws s3 sync` with split cache headers:
  - Hashed assets (JS/CSS): `max-age=31536000,immutable` — browser caches forever, hash changes on each deploy
  - `index.html`: `no-cache` — browser always revalidates, gets the new hashes
- CloudFront invalidation: `--paths "/*"` — clears all edge caches

### Part 4: CloudFront + S3 hosting

Walk through `infrastructure/template-frontend.yaml`:
- Private S3 bucket (no public access)
- Origin Access Control (OAC) — replaces the legacy OAI pattern, uses SigV4 signing
- Bucket policy that only allows CloudFront to read (via `AWS:SourceArn` condition)
- Custom error responses: 403/404 → 200 `/index.html` — required for React Router client-side routing

### Part 5: Environment promotion

The parameter files in `infrastructure/parameters/` enable promoting to production with a single variable change:
- Dev: `EnableNatGateway=false`, `PubliclyAccessible=true` (lower cost)
- Prod: `EnableNatGateway=true`, `PubliclyAccessible=false` (Lambda in VPC, no public DB endpoint)

Add a `deploy-prod` job that triggers on release tags rather than branch pushes.

### Summary: What this setup demonstrates
- No stored AWS credentials anywhere (OIDC)
- Infrastructure as code (SAM + CloudFormation)
- Immutable deployments (S3 + CloudFront invalidation)
- Separation of CI (tests) and CD (deploy)
- Multi-environment promotion via parameter files

---

## Key code references
- `scripts/bootstrap.sh` — OIDC provider + IAM role creation
- `.github/workflows/ci.yml` — PR checks
- `.github/workflows/deploy.yml` — full deploy pipeline
- `infrastructure/template-frontend.yaml` — CloudFront + S3 OAC
- `infrastructure/parameters/dev.json` and `prod.json`
