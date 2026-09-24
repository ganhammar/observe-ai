# Findings

Measurements from 2026-09-24, on a 32 row synthetic fixture: 10 `clear_bug`, 10 `clear_downstream`, 12 `ambiguous`. Raw outputs and per-run metrics are in [../results/raw](../results/raw). Every figure here is reproducible from those files.

The question throughout: can an open model reading typed option logits decide whether an error log is a defect in our own code or a failure in something we call, well enough and cheaply enough to gate a triage pipeline.

## The fixture matters more than any model here

The clear bands are solvable by an exception-type lookup table. `SocketTimeoutException` is a dependency; `NullReferenceException` is ours. Any system scoring well there has demonstrated nothing.

The ambiguous band is the experiment. Twelve rows where the exception surface points one way and the cause points the other: a null reference caused by a dependency returning an empty body, a 400 from a dependency caused by our own arithmetic underflow, a connection pool exhausted by our own missing dispose. Each carries an `evidence` field holding the signals a log pipeline already has, stating facts and never naming a cause.

A first version of the fixture narrated the cause in a trailing `Context:` sentence. That version measured reading comprehension and was discarded. Anything that scores well on the clear bands and at chance on the ambiguous band has learned the lookup table.

## The readout mechanism works

Declared mass, the share of the model's next-token probability landing on the declared option letters before renormalisation, is **1.0000 at the median and 0.9996 at the minimum**.

This was the main risk. If the model spread its mass over other tokens, renormalising across the options would be a ratio of two small numbers dressed up as a probability. It does not. Reading a typed decision off the logits is sound.

## Single question: what scale buys, and what it does not

| model | ambiguous, evidence | ambiguous, no evidence | swing |
|---|---:|---:|---:|
| Qwen3-4B | 0.417 | 0.417 | **+0.000** |
| Qwen3-32B | 0.500 | 0.250 | +0.250 |
| Jev (hosted) | 0.667 | 0.333 | +0.333 |

The swing is the interesting column, because it is a within-model comparison: same model, same rows, one variable.

**The 4B does not read the evidence at all.** Identical band accuracy either way. On the five rows it misses most confidently it reports P(bug) = 0.0000 with and without the evidence present. It is not weighing the context and getting it wrong, it is not looking.

**Evidence integration appears somewhere between 4B and 32B.** The 32B behaves qualitatively like the hosted purpose-trained model: remove the context and it degrades sharply. That is a capability threshold, not a gradient.

**But integration is not accuracy.** The 32B lands at 50% on the ambiguous band, which is chance. Eight times the parameters bought 8.3 points.

## The flat readout produces labels, not probabilities

The 4B's output is saturated: 31 of 32 rows fall in the 0.00 to 0.10 or 0.90 to 1.00 bins, with exactly one in between. The 0.00 to 0.10 bin carries an observed bug rate of 27.8% against a mean predicted 0.0000. ECE is 0.210.

This matters more than the accuracy number. The argument for typed logit readout over a chat model is a calibrated probability you can threshold. A model that only answers 0 or 1 does not provide one, and five of the six missed bugs sit at exactly 0.0000, so no threshold recovers them.

## The model cannot compare two numbers

Tested directly, outside the tree, with the evidence in front of it:

| row | question | answer | truth |
|---|---|---|---|
| am-05 | Are connections acquired far more often than released? (2841 vs 12) | P(yes) = 1.000 | yes |
| am-07 | Is the refresh interval longer than the token lifetime? (3600 vs 900) | P(yes) = 0.000 | yes |
| am-12 | Is our request rate far above its own baseline? (8400 vs 210) | P(yes) = 0.000 | yes |

Rephrasing to state the comparison explicitly ("is X more than ten times Y") did not help. This is not a prompting problem.

## Decomposition beats scale

Replacing one causal question with seven grounded ones plus a combining rule in code:

