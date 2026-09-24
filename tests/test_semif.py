import math

import pytest

from observe_ai.semif import direct_messages, softmax, validate_row

ROW = {
    "id": "x",
    "state": "owned evidence",
    "question": "Which answer follows?",
    "options": [
        {"id": "yes", "description": "Yes."},
        {"id": "no", "description": "No."},
    ],
}


def test_direct_prompt_excludes_extra_fields():
    row = dict(ROW, label="yes", provenance={"secret": "do not leak"})
    rendered = str(direct_messages(row))
    assert "owned evidence" in rendered
    assert "secret" not in rendered
    assert "label" not in rendered


def test_softmax_is_finite_and_normalized():
    values = softmax([1000.0, 999.0, -1000.0])
    assert all(math.isfinite(value) for value in values)
    assert sum(values) == pytest.approx(1.0)
    assert values[0] > values[1] > values[2]


def test_softmax_rejects_fewer_than_two_scores():
    with pytest.raises(ValueError, match="two finite"):
        softmax([1.0])


def test_softmax_rejects_nonfinite_scores():
    with pytest.raises(ValueError, match="two finite"):
        softmax([1.0, float("-inf")])


def test_duplicate_options_rejected():
    row = dict(ROW, options=[ROW["options"][0], ROW["options"][0]])
    with pytest.raises(ValueError, match="unique"):
        validate_row(row)


def test_too_few_options_rejected():
    row = dict(ROW, options=[ROW["options"][0]])
    with pytest.raises(ValueError, match="2-16"):
        validate_row(row)


def test_too_many_options_rejected():
    row = dict(ROW, options=[{"id": str(i), "description": "d"} for i in range(17)])
    with pytest.raises(ValueError, match="2-16"):
        validate_row(row)


def test_missing_required_field_rejected():
    row = {key: value for key, value in ROW.items() if key != "question"}
    with pytest.raises(ValueError, match="missing fields"):
        validate_row(row)


def test_empty_id_rejected():
    row = dict(ROW, id="")
    with pytest.raises(ValueError, match="nonempty strings"):
        validate_row(row)


def test_option_missing_description_rejected():
    row = dict(ROW, options=[{"id": "yes"}, ROW["options"][1]])
    with pytest.raises(ValueError, match="id and description"):
        validate_row(row)


def test_structured_json_state_is_supported():
    row = dict(ROW, state={"policy": "Never request passwords", "candidate": ["invoice id"]})
    validate_row(row)
    assert '"policy"' in direct_messages(row)[1]["content"]


def test_nonfinite_structured_state_is_rejected():
    with pytest.raises(ValueError, match="finite JSON-compatible"):
        validate_row(dict(ROW, state={"score": float("nan")}))


def test_empty_state_rejected():
    with pytest.raises(ValueError, match="nonempty string, object, or array"):
        validate_row(dict(ROW, state=""))
