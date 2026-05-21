---
title: "Multi-Tenant Auth with Cognito and PostgreSQL Row-Level Security (Part 2)"
published: true
description: "How a single Postgres session variable — app.current_tenant_id — eliminates an entire class of data-leak bugs at the database level."
tags: aws, security, postgresql, dotnet
series: "Building Sift: A Multi-Tenant AI Platform on AWS"
cover_image: https://raw.githubusercontent.com/joshblair/sift/main/docs/diagrams/sift-diagram-multitenancy.png
---

# Multi-Tenant Auth with Cognito and PostgreSQL Row-Level Security (Part 2)

*How a single Postgres session variable — `app.current_tenant_id` — eliminates an entire class of data-leak bugs at the database level.*

---

The hardest bug to find in a multi-tenant SaaS app is the one that silently returns the wrong data. It doesn't throw an exception. It doesn't return a 403. It just quietly hands tenant A's documents to tenant B, and you don't find out until a customer does.

Most demo apps guard against this with `WHERE tenant_id = @tenantId` clauses scattered through their query code. That works until one gets missed — a new endpoint, a refactor that drops the filter, a copy-paste that forgets to update the variable name. One missed clause is a data leak.

Sift uses a different approach: the database enforces tenant isolation automatically, regardless of what the application code does. This post covers how the full chain works — from Cognito JWT to Postgres policy — and why each piece is necessary.

---

## The Full Trust Chain

Here's the sequence for every authenticated API request:

```
Browser → Cognito (login)
       ← JWT signed with tenantId claim

Browser → API Gateway (request + JWT in Authorization header)
        → API Gateway validates JWT signature, rejects if invalid
        → Lambda receives validated claims in request context

Lambda  → extracts tenantId from request.RequestContext.Authorizer.Jwt.Claims
        → opens DB connection
        → calls set_config('app.current_tenant_id', tenantId, false)
        → runs query — Postgres RLS policy auto-filters every row
```

Each step is independently enforced. A user can't forge the `tenantId` in the JWT because they don't have Cognito's signing key. A request can't skip the JWT check because API Gateway rejects it before Lambda runs. A query can't bypass the RLS filter because the application user doesn't have `BYPASSRLS`.

Let's walk through each layer.

---

## Layer 1: Cognito — Injecting tenantId into the JWT

Cognito User Pools support custom attributes on user objects. In `template-cognito.yaml`, the User Pool schema includes a `custom:tenantId` attribute:

```yaml
UserPool:
  Type: AWS::Cognito::UserPool
  Properties:
    Schema:
      - Name: tenantId
        AttributeDataType: String
        Mutable: true
    LambdaConfig:
      PreTokenGeneration: !GetAtt PreTokenLambda.Arn
```

The `custom:tenantId` attribute is stored on the Cognito user record — set at invite/signup time by an admin or provisioning flow. But custom attributes don't appear in the JWT by default. That's where the Pre-Token Generation Lambda comes in.

### The Pre-Token Generation Lambda

Every time Cognito issues a token, it calls this Lambda before signing it. The Lambda reads the user's `custom:tenantId` attribute and injects it as a top-level claim:

```python
DEFAULT_TENANT = 'aaaaaaaa-0000-0000-0000-000000000001'  # acme demo tenant

def handler(event, context):
    tenant_id = event['request']['userAttributes'].get('custom:tenantId') or DEFAULT_TENANT
    event['response']['claimsOverrideDetails'] = {
        'claimsToAddOrOverride': {'tenantId': tenant_id}
    }
    return event
```

After this Lambda runs, the resulting JWT contains `"tenantId": "<uuid>"` as a standard claim alongside `sub`, `email`, and the rest. Cognito then signs the token with its RSA private key.

The security guarantee here is important: the `tenantId` in the JWT is now as trustworthy as the user's identity itself. The browser receives a signed token and passes it on requests — it cannot alter the `tenantId` without invalidating the signature.

---

