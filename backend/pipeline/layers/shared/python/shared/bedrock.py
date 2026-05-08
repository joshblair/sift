import json
import os

import boto3

_client = None

EMBED_MODEL_ID = "amazon.titan-embed-text-v2:0"
CHAT_MODEL_ID  = "anthropic.claude-3-5-haiku-20241022-v1:0"


def _get_client():
    global _client
    if _client is None:
        _client = boto3.client(
            "bedrock-runtime",
            region_name=os.environ.get("AWS_REGION_NAME", "us-west-2"),
        )
    return _client


def embed(text: str) -> list[float]:
    payload = json.dumps({"inputText": text, "dimensions": 1024, "normalize": True})
    response = _get_client().invoke_model(
        modelId=EMBED_MODEL_ID,
        contentType="application/json",
        accept="application/json",
        body=payload,
    )
    return json.loads(response["body"].read())["embedding"]


def complete(system: str, user: str, max_tokens: int = 512) -> str:
    payload = json.dumps({
        "anthropic_version": "bedrock-2023-05-31",
        "max_tokens": max_tokens,
        "system": system,
        "messages": [{"role": "user", "content": user}],
    })
    response = _get_client().invoke_model(
        modelId=CHAT_MODEL_ID,
        contentType="application/json",
        accept="application/json",
        body=payload,
    )
    return json.loads(response["body"].read())["content"][0]["text"]
