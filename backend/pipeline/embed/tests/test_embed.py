from unittest.mock import patch, MagicMock

import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../layers/shared/python"))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))


class TestEmbedHandler:
    def test_returns_ok_status(self):
        from embed_handler import handler

        fake_embedding = [0.1] * 1536
        event = {
            "documentId": "doc-1",
            "tenantId":   "tenant-1",
            "index":      0,
            "content":    "Some chunk content here.",
        }

        with patch("embed_handler.embed", return_value=fake_embedding), \
             patch("embed_handler._insert_chunk"):
            result = handler(event, None)

        assert result["status"]     == "ok"
        assert result["documentId"] == "doc-1"
        assert result["index"]      == 0

    def test_inserts_chunk_with_correct_args(self):
        from embed_handler import handler

        fake_embedding = [0.5] * 1536
        event = {
            "documentId": "doc-abc",
            "tenantId":   "tenant-xyz",
            "index":      3,
            "content":    "The quick brown fox.",
        }

        with patch("embed_handler.embed", return_value=fake_embedding) as mock_embed, \
             patch("embed_handler._insert_chunk") as mock_insert:
            handler(event, None)

        mock_embed.assert_called_once_with("The quick brown fox.")
        mock_insert.assert_called_once_with(
            "doc-abc", "tenant-xyz", 3, "The quick brown fox.", fake_embedding
        )
