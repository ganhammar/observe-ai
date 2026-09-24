# observe-ai

First-pass triage of application error logs. Given a log event with a stack trace, decide whether it indicates a defect in the service's own code or a failure in a dependency it calls, and return a probability rather than a label.

The decision is read from an open model's next-token log probabilities over declared options, not from generated text. A Lambda holds the request contract and Bedrock Custom Model Import runs the forward pass, so there is no GPU to provision and nothing to keep warm.

```
log event ──> Lambda ──> Bedrock (imported Qwen3) ──> logprobs over {A,B}
                 │                                         │
                 └────────── softmax, JSON out <───────────┘
```

## Status

Measured against Qwen3-4B imported into Bedrock, on the 32 row fixture. Raw outputs and metrics are in [results/raw](results/raw).

**The readout mechanism works.** Declared mass is 1.0000 at the median and 0.9996 at the minimum, so the model puts essentially all of its next-token probability on the declared option letters. Reading a decision off the logits is sound.

**The model does not do the task.**

| Band | Rows | Accuracy | Balanced accuracy |
|---|---:|---:|---:|
| clear_downstream | 10 | 100% | 100% |
| clear_bug | 10 | 100% | 100% |
| ambiguous | 12 | 41.7% | 44.3% |

Perfect where an exception-type lookup table would also be perfect, and below chance where the answer requires relating the frames to the surrounding evidence. It classifies by which side of the network boundary an exception surfaced on, which is the correct answer only when surface and cause agree.

**The probabilities do not support a threshold.** 31 of 32 rows fall in the 0.00 to 0.10 or 0.90 to 1.00 bins, with exactly one in between. The 0.00 to 0.10 bin carries an observed bug rate of 27.8% against a mean predicted probability of 0.0000, and ECE is 0.21. Five of the six missed bugs sit at exactly 0.0000, so no threshold recovers them. Calibrated confidence was the main argument for this approach over a chat model, and this model does not provide it.

**Extra context does not help.** Running the same rows with the evidence field withheld gives identical band accuracy. Two rows flip and cancel each other out; the five confident misses read 0.0000 both ways.

The upstream project reproduces an interface pattern using off-the-shelf open models, explicitly not a model trained for typed decisions. These numbers are the size of that gap on this workload.

Full measurements, including the model comparison and what each iteration of the question tree fixed, are in [docs/FINDINGS.md](docs/FINDINGS.md).

### Why the prompt is rendered locally

Qwen3's packaged chat template ends a prompt at `<|im_start|>assistant\n`, which leaves the model free to open a reasoning block. The first sampled position would then hold the distribution over `<think>`, not over the answer letters, and every declared option would read as near-zero mass. Upstream avoids this by rendering with `enable_thinking=False`, which closes an empty reasoning block inside the prompt itself.

Sending `messages` and letting Bedrock apply the template server-side reintroduces the problem, so the default path renders the ChatML string in `semif.render_qwen3_prompt` and sends it as a raw prompt. That render is verified byte-identical to `apply_chat_template(enable_thinking=False)`, which also keeps `prompt_sha256` comparable with upstream.

Constrained decoding does not rescue the chat path, it conceals it: masking to the option letters still returns a confident-looking letter, drawn from a renormalised tail. This is the concrete reason `declared_mass` exists and the reason to run at least one pass with `--unconstrained`.

The `SEMIF_API` environment variable flips the deployed function between `completion` and `chat` without a code change, so the two can be compared on a live model.

## Evaluation

`eval/fixture.py` generates 32 synthetic labelled log events across .NET, Java, Python, Node and Go, in three bands:

| Band | Rows | What it tests |
|---|---:|---|
| `clear_downstream` | 10 | Floor check. An exception-type lookup table solves these. |
| `clear_bug` | 10 | Floor check, other direction. |
| `ambiguous` | 12 | The band that decides whether the model earns its place. The exception type points one way and the causal story points the other. |

The ambiguous band is the point of the fixture. A null reference caused by a dependency returning an empty body is labelled `downstream`; a 400 from a dependency caused by our own arithmetic underflow is labelled `bug`. Anything that scores well on the clear bands and at chance on the ambiguous band has learned the lookup table and nothing more.

Those rows carry an `evidence` field holding the signals a log pipeline already has: adjacent response metadata, dependency health, connection counts, recent deploys. The evidence states facts and never names a cause, so deciding still means relating the frames to the signals. Generate the fixture with `--no-evidence` to ask the harder question of whether the trace alone is enough:

```bash
python eval/fixture.py                  # ambiguous rows carry evidence
python eval/fixture.py --no-evidence    # trace only
```

Both variants are worth running. The gap between them is how much the surrounding context is worth, which is a separate question from whether the readout works at all.

```bash
python eval/fixture.py                       # regenerate logs.jsonl and labels.json
python eval/run_bedrock.py --model-arn ...   # score the fixture, writes results.jsonl
python eval/evaluate.py --labels eval/labels.json --results results.jsonl
```

Replace the synthetic fixture with real labelled traces as soon as any exist. Synthetic rows were written to be hard, but they were written by the same process that is being tested for bias, so they bound nothing.

## Deployment

See [infra/README.md](infra/README.md). Ordered setup, the one-time model import, the repository variables CI expects, and the cost model.

Two constraints worth knowing before planning around this:

- Bedrock Custom Model Import supports `Qwen3ForCausalLM` and `Qwen3MoeForCausalLM`. Qwen3.5 checkpoints do not import, so this targets Qwen3.
- Billing is per Custom Model Unit per minute while the model is active, in 5 minute windows, scaling to zero after 5 minutes idle. Continuous low-rate traffic never lets it idle out and costs far more than deliberate batching. Buffer and drain on a schedule rather than invoking per log line.

## Not included yet

Structural stack-trace fingerprinting and deduplication. In production that layer belongs upstream of this one and removes most of the volume, since a single repeating defect usually accounts for the bulk of error logs. Triage quality matters less than not calling the model 9,800 times for the same crash.

## Attribution

Prompt construction and the option-slot readout are adapted from [SemIf](https://github.com/TheoLeeCJ/SemIf-OpenJev) (MIT). Prompt strings are kept byte-identical so `prompt_sha256` stays comparable with that project's published results.
