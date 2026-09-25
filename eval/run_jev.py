"""Score the same fixture against TypeSafe's Jev, for comparison.

Jev takes the option descriptions directly as named criteria and returns a
probability per option, so there is no letter slot and no renormalisation.
This is a reference point for the readout: the prompt sent to Jev is its
own, different from the frozen prompt the Bedrock path sends.

The API key is read from TYPESAFE_API_KEY. It is never written to the output.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ENDPOINT = "https://api.typesafe.ai/v1/systemone"
QUESTION_KEY = "triage"


def score(api_key: str, row: dict, model: str, timeout: float) -> dict:
    state = row["state"]
    payload = {
        "model": model,
        "state": state if isinstance(state, str) else json.dumps(state, ensure_ascii=False),
        "questions": {
            QUESTION_KEY: {
                "type": "choice",
                "instructions": row["question"],
                "criteria": {option["id"]: option["description"] for option in row["options"]},
            }
        },
    }
    request = urllib.request.Request(
        ENDPOINT,
        data=json.dumps(payload).encode(),
        headers={"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"},
        method="POST",
    )
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        body = json.loads(response.read())
    elapsed = time.perf_counter() - started

    answer = body.get("answers", body).get(QUESTION_KEY)
    if answer is None:
        raise ValueError(f"Response carried no answer for {QUESTION_KEY!r}: {sorted(body)}")
    probabilities = answer.get("probabilities") or {}
    option_ids = [option["id"] for option in row["options"]]
    missing = [oid for oid in option_ids if oid not in probabilities]
    return {
        "id": row["id"],
        "option_ids": option_ids,
        "probabilities": [float(probabilities.get(oid, 0.0)) for oid in option_ids],
        "missing_options": missing,
        "abstained": len(missing) == len(option_ids),
        "choice": answer.get("choice"),
        "confidence": answer.get("confidence"),
        "total_seconds": elapsed,
        "backend": "jev",
        "model": model,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=Path(__file__).parent / "logs.jsonl")
    parser.add_argument("--output", type=Path, default=Path("jev-results.jsonl"))
    parser.add_argument("--model", default="jev-latest")
    parser.add_argument("--timeout", type=float, default=60.0)
    parser.add_argument("--limit", type=int)
    args = parser.parse_args()

    api_key = os.environ.get("TYPESAFE_API_KEY")
    if not api_key:
        parser.error("TYPESAFE_API_KEY is not set")

    rows = [json.loads(line) for line in args.input.read_text().splitlines() if line.strip()]
    if args.limit:
        rows = rows[: args.limit]

    written = failed = 0
    with args.output.open("w") as out:
        for index, row in enumerate(rows, start=1):
            try:
                result = score(api_key, row, args.model, args.timeout)
            except urllib.error.HTTPError as error:
                detail = error.read().decode(errors="replace")[:300]
                result = {"id": row.get("id"), "error": f"HTTP {error.code}: {detail}"}
                failed += 1
            except Exception as error:
                result = {"id": row.get("id"), "error": f"{type(error).__name__}: {error}"}
                failed += 1
            else:
                written += 1
            out.write(json.dumps(result, ensure_ascii=False) + "\n")
            out.flush()
            print(f"[{index}/{len(rows)}] {row.get('id')}", file=sys.stderr)

    print(f"{written} scored, {failed} failed -> {args.output}", file=sys.stderr)
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
