"""
Step 1 — ExtractText

Input:  { s3Key, bucketName }
Output: { s3Key, bucketName, tenantId, documentId, filename, text, pageCount }

Parses tenantId/documentId from the S3 key ({tenantId}/{docId}/{filename}),
downloads the file, extracts plain text, and marks the document as 'processing'.
"""
import os
from io import BytesIO

import boto3

s3_client = boto3.client("s3")


def handler(event: dict, context) -> dict:
    s3_key     = event["s3Key"]
    bucket     = event["bucketName"]

    # Key format: {tenantId}/{documentId}/{filename}
    parts       = s3_key.split("/", 2)
    tenant_id   = parts[0]
    document_id = parts[1]
    filename    = parts[2]

    obj     = s3_client.get_object(Bucket=bucket, Key=s3_key)
    content = obj["Body"].read()

    ext = filename.rsplit(".", 1)[-1].lower()
    if ext == "pdf":
        text, page_count = _extract_pdf(content)
    elif ext == "docx":
        text, page_count = _extract_docx(content)
    elif ext == "csv":
        text, page_count = _extract_csv(content)
    elif ext == "txt":
        text       = content.decode("utf-8", errors="replace")
        page_count = 1
    else:
        raise ValueError(f"Unsupported file extension: {ext}")

    _mark_processing(document_id, tenant_id)

    return {
        **event,
        "tenantId":   tenant_id,
        "documentId": document_id,
        "filename":   filename,
        "text":       text,
        "pageCount":  page_count,
    }


def _extract_pdf(content: bytes) -> tuple[str, int]:
    import pdfplumber

    with pdfplumber.open(BytesIO(content)) as pdf:
        pages = [page.extract_text() or "" for page in pdf.pages]
    return "\n\n".join(pages), len(pages)


def _extract_docx(content: bytes) -> tuple[str, int]:
    from docx import Document

    doc  = Document(BytesIO(content))
    text = "\n".join(p.text for p in doc.paragraphs if p.text.strip())
    return text, 1


def _extract_csv(content: bytes) -> tuple[str, int]:
    import pandas as pd

    df   = pd.read_csv(BytesIO(content))
    text = f"Columns: {', '.join(df.columns)}\n\n" + df.to_string(index=False, max_rows=500)
    return text, 1


def _mark_processing(document_id: str, tenant_id: str) -> None:
    from shared.db import get_connection

    conn = get_connection(tenant_id)
    try:
        with conn.cursor() as cur:
            cur.execute(
                "UPDATE documents SET status = 'processing' WHERE id = %s",
                (document_id,),
            )
        conn.commit()
    finally:
        conn.close()
