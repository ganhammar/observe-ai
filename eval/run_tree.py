"""Score the fixture with a decomposed question tree of several sub-questions.

Each input row becomes several independent decisions against the same state.
The model answers those; combine() in tree.py turns them into the bug versus
downstream probability. Output matches the single-question runner's shape, so
eval/evaluate.py scores both the same way.

Writes a sidecar .tree.jsonl holding every sub-answer, so a wrong verdict can
be traced to a wrong sub-answer or to the combining rule.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from observe_ai.bedrock_backend import score  # noqa: E402
from derived import with_derived  # noqa: E402
from tree import combine, has_evidence, rows_for  # noqa: E402


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-arn", required=True)
    parser.add_argument("--region", default="eu-central-1")
    parser.add_argument("--input", type=Path, default=Path(__file__).parent / "logs.jsonl")
    parser.add_argument("--output", type=Path, default=Path("tree-results.jsonl"))
    parser.add_argument("--top-logprobs", type=int, default=20)
    parser.add_argument("--limit", type=int)
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

    detail_path = args.output.with_suffix(".tree.jsonl")
    calls = 0
    started_all = time.perf_counter()
    with args.output.open("w") as out, detail_path.open("w") as detail:
        for index, row in enumerate(rows, start=1):
            started = time.perf_counter()
            answers, errors = {}, []
            enriched = dict(row, state=with_derived(row["state"]))
            for sub in rows_for(enriched):
                key = sub["id"].split("::", 1)[1]
                try:
                    result = score(client, args.model_arn, sub, top_logprobs=args.top_logprobs,
                                   constrain=False)
                    calls += 1
                except Exception as error:
                    errors.append(f"{key}: {type(error).__name__}: {error}")
                    continue
                answers[key] = dict(zip(result["option_ids"], result["probabilities"]))
                detail.write(json.dumps({"row": row["id"], "signal": key,
                                         "probabilities": answers[key],
                                         "declared_mass": result.get("declared_mass")},
                                        ensure_ascii=False) + "\n")
            if errors:
                record = {"id": row["id"], "error": "; ".join(errors)[:400]}
            else:
                verdict = combine(answers, has_evidence(enriched["state"]))
                record = {
                    "id": row["id"],
                    "option_ids": ["bug", "downstream"],
                    "probabilities": [verdict["bug"], verdict["downstream"]],
                    "missing_options": [],
                    "abstained": False,
                    "fallback": verdict["fallback"],
                    "total_seconds": time.perf_counter() - started,
                }
            out.write(json.dumps(record, ensure_ascii=False) + "\n")
            out.flush()
            detail.flush()
            print(f"[{index}/{len(rows)}] {row['id']}", file=sys.stderr)

    elapsed = time.perf_counter() - started_all
    print(f"{len(rows)} rows, {calls} model calls, {elapsed:.1f}s -> {args.output}", file=sys.stderr)
    print(f"sub-answers -> {detail_path}", file=sys.stderr)


if __name__ == "__main__":
    main()