## Layer 2: API Gateway — JWT Validation for Free

API Gateway HTTP APIs support a native JWT authorizer. No custom Lambda authorizer needed — API Gateway handles validation itself before the request reaches the Lambda function:

```yaml
HttpApi:
  Type: AWS::Serverless::HttpApi
  Properties:
    Auth:
      DefaultAuthorizer: CognitoJwtAuthorizer
      Authorizers:
        CognitoJwtAuthorizer:
          IdentitySource: $request.header.Authorization
          JwtConfiguration:
            issuer: !Sub
              - https://cognito-idp.${AWS::Region}.amazonaws.com/${UserPoolId}
              - UserPoolId: !ImportValue
                  Fn::Sub: sift-${Env}-UserPoolId
            audience:
              - !ImportValue
                  Fn::Sub: sift-${Env}-UserPoolClientId
```

This configuration tells API Gateway to:
1. Extract the Bearer token from the `Authorization` header
2. Verify the signature against Cognito's public keys (fetched from the issuer URL's JWKS endpoint)
3. Verify the `aud` claim matches the expected client ID
4. Reject the request with a 401 if any check fails

If the JWT is invalid, expired, or has the wrong audience, Lambda never runs. The `tenantId` claim that reaches Lambda has already been cryptographically validated.

---

## Layer 3: Lambda — Extracting the Claim

Once API Gateway passes the request through, the validated JWT claims are available in the Lambda event's request context. Extracting `tenantId` is a single line:

```csharp
private static Guid GetTenantId(APIGatewayHttpApiV2ProxyRequest request)
{
    var claim = request.RequestContext.Authorizer.Jwt.Claims["tenantId"];
    return Guid.Parse(claim);
}
```

This runs at the top of every Lambda handler before any service code executes:

```csharp
public async Task<APIGatewayHttpApiV2ProxyResponse> FunctionHandler(
    APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
{
    var tenantId   = GetTenantId(request);
    var cognitoSub = request.RequestContext.Authorizer.Jwt.Claims["sub"];
    // ... route to service
}
```

The `tenantId` extracted here gets passed down to the service layer, which uses it to set the database session variable before running any query.

---

## Layer 4: PostgreSQL Row-Level Security

This is where the real defense happens.

### The Problem with WHERE Clauses

The naive approach is filtering every query manually:

```sql
SELECT * FROM documents WHERE tenant_id = @tenantId AND id = @docId
```

This works until it doesn't. Add a new endpoint in a hurry, forget the `WHERE tenant_id` clause, and you've got a data leak — with no runtime error to alert you. The query succeeds; it just returns data it shouldn't.

Row-Level Security moves the filter into the database engine. The policy is applied automatically to every query on that table, whether the application code includes a filter or not.

### Setting Up RLS

The schema enables RLS on every tenant-scoped table and defines a single policy:

```sql
ALTER TABLE documents       ENABLE ROW LEVEL SECURITY;
ALTER TABLE document_chunks ENABLE ROW LEVEL SECURITY;
ALTER TABLE users           ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON documents
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);

CREATE POLICY tenant_isolation ON document_chunks
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);

CREATE POLICY tenant_isolation ON users
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
```

`current_setting('app.current_tenant_id')` reads a Postgres session-level variable that the application sets before running any query. The `::UUID` cast means a missing or malformed value throws an error rather than silently returning empty results.

### Setting the Session Variable

In `TenantContext.cs`, every service method that opens a database connection immediately calls:

```csharp
public static async Task SetAsync(NpgsqlConnection connection, Guid tenantId)
{
    await using var cmd = connection.CreateCommand();
    // is_local=false → session scope. Safe because Lambda connections are
    // never pooled across requests (Pooling=false in DbConnectionFactory).
    cmd.CommandText = "SELECT set_config('app.current_tenant_id', $1, false)";
    cmd.Parameters.AddWithValue(tenantId.ToString());
    await cmd.ExecuteNonQueryAsync();
}
```

