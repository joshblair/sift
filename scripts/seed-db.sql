-- Seed script for demo environment.
-- Run after migrations/001_initial_schema.sql and after deploying
-- the Cognito stack so you have real cognito_sub values to substitute.
--
-- Replace the two placeholder cognito_sub values below with real ones
-- from your Cognito User Pool before running.
--
-- Usage:
--   psql "host=<DB_HOST> dbname=sift user=siftadmin sslmode=require" -f scripts/seed-db.sql

BEGIN;

-- Tenants are already seeded in 001_initial_schema.sql:
--   acme  → aaaaaaaa-0000-0000-0000-000000000001
--   globex → aaaaaaaa-0000-0000-0000-000000000002

-- ── Demo users ────────────────────────────────────────────────────────────────
-- Replace cognito_sub with values from your Cognito User Pool
-- (Cognito console → Users → select user → copy "sub" attribute)

INSERT INTO users (id, tenant_id, cognito_sub, email, role) VALUES
  (
    'bbbbbbbb-0000-0000-0000-000000000001',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'REPLACE_WITH_ACME_ADMIN_COGNITO_SUB',
    'admin@acme-demo.com',
    'admin'
  ),
  (
    'bbbbbbbb-0000-0000-0000-000000000002',
    'aaaaaaaa-0000-0000-0000-000000000002',
    'REPLACE_WITH_GLOBEX_ADMIN_COGNITO_SUB',
    'admin@globex-demo.com',
    'admin'
  )
ON CONFLICT (cognito_sub) DO NOTHING;

-- ── Demo documents (pre-processed, status=ready) ───────────────────────────────
-- These give the UI something to display before you upload real files.
-- The s3_key values won't resolve to real objects until you upload matching files.

-- RLS requires app.current_tenant_id to be set for inserts.
-- We bypass RLS here by inserting as the superuser (siftadmin with BYPASSRLS).
-- In production you'd use a migration user with BYPASSRLS privilege.

SET LOCAL app.current_tenant_id = 'aaaaaaaa-0000-0000-0000-000000000001';

INSERT INTO documents
  (id, tenant_id, uploaded_by, filename, s3_key, file_type, status,
   summary, topics, page_count, chunk_count, processed_at)
VALUES
  (
    'cccccccc-0000-0000-0000-000000000001',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001',
    'Q3-2024-Financial-Report.pdf',
    'aaaaaaaa-0000-0000-0000-000000000001/cccccccc-0000-0000-0000-000000000001/Q3-2024-Financial-Report.pdf',
    'pdf',
    'ready',
    'Acme Corp Q3 2024 financial results showing 18% YoY revenue growth to $4.2M, driven by enterprise subscription expansion and a new strategic partnership with TechCorp. Operating margin improved to 22% from 17% in Q3 2023.',
    ARRAY['finance', 'Q3 2024', 'revenue growth', 'enterprise', 'operating margin'],
    12,
    47,
    NOW() - INTERVAL '2 days'
  ),
  (
    'cccccccc-0000-0000-0000-000000000002',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001',
    'Employee-Handbook-2024.pdf',
    'aaaaaaaa-0000-0000-0000-000000000001/cccccccc-0000-0000-0000-000000000002/Employee-Handbook-2024.pdf',
    'pdf',
    'ready',
    'Acme Corp employee handbook covering company policies, benefits, remote work guidelines, code of conduct, performance review process, and career development programs for 2024.',
    ARRAY['HR', 'company policy', 'benefits', 'remote work', 'performance reviews'],
    34,
    128,
    NOW() - INTERVAL '5 days'
  ),
  (
    'cccccccc-0000-0000-0000-000000000003',
    'aaaaaaaa-0000-0000-0000-000000000001',
    'bbbbbbbb-0000-0000-0000-000000000001',
    'Product-Roadmap-H1-2025.docx',
    'aaaaaaaa-0000-0000-0000-000000000001/cccccccc-0000-0000-0000-000000000003/Product-Roadmap-H1-2025.docx',
    'docx',
    'ready',
    'H1 2025 product roadmap detailing three major feature launches: AI-powered analytics dashboard (Q1), mobile app v2.0 with offline support (Q1), and multi-region data residency compliance (Q2). Includes resource allocation and success metrics.',
    ARRAY['product roadmap', 'AI analytics', 'mobile app', 'compliance', 'H1 2025'],
    8,
    31,
    NOW() - INTERVAL '1 day'
  )
ON CONFLICT DO NOTHING;

SET LOCAL app.current_tenant_id = 'aaaaaaaa-0000-0000-0000-000000000002';

INSERT INTO documents
  (id, tenant_id, uploaded_by, filename, s3_key, file_type, status,
   summary, topics, page_count, chunk_count, processed_at)
VALUES
  (
    'cccccccc-0000-0000-0000-000000000004',
    'aaaaaaaa-0000-0000-0000-000000000002',
    'bbbbbbbb-0000-0000-0000-000000000002',
    'Globex-Safety-Manual.pdf',
    'aaaaaaaa-0000-0000-0000-000000000002/cccccccc-0000-0000-0000-000000000004/Globex-Safety-Manual.pdf',
    'pdf',
    'ready',
    'Globex Inc safety manual outlining plant safety protocols, emergency procedures, PPE requirements, incident reporting, and annual safety certification requirements for all facility personnel.',
    ARRAY['safety', 'compliance', 'emergency procedures', 'PPE', 'plant operations'],
    22,
    89,
    NOW() - INTERVAL '3 days'
  )
ON CONFLICT DO NOTHING;

COMMIT;

-- Verify
SELECT t.name AS tenant, d.filename, d.status, d.chunk_count
FROM documents d
JOIN tenants t ON t.id = d.tenant_id
ORDER BY t.name, d.created_at;
