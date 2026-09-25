# Demo

`pricing-demo/` is a Lambda function with one planted bug, published as its own repository so the pipeline fetches its source as it would for any real service.

Publish the repository before producing the error. Escalation fetches source from GitHub, so an error that arrives first ends in an issue with no root cause.

1. Create `<org>/pricing-demo` on GitHub (private is fine; the token reads it), then run `demo/publish.sh` to push this folder to it.
2. Produce the error in one of two ways:
   - `demo/emit-error.sh` writes one event in the Python runtime's log shape into `/aws/lambda/pricing-demo`, without deploying anything.
   - Deploy `pricing-demo/` with `sam deploy --guided` and invoke it with `{"tier": "enterprise"}` to run the real path end to end.
3. Watch the `observe-ai-TriagePipeline` execution, then the repository's issues.

The fingerprint is the exception type plus the in-app frames, so for thirty days every later occurrence only increments the counter, whatever the tier. To file again, delete the fingerprint's row from the seen table.
