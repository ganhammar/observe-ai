# pricing-demo

A Lambda function with one bug, used to test observe-ai end to end. `price_for` looks a customer's tier up in `TIERS`, which knows `basic` and `pro`, so any other tier raises `KeyError` from application code two frames below the handler.

Trigger it after deploying:

```
aws lambda invoke --function-name pricing-demo \
  --payload '{"tier":"enterprise"}' --cli-binary-format raw-in-base64-out /dev/stdout
```

This folder is published as its own repository by `demo/publish.sh`. The pipeline reads source through the GitHub Contents API of the repository named after the function, so the code has to live at `<org>/pricing-demo`, not in a subfolder of observe-ai.
