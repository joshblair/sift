import json
import os
import boto3
import psycopg2

SECRET_ARN = os.environ["SECRET_ARN"]
DB_HOST    = os.environ["DB_HOST"]
DB_NAME    = os.environ.get("DB_NAME", "sift")

MIGRATION_SQL = """
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE IF NOT EXISTS tenants (
  id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name       TEXT NOT NULL,
  slug       TEXT UNIQUE NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS users (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   UUID NOT NULL REFERENCES tenants(id),
  cognito_sub TEXT UNIQUE NOT NULL,
  email       TEXT NOT NULL,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('admin', 'member')),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_users_tenant ON users(tenant_id);

CREATE TABLE IF NOT EXISTS documents (
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
);

CREATE INDEX IF NOT EXISTS idx_documents_tenant ON documents(tenant_id);
CREATE INDEX IF NOT EXISTS idx_documents_status  ON documents(tenant_id, status);

CREATE TABLE IF NOT EXISTS document_chunks (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id   UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  tenant_id     UUID NOT NULL REFERENCES tenants(id),
  chunk_index   INT NOT NULL,
  content       TEXT NOT NULL,
  embedding     vector(1536),
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_chunks_embedding ON document_chunks
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE INDEX IF NOT EXISTS idx_chunks_document ON document_chunks(document_id);
CREATE INDEX IF NOT EXISTS idx_chunks_tenant   ON document_chunks(tenant_id);

ALTER TABLE users           ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents       ENABLE ROW LEVEL SECURITY;
ALTER TABLE document_chunks ENABLE ROW LEVEL SECURITY;

DO $$ BEGIN
  CREATE POLICY tenant_isolation ON users
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE POLICY tenant_isolation ON documents
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

DO $$ BEGIN
  CREATE POLICY tenant_isolation ON document_chunks
    USING (tenant_id = current_setting('app.current_tenant_id')::UUID);
EXCEPTION WHEN duplicate_object THEN NULL;
END $$;

INSERT INTO tenants (id, name, slug) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000001', 'Acme Corp',  'acme'),
  ('aaaaaaaa-0000-0000-0000-000000000002', 'Globex Inc', 'globex')
ON CONFLICT DO NOTHING;
"""


def handler(event, context):
    sm = boto3.client("secretsmanager")
    secret = json.loads(
        sm.get_secret_value(SecretId=SECRET_ARN)["SecretString"]
    )

    conn = psycopg2.connect(
        host=DB_HOST,
        port=5432,
        dbname=DB_NAME,
        user=secret["username"],
        password=secret["password"],
        connect_timeout=15,
        sslmode="require",
    )
    conn.autocommit = True

    with conn.cursor() as cur:
        cur.execute(MIGRATION_SQL)

    conn.close()
    print("Migration complete")
    return {"status": "ok", "message": "Migration applied successfully"}
