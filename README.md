# observe-ai

First-pass triage of application error logs. Given a log event with a stack trace, decide whether it indicates a defect in the service's own code or a failure in a dependency it calls, and return a probability rather than a label.

The decision is read from an open model's next-token log probabilities over declared options, not from generated text. A Lambda holds the request contract and Bedrock Custom Model Import runs the forward pass, so there is no GPU to provision and nothing to keep warm.

```
log event ──> Lambda ──> Bedrock (imported Qwen3) ──> logprobs over {A,B}
                 │                                         │
                 └────────── softmax, JSON out <───────────┘
```

## Status

The mechanism is verified against the Bedrock API. The **quality is not yet measured**, and that is the open question the eval harness exists to answer.

Two things have to hold for the probabilities to be worth anything, and neither is guaranteed by the plumbing:

1. The model must put most of its probability mass on the declared option letters. If it does not, renormalising over them is a ratio of two small numbers and the result is close to noise. `bedrock_backend.score` reports this as `declared_mass` for exactly this reason.
2. The probabilities must be calibrated before a threshold means anything.

The upstream work this borrows from reproduces an interface pattern using off-the-shelf open models, explicitly not a model trained for typed decisions. Treat published accuracy figures from that project as a starting point, not a prediction for this workload.

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
