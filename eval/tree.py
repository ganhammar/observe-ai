"""Decomposed triage: several grounded questions instead of one causal leap.

The single-question baseline scored 100% on both clear bands and 41.7% on the
ambiguous band, with identical answers whether or not the evidence was present.
The model reads the trace well and does not make the jump from trace to cause.

So the jump is removed. Each question below asks the model something it can
read off the evidence, and the attribution is done in combine(), in code. The
division is deliberate: the model decides what the text means, arithmetic and
causal composition stay somewhere deterministic and testable.

Stage 2 questions are independent rather than chained, so one wrong answer
degrades the result instead of derailing the rest of the tree.
"""

import json
from pathlib import Path

# The definitions live in src/ObserveAi/tree.json so the C# runtime can embed
# the same bytes it is deployed with. Loading rather than duplicating them is
# what stops the two implementations drifting; tests/vectors/combine.json
# guards the combining rule on top of that.
_DEFINITION = json.loads((Path(__file__).resolve().parents[1]
                          / "src" / "ObserveAi" / "tree.json").read_text())

PRIOR = _DEFINITION["prior"]
BASELINE = _DEFINITION["baseline"]
SURFACE = _DEFINITION["surface"]
BUG_SIGNALS = [s for s in _DEFINITION["signals"] if s["side"] == "bug"]
DOWNSTREAM_SIGNALS = [s for s in _DEFINITION["signals"] if s["side"] == "downstream"]
STAGE2 = BUG_SIGNALS + DOWNSTREAM_SIGNALS

def rows_for(row: dict) -> list[dict]:
    """Expand one triage row into the tree's individual decision rows."""
    out = [{
        "id": f"{row['id']}::{BASELINE['key']}",
        "state": row["state"],
        "question": BASELINE["question"],
        "options": BASELINE["options"],
    }, {
        "id": f"{row['id']}::{SURFACE['key']}",
        "state": row["state"],
        "question": SURFACE["question"],
        "options": SURFACE["options"],
    }]
    for signal in STAGE2:
        out.append({
            "id": f"{row['id']}::{signal['key']}",
            "state": row["state"],
            "question": signal["question"],
            "options": signal["options"],
        })
    return out


def _yes(answers: dict, key: str) -> float:
    """Probability that a signal answered yes, or 0.0 when it did not run."""
    answer = answers.get(key)
    if not answer:
        return 0.0
    return float(answer.get("yes", 0.0))


def _noisy_or(values: list[float]) -> float:
    """Combine independent signals: any one firing is enough."""
    product = 1.0
    for value in values:
        product *= (1.0 - value)
    return 1.0 - product


def has_evidence(state: dict) -> bool:
    """True when the row carries signals the stage 2 questions can actually read."""
    return bool(state.get("evidence")) or bool(state.get("derived_facts"))


def combine(answers: dict, evidence_present: bool = True) -> dict:
    """Turn the tree's typed answers into a bug versus downstream probability.

    A noisy-OR over each side treats the signals as independent evidence, so
    one confident signal decides while several weak ones accumulate.

    PRIOR is added to both sides before normalising. Without it a single signal
    at 0.92 against nothing on the other side normalises to exactly 1.0, which
    reproduces the saturation the single-question readout already suffers from.
    With it, confidence tracks how much evidence actually fired: one strong
    signal lands near 0.88, two opposing mid signals stay near the middle, and
    the output remains something a threshold can be set against.
    """
    if not evidence_present:
        # Nothing for the signal questions to read. Anything they report is
        # drawn from the stack trace, which the flat question already reads
        # better, so defer to it rather than let a signal fire on nothing.
        flat = answers.get(BASELINE["key"], {})
        bug_p = float(flat.get("bug", 0.5))
        return {"bug": bug_p, "downstream": 1.0 - bug_p, "fallback": True}

    bug_values = []
    for signal in BUG_SIGNALS:
        value = _yes(answers, signal["key"])
        dampener = signal.get("dampened_by")
        if dampener:
            # Sending a value the other side rejects is only ours when the other
            # side did not just change what it accepts. Without this the two
            # signals both fire on a contract change and cancel each other.
            value *= (1.0 - _yes(answers, dampener))
        bug_values.append(value)
    bug = _noisy_or(bug_values)
    downstream = _noisy_or([_yes(answers, signal["key"]) for signal in DOWNSTREAM_SIGNALS])
    total = bug + downstream
    if total == 0.0:
        # Nothing fired either way. Fall back to the surface reading, which the
        # baseline already answers reliably: a failure that surfaced on a call
        # out is more often the other side's, anything else more often ours.
        surface = answers.get(SURFACE["key"], {})
        network = float(surface.get("network", 0.0))
        return {"bug": 1.0 - network, "downstream": network, "fallback": True}
    return {"bug": (bug + PRIOR) / (bug + downstream + 2 * PRIOR),
            "downstream": (downstream + PRIOR) / (bug + downstream + 2 * PRIOR),
            "fallback": False}
