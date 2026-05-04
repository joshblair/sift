-- Run once against the Aurora cluster after stack deploy.
-- Enables pgvector and creates all tables with row-level security.

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- ── Tenants ───────────────────────────────────────────────────────────────────

CREATE TABLE tenants (
  id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name       TEXT NOT NULL,
  slug       TEXT UNIQUE NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- ── Users ─────────────────────────────────────────────────────────────────────

CREATE TABLE users (
  id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id   UUID NOT NULL REFERENCES tenants(id),
  cognito_sub TEXT UNIQUE NOT NULL,
  email       TEXT NOT NULL,
  role        TEXT NOT NULL DEFAULT 'member' CHECK (role IN ('admin', 'member')),
  created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_users_tenant ON users(tenant_id);

-- ── Documents ─────────────────────────────────────────────────────────────────

CREATE TABLE documents (
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

CREATE INDEX idx_documents_tenant ON documents(tenant_id);
CREATE INDEX idx_documents_status  ON documents(tenant_id, status);

-- ── Document Chunks ───────────────────────────────────────────────────────────

CREATE TABLE document_chunks (
  id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  document_id   UUID NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  tenant_id     UUID NOT NULL REFERENCES tenants(id),
  chunk_index   INT NOT NULL,
  content       TEXT NOT NULL,
  embedding     vector(1536),   -- Bedrock Titan Embed v2 dimensions
  created_at    TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- IVFFlat index: rebuild after loading embeddings with ANALYZE
CREATE INDEX idx_chunks_embedding ON document_chunks
  USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE INDEX idx_chunks_document ON document_chunks(document_id);
CREATE INDEX idx_chunks_tenant   ON document_chunks(tenant_id);

-- ── Row-Level Security ────────────────────────────────────────────────────────
-- The C# middleware sets: SET LOCAL app.current_tenant_id = '<uuid>' on each connection.

ALTER TABLE users           ENABLE ROW LEVEL SECURITY;
ALTER TABLE documents       ENABLE ROW LEVEL SECURITY;
ALTER TABLE document_chunks ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON users
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);

CREATE POLICY tenant_isolation ON documents
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);

CREATE POLICY tenant_isolation ON document_chunks
  USING (tenant_id = current_setting('app.current_tenant_id')::UUID);

-- ── Seed: two demo tenants ────────────────────────────────────────────────────
-- Actual users are created by Cognito signup; these are just the tenant rows.

INSERT INTO tenants (id, name, slug) VALUES
  ('aaaaaaaa-0000-0000-0000-000000000001', 'Acme Corp',    'acme'),
  ('aaaaaaaa-0000-0000-0000-000000000002', 'Globex Inc',   'globex');
