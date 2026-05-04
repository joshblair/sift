import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from chunk_handler import handler, _chunk


class TestChunkHandler:
    def test_produces_chunks_for_long_text(self):
        text  = " ".join(["word"] * 3000)
        event = {"text": text, "tenantId": "t", "documentId": "d", "pageCount": 1}

        result = handler(event, None)

        assert len(result["chunks"]) > 1
        assert all("index" in c and "content" in c for c in result["chunks"])

    def test_chunks_are_sequentially_indexed(self):
        text  = " ".join(["word"] * 3000)
        event = {"text": text, "tenantId": "t", "documentId": "d", "pageCount": 1}

        result = handler(event, None)
        indices = [c["index"] for c in result["chunks"]]

        assert indices == list(range(len(indices)))

    def test_empty_text_returns_no_chunks(self):
        event = {"text": "", "tenantId": "t", "documentId": "d", "pageCount": 1}

        result = handler(event, None)

        assert result["chunks"] == []

    def test_short_text_produces_single_chunk(self):
        event = {"text": "Short text.", "tenantId": "t", "documentId": "d", "pageCount": 1}

        result = handler(event, None)

        assert len(result["chunks"]) == 1
        assert result["chunks"][0]["content"] == "Short text."

    def test_overlap_creates_shared_words(self):
        # Build text large enough for multiple chunks
        text   = " ".join([f"word{i}" for i in range(1000)])
        chunks = _chunk(text, size=200, overlap=50)

        assert len(chunks) > 1
        # The tail of chunk n should appear in the head of chunk n+1
        tail_words = set(chunks[0].split()[-5:])
        head_words = set(chunks[1].split()[:10])
        assert tail_words & head_words, "Expected overlap between consecutive chunks"

    def test_passthrough_fields_preserved(self):
        event = {"text": "hello", "tenantId": "t", "documentId": "d",
                 "pageCount": 2, "s3Key": "t/d/f.txt"}

        result = handler(event, None)

        assert result["tenantId"]   == "t"
        assert result["documentId"] == "d"
        assert result["pageCount"]  == 2
        assert result["s3Key"]      == "t/d/f.txt"
