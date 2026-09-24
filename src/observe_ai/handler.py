"""AWS Lambda entrypoint scoring SemIf rows against Bedrock Custom Model Import.

The boto3 client is created lazily on first invocation and reused across
warm invocations, since constructing it and letting botocore renegotiate
retries on every call would be wasted work in a warm container.
"""

from __future__ import annotations

import logging
import os

import boto3
from botocore.config import Config
from botocore.exceptions import BotoCoreError, ClientError

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
    if not isinstance(row, dict):
        logger.warning("Row rejected: expected an object, got %s", type(row).__name__)
        return {"id": None, "error": f"Row must be a JSON object, got {type(row).__name__}"}
    row_id = row.get("id")
    try:
        return score(_get_client(), model_arn, row, api=api)
    except (ValueError, ClientError, BotoCoreError) as error:
        logger.warning("Row %s rejected: %s: %s", row_id, type(error).__name__, error)
        return {"id": row_id, "error": f"{type(error).__name__}: {error}"}


def handler(event, context):
    """Score a single SemIf row or a {"rows": [...]} batch, returning {"results": [...]}.

    Takes an already-parsed event: a direct Lambda invoke, or a consumer reading
    rows off a queue such as SQS. An HTTP front end would need to parse
    event["body"] itself before calling this, which is not implemented here.
    """
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
    return {"results": results}
