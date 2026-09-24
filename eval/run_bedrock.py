"""Score a SemIf JSONL fixture against an imported Bedrock model.

Writes one result object per input row to the output file. Rows that fail are
recorded with an `error` field and do not stop the run, so a partial fixture
still produces a scoreable results file.

Run with --unconstrained at least once. Constrained decoding masks the sampler
to the option letters, which can make `declared_mass` read close to 1.0 whether
or not the model genuinely favoured those options. The unconstrained pass is
the one that shows how much mass the declared options actually hold.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from observe_ai.bedrock_backend import score  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-arn", required=True, help="Imported model ARN")
    parser.add_argument("--region", default="eu-central-1")
    parser.add_argument("--input", type=Path, default=Path(__file__).parent / "logs.jsonl")
    parser.add_argument("--output", type=Path, default=Path("results.jsonl"))
    parser.add_argument("--top-logprobs", type=int, default=20)
    parser.add_argument("--api", choices=("completion", "chat"), default="completion",
                        help="Request shape. completion renders the ChatML prompt locally with "
                             "reasoning suppressed; chat lets Bedrock apply the packaged template")
    parser.add_argument("--unconstrained", action="store_true",
                        help="Drop the choice constraint so declared_mass reflects the true distribution")
    parser.add_argument("--limit", type=int, help="Score only the first N rows")
    args = parser.parse_args()

    import boto3
    from botocore.config import Config

    client = boto3.client(
        "bedrock-runtime",
        region_name=args.region,
        config=Config(retries={"total_max_attempts": 10, "mode": "standard"}),
    )

    rows = [json.loads(line) for line in args.input.read_text().splitlines() if line.strip()]
    if args.limit:
        rows = rows[: args.limit]

    started = time.perf_counter()
    written = failed = 0
    with args.output.open("w") as out:
        for index, row in enumerate(rows, start=1):
            try:
                result = score(client, args.model_arn, row,
                               top_logprobs=args.top_logprobs,
                               constrain=not args.unconstrained,
                               api=args.api)
            except Exception as error:
                result = {"id": row.get("id"), "error": f"{type(error).__name__}: {error}"}
                failed += 1
            else:
                written += 1
            out.write(json.dumps(result, ensure_ascii=False) + "\n")
            out.flush()
            print(f"[{index}/{len(rows)}] {row.get('id')}", file=sys.stderr)

    elapsed = time.perf_counter() - started
    print(f"{written} scored, {failed} failed, {elapsed:.1f}s -> {args.output}", file=sys.stderr)
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
