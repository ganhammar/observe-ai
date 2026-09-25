"""Direct-decision readout sourced from Bedrock Custom Model Import log probabilities.

Mirrors the upstream native-logits backend (src/semif_phase1/direct.py in
SemIf). It reads the next-token distribution from a Bedrock invoke_model
response; a local forward pass never runs here, and the model's logits never
leave Bedrock, only the top_logprobs it chooses to report.
"""

from __future__ import annotations

import json
import math
import time

from .semif import (
    LETTERS,
    digest,
    direct_messages,
    render_qwen3_prompt,
    softmax,
    validate_row,
)

PROMPT_VERSION = "bedrock-direct-v1"


def score(client, model_arn: str, row: dict, *, top_logprobs: int = 20, constrain: bool = True,
          api: str = "completion") -> dict:
    """Score one SemIf row against a Bedrock Custom Model Import target.

    Sends an invoke_model request with max_tokens=1 and logprobs enabled, reads
    the log probability of each declared option letter (A, B, ...) at the first
    sampled position, and softmaxes the recovered values the way the upstream
    native-logits readout softmaxes full-vocabulary logits restricted to the
    answer slots.

    A letter that does not appear anywhere in top_logprobs carries negligible
    probability mass. It is recorded in missing_options and treated as -inf
    (probability 0); one crowded-out option does not fail the whole row.

    abstained is True when every option letter is missing from top_logprobs, so
    declared_mass is 0 and probabilities are all zero; it is False otherwise.
    This spares a consumer from inferring abstention from an all-zero
    probability vector: an unguarded argmax would score that vector as a
    confident prediction for the first option.

    declared_mass is the sum of exp(logprob) over the option letters, taken
    before renormalisation into probabilities. probabilities answers which
    option is favoured; declared_mass answers how much of the model's total
    next-token probability landed on one of the declared options at all. A
    low declared_mass means the renormalised probabilities are a ratio of two
    small, noisy numbers and should not be trusted as calibrated confidence.
    With constrain=True, the request already restricts sampling to the option
    letters via structured_outputs, so the logprobs Bedrock returns are
    post-mask and declared_mass reads close to 1.0 regardless of the model's
    unconstrained preference; it is uninformative in that mode. Call with
    constrain=False to get a declared_mass that reflects the model's free
    choice.

    api selects which Bedrock request shape to send. "completion" renders the
    Qwen3 ChatML prompt here and sends it as a raw prompt string; "chat" sends
    the messages and lets Bedrock apply the model's packaged chat template.
    The default is "completion" because Qwen3's packaged template ends the
    prompt at the assistant turn without closing a reasoning block. Under
    "chat" the first sampled position then carries the distribution over
    <think>, not the answer letters, and every option letter reads as
    near-zero mass. Constrained sampling hides that failure: masking to the
    letters still yields a confident-looking letter drawn from a renormalised
    tail.

    prompt_sha256 hashes the exact prompt sent. Under "completion" that is the
    rendered ChatML string, which is byte-identical to upstream's
    apply_chat_template(enable_thinking=False) output, so the hash is directly
    comparable with SemIf's published values. Under "chat" the rendered string
    never exists on this side, so the messages are hashed instead and the value
    is not comparable with upstream.
    """
    started = time.perf_counter()
    validate_row(row)
    options = row["options"]
    if len(options) > top_logprobs:
        raise ValueError(
            f"Row {row['id']}: {len(options)} options exceed top_logprobs={top_logprobs}; "
            "not enough candidates could possibly be returned to cover every option letter"
        )
    if api not in {"completion", "chat"}:
        raise ValueError("api must be 'completion' or 'chat'")
    messages = direct_messages(row)
    letters = list(LETTERS[: len(options)])

    body = {"max_tokens": 1, "temperature": 0}
    if api == "completion":
        prompt = render_qwen3_prompt(messages)
        prompt_hash = digest(prompt)
        body["prompt"] = prompt
        # The Completions schema carries the candidate count in logprobs
        # itself, as an integer. Sending a boolean here is accepted and
        # coerces to 1, which returns only the sampled token and no
        # distribution to read the option letters from.
        body["logprobs"] = top_logprobs
    else:
        prompt_hash = digest(json.dumps(messages, ensure_ascii=False))
        body["messages"] = messages
        # The Chat Completions schema splits the same request across a
        # boolean switch and a separate count.
        body["logprobs"] = True
        body["top_logprobs"] = top_logprobs
    if constrain:
        body["structured_outputs"] = {"choice": letters}

    response = client.invoke_model(
        modelId=model_arn,
        body=json.dumps(body).encode(),
        contentType="application/json",
        accept="application/json",
    )
    payload = json.loads(response["body"].read())

    by_letter = _first_position_logprobs(payload)
    if not by_letter:
        raise ValueError(f"Row {row['id']}: response returned no candidate tokens in top_logprobs")

    option_logprobs = []
    missing_options = []
    for option, letter in zip(options, letters):
        logprob = by_letter.get(letter)
        if logprob is None:
            missing_options.append(option["id"])
            option_logprobs.append(float("-inf"))
        else:
            option_logprobs.append(logprob)

    declared_mass = sum(math.exp(logprob) for logprob in option_logprobs if math.isfinite(logprob))
    probabilities = _softmax_allow_missing(option_logprobs)
    abstained = len(missing_options) == len(options)

    top_token_name = max(by_letter, key=by_letter.get)
    top_token = {"token": top_token_name, "probability": math.exp(by_letter[top_token_name])}

    return {
        "id": row["id"],
        "option_ids": [option["id"] for option in options],
        "probabilities": probabilities,
        "option_logprobs": option_logprobs,
        "declared_mass": declared_mass,
        "missing_options": missing_options,
        "abstained": abstained,
        "top_token": top_token,
        "input_tokens": payload["usage"]["prompt_tokens"],
        "total_seconds": time.perf_counter() - started,
        "prompt_sha256": prompt_hash,
        "prompt_version": PROMPT_VERSION,
    }


def _softmax_allow_missing(logprobs: list[float]) -> list[float]:
    """Softmax a list of logprobs, giving probability 0 to any -inf (missing) entry."""
    finite = [logprob for logprob in logprobs if math.isfinite(logprob)]
    if not finite:
        return [0.0 for _ in logprobs]
    if len(finite) == 1:
        return [1.0 if math.isfinite(logprob) else 0.0 for logprob in logprobs]
    finite_probabilities = iter(softmax(finite))
    return [next(finite_probabilities) if math.isfinite(logprob) else 0.0 for logprob in logprobs]


def _first_position_logprobs(payload: dict) -> dict:
    """Map token to log probability for the first sampled position.

    The two Bedrock request shapes report logprobs differently. The chat shape
    nests a list of {token, logprob} objects under logprobs.content[0]. The
    completion shape follows the older OpenAI Completions schema, where
    logprobs.top_logprobs is a list holding one token-to-logprob mapping per
    position. Both normalise to the same dictionary here.
    """
    logprobs = payload["choices"][0].get("logprobs")
    if not logprobs:
        raise ValueError("Response carries no logprobs; confirm logprobs and top_logprobs were accepted")
    content = logprobs.get("content")
    if content:
        return {entry["token"]: entry["logprob"] for entry in content[0]["top_logprobs"]}
    positions = logprobs.get("top_logprobs")
    if positions:
        return dict(positions[0])
    raise ValueError(f"Unrecognised logprobs shape: {sorted(logprobs)}")