| approach | clear_bug | clear_dn | ambiguous | overall | ECE |
|---|---:|---:|---:|---:|---:|
| 4B, one question | 1.000 | 1.000 | 0.417 | 0.781 | 0.210 |
| 4B, tree v1 | 0.800 | 0.800 | 0.500 | 0.688 | 0.168 |
| 4B, tree v2 | 0.800 | 0.800 | 0.833 | 0.812 | 0.186 |
| **4B, tree v3** | **1.000** | **1.000** | **0.833** | **0.938** | **0.102** |
| 32B, one question | 1.000 | 1.000 | 0.500 | 0.812 | — |
| Jev, one question | 1.000 | 0.900 | 0.667 | 0.844 | 0.095 |

Restructuring the question was worth **+41.6 points** on the ambiguous band. Multiplying parameters by eight was worth **+8.3**.

Each version fixed one thing, and the failures were more instructive than the successes:

**v1 to v2: move the arithmetic into code.** The tree still asked the model to compare numbers, which it cannot do. `derived.py` now walks the structured evidence, finds the relations worth naming, and states each as a sentence appended to the state. The model is asked what a stated comparison means rather than to compute it. Three rows flipped.

**v2 to v3: stop asking questions there is no evidence to answer.** Clear-band rows carry no evidence field, so the signal questions answered off the stack trace instead and invented confident results: `unreleased_resource` at 1.00 on the words "pool exhausted", `repeated_work` at 1.00 on a throttling retry message. Rows with no evidence now route to the flat question, which is perfect on exactly those rows.

**Also in v2: arbitrate opposing signals.** One row had two correct signals firing at 1.00 on opposite sides and cancelling to exactly 0.50. A value we sent only counts against us when the other side did not just change what it accepts.

## The division of labour that emerged

The model is reliable at "does this text have property P" and "do these two things mean the same". It is unreliable at arithmetic and multi-hop attribution. Give it only the first kind of work:

- **Model**: classify the failure surface, read whether an error names our value as invalid, judge whether two traces share a root cause, decide whether an external party is described as having changed.
- **Code**: every numeric comparison, the noisy-OR combination, the arbitration precedence, the routing.

Framed that way the model is a cheap semantic operator called several times per item, and deterministic code is the skeleton. That is a different architecture from "the model triages the log".

## Latency and operational notes

| | median | p95 |
|---|---:|---:|
| Qwen3-4B on Bedrock | 0.22s | 0.30s |
| Qwen3-32B on Bedrock | 0.16s | 0.24s |
| Jev (hosted, from eu-central-1) | 0.54s | 0.58s |

The 32B being faster than the 4B is presumably provisioning per model copy, not model efficiency.

Two things worth knowing before building on this:

- **Cold start scales badly with model size.** The 32B exhausted nine retries and still returned `ModelNotReadyException` on first invocation after import. Behind a scale-to-zero endpoint, the first request after idle is a failure, not a slow response. The 4B did not have this problem.
- **`instructSupported` is False** for both imported Qwen3 models, so Bedrock detects no chat template. Sending `messages` and relying on server-side templating is not safe; the prompt is rendered locally with reasoning suppressed instead.

## An API detail that silently destroys the readout

On the OpenAI Completions request shape, `logprobs` is an integer count. Sending `logprobs: true` is accepted and coerces to 1:

| request | candidates returned |
|---|---:|
| `logprobs: true, top_logprobs: 20` | 1 |
| `logprobs: 20` | 20 |

With one candidate there is no distribution, so every option but the argmax reads as missing and the probabilities are meaningless while still looking plausible. The Chat Completions shape genuinely does use the boolean plus a separate count, so the two paths need different parameters.

## What these numbers do not support

- **The fixture is synthetic and small.** Twelve ambiguous rows, written by the same process being tested.
- **The tree was iterated three times against those twelve rows.** Some of the 0.833 is fitted. A held-out set the design never saw is the honest test and has not been run.
- **The Jev comparison is not controlled.** Jev received the raw state through its own prompting; our path used a frozen prompt. It is a useful reference point, not a like-for-like benchmark. Read "competitive with" rather than "beats".
- **One domain.** Log triage over five runtimes. Nothing here generalises to other decision tasks without measurement.

The findings that survive those caveats best are the evidence swing table, because it is within-model, and the arithmetic limitation, because it was tested directly rather than inferred from aggregate scores.
