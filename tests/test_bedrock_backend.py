import io
import json
import math

import pytest

from observe_ai import handler as handler_module
from observe_ai import semif
from observe_ai.bedrock_backend import score

ROW_2 = {
    "id": "row-2",
    "state": {"stack_trace": "NullReferenceException at Foo.Bar"},
    "question": "Is this a bug or a downstream failure?",
    "options": [
        {"id": "bug", "description": "A defect in this service's own code."},
        {"id": "downstream", "description": "A failure in an external dependency."},
    ],
}

ROW_4 = {
    "id": "row-4",
    "state": "evidence text",
    "question": "Which quadrant applies?",
    "options": [
        {"id": "a", "description": "First."},
        {"id": "b", "description": "Second."},
        {"id": "c", "description": "Third."},
        {"id": "d", "description": "Fourth."},
    ],
}


class StubClient:
    """Stands in for a boto3 bedrock-runtime client, returning a canned invoke_model response."""

    def __init__(self, response_body: dict):
        self.response_body = response_body
        self.calls = []

    def invoke_model(self, **kwargs):
        self.calls.append(kwargs)
        return {"body": io.BytesIO(json.dumps(self.response_body).encode())}


def make_body(top_logprobs, chosen_token, chosen_logprob, prompt_tokens=41):
    return {
        "choices": [
            {
                "index": 0,
                "message": {"role": "assistant", "content": chosen_token},
                "logprobs": {
                    "content": [
                        {
                            "token": chosen_token,
                            "logprob": chosen_logprob,
                            "bytes": list(chosen_token.encode()),
                            "top_logprobs": top_logprobs,
                        }
                    ]
                },
                "finish_reason": "stop",
            }
        ],
        "usage": {"prompt_tokens": prompt_tokens, "completion_tokens": 1, "total_tokens": prompt_tokens + 1},
    }


def test_letter_mapping_two_options():
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    client = StubClient(make_body(top_logprobs, "A", -0.1))

    result = score(client, "arn:model", ROW_2, constrain=False)

    assert result["id"] == "row-2"
    assert result["option_ids"] == ["bug", "downstream"]
    assert result["option_logprobs"] == [-0.1, -2.5]
    assert result["missing_options"] == []
    expected = [math.exp(-0.1), math.exp(-2.5)]
    total = sum(expected)
    expected = [value / total for value in expected]
    assert result["probabilities"] == pytest.approx(expected)
    assert sum(result["probabilities"]) == pytest.approx(1.0)
    assert result["input_tokens"] == 41
    assert result["prompt_version"] == "bedrock-direct-v1"
    assert isinstance(result["prompt_sha256"], str) and len(result["prompt_sha256"]) == 64


def test_letter_mapping_four_options_preserves_order():
    top_logprobs = [
        {"token": "C", "logprob": -0.2, "bytes": [67]},
        {"token": "A", "logprob": -1.0, "bytes": [65]},
        {"token": "D", "logprob": -3.0, "bytes": [68]},
        {"token": "B", "logprob": -4.0, "bytes": [66]},
    ]
    client = StubClient(make_body(top_logprobs, "C", -0.2))

    result = score(client, "arn:model", ROW_4, constrain=False)

    assert result["option_ids"] == ["a", "b", "c", "d"]
    assert result["option_logprobs"] == [-1.0, -4.0, -0.2, -3.0]
    assert result["missing_options"] == []
    # Highest logprob (C, index 2) should have the highest probability.
    assert result["probabilities"].index(max(result["probabilities"])) == 2


def test_missing_option_letter_is_recorded_and_zeroed():
    row = dict(ROW_4, options=ROW_4["options"][:3])  # a, b, c only
    top_logprobs = [
        {"token": "A", "logprob": -0.5, "bytes": [65]},
        {"token": "B", "logprob": -1.5, "bytes": [66]},
        {"token": "X", "logprob": -3.0, "bytes": [88]},
    ]
    client = StubClient(make_body(top_logprobs, "A", -0.5))

    result = score(client, "arn:model", row, constrain=False)

    assert result["missing_options"] == ["c"]
    assert result["option_logprobs"][2] == float("-inf")
    assert result["probabilities"][2] == 0.0
    expected_ab = [math.exp(-0.5), math.exp(-1.5)]
    total = sum(expected_ab)
    assert result["probabilities"][0] == pytest.approx(expected_ab[0] / total)
    assert result["probabilities"][1] == pytest.approx(expected_ab[1] / total)
    # declared_mass excludes the missing letter's contribution (it is -inf, exp = 0).
    assert result["declared_mass"] == pytest.approx(math.exp(-0.5) + math.exp(-1.5))


