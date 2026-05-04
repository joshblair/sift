from unittest.mock import MagicMock, patch

import pytest

import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "../../layers/shared/python"))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))


class TestExtractHandler:
    def test_txt_extraction(self):
        from extract_handler import handler

        s3_content = b"Hello world. This is a test document."
        event = {
            "s3Key":      "tenant-1/doc-1/notes.txt",
            "bucketName": "test-bucket",
        }

        with patch("extract_handler.s3_client") as mock_s3, \
             patch("extract_handler._mark_processing"):
            mock_s3.get_object.return_value = {"Body": MagicMock(read=lambda: s3_content)}
            result = handler(event, None)

        assert result["tenantId"]   == "tenant-1"
        assert result["documentId"] == "doc-1"
        assert result["filename"]   == "notes.txt"
        assert result["pageCount"]  == 1
        assert "Hello world" in result["text"]

    def test_unsupported_extension_raises(self):
        from extract_handler import handler

        event = {"s3Key": "t/d/file.xyz", "bucketName": "bucket"}

        with patch("extract_handler.s3_client") as mock_s3, \
             patch("extract_handler._mark_processing"):
            mock_s3.get_object.return_value = {"Body": MagicMock(read=lambda: b"data")}
            with pytest.raises(ValueError, match="Unsupported"):
                handler(event, None)

    def test_parses_key_segments_correctly(self):
        from extract_handler import handler

        event = {
            "s3Key":      "aaaa-bbbb/cccc-dddd/my report.txt",
            "bucketName": "uploads",
        }

        with patch("extract_handler.s3_client") as mock_s3, \
             patch("extract_handler._mark_processing"):
            mock_s3.get_object.return_value = {"Body": MagicMock(read=lambda: b"content")}
            result = handler(event, None)

        assert result["tenantId"]   == "aaaa-bbbb"
        assert result["documentId"] == "cccc-dddd"
        assert result["filename"]   == "my report.txt"
