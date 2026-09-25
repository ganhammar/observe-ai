# ObserveAi source layout

Folders mirror the pipeline stages in [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md). `Function.cs` and `ResponseDto.cs` stay at the root because they wire the stages together rather than belonging to one of them.

- `Ingest/`: unwraps the CloudWatch Logs envelope carried by the Kinesis batch. Serves the Ingest stage.
- `Identify/`: parses the stack trace, computes the fingerprint, and resolves the owning service and repository. Serves the Identify stage.
- `Decide/`: derives the numeric signals, runs the triage tree's model calls through the readout in `BedrockBackend.cs`, and combines them into P(bug) for the drop/hold/escalate gate. Serves the Triage and Route stages.
- `Remember/`: records seen fingerprints for deduplication and enforces the per-repo escalation rate cap, both through the one DynamoDB delegate in `Dynamo.cs`. Serves the Deduplicate stage and the cap check inside Escalate.
- `Escalate/`: fetches source at the frame paths, verifies it against the trace, drafts the root cause, and files the GitHub issue. `Escalation.cs` runs the stage and `GitHub.cs` is the one GitHub client. Serves the Escalate stage.

Every file is in `namespace ObserveAi;`. One deployable of about twenty files gains nothing from per-folder namespaces except using directives in every file and test.
