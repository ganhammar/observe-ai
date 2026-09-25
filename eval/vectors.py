"""Regenerates tests/vectors/*.json from the Python reference implementation.

The C# port is checked against these files, so run this after changing
derived.py, tree.py or the fixture: `python eval/vectors.py`.
"""

import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from derived import _acquire_release, _current_versus_baseline, _durations, _numbers, _repetition, derive  # noqa: E402
from tree import combine  # noqa: E402

OUT = HERE.parent / "tests" / "vectors"

PRODUCERS = {
    "acquire_release": _acquire_release,
    "current_versus_baseline": _current_versus_baseline,
    "repetition": _repetition,
    "durations": _durations,
}

COMBINE_CASES = [
    {
        "name": "no_evidence_uses_baseline",
        "answers": {
            "baseline": {
                "bug": 0.73,
                "external": 0.27
            }
        },
        "evidence_present": False
    },
    {
        "name": "no_evidence_missing_baseline_defaults_half",
        "answers": {},
        "evidence_present": False
    },
    {
        "name": "one_strong_bug_signal",
        "answers": {
            "unreleased_resource": {
                "yes": 0.92,
                "no": 0.07999999999999996
            }
        },
        "evidence_present": True
    },
    {
        "name": "one_weak_bug_signal",
        "answers": {
            "self_inflicted_load": {
                "yes": 0.2,
                "no": 0.8
            }
        },
        "evidence_present": True
    },
    {
        "name": "opposing_signals",
        "answers": {
            "internal_inconsistency": {
                "yes": 0.8,
                "no": 0.19999999999999996
            },
            "external_unavailable": {
                "yes": 0.6,
                "no": 0.4
            }
        },
        "evidence_present": True
    },
    {
        "name": "dampened_invalid_value_sent_with_external_change",
        "answers": {
            "invalid_value_sent": {
                "yes": 0.9,
                "no": 0.09999999999999998
            },
            "external_change": {
                "yes": 0.7,
                "no": 0.30000000000000004
            }
        },
        "evidence_present": True
    },
    {
        "name": "dampened_invalid_value_sent_without_external_change",
        "answers": {
            "invalid_value_sent": {
                "yes": 0.9,
                "no": 0.09999999999999998
            },
            "external_change": {
                "yes": 0.0,
                "no": 1.0
            }
        },
        "evidence_present": True
    },
    {
        "name": "invalid_value_sent_no_external_change_key",
        "answers": {
            "invalid_value_sent": {
                "yes": 0.85,
                "no": 0.15000000000000002
            }
        },
        "evidence_present": True
    },
    {
        "name": "all_zero_explicit_no",
        "answers": {
            "unreleased_resource": {
                "yes": 0.0,
                "no": 1.0
            },
            "self_inflicted_load": {
                "yes": 0.0,
                "no": 1.0
            },
            "invalid_value_sent": {
                "yes": 0.0,
                "no": 1.0
            },
            "internal_inconsistency": {
                "yes": 0.0,
                "no": 1.0
            },
            "repeated_work": {
                "yes": 0.0,
                "no": 1.0
            },
            "external_change": {
                "yes": 0.0,
                "no": 1.0
            },
            "external_unavailable": {
                "yes": 0.0,
                "no": 1.0
            }
        },
        "evidence_present": True
    },
    {
        "name": "all_zero_no_answers_falls_back_to_surface",
        "answers": {},
        "evidence_present": True
    },
    {
        "name": "all_zero_no_answers_surface_network",
        "answers": {
            "surface": {
                "network": 1.0,
                "resource": 0.0,
                "data": 0.0,
                "logic": 0.0
            }
        },
        "evidence_present": True
    },
    {
        "name": "all_bug_signals_strong",
        "answers": {
            "unreleased_resource": {
                "yes": 0.9,
                "no": 0.09999999999999998
            },
            "self_inflicted_load": {
                "yes": 0.85,
                "no": 0.15000000000000002
            },
            "invalid_value_sent": {
                "yes": 0.95,
                "no": 0.050000000000000044
            },
            "internal_inconsistency": {
                "yes": 0.7,
                "no": 0.30000000000000004
            },
            "repeated_work": {
                "yes": 0.6,
                "no": 0.4
            }
        },
        "evidence_present": True
    },
    {
        "name": "all_external_signals_strong",
        "answers": {
            "external_change": {
                "yes": 0.9,
                "no": 0.09999999999999998
            },
            "external_unavailable": {
                "yes": 0.8,
                "no": 0.19999999999999996
            }
        },
        "evidence_present": True
    },
    {
        "name": "platform_intervention_alone",
        "answers": {
            "baseline": {
                "bug": 0.8,
                "external": 0.2
            },
            "platform_intervention": {
                "yes": 0.95,
                "no": 0.05
            }
        },
        "evidence_present": True
    },
    {
        "name": "platform_intervention_against_unreleased_resource",
        "answers": {
            "platform_intervention": {
                "yes": 0.9,
                "no": 0.1
            },
            "unreleased_resource": {
                "yes": 0.7,
                "no": 0.3
            }
        },
        "evidence_present": True
    }
]


def derived_vectors():
    rows = [json.loads(line) for line in (HERE / "logs.jsonl").read_text().splitlines()]
    vectors = []
    for row in rows:
        state = row["state"]
        numbers = _numbers(state.get("evidence", {})) if isinstance(state.get("evidence"), dict) else {}
        fired = [name for name, producer in PRODUCERS.items() if producer(numbers)]
        producer = fired[0] if fired else "none"
        name = "no_evidence_state_yields_no_facts" if row["id"] == "dn-01" else f"{producer if fired else 'fixture'}_{row['id']}"
        vectors.append({"name": name, "id": row["id"], "producer": producer if fired or row["id"] != "dn-01" else None,
                        "state": state, "expected_facts": derive(state)})
    return vectors


def combine_vectors():
    vectors = []
    for case in COMBINE_CASES:
        verdict = combine(case["answers"], case["evidence_present"])
        vectors.append({**case, "expected_bug": verdict["bug"], "expected_external": verdict["external"],
                        "expected_fallback": verdict["fallback"]})
    return vectors


if __name__ == "__main__":
    (OUT / "derived.json").write_text(json.dumps(derived_vectors(), indent=2, ensure_ascii=False) + "\n")
    (OUT / "combine.json").write_text(json.dumps(combine_vectors(), indent=2, ensure_ascii=False) + "\n")
    print("wrote", OUT / "derived.json", "and", OUT / "combine.json")
