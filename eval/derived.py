"""State the numeric comparisons in the evidence so the model does not have to.

The model reliably answers what a piece of evidence means, but reliably fails
to compare two numbers: asked directly whether 3600 is larger than 900, or
whether 8400 is more than ten times 210, it answers no. Those are the rows
the tree misses.

This module walks the evidence, finds numeric relations worth naming, and
renders each as a plain sentence appended to the state. The model is then
asked what the stated comparison means, a question it answers well.

Pairing is by field-name convention, which a log pipeline with a fixed schema
can rely on. Nothing here decides bug versus downstream; it only makes the
magnitudes legible.
"""

from __future__ import annotations

import re

RATIO_THRESHOLD = 5.0

# Field-name pairs that mean "this happened" against "this undid it".
ACQUIRE_RELEASE = [("opened", "disposed"), ("opened", "closed"),
                   ("acquired", "released"), ("created", "destroyed")]

# Suffixes marking a value as the historical reference for a current reading.
BASELINE_MARKERS = ("_7d_avg", "_avg", "_average", "_baseline", "prior_", "_7d_max")


def _numbers(evidence: dict) -> dict:
    return {k: float(v) for k, v in evidence.items()
            if isinstance(v, (int, float)) and not isinstance(v, bool)}


def _ratio_sentence(high_name: str, high: float, low_name: str, low: float) -> str | None:
    if low == 0:
        return f"{high_name} is {high:g} while {low_name} is 0."
    ratio = high / low
    if ratio < RATIO_THRESHOLD:
        return None
    return f"{high_name} is {ratio:.0f} times {low_name} ({high:g} against {low:g})."


def _acquire_release(numbers: dict) -> list[str]:
    facts = []
    for up, down in ACQUIRE_RELEASE:
        highs = [k for k in numbers if up in k]
        lows = [k for k in numbers if down in k]
        for high_name in highs:
            for low_name in lows:
                sentence = _ratio_sentence(high_name, numbers[high_name], low_name, numbers[low_name])
                if sentence:
                    facts.append(sentence)
    return facts


def _strip_markers(name: str) -> str:
    """Reduce a baseline field name to the reading it is a baseline for."""
    stem = name
    for marker in BASELINE_MARKERS:
        stem = stem.replace(marker, "_")
    return re.sub(r"_+", "_", stem).strip("_")


def _current_versus_baseline(numbers: dict) -> list[str]:
    facts = []
    baselines = {k: v for k, v in numbers.items() if any(m in k for m in BASELINE_MARKERS)}
    readings = {k: v for k, v in numbers.items() if k not in baselines}
    for other, baseline in baselines.items():
        stem = _strip_markers(other)
        if not stem:
            continue
        # A reading matches its baseline when one name contains the other's
        # stem, which survives prefix markers such as prior_ and suffix ones
        # such as _7d_avg appearing in either order.
        for name, value in readings.items():
            if stem not in name and _strip_markers(name) not in stem:
                continue
            sentence = _ratio_sentence(name, value, other, baseline)
            if sentence:
                facts.append(sentence)
    return facts


def _repetition(numbers: dict) -> list[str]:
    counts = [k for k in numbers if re.search(r"count|_calls|queries|attempts", k)]
    distincts = [k for k in numbers if "distinct" in k]
    facts = []
    for count in counts:
        for distinct in distincts:
            sentence = _ratio_sentence(count, numbers[count], distinct, numbers[distinct])
            if sentence:
                facts.append(f"{sentence} The same work is repeated within one operation.")
    return facts


def _durations(numbers: dict) -> list[str]:
    """Name an interval that outlasts the lifetime it is meant to stay inside."""
    lifetimes = {k: v for k, v in numbers.items() if "expires" in k or "lifetime" in k}
    intervals = {k: v for k, v in numbers.items() if "interval" in k or "refresh" in k}
    facts = []
    for iname, ivalue in intervals.items():
        for lname, lvalue in lifetimes.items():
            if ivalue > lvalue:
                facts.append(
                    f"{iname} ({ivalue:g}) is longer than {lname} ({lvalue:g}), "
                    f"so the value is stale for part of every cycle.")
    return facts


def derive(state: dict) -> list[str]:
    """Return plain-sentence statements of the numeric relations in the evidence."""
    evidence = state.get("evidence")
    if not isinstance(evidence, dict):
        return []
    numbers = _numbers(evidence)
    facts = []
    for producer in (_acquire_release, _current_versus_baseline, _repetition, _durations):
        for fact in producer(numbers):
            if fact not in facts:
                facts.append(fact)
    return facts


def with_derived(state: dict) -> dict:
    """Copy the state with a derived_facts list added when anything was found."""
    facts = derive(state)
    if not facts:
        return state
    enriched = dict(state)
    enriched["derived_facts"] = facts
    return enriched
