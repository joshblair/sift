"""
Step 2 — ChunkText

Input:  { ..., text, pageCount }
Output: { ..., pageCount, chunks: [{ index, content }] }

Splits the extracted text into overlapping windows. The chunks list is
consumed by the GenerateEmbeddings Map state in Step 3.
"""

CHUNK_SIZE    = 512   # tokens, approximated as characters / 4
CHUNK_OVERLAP = 64    # tokens of overlap between adjacent chunks
CHARS_PER_TOKEN = 4


def handler(event: dict, context) -> dict:
    text   = event["text"]
    chunks = _chunk(text, CHUNK_SIZE * CHARS_PER_TOKEN, CHUNK_OVERLAP * CHARS_PER_TOKEN)

    return {
        **event,
        "chunks": [{"index": i, "content": c} for i, c in enumerate(chunks)],
    }


def _chunk(text: str, size: int, overlap: int) -> list[str]:
    """Sliding-window character chunker. Splits on whitespace boundaries."""
    if not text.strip():
        return []

    words  = text.split()
    chunks = []
    buf: list[str] = []
    buf_len = 0

    for word in words:
        word_len = len(word) + 1  # +1 for space
        if buf_len + word_len > size and buf:
            chunks.append(" ".join(buf))
            # Roll back by overlap characters
            rolled, rolled_len = [], 0
            for w in reversed(buf):
                if rolled_len + len(w) + 1 > overlap:
                    break
                rolled.insert(0, w)
                rolled_len += len(w) + 1
            buf     = rolled
            buf_len = rolled_len

        buf.append(word)
        buf_len += word_len

    if buf:
        chunks.append(" ".join(buf))

    return chunks
