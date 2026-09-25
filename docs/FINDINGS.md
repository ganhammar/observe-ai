# Findings

Measurements from 2026-09-24, on a 32 row synthetic fixture: 10 `clear_bug`, 10 `clear_downstream`, 12 `ambiguous`. The label has since been renamed `external` and widened to cover the platform the service runs on; the section on widening below has the measurements for that, and the raw files from this date keep the earlier names. Raw outputs and per-run metrics are in [../results/raw](../results/raw), and every figure here is reproducible from them.

The question throughout is whether an open model reading typed option logits can decide if an error log is a defect in our own code or a failure in something we call, well enough and cheaply enough to gate a triage pipeline.

## The fixture

The clear bands are solvable by an exception-type lookup table: `SocketTimeoutException` is a dependency, `NullReferenceException` is ours. Scoring well there demonstrates nothing.

The ambiguous band has twelve rows where the exception surface points one way and the cause the other: a null reference caused by a dependency returning an empty body, a 400 from a dependency caused by our own arithmetic underflow, a connection pool exhausted by our own missing dispose. Each carries an `evidence` field with the signals a log pipeline already has, stating facts and never naming a cause.

A first version of the fixture narrated the cause in a trailing `Context:` sentence. It measured reading comprehension and was discarded. A system that scores well on the clear bands and at chance on the ambiguous band has learned the lookup table.

## Declared mass

Declared mass, the share of the model's next-token probability landing on the declared option letters before renormalisation, is 1.0000 at the median and 0.9996 at the minimum.

This was the main risk: had the model spread its mass over other tokens, renormalising across the options would give a ratio of two small numbers presented as a probability. It does not, so reading a typed decision off the logits is sound.

## Evidence swing by model

| model | ambiguous, evidence | ambiguous, no evidence | swing |
|---|---:|---:|---:|
| Qwen3-4B | 0.417 | 0.417 | +0.000 |
| Qwen3-32B | 0.500 | 0.250 | +0.250 |
| Jev (hosted) | 0.667 | 0.333 | +0.333 |

Single question throughout. The swing is a within-model comparison: same model, same rows, one variable.

The 4B does not use the evidence. On the five rows it misses most confidently it reports P(bug) = 0.0000 with and without it.

Evidence integration appears somewhere between 4B and 32B. Like the hosted purpose-trained model, the 32B degrades sharply when the context is removed, which is a capability threshold rather than a gradient.

Integration does not bring accuracy with it: the 32B's 50% on the ambiguous band is chance, and eight times the parameters bought 8.3 points.

## Portability to Mistral-7B

Qwen3 was chosen because it was on Bedrock Custom Model Import's architecture list, not for any measured property. Testing Mistral-7B-Instruct-v0.3 on the same fixture shows the choice mattered more than expected.

Rendered verbatim through Mistral's own chat template, the model puts no probability on the declared option letters. Its top token is `**` at probability 0.0009, the start of a formatted answer rather than a letter.

| prompt | declared mass | top token |
|---|---:|---|
| Mistral's own template | 0.0000 | `**` |
| with a leading BOS | 0.0000 | `The` |
| with a trailing space | 0.155 | the answer letter |
| with a `" The answer is "` lead-in | 0.24 average | the answer letter |
| **Qwen3-4B, no lead-in** | **1.0000** | the answer letter |

A trailing space moves the model from never producing a letter to the letter being its most likely token, and the best lead-in found still reaches about a quarter of Qwen's mass.

With the lead-in prompt, on the full fixture:

| | Qwen3-4B | Mistral-7B |
|---|---:|---:|
| declared mass, median | 1.0000 | 0.19 |
| rows missing an option letter | 0 of 32 | 27 of 32 |
| clear_downstream | 1.000 | 1.000 |
| clear_bug | 1.000 | 0.700 |
| ambiguous | 0.417 | 0.417 |
| overall | 0.781 | 0.688 |

Low declared mass degrades the result where the lexical cue is weakest. Mistral holds `clear_downstream`, where a connection refused is unmistakable, and loses 30 points on `clear_bug`. The ambiguous band is unchanged only because both models already sit at chance there.

The requirement is that the model emits a bare option letter as its first token. That is a property of instruction tuning rather than capability or size, and no benchmark score shows it. The comparison gives Mistral a tuned lead-in that Qwen does not need, so it compares each model at its best effort rather than on an identical prompt.

