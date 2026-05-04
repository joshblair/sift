"""
Step 4 — ExtractMetadata

Input:  { ..., documentId, tenantId, text, pageCount, chunks }
Output: { documentId, tenantId, pageCount, chunkCount, summary, topics }

Calls Bedrock Claude Haiku to generate a one-paragraph summary and
a list of key topics, then persists them to the documents table.
"""
import json

from shared.bedrock import complete
from shared.db import get_connection


def handler(event: dict, context) -> dict:
    document_id = event["documentId"]
    tenant_id   = event["tenantId"]
    text        = event["text"]
    page_count  = event.get("pageCount", 1)
    chunk_count = len(event.get("chunks", []))

    # Truncate text sent to Haiku — keep first ~6000 chars to stay within token limits
    excerpt = text[:6000].strip()

    summary, topics = _extract(excerpt)
    _persist(document_id, tenant_id, summary, topics, page_count, chunk_count)

    return {
        "documentId": document_id,
        "tenantId":   tenant_id,
        "pageCount":  page_count,
        "chunkCount": chunk_count,
        "summary":    summary,
        "topics":     topics,
    }


def _extract(text: str) -> tuple[str, list[str]]:
    system = (
        "You are a document analyst. Given document text, return a JSON object "
        "with exactly two keys: "
        '"summary" (one paragraph, max 200 words) and '
        '"topics" (array of 3-7 short topic strings). '
        "Return only the JSON object, no other text."
    )
    response = complete(system, text, max_tokens=512)

    # Strip markdown code fences if present
    cleaned = response.strip().removeprefix("```json").removeprefix("```").removesuffix("```").strip()
    data    = json.loads(cleaned)

    return str(data.get("summary", "")), [str(t) for t in data.get("topics", [])]


def _persist(
    document_id: str,
    tenant_id: str,
    summary: str,
    topics: list[str],
    page_count: int,
    chunk_count: int,
) -> None:
    conn = get_connection(tenant_id)
    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                UPDATE documents
                SET summary     = %s,
                    topics      = %s,
                    page_count  = %s,
                    chunk_count = %s
                WHERE id = %s
                """,
                (summary, topics, page_count, chunk_count, document_id),
            )
        conn.commit()
    finally:
        conn.close()
