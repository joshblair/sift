"""
Step 5 — MarkReady

Input:  { documentId, tenantId, pageCount, chunkCount, summary, topics }
Output: { documentId, tenantId, status: "ready" }

Final step: sets document status to 'ready' and stamps processed_at.
"""
from shared.db import get_connection


def handler(event: dict, context) -> dict:
    document_id = event["documentId"]
    tenant_id   = event["tenantId"]

    conn = get_connection(tenant_id)
    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE documents
                SET status       = 'ready',
                    processed_at = NOW()
                WHERE id = %s
                """,
                (document_id,),
            )
        conn.commit()
    finally:
        conn.close()

    return {"documentId": document_id, "tenantId": tenant_id, "status": "ready"}
