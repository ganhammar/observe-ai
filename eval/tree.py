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

from __future__ import annotations

# The flat question the single-question baseline asks. It scores 100% on rows
# that carry no evidence, where the signal questions have nothing to read and
# answer off the stack trace instead.
BASELINE = {
    "key": "baseline",
    "question": ("Does this error indicate a defect in this service's own source code, "
                 "or a failure in an external dependency or infrastructure it calls?"),
    "options": [
        {"id": "bug", "description": "A defect in this service's own source code. Resolving it requires a change to this repository."},
        {"id": "downstream", "description": "A failure in an external dependency or in infrastructure this service calls. This service's own code is behaving correctly."},
    ],
}

SURFACE = {
    "key": "surface",
    "question": "Where did this failure surface?",
    "options": [
        {"id": "network", "description": "While calling another service over the network, or awaiting its response."},
        {"id": "resource", "description": "While acquiring a finite local resource such as a connection, thread, or memory."},
        {"id": "data", "description": "While parsing, deserialising, or validating data."},
        {"id": "logic", "description": "Inside this service's own computation, with no external call or resource involved."},
    ],
}

# Signals that attribute the failure to this service. Each asks about something
# stated in the evidence, never about blame.
BUG_SIGNALS = [
    {
        "key": "unreleased_resource",
        "question": "Does the evidence show a resource being acquired far more often than it is released or returned?",
        "options": [
            {"id": "yes", "description": "Acquisitions substantially outnumber releases, or usage grows and never falls."},
            {"id": "no", "description": "Acquisition and release are balanced, or the evidence does not describe this."},
        ],
    },
    {
        "key": "self_inflicted_load",
        "question": "Does the evidence show this service's own request volume or repetition as unusual compared with its own recent baseline?",
        "options": [
            {"id": "yes", "description": "This service's own rate or repeat count is far above its normal level."},
            {"id": "no", "description": "This service's own volume is normal, or the evidence does not describe it."},
        ],
    },
    {
        "key": "invalid_value_sent",
        "question": "Does the evidence show that a value this service supplied is the one the error identifies as unacceptable?",
        "options": [
            {"id": "yes", "description": "A value in the outgoing request matches what the error names as invalid."},
            {"id": "no", "description": "No supplied value is identified as invalid, or the evidence does not describe this."},
        ],
    },
    {
        "key": "internal_inconsistency",
        "question": "Does the evidence show two of this service's own settings or assumptions that disagree with each other?",
        "options": [
            {"id": "yes", "description": "Two values this service controls are mutually inconsistent."},
            {"id": "no", "description": "This service's own settings are consistent, or the evidence does not describe them."},
        ],
    },
]

# Signals that attribute the failure outside this service.
BUG_SIGNALS.append({
    "key": "repeated_work",
    "question": "Does the evidence show this service repeating the same work many times within a single operation?",
    "options": [
        {"id": "yes", "description": "The same call or statement is issued far more times than there are distinct items."},
        {"id": "no", "description": "Work is not repeated in that way."},
    ],
})

DOWNSTREAM_SIGNALS = [
    {
        "key": "external_change",
        "question": "Does the evidence show that an external party recently changed its data, limits, or configuration?",
        "options": [
            {"id": "yes", "description": "Something outside this service changed recently, and the change is described."},
            {"id": "no", "description": "Nothing external is described as having changed."},
        ],
    },
    {
        "key": "external_unavailable",
        "question": "Does the evidence show the external party failing or refusing service for reasons unrelated to what this service sent?",
        "options": [
            {"id": "yes", "description": "The external party is unreachable, erroring, or rejecting independently of our request content."},
            {"id": "no", "description": "The external party is responding normally, or its failure follows from what we sent."},
        ],
    },
]

STAGE2 = BUG_SIGNALS + DOWNSTREAM_SIGNALS

# Uniform mass added to both sides before normalising, so a verdict cannot
# reach certainty on the strength of one signal alone.
PRIOR = 0.15


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

    external_change = _yes(answers, "external_change")
    bug_values = []
    for signal in BUG_SIGNALS:
        value = _yes(answers, signal["key"])
        if signal["key"] == "invalid_value_sent":
            # Sending a value the other side rejects is only ours when the other
            # side did not just change what it accepts. Without this the two
            # signals both fire on a contract change and cancel each other.
            value *= (1.0 - external_change)
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