The `false` parameter in `set_config` means session scope rather than transaction scope. This is intentional: the Lambda's database connections are not pooled across requests (`Pooling=false` in the connection factory), so a session-scoped variable is both safe and slightly more efficient than resetting it per-transaction.

Every service method calls this before touching data:

```csharp
public async Task<IEnumerable<Document>> ListAsync(Guid tenantId)
{
    await using var conn = await _db.CreateAsync();
    await TenantContext.SetAsync(conn, tenantId);
    // From this point on, every query automatically filters to tenantId's rows
    // ...
}
```

### The BYPASSRLS Gotcha

There's one critical detail that's easy to miss: **Postgres superusers bypass RLS by default.** If your application database user has superuser privileges, `ENABLE ROW LEVEL SECURITY` does nothing — the policies are silently skipped.

The application user in Sift is the standard credentials from Secrets Manager — a normal role with `SELECT`, `INSERT`, `UPDATE`, `DELETE` on the application tables, and nothing more. No `SUPERUSER`. No `BYPASSRLS`. This isn't incidental; it's a deliberate requirement for RLS to actually work.

If you're debugging why your RLS policies seem to have no effect, check the role: `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = 'your_app_user';`

---

## Layer 5: S3 Key Prefix

RLS handles the database. S3 needs a complementary approach.

Every document is stored at a key prefixed with the tenant's ID:

```
{tenantId}/{documentId}/filename.pdf
```

When a user requests a presigned upload URL, the Lambda constructs the key using the `tenantId` from their validated JWT. The browser then uploads directly to S3 using that presigned URL — it has no say in what key gets used.

The result: even if a presigned URL leaked somehow, it would only allow access to that specific object under the tenant's prefix. Tenant A's presigned URL cannot be used to list or access tenant B's documents — the key prefixes are different UUIDs.

S3 doesn't have "row-level security" — but a properly namespaced key structure combined with IAM policies that don't allow `s3:ListBucket` on the uploads bucket achieves the same practical outcome.

---

## Why Three Layers?

Each layer protects a different attack surface:

| Layer | Protects Against |
|---|---|
| Cognito JWT | User forging a different tenantId on the client |
| API Gateway validation | Bypassing auth entirely (no token, expired token) |
| Postgres RLS | Application bugs — a missing WHERE clause, a new query that forgets the filter |
| S3 key prefix | Cross-tenant object access, presigned URL misuse |

The layers are independent. A bug that breaks one doesn't compromise the others. If somehow a `tenantId` was wrong in application code, RLS would still return empty results — not the wrong tenant's data. Defense in depth means the blast radius of any single failure is contained.

---

## Seeing It in Action

The live demo has two tenants: **Acme Corp** and **Globex Inc**, both seeded in the initial migration. You can log in as an Acme Corp user, upload a document, then log in as a Globex user — the document list is empty. Both tenants run against the same Aurora cluster, the same Lambda functions, the same S3 bucket. The query is identical. Postgres silently returns the right rows for each.

---

## What's Next

**[Part 3](https://dev.to/josh_blair/serverless-document-pipelines-with-aws-step-functions-part-3-2111)** covers the Step Functions Express pipeline — how the six-stage document processing workflow is orchestrated, why the Map state handles embedding generation, and how the state machine's declarative retry config replaces dozens of lines of error-handling code.

The code for everything in this post:
- `infrastructure/template-cognito.yaml` — Pre-Token Lambda and User Pool definition
- `migrations/001_initial_schema.sql` — full `CREATE POLICY` statements
- `backend/src/Sift.Api/Infrastructure/TenantContext.cs` — the `set_config` call
- `backend/src/Sift.Api/Infrastructure/DbConnectionFactory.cs` — why `Pooling=false` matters
- `backend/src/Sift.Api/Functions/DocumentsFunction.cs` — JWT claim extraction

---

*Part of the Sift series: building a production-ready multi-tenant RAG platform on AWS.*
