# Deploying observe-ai

Everything except the model import runs on push to `main`. Nothing is created by hand.

## First time

1. Create an IAM role in the target account trusted by GitHub OIDC, and set it as the repository variable `AWS_DEPLOY_ROLE_ARN`. The subject to trust is:

   ```
   repo:<owner>@<owner_id>/<repo>@<repo_id>:environment:production
   ```

   The environment form is required because both workflows declare `environment: production`. The `production` environment is restricted to the `main` branch, so no other ref can mint that claim.

2. Create a fine-grained GitHub token with Contents read and Issues write on the repositories the pipeline may file against, and set it as the repository secret `GH_ISSUES_TOKEN`. Deploy writes it into the stack's secret; without it the pipeline triages but cannot read source or file.

3. Run the **Import Model** workflow from the Actions tab. It deploys the bootstrap stack, downloads the weights, stages them, starts the Bedrock import job and polls it to completion.

4. Push to `main`, or run **Deploy**.

## Workflows

| Workflow | Trigger | Does |
|---|---|---|
| PR | pull_request | Tests |
| Import Model | manual | Bootstrap stack, then the one-time model import |
| Deploy | push to `main`, manual | Ingest stack, then resolves the model by name, then the app stack, then stores the GitHub token |

Import Model is manual because it downloads several GB and produces a resource that bills monthly. Re-running it is safe: it exits early when a model of that name already exists.

Deploy looks the model up by name at deploy time rather than reading a stored ARN, so the stack cannot point at a model that no longer exists. With no model imported it fails with a message saying to run Import Model first.

## Stacks

| Stack | Template | Holds |
|---|---|---|
| `observe-ai-bootstrap` | `infra/bootstrap.yaml` | Staging bucket, Bedrock import role |
| `observe-ai-ingest` | `infra/ingest.yaml` | Kinesis stream, delivery role, account capture policy |
| `observe-ai` | `infra/template.yaml` | The triage Lambda, its tables and IAM |

They are separate because their lifecycles differ. The bootstrap has to exist before an imported model does, and the app stack cannot deploy until that model exists. Ingestion is account-wide and rarely changes, so it deploys ahead of the app stack in the same workflow and is a no-op when unchanged. The imported model itself sits between them and is not a stack resource: AWS does not support Custom Model Import in CloudFormation.

Staged weights expire after 7 days by a lifecycle rule. Bedrock copies them during import, so they are only needed while a job runs.

## Permissions

The deploy role is assumed by both workflows and needs, beyond ordinary CloudFormation and Lambda deployment rights:

| Action | Used by |
|---|---|
| `bedrock:ListImportedModels` | Deploy, Import Model |
| `kinesis:*`, `logs:PutAccountPolicy`, `logs:DescribeAccountPolicies`, `logs:DeleteAccountPolicy`, `iam:*Role*` on the delivery role | Deploy, through CloudFormation for the ingest stack |
| `secretsmanager:CreateSecret`, `GetSecretValue`, `PutSecretValue` on `observe-ai/github-token` | Deploy |
| `bedrock:CreateModelImportJob` | Import Model |
| `bedrock:GetModelImportJob` | Import Model |
| `s3:PutObject`, `s3:ListBucket` on the staging bucket | Import Model |
| `iam:PassRole` for the Bedrock import role | Import Model |

Scope `iam:PassRole` to the import role rather than granting it broadly, since passing arbitrary roles is an escalation path.

## Cost

The import job is not charged. What costs money:

- Model storage, per Custom Model Unit per month, from import until the model is deleted.
- Inference, per CMU per minute while a model copy is active, billed in 5 minute windows from the first successful call, scaling to zero after 5 minutes idle.
- S3 for staged weights until the lifecycle rule expires them.

Check current Bedrock pricing for the region; the CMU count for a given model is set by Bedrock at import and readable from `GetImportedModel`.