Declared mass is the diagnostic for this. Mistral answers the first row correctly, `downstream` at probability 1.0, while holding 0.00004 of the distribution. Without the diagnostic, a model swap degrades results behind answers that still look confident.

## Calibration of the single question

The 4B's single-question output is saturated: 31 of 32 rows fall in the 0.00 to 0.10 or 0.90 to 1.00 bins, with one in between. The 0.00 to 0.10 bin has an observed bug rate of 27.8% against a mean predicted 0.0000. ECE is 0.210.

The argument for typed logit readout over a chat model is a calibrated probability that can be thresholded. A model that only answers 0 or 1 does not provide one, and five of the six missed bugs sit at 0.0000, so no threshold recovers them.

## Numeric comparison

Tested directly, outside the tree, with the evidence in front of it:

| row | question | answer | truth |
|---|---|---|---|
| am-05 | Are connections acquired far more often than released? (2841 vs 12) | P(yes) = 1.000 | yes |
| am-07 | Is the refresh interval longer than the token lifetime? (3600 vs 900) | P(yes) = 0.000 | yes |
| am-12 | Is our request rate far above its own baseline? (8400 vs 210) | P(yes) = 0.000 | yes |

Rephrasing to state the comparison explicitly ("is X more than ten times Y") did not help, so the limitation is not in the prompt wording.

## Question tree against scale

Replacing one causal question with seven grounded ones (eight since the platform signal was added) plus a combining rule in code:

| approach | clear_bug | clear_dn | ambiguous | overall | ECE |
|---|---:|---:|---:|---:|---:|
| 4B, one question | 1.000 | 1.000 | 0.417 | 0.781 | 0.210 |
| 4B, tree v1 | 0.800 | 0.800 | 0.500 | 0.688 | 0.168 |
| 4B, tree v2 | 0.800 | 0.800 | 0.833 | 0.812 | 0.186 |
| **4B, tree v3** | **1.000** | **1.000** | **0.833** | **0.938** | **0.102** |
| 32B, one question | 1.000 | 1.000 | 0.500 | 0.812 | n/a |
| Jev, one question | 1.000 | 0.900 | 0.667 | 0.844 | 0.095 |

On the ambiguous band, restructuring the question was worth +41.6 points and multiplying parameters by eight was worth +8.3. Each version fixed one thing:

**v1 to v2: move the arithmetic into code.** v1 still asked the model to compare numbers. `derived.py` walks the structured evidence, finds the relations worth naming, and appends each to the state as a sentence, so the model is asked what a stated comparison means rather than to compute it. Three rows flipped.

**v2 to v3: skip questions with no evidence to answer them.** Clear-band rows carry no evidence field, so the signal questions answered off the stack trace and invented confident results: `unreleased_resource` at 1.00 on the words "pool exhausted", `repeated_work` at 1.00 on a throttling retry message. Rows with no evidence now route to the flat question, which is perfect on those rows.

**Also in v2: arbitrate opposing signals.** One row had two correct signals firing at 1.00 on opposite sides and cancelling to 0.50. The rule: a value we sent counts against us only when the other side did not just change what it accepts.

## Widening the decision to code versus external

Measured 2026-09-25 after the second option was renamed from `downstream` to `external` and widened to cover the platform the service runs on, with an eighth signal (`platform_intervention`) and six new ambiguous rows: a memory limit halved by ops, a heap leak after a deploy, a disk filled by the host's log agent, a hypervisor network reset, a service that broke its own header contract, and a gateway that stopped validating a caller's payload. Raw outputs are the `*-v2-*` files.

| run | clear_bug | clear_external | ambiguous, original 12 | ambiguous, new 6 | overall | ECE |
|---|---:|---:|---:|---:|---:|---:|
| **tree, evidence** | **10/10** | **10/10** | **10/12** | **5/6** | **92.1%** | **0.080** |
| tree, no evidence | 10/10 | 10/10 | 4/12 | 4/6 | 73.7% | 0.260 |
| flat, evidence | 10/10 | 10/10 | 6/12 | 5/6 | 81.6% | 0.182 |
| flat, no evidence | 10/10 | 10/10 | 4/12 | 4/6 | 73.7% | 0.260 |

The original twelve hold at 10/12 with the same two misses as before. Of the six new rows the tree gets five, and the miss (am-17, a header contract the service itself broke by deploy) is the model answering `external_change: yes` to the service's own deploy despite the derived sentence saying the deploy was of this service, one minute before the errors.