def test_declared_mass_is_low_when_model_prefers_other_tokens():
    top_logprobs = [
        {"token": "Based", "logprob": -0.05, "bytes": [66, 97]},
        {"token": "A", "logprob": -6.0, "bytes": [65]},
        {"token": "B", "logprob": -7.0, "bytes": [66]},
    ]
    client = StubClient(make_body(top_logprobs, "Based", -0.05))

    result = score(client, "arn:model", ROW_2, constrain=False)

    assert result["declared_mass"] == pytest.approx(math.exp(-6.0) + math.exp(-7.0))
    assert result["declared_mass"] < 0.01
    assert result["top_token"]["token"] == "Based"
    assert result["top_token"]["probability"] == pytest.approx(math.exp(-0.05))


def test_constrain_true_sets_structured_outputs():
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    client = StubClient(make_body(top_logprobs, "A", -0.1))

    score(client, "arn:model", ROW_2, constrain=True)

    sent_body = json.loads(client.calls[0]["body"])
    assert sent_body["structured_outputs"] == {"choice": ["A", "B"]}


def test_constrain_false_omits_structured_outputs():
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    client = StubClient(make_body(top_logprobs, "A", -0.1))

    score(client, "arn:model", ROW_2, constrain=False)

    sent_body = json.loads(client.calls[0]["body"])
    assert "structured_outputs" not in sent_body


def test_too_many_options_raises_without_calling_bedrock():
    client = StubClient(make_body([{"token": "A", "logprob": -0.1, "bytes": [65]}], "A", -0.1))

    with pytest.raises(ValueError, match="top_logprobs"):
        score(client, "arn:model", ROW_4, top_logprobs=2)

    assert client.calls == []


def test_invalid_row_raises_before_calling_bedrock():
    client = StubClient(make_body([{"token": "A", "logprob": -0.1, "bytes": [65]}], "A", -0.1))
    bad_row = dict(ROW_2, options=ROW_2["options"][:1])

    with pytest.raises(ValueError, match="2-16"):
        score(client, "arn:model", bad_row)

    assert client.calls == []


def test_handler_batch_with_one_invalid_row(monkeypatch):
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    stub_client = StubClient(make_body(top_logprobs, "A", -0.1))
    monkeypatch.setattr(handler_module, "_get_client", lambda: stub_client)
    monkeypatch.setenv("MODEL_ARN", "arn:model")
    monkeypatch.setenv("BEDROCK_REGION", "eu-central-1")

    invalid_row = dict(ROW_2, id="row-invalid", options=ROW_2["options"][:1])
    event = {"rows": [ROW_2, invalid_row]}

    response = handler_module.handler(event, None)

    assert "requestContext" not in event
    results = response["results"]
    assert len(results) == 2
    assert results[0]["id"] == "row-2"
    assert "error" not in results[0]
    assert results[1]["id"] == "row-invalid"
    assert "error" in results[1]
    assert "2-16" in results[1]["error"]


def test_handler_single_row_without_wrapper(monkeypatch):
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    stub_client = StubClient(make_body(top_logprobs, "A", -0.1))
    monkeypatch.setattr(handler_module, "_get_client", lambda: stub_client)
    monkeypatch.setenv("MODEL_ARN", "arn:model")
    monkeypatch.setenv("BEDROCK_REGION", "eu-central-1")

    response = handler_module.handler(ROW_2, None)

    assert response["results"][0]["id"] == "row-2"


