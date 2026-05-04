"""
MarkFailed — error catch target for all pipeline states

Input:  { documentId, tenantId, Error, Cause }
Output: { documentId, tenantId, status: "failed" }
"""
from shared.db import get_connection


def handler(event: dict, context) -> dict:
    document_id   = event.get("documentId", "")
    tenant_id     = event.get("tenantId", "")
    error_message = event.get("Cause", event.get("Error", "Unknown error"))[:1000]

    if document_id and tenant_id:
        conn = get_connection(tenant_id)
        try:
            with conn.cursor() as cur:
                cur.execute(
                    """
                    UPDATE documents
                    SET status        = 'failed',
                        error_message = %s,
                        processed_at  = NOW()
                    WHERE id = %s
                    """,
                    (error_message, document_id),
                )
            conn.commit()
        finally:
            conn.close()

    return {"documentId": document_id, "tenantId": tenant_id, "status": "failed"}
