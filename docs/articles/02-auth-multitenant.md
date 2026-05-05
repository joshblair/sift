# Article 2: Multi-Tenant Auth with Cognito + Row-Level Security in PostgreSQL

**Tagline:** How a single line of SQL — `SET LOCAL app.current_tenant_id = $1` — eliminates an entire class of data-leak bugs.

---

## Outline

### Hook
The hardest bug to find in a multi-tenant app is the one that leaks tenant A's data to tenant B. It doesn't crash, it doesn't error — it just quietly returns the wrong rows. Postgres RLS with a session variable makes this class of bug impossible by default.

### Part 1: Cognito custom claim flow

**Step 1: Custom attribute at signup**
Cognito User Pool schema includes `custom:tenantId`. Show how this is set at user creation time (or by an admin trigger for invite flows).

**Step 2: Pre-Token Generation Lambda**
Walk through `template-cognito.yaml` → `PreTokenLambda` inline Python:
```python
tenant_id = event['request']['userAttributes'].get('custom:tenantId', '')
event['response']['claimsOverrideDetails'] = {
    'claimsToAddOrOverride': {'tenantId': tenant_id}
}
```
Explain why this matters: the JWT's `tenantId` claim is now trusted because Cognito signed it — the client can't tamper with it.

**Step 3: API Gateway JWT Authorizer**
API Gateway validates the Cognito JWT and passes claims to the Lambda via `$context.authorizer.claims`. No custom authorizer Lambda needed. Show the SAM `Auth` block in `template.yaml`.

**Step 4: Lambda extracts claim**
```csharp
string tenantId = request.RequestContext.Authorizer.Jwt.Claims["tenantId"];
```

### Part 2: Postgres Row-Level Security

**The problem with WHERE clauses**
Show a naive approach (WHERE tenant_id = @tenantId in every query), explain why it fails: one missed WHERE clause in a new query silently leaks data. RLS makes the WHERE clause automatic.

**Schema setup**
Walk through the migration:
```sql
ALTER TABLE documents ENABLE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON documents
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
```
Key insight: `current_setting` reads a Postgres session variable, not a column.

**The C# middleware**
Walk through `TenantContext.cs`:
```csharp
cmd.CommandText = "SELECT set_config('app.current_tenant_id', $1, true)";
```
Explain `is_local=true` — scopes the value to the current transaction, not the connection (important for connection pooling).

**The `BYPASSRLS` gotcha**
The DB superuser bypasses RLS. The application user must NOT have superuser or BYPASSRLS. Show how to create a restricted app role in the seed script.

**Demo: RLS working**
Sign in as Tenant A → upload a document → sign in as Tenant B → document list is empty. The query is identical; Postgres silently filters.

### Part 3: S3 isolation
Complement to RLS: S3 keys are prefixed `{tenantId}/` so even pre-signed URL leaks can't cross tenant boundaries.

---

## Key code references
- `infrastructure/template-cognito.yaml` — full Cognito setup
- `migrations/001_initial_schema.sql` — `CREATE POLICY` statements
- `backend/src/Sift.Api/Infrastructure/TenantContext.cs` — `set_config` call
- `backend/src/Sift.Api/Functions/DocumentsFunction.cs` — JWT claim extraction
