# Demo

`pricing-demo/` is a Lambda function with one deliberate bug, published as its own repository so the pipeline can fetch its source the way it would for any real service.

Order matters: publish the repository first, then produce the error. Escalation fetches source from GitHub, so an error that arrives before the repository exists ends in an issue with no root cause.

1. Create `<org>/pricing-demo` on GitHub, then `demo/publish.sh` to push this folder to it.
2. Produce the error, either way:
   - `demo/emit-error.sh` writes one event in the Python runtime's exact log shape into `/aws/lambda/pricing-demo`. Nothing is deployed.
   - Or deploy `pricing-demo/` with `sam deploy --guided` and invoke it with `{"tier": "enterprise"}`, which is the real thing end to end.
3. Watch the `observe-ai-TriagePipeline` execution, then the repository's issues.

Every later occurrence only increments the counter for thirty days, whatever the tier, since the fingerprint is the exception type plus the in-app frames. To file again, delete the fingerprint's row from the seen table.
