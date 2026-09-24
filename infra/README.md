# infra

Deploys the observe-ai triage Lambda with AWS SAM. The function calls a Bedrock Custom Model Import model that must exist before the first deploy, so setup has a manual, one-time step ahead of anything CI does.

## First-time setup

1. Create an S3 bucket in `eu-central-1` to stage the model weights, for example `aws s3 mb s3://your-observe-ai-models --region eu-central-1`.
2. Create an IAM role that Bedrock can assume to read that bucket during import. Trust policy:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Principal": { "Service": "bedrock.amazonaws.com" },
         "Action": "sts:AssumeRole"
       }
     ]
   }
   ```

   Permissions policy, scoped to the bucket from step 1:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Action": ["s3:GetObject", "s3:ListBucket"],
         "Resource": [
           "arn:aws:s3:::your-observe-ai-models",
           "arn:aws:s3:::your-observe-ai-models/*"
         ]
       }
     ]
   }
   ```

3. Run `infra/import-model.sh --bucket your-observe-ai-models --role-arn <role ARN from step 2>`. It downloads the model, uploads it to S3, starts the Bedrock import job, waits for it to finish, and prints the resulting model ARN. Read the cost warning it prints before confirming; see [Cost](#cost) below.
4. Note the model ARN from step 3. It becomes the `ModelArn` stack parameter and the the imported model repository variable.
5. Create an IAM role for GitHub Actions to assume over OIDC, no long-lived access keys. If the account does not already have a `token.actions.githubusercontent.com` OIDC identity provider, create that first. Trust policy for the role:

   ```json
   {
     "Version": "2012-10-17",
     "Statement": [
       {
         "Effect": "Allow",
         "Principal": {
           "Federated": "arn:aws:iam::<ACCOUNT_ID>:oidc-provider/token.actions.githubusercontent.com"
         },
         "Action": "sts:AssumeRoleWithWebIdentity",
         "Condition": {
           "StringEquals": {
             "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
           },
           "StringLike": {
             "token.actions.githubusercontent.com:sub": "repo:<ORG>/<REPO>:environment:production"
           }
         }
       }
     ]
   }
   ```

   Attach a permissions policy that covers what `sam deploy` needs for this stack: CloudFormation stack operations, Lambda function management, IAM role creation for the function's own execution role, CloudWatch Logs, and S3 access to the SAM managed deployment bucket (`resolve_s3 = true` in `samconfig.toml` creates and uses one automatically).
6. In the GitHub repository, create an environment named `production` (Settings > Environments) and add required reviewers there if deploys should wait for approval. Then set the repository variables listed below.
7. Push to `main`, or run the `Deploy` workflow manually with `workflow_dispatch`.

## Repository variables

Set these under Settings > Secrets and variables > Actions > Variables. No secrets are needed since deploys authenticate with OIDC.

| Variable | Used by | Value |
|---|---|---|
| `AWS_DEPLOY_ROLE_ARN` | `.github/workflows/deploy.yml` | ARN of the OIDC role from setup step 5 |

## Cost

Amazon Bedrock Custom Model Import bills per Custom Model Unit (CMU) per minute while the imported model is active, plus a separate monthly storage charge per CMU regardless of activity. An active model scales down to zero CMUs after about 5 minutes with no invocations, and reactivates (with added latency) on the next call. Traffic patterns that keep pinging the model just often enough to prevent that scale-down will cost far more than batching calls and letting it idle out between batches. Check current Bedrock pricing for `eu-central-1` for exact per-CMU rates before importing a model.

Bedrock Custom Model Import only supports the `Qwen3ForCausalLM` and `Qwen3MoeForCausalLM` architectures for the Qwen3 family; Qwen3.5 checkpoints report a different architecture string and will not import. `infra/import-model.sh` defaults to `Qwen/Qwen3-4B`.