def test_handler_wraps_http_response_for_api_gateway_events(monkeypatch):
    top_logprobs = [
        {"token": "A", "logprob": -0.1, "bytes": [65]},
        {"token": "B", "logprob": -2.5, "bytes": [66]},
    ]
    stub_client = StubClient(make_body(top_logprobs, "A", -0.1))
    monkeypatch.setattr(handler_module, "_get_client", lambda: stub_client)
    monkeypatch.setenv("MODEL_ARN", "arn:model")
    monkeypatch.setenv("BEDROCK_REGION", "eu-central-1")

    event = dict(ROW_2, requestContext={"http": {"method": "POST"}})
    response = handler_module.handler(event, None)

    assert response["statusCode"] == 200
    body = json.loads(response["body"])
    assert body["results"][0]["id"] == "row-2"


# --- raw completion path and dual logprobs shapes ---

class _CapturingClient:
    """Stub that records the request body and returns a canned completion response."""

    def __init__(self, payload):
        self.payload = payload
        self.body = None

    def invoke_model(self, *, modelId, body, contentType, accept):
        self.body = json.loads(body)
        return {"body": io.BytesIO(json.dumps(self.payload).encode())}


def _completion_payload(mapping):
    return {
        "choices": [{"index": 0, "text": "A",
                     "logprobs": {"top_logprobs": [mapping]},
                     "finish_reason": "stop"}],
        "usage": {"prompt_tokens": 51, "completion_tokens": 1, "total_tokens": 52},
    }


def _row():
    return {
        "id": "row-1",
        "state": {"service": "checkout", "stack_trace": "boom"},
        "question": "Bug or dependency?",
        "options": [{"id": "bug", "description": "Our code."},
                    {"id": "downstream", "description": "Their service."}],
    }


def test_completion_api_sends_rendered_prompt_not_messages():
    client = _CapturingClient(_completion_payload({"A": -0.2, "B": -1.7}))
    result = score(client, "arn:model", _row())
    assert "prompt" in client.body
    assert "messages" not in client.body
    assert client.body["prompt"].endswith("<|im_start|>assistant\n<think>\n\n</think>\n\n")
    assert client.body["max_tokens"] == 1
    assert result["option_ids"] == ["bug", "downstream"]
    assert result["probabilities"][0] > result["probabilities"][1]


def test_completion_prompt_hash_matches_rendered_prompt():
    client = _CapturingClient(_completion_payload({"A": -0.2, "B": -1.7}))
    result = score(client, "arn:model", _row())
    assert result["prompt_sha256"] == semif.digest(client.body["prompt"])


def test_chat_api_still_sends_messages():
    payload = {
        "choices": [{"index": 0, "message": {"role": "assistant", "content": "A"},
                     "logprobs": {"content": [{"token": "A", "logprob": -0.2, "top_logprobs": [
                         {"token": "A", "logprob": -0.2}, {"token": "B", "logprob": -1.7}]}]},
                     "finish_reason": "stop"}],
        "usage": {"prompt_tokens": 51, "completion_tokens": 1, "total_tokens": 52},
    }
    client = _CapturingClient(payload)
    score(client, "arn:model", _row(), api="chat")
    assert "messages" in client.body
    assert "prompt" not in client.body


def test_rejects_unknown_api():
    client = _CapturingClient(_completion_payload({"A": -0.2, "B": -1.7}))
    with pytest.raises(ValueError, match="api must be"):
        score(client, "arn:model", _row(), api="responses")


def test_missing_logprobs_raises_actionable_error():
    payload = {"choices": [{"index": 0, "text": "A", "logprobs": None, "finish_reason": "stop"}],
               "usage": {"prompt_tokens": 51, "completion_tokens": 1, "total_tokens": 52}}
    client = _CapturingClient(payload)
    with pytest.raises(ValueError, match="no logprobs"):
        score(client, "arn:model", _row())


def test_declared_mass_is_low_when_other_tokens_dominate():
    # Reasoning token holds most of the mass; the option letters are a thin tail.
    client = _CapturingClient(_completion_payload(
        {"<think>": math.log(0.94), "A": math.log(0.04), "B": math.log(0.02)}))
    result = score(client, "arn:model", _row(), constrain=False)
    assert result["declared_mass"] == pytest.approx(0.06, abs=1e-6)
    assert result["top_token"]["token"] == "<think>"
    # Renormalisation still reports a confident-looking split off that thin tail.
    assert result["probabilities"][0] == pytest.approx(2 / 3, abs=1e-6)
