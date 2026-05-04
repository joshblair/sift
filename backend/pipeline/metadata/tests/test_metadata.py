import json
from unittest.mock import patch

import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../layers/shared/python"))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))


class TestMetadataHandler:
    def _make_event(self, text="Some document text."):
        return {
            "documentId": "doc-1",
            "tenantId":   "tenant-1",
            "text":       text,
            "pageCount":  2,
            "chunks":     [{"index": 0, "content": "chunk1"}, {"index": 1, "content": "chunk2"}],
        }

    def test_returns_summary_and_topics(self):
        from metadata_handler import handler

        llm_response = json.dumps({
            "summary": "This document covers quarterly results.",
            "topics":  ["finance", "Q3", "revenue"],
        })

        with patch("metadata_handler.complete", return_value=llm_response), \
             patch("metadata_handler._persist"):
            result = handler(self._make_event(), None)

        assert result["summary"] == "This document covers quarterly results."
        assert "finance" in result["topics"]
        assert result["chunkCount"] == 2

    def test_strips_markdown_fences(self):
        from metadata_handler import _extract

        llm_response = "```json\n" + json.dumps({
            "summary": "A summary.",
            "topics":  ["topic1"],
        }) + "\n```"

        with patch("metadata_handler.complete", return_value=llm_response):
            summary, topics = _extract("some text")

        assert summary == "A summary."
        assert topics  == ["topic1"]

    def test_persist_called_with_correct_args(self):
        from metadata_handler import handler

        llm_response = json.dumps({"summary": "Summary", "topics": ["a", "b"]})

        with patch("metadata_handler.complete", return_value=llm_response), \
             patch("metadata_handler._persist") as mock_persist:
            handler(self._make_event(), None)

        mock_persist.assert_called_once_with("doc-1", "tenant-1", "Summary", ["a", "b"], 2, 2)
