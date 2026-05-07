#!/usr/bin/env python3
"""Run DB migrations against Aurora via the RDS Data API (no psql or VPC needed)."""
import sys
import boto3

REGION      = "us-west-2"
CLUSTER_ARN = "arn:aws:rds:us-west-2:709085484102:cluster:sift-database-dev-dbcluster-rretvpgesdca"
SECRET_ARN  = "arn:aws:secretsmanager:us-west-2:709085484102:secret:sift-dev-db-credentials-wqkVbM"
DATABASE    = "sift"

# Each item is a single SQL statement (the Data API executes one at a time).
STATEMENTS = [
    "CREATE EXTENSION IF NOT EXISTS vector",
    'CREATE EXTENSION IF NOT EXISTS "uuid-ossp"',

    # Tenants
    """CREATE TABLE IF NOT EXISTS tenants (
  id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name       TEXT NOT NULL,
  slug       TEXT UNIQUE NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
)""",

    # Users
    """CREATE TABLE IF NOT EXISTS users (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   UUID NOT NULL REFERENCES tenants(id),
  cognito_sub TEXT UNIQUE NOT NULL,
  email       TEXT NOT NULL,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('admin', 'member')),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
)""",
    "CREATE INDEX IF NOT EXISTS idx_users_tenant ON users(tenant_id)",

    # Documents
    """CREATE TABLE IF NOT EXISTS documents (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id     UUID NOT NULL REFERENCES tenants(id),
  uploaded_by   UUID NOT NULL REFERENCES users(id),
  filename      TEXT NOT NULL,
  s3_key        TEXT NOT NULL,
  file_type     TEXT NOT NULL CHECK (file_type IN ('pdf', 'csv', 'docx', 'txt')),
  status        TEXT NOT NULL DEFAULT 'pending'
                  CHECK (status IN ('pending', 'processing', 'ready', 'failed')),
  summary       TEXT,
  topics        TEXT[],
  page_count    INT,
  chunk_count   INT,
  error_message TEXT,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  processed_at  TIMESTAMPTZ
)""",
    "CREATE INDEX IF NOT EXISTS idx_documents_tenant ON documents(tenant_id)",
    "CREATE INDEX IF NOT EXISTS idx_documents_status ON documents(tenant_id, status)",

    # Document chunks
    """CREATE TABLE IF NOT EXISTS document_chunks (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id   UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  tenant_id     UUID NOT NULL REFERENCES tenants(id),
  chunk_index   INT NOT NULL,
  content       TEXT NOT NULL,
  embedding     vector(1536),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
)""",
    """CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON document_chunks
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100)""",
    "CREATE INDEX IF NOT EXISTS idx_chunks_document ON document_chunks(document_id)",
    "CREATE INDEX IF NOT EXISTS idx_chunks_tenant   ON document_chunks(tenant_id)",

    # RLS
    "ALTER TABLE users           ENABLE ROW LEVEL SECURITY",
    "ALTER TABLE documents       ENABLE ROW LEVEL SECURITY",
    "ALTER TABLE document_chunks ENABLE ROW LEVEL SECURITY",

    """DO $$ BEGIN
  CREATE POLICY tenant_isolation ON users
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$""",

    """DO $$ BEGIN
  CREATE POLICY tenant_isolation ON documents
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$""",

    """DO $$ BEGIN
  CREATE POLICY tenant_isolation ON document_chunks
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$""",

    # Seed tenants
    """INSERT INTO tenants (id, name, slug) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000001', 'Acme Corp',  'acme'),
  ('aaaaaaaa-0000-0000-0000-000000000002', 'Globex Inc', 'globex')
ON CONFLICT DO NOTHING""",
]


def run():
    client = boto3.client("rds-data", region_name=REGION)
    total = len(STATEMENTS)

    for i, sql in enumerate(STATEMENTS, 1):
        label = sql.split("\n")[0][:60]
        print(f"[{i:02d}/{total}] {label}...", end=" ", flush=True)
        try:
            client.execute_statement(
                resourceArn=CLUSTER_ARN,
                secretArn=SECRET_ARN,
                database=DATABASE,
                sql=sql,
            )
            print("ok")
        except Exception as e:
            print(f"FAILED\n  {e}")
            sys.exit(1)

    print("\nMigration complete.")


if __name__ == "__main__":
    run()
