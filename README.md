# observe-ai

First-pass triage of application error logs. Given a log event with a stack trace, it decides whether the event comes from a defect in the service's own code or a failure in a dependency it calls, and returns a probability rather than a label.

The decision is read from an open model's next-token log probabilities over declared options, not from generated text. A Lambda holds the request contract and Bedrock Custom Model Import runs the forward pass, so there is no GPU to provision and nothing to keep warm.

```
log event ──> Lambda ──> Bedrock (imported Qwen3) ──> logprobs over {A,B}
                 │                                         │
                 └────────── softmax, JSON out <───────────┘
```

## Status

Measured against Qwen3-4B imported into Bedrock, on the 32 row fixture. Raw outputs and metrics are in [results/raw](results/raw).

Declared mass is 1.0000 at the median and 0.9996 at the minimum: nearly all next-token probability lands on the declared option letters, so reading a decision off the logits is sound.

Accuracy by band:

| Band | Rows | Accuracy | Balanced accuracy |
|---|---:|---:|---:|
| clear_downstream | 10 | 100% | 100% |
| clear_bug | 10 | 100% | 100% |
| ambiguous | 12 | 41.7% | 44.3% |

The ambiguous band, where the answer requires relating the frames to the evidence, is below chance. The model classifies by which side of the network boundary an exception surfaced on, which is correct only when surface and cause agree.

31 of 32 rows fall in the 0.00 to 0.10 or 0.90 to 1.00 bins, with one in between. The 0.00 to 0.10 bin has an observed bug rate of 27.8% against a mean predicted probability of 0.0000, and ECE is 0.21. Five of the six missed bugs sit at 0.0000, so no threshold recovers them. Calibrated confidence was the main argument for this approach over a chat model, and this model does not provide it.

Withholding the evidence field gives identical band accuracy. Two rows flip in opposite directions, and the five confident misses read 0.0000 both ways.

The upstream project reproduces an interface pattern using off-the-shelf open models, not a model trained for typed decisions. These numbers measure that gap on this workload.

Full measurements, including the model comparison and the question tree iterations, are in [docs/FINDINGS.md](docs/FINDINGS.md).

### Why the prompt is rendered locally

Qwen3's packaged chat template ends a prompt at `<|im_start|>assistant\n`, which leaves the model free to open a reasoning block, so the first sampled position holds the distribution over `<think>` and every declared option reads as near-zero mass. Upstream renders with `enable_thinking=False`, which closes an empty reasoning block inside the prompt.

Letting Bedrock apply the template server-side to `messages` reintroduces the problem, so the default path renders the ChatML string in `semif.render_qwen3_prompt` and sends it as a raw prompt. It is verified byte-identical to `apply_chat_template(enable_thinking=False)`, which keeps `prompt_sha256` comparable with upstream.

Constrained decoding hides the chat path's failure: masking to the option letters still returns a confident-looking letter, drawn from a renormalised tail. `declared_mass` exists to detect this, and at least one pass should run with `--unconstrained`.

`SEMIF_API` switches the deployed function between `completion` and `chat` without a code change, so the two can be compared on a live model.

## Evaluation

`eval/fixture.py` generates 32 synthetic labelled log events across .NET, Java, Python, Node and Go, in three bands:

| Band | Rows | What it tests |
|---|---:|---|
| `clear_downstream` | 10 | Floor check. An exception-type lookup table solves these. |
| `clear_bug` | 10 | Floor check, other direction. |
| `ambiguous` | 12 | The exception type points one way and the causal story the other. |

In the ambiguous band, a null reference caused by a dependency returning an empty body is labelled `downstream`, and a 400 from a dependency caused by our own arithmetic underflow is labelled `bug`. Scoring well on the clear bands and at chance on the ambiguous band means the model has learned the lookup table and nothing more.

Ambiguous rows carry an `evidence` field with the signals a log pipeline already has: adjacent response metadata, dependency health, connection counts, recent deploys. The evidence states facts and never names a cause, so the model still has to relate the frames to the signals. `--no-evidence` generates a trace-only fixture:

```bash
python eval/fixture.py                  # ambiguous rows carry evidence
python eval/fixture.py --no-evidence    # trace only
```

The gap between the variants measures what the context is worth, separately from whether the readout works.

```bash
python eval/fixture.py                       # regenerate logs.jsonl and labels.json
python eval/run_bedrock.py --model-arn ...   # score the fixture, writes results.jsonl
python eval/evaluate.py --labels eval/labels.json --results results.jsonl
```

Replace the synthetic fixture with real labelled traces as soon as any exist. The synthetic rows were written to be hard, but by the same process that is being tested for bias, so they bound nothing.

## Deployment

See [infra/README.md](infra/README.md) for ordered setup, the one-time model import, the repository variables CI expects, and the cost model.

Two constraints affect planning:

- Bedrock Custom Model Import supports `Qwen3ForCausalLM` and `Qwen3MoeForCausalLM`. Qwen3.5 checkpoints do not import, so this targets Qwen3.
- Billing is per Custom Model Unit per minute while the model is active, in 5 minute windows, scaling to zero after 5 minutes idle. Continuous low-rate traffic never idles out and costs far more than batching, so buffer and drain on a schedule rather than invoking per log line.

## Not included yet

- **Evidence gathering.** The tree's 0.833 on the ambiguous band needs the evidence field the fixture carries by hand. Nothing in the pipeline produces it, so a real log is triaged by the flat question at 0.417. See the last sections of [docs/FINDINGS.md](docs/FINDINGS.md).
- **Commit resolution.** Escalation reads `main`. The verification step reports when the checkout does not match the trace, but nothing yet resolves which commit was running.
- **The held-for-review path.** A verdict between the thresholds stops the execution with a reason, and nothing picks it up.

## Attribution

Prompt construction and the option-slot readout are adapted from [SemIf](https://github.com/TheoLeeCJ/SemIf-OpenJev) (MIT). Prompt strings are kept byte-identical so `prompt_sha256` stays comparable with that project's published results.
