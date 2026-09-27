# observe-ai

Companion repository for the blog post [Running a System One Model of Your Own on Amazon Bedrock](https://www.ganhammar.se/posts/running-a-system-one-model-of-your-own-on-bedrock).

A log triage pipeline that reads every error log in an AWS account, asks a small open model on Bedrock whether the error is a defect in the service's own code, and files a GitHub issue with a root cause when it is. The decision is read from the model's next-token probabilities over lettered options rather than from generated text. The post covers how the readout works, what it took to get right, how the 4B compares with a purpose-trained model, and what it costs.

## Structure

```
.
├── .github/workflows/
│   ├── import-model.yml   # One-time Bedrock Custom Model Import, run manually
│   └── deploy.yml         # Ingest stack, app stack and GitHub token, on push to main
├── infra/                 # CloudFormation templates and the state machine definition
├── src/
│   ├── ObserveAi/         # The Lambda: parse, fingerprint, readout, escalate (C#, Native AOT)
│   └── observe_ai/        # Python reference for the readout, kept in parity with the C#
├── tests/                 # Unit and parity tests
├── demo/                  # A function with one planted bug, to exercise the whole path
├── eval/                  # The labelled rows and the runners for Bedrock and Jev
├── results/raw/           # The outputs and metrics behind every number in the post
└── docs/                  # ARCHITECTURE.md and FINDINGS.md
```

## Deploy

Everything except the model import deploys on push to `main`, and nothing is created by hand. [infra/README.md](infra/README.md) has the ordered setup, the stacks, the permissions and the cost model.

Prerequisites, once per account:

1. An IAM role trusted by GitHub OIDC, set as the repository variable `AWS_DEPLOY_ROLE_ARN`
2. A fine-grained GitHub token with Contents read and Issues write on the repositories the pipeline may file against, set as the repository secret `GH_ISSUES_TOKEN`
3. The repository variable `GITHUB_ORG`, the organisation whose repositories the log group names map to, and optionally `MODEL_NAME` if the imported model is not called `observe-ai`

Run **Import Model** once from the Actions tab, then push to `main` or run **Deploy**. [demo/README.md](demo/README.md) explains how to produce an error and watch it become an issue.

## Evaluation

```bash
python eval/run_bedrock.py --model-arn ...                              # single question
python eval/run_tree.py --model-arn ...                                 # ten questions, combined in code
TYPESAFE_API_KEY=... python eval/run_jev.py                             # the same rows against Jev
python eval/evaluate.py --labels eval/labels.json --results results.jsonl
```

The rows are synthetic and small, and the caveats that come with that are in the post and in [docs/FINDINGS.md](docs/FINDINGS.md).

## Attribution

Prompt construction and the option-slot readout are adapted from [SemIf](https://github.com/TheoLeeCJ/SemIf-OpenJev) (MIT). Prompt strings are kept byte-identical so `prompt_sha256` stays comparable with that project's published results.

Read more at [ganhammar.se](https://www.ganhammar.se).
