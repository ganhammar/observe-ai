# pricing-demo

A Lambda function with one bug, used to test observe-ai end to end. `price_for` looks up a customer's tier in `TIERS`, which knows `basic` and `pro`, so any other tier raises `KeyError` from application code two frames below the handler.

After deploying, trigger it with:

```
aws lambda invoke --function-name pricing-demo \
  --payload '{"tier":"enterprise"}' --cli-binary-format raw-in-base64-out /dev/stdout
```

`demo/publish.sh` publishes this folder as its own repository. The pipeline reads source through the GitHub Contents API from the repository named after the function, so the code has to live at `<org>/pricing-demo` rather than in a subfolder of observe-ai.
