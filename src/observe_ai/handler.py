"""AWS Lambda entrypoint scoring SemIf rows against Bedrock Custom Model Import.

The boto3 client is created lazily on first invocation and reused across
warm invocations, since constructing it and letting botocore renegotiate
retries on every call would be wasted work in a warm container.
"""

from __future__ import annotations

import json
import logging
import os

import boto3
from botocore.config import Config

from .bedrock_backend import score

logger = logging.getLogger(__name__)
logger.setLevel(os.environ.get("LOG_LEVEL", "INFO"))

_client = None


def _get_client():
    global _client
    if _client is None:
        _client = boto3.client(
            "bedrock-runtime",
            region_name=os.environ["BEDROCK_REGION"],
            config=Config(retries={"total_max_attempts": 10, "mode": "standard"}),
        )
    return _client


def _score_row(row: dict, model_arn: str, api: str) -> dict:
    try:
        return score(_get_client(), model_arn, row, api=api)
    except ValueError as error:
        row_id = row.get("id") if isinstance(row, dict) else None
        logger.warning("Row %s rejected: %s", row_id, error)
        return {"id": row_id, "error": str(error)}


def handler(event, context):
    """Score a single SemIf row or a {"rows": [...]} batch, returning {"results": [...]}."""
    model_arn = os.environ["MODEL_ARN"]
    # SEMIF_API selects the Bedrock request shape. It is an environment switch
    # rather than a constant so the chat path can be tried against a deployed
    # model without shipping code, which matters while it is still unconfirmed
    # which shape the imported model answers best.
    api = os.environ.get("SEMIF_API", "completion")
    rows = event["rows"] if "rows" in event else [event]
    results = [_score_row(row, model_arn, api) for row in rows]
    failures = sum(1 for result in results if "error" in result)
    logger.info("Scored %d rows, %d rejected", len(results), failures)
    body = {"results": results}
    if "requestContext" in event:
        return {"statusCode": 200, "body": json.dumps(body)}
    return body