Three things the first run of the widened tree got wrong, all fixed in code rather than in the model:

- **The baseline wording decides the trace-only path.** A first version named "a caller violating the documented contract" as external, and the flat readout then sent `KeyError` and `Sequence contains no elements` rows to external, including the demo service's own `KeyError` in production. Production runs the trace-only path, so that wording cost more than it bought. The caller clause came out; a bug now explicitly includes input the service failed to validate or handle, and the gateway case is carried by `external_change`.
- **A deploy of the service itself read as an external change.** `derived` now states whether the last deploy described is of this service and how long before the errors it happened, and says nothing when the deploy is undated against the error, since a first version that named a month-old deploy flipped am-04 the other way.
- **A limit lowered by the platform read as an internal inconsistency** and cancelled the platform signal to 0.49. `internal_inconsistency` is now dampened by `platform_intervention`, the same arbitration `invalid_value_sent` already has against `external_change`.

Rows still at 0.50 (am-06, am-15, and three of the original bug rows) are cancellations: one correct signal on each side with nothing to arbitrate between them. That is the tree's remaining failure shape, and a threshold cannot fix it.

## Division of labour

The model is reliable at "does this text have property P" and "do these two things mean the same", and unreliable at arithmetic and multi-hop attribution. The work splits accordingly:

- **Model**: classify the failure surface, read whether an error names our value as invalid, judge whether two traces share a root cause, decide whether an external party is described as having changed.
- **Code**: every numeric comparison, the noisy-OR combination, the arbitration precedence, the routing.

The model is then a cheap semantic operator called several times per item inside deterministic code, which is a different architecture from one where the model triages the log.

## Latency and operational notes

| | median | p95 |
|---|---:|---:|
| Qwen3-4B on Bedrock | 0.22s | 0.30s |
| Qwen3-32B on Bedrock | 0.16s | 0.24s |
| Jev (hosted, from eu-central-1) | 0.54s | 0.58s |

The 32B being faster than the 4B presumably reflects provisioning per model copy rather than model efficiency.

- Cold start gets worse with model size across all three models. First invocation after import: Qwen3-4B at 8 GB succeeded in about 36 seconds; Mistral-7B at 14.5 GB exhausted nine retries over 68 seconds; Qwen3-32B at 65 GB exhausted nine retries. Above roughly 8 GB the first request after a scale-to-zero fails rather than responding slowly, so a caller has to retry rather than wait.
- `instructSupported` is False for both imported Qwen3 models, so Bedrock detects no chat template. Relying on server-side templating of `messages` is not safe, and the prompt is rendered locally with reasoning suppressed.

## `logprobs` on the Completions request shape

On the OpenAI Completions request shape, `logprobs` is an integer count. Sending `logprobs: true` is accepted and coerces to 1:

| request | candidates returned |
|---|---:|
| `logprobs: true, top_logprobs: 20` | 1 |
| `logprobs: 20` | 20 |

With one candidate there is no distribution, so every option but the argmax reads as missing and the probabilities are meaningless while still looking plausible. The Chat Completions shape does use the boolean plus a separate count, so the two paths need different parameters.

## Evidence in production

The tree's 0.833 on the ambiguous band depends on the `evidence` field (connection counts, token lifetimes, request rates against their own baseline), which was authored by hand for the fixture. Nothing in the pipeline produces it. A CloudWatch log event carries a message and a log group, so the deployed triage sees only a stack trace, the evidence check is false, and the verdict falls back to the single baseline question, the 0.417 path.

Evidence gathering has not been started and is not small: it means correlating a trace with metrics, recent deploys, dependency health and the service's own history at the moment the error fired. Until it exists, the decomposition is a measured result about what the approach can do given evidence, not a capability this system currently has.

## What these numbers do not support

- The fixture is synthetic and small: twelve ambiguous rows, written by the same process being tested.
- The tree was iterated three times against those twelve rows, so some of the 0.833 is fitted. The proper test is a held-out set the design never saw, and it has not been run.
- The Jev comparison is not controlled. Jev received the raw state through its own prompting; our path used a frozen prompt. It is a reference point, not a like-for-like benchmark. Read "competitive with" rather than "beats".
- One domain: log triage over five runtimes. Nothing here generalises to other decision tasks without measurement.

The findings that survive those caveats best are the evidence swing table, because it is within-model, and the arithmetic limitation, because it was tested directly rather than inferred from aggregate scores.
