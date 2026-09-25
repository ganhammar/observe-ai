# ObserveAi source layout

Folders mirror the pipeline stages in [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md). `Function.cs` and `ResponseDto.cs` stay at the root because they wire the stages together rather than belonging to one of them.

- `Ingest/`: unwraps the CloudWatch Logs envelope carried by the Kinesis batch. Serves the Ingest stage.
- `Identify/`: parses the stack trace, computes the fingerprint, and resolves the owning service and repository. Serves the Identify stage.
- `Decide/`: derives the numeric signals, runs the triage tree's model calls, and combines them into P(bug) for the drop/hold/escalate gate. Serves the Triage and Route stages.
- `Remember/`: records seen fingerprints for deduplication and enforces the per-repo escalation rate cap. Serves the Deduplicate stage and the cap check inside Escalate.
- `Escalate/`: fetches source at the frame paths, verifies it against the trace, drafts the root cause, and files or rolls up the GitHub issue. Serves the Escalate stage.

Every file stays in `namespace ObserveAi;` rather than a per-folder namespace: this is one deployable of about twenty files, and per-folder namespaces would add using directives to nearly every file and test while buying no real isolation.
