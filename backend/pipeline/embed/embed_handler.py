"""
Step 3 — EmbedChunk  (runs inside a Step Functions Map state)

Input per iteration:
  { documentId, tenantId, index, content }

Output per iteration:
  { documentId, tenantId, index, status: "ok" }

Generates a Bedrock Titan Embed v2 embedding for a single chunk and
inserts it into the document_chunks table. The Map state in the state
machine fans out one invocation per chunk with MaxConcurrency=5.
"""

from shared.bedrock import embed
from shared.db import get_connection


def handler(event: dict, context) -> dict:
    document_id = event["documentId"]
    tenant_id   = event["tenantId"]
    index       = event["index"]
    content     = event["content"]

    embedding = embed(content)
    _insert_chunk(document_id, tenant_id, index, content, embedding)

    return {"documentId": document_id, "tenantId": tenant_id, "index": index, "status": "ok"}


def _insert_chunk(
    document_id: str,
    tenant_id: str,
    chunk_index: int,
    content: str,
    embedding: list[float],
) -> None:
    # Postgres vector literal: '[0.1, 0.2, ...]'
    vector_literal = "[" + ",".join(str(v) for v in embedding) + "]"

    conn = get_connection(tenant_id)
    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                INSERT INTO document_chunks
                    (document_id, tenant_id, chunk_index, content, embedding)
                VALUES (%s, %s, %s, %s, %s::vector)
                ON CONFLICT DO NOTHING
                """,
                (document_id, tenant_id, chunk_index, content, vector_literal),
            )
        conn.commit()
    finally:
        conn.close()
