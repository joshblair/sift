import json
import os

import boto3
import psycopg2
import psycopg2.extras


# Cached per Lambda container — avoids hitting Secrets Manager on every invocation.
_credentials: dict | None = None


def _get_credentials() -> dict:
    global _credentials
    if _credentials is None:
        client = boto3.client("secretsmanager")
        secret = client.get_secret_value(SecretId=os.environ["DB_SECRET_ARN"])
        _credentials = json.loads(secret["SecretString"])
    return _credentials


def get_connection(tenant_id: str | None = None) -> psycopg2.extensions.connection:
    creds = _get_credentials()
    conn = psycopg2.connect(
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "sift"),
        user=creds["username"],
        password=creds["password"],
        sslmode="require",
        connect_timeout=10,
        cursor_factory=psycopg2.extras.RealDictCursor,
    )
    if tenant_id:
        with conn.cursor() as cur:
            cur.execute(
                "SELECT set_config('app.current_tenant_id', %s, false)",
                (str(tenant_id),),
            )
        conn.commit()
    return conn
