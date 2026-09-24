"""Portable SemIf direct-decision contract: input validation, prompts, and softmax.

Ported from SemIf (formerly OpenJev), https://github.com/TheoLeeCJ/SemIf-OpenJev,
Copyright (c) 2026 TheoLeeCJ, MIT License (see upstream LICENSE). LETTERS,
DIRECT_SYSTEM, validate_row, direct_messages, softmax, and digest are copied
verbatim from src/semif_phase1/core.py so the prompt text stays byte-identical
and prompt_sha256 values stay comparable with that project's published
results. The GPU model-loading helpers in the upstream module are left out;
Bedrock Custom Model Import serves the model instead.
"""

from __future__ import annotations

import hashlib
import json
import math

LETTERS = "ABCDEFGHIJKLMNOP"
DIRECT_SYSTEM = (
    "Apply the supplied criterion to the supplied evidence. Choose exactly one listed option. "
    "Respond with only its uppercase letter, with no explanation or reasoning."
)


def validate_row(row: dict) -> None:
    required = {"id", "state", "question", "options"}
    if not required <= row.keys():
        raise ValueError(f"Row is missing fields: {sorted(required - row.keys())}")
    if not all(isinstance(row[key], str) and row[key] for key in ("id", "question")):
        raise ValueError("id and question must be nonempty strings")
    state = row["state"]
    if not isinstance(state, (str, dict, list)) or not state:
        raise ValueError("state must be a nonempty string, object, or array")
    try:
        json.dumps(state, ensure_ascii=False, allow_nan=False)
    except (TypeError, ValueError) as error:
        raise ValueError("state must be finite JSON-compatible data") from error
    options = row["options"]
    if not isinstance(options, list) or not 2 <= len(options) <= len(LETTERS):
        raise ValueError("options must contain 2-16 entries")
    ids = []
    for option in options:
        if not isinstance(option, dict) or not isinstance(option.get("id"), str) or not isinstance(option.get("description"), str):
            raise ValueError("Each option needs string id and description fields")
        ids.append(option["id"])
    if len(ids) != len(set(ids)):
        raise ValueError("Option IDs must be unique")


def direct_messages(row: dict) -> list[dict]:
    validate_row(row)
    payload = {
        "evidence": row["state"],
        "criterion": row["question"],
        "options": [
            {"letter": LETTERS[index], "description": option["description"]}
            for index, option in enumerate(row["options"])
        ],
    }
    return [
        {"role": "system", "content": DIRECT_SYSTEM},
        {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
    ]


def softmax(values: list[float]) -> list[float]:
    if len(values) < 2 or any(not math.isfinite(value) for value in values):
        raise ValueError("Need at least two finite scores")
    maximum = max(values)
    weights = [math.exp(value - maximum) for value in values]
    total = sum(weights)
    return [weight / total for weight in weights]


def digest(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


# Qwen3 ChatML control tokens. Rendering the prompt here rather than letting
# Bedrock apply the model's packaged chat template is deliberate: Qwen3's
# default template ends the prompt at "<|im_start|>assistant\n", which leaves
# the model free to open a reasoning block, so the first sampled position holds
# the distribution over <think> rather than over the answer letters. The
# thinking-suppressed form closes an empty reasoning block in the prompt itself,
# which puts the answer letter at the first sampled position. Upstream SemIf
# reaches the same string through apply_chat_template(enable_thinking=False).
QWEN3_THINK_SUPPRESSED_SUFFIX = "<|im_start|>assistant\n<think>\n\n</think>\n\n"


def render_qwen3_prompt(messages: list[dict]) -> str:
    """Render chat messages into the Qwen3 ChatML string with reasoning suppressed."""
    parts = [f"<|im_start|>{message['role']}\n{message['content']}<|im_end|>\n"
             for message in messages]
    parts.append(QWEN3_THINK_SUPPRESSED_SUFFIX)
    return "".join(parts)


# Mistral has no system role: its template merges the system text into the first
# user turn. It is not a reasoning model, so nothing needs suppressing, and the
# leading <s> is left out because the serving tokenizer adds BOS itself and
# including it here would double it.
def render_mistral_prompt(messages: list[dict]) -> str:
    """Render chat messages into Mistral's instruction format."""
    system = "".join(m["content"] for m in messages if m["role"] == "system")
    user = "".join(m["content"] for m in messages if m["role"] == "user")
    body = f"{system}\n\n{user}" if system else user
    # The bare template leaves no probability on the option letters at all: the
    # model opens a formatted answer instead, with "**" as its top token. A
    # trailing lead-in puts a letter at the first sampled position. Measured
    # declared mass over three rows: 0.000 bare, 0.12 with a space, 0.24 with
    # this, against 1.000 for Qwen3 with no lead-in at all.
    return f"[INST] {body}[/INST] The answer is "


RENDERERS = {"qwen3": render_qwen3_prompt, "mistral": render_mistral_prompt}
