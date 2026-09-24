#!/usr/bin/env bash
#
# import-model.sh - gets a Qwen3 model into Amazon Bedrock Custom Model
# Import so infra/template.yaml has a ModelArn to deploy against. The
# Import Model GitHub Actions workflow runs this after deploying
# infra/bootstrap.yaml, passing the staging bucket and Bedrock import role
# from that stack's outputs as --bucket and --role-arn. It can also be run
# by hand against an existing bucket and role.
#
# Verified constraint: Bedrock Custom Model Import supports only the
# Qwen3ForCausalLM and Qwen3MoeForCausalLM architectures for the Qwen3
# family. Qwen3.5 checkpoints report a different architecture string and
# will NOT import. Use a Qwen3 (dense or MoE) checkpoint, such as the
# default below.
#
# What this does, in order:
#   1. Downloads the model weights from HuggingFace with the hf CLI.
#   2. Syncs the weights to an S3 bucket/prefix that Bedrock reads from.
#   3. Calls `aws bedrock create-model-import-job` to start the import.
#   4. Polls `aws bedrock get-model-import-job` until the job reaches a
#      terminal state, then prints the resulting model ARN.
#
# Idempotency: the HuggingFace download resumes/skips files it already
# has, `aws s3 sync` only uploads changed objects, and if an import job
# with the target name is already in progress or already completed, this
# script reuses it instead of starting a duplicate. A previously failed
# job is not reused; this script starts a new one with a timestamp suffix
# and leaves the failed job in place for inspection.
#
# Prerequisites: an S3 bucket to stage weights in, and an IAM role Bedrock
# assumes to read them (trust policy for the bedrock.amazonaws.com service
# principal, s3:GetObject / s3:ListBucket on the bucket). The Import Model
# workflow creates both by deploying infra/bootstrap.yaml and passes their
# names through; running this by hand needs an existing bucket and role
# instead, see infra/bootstrap.yaml for the exact policy documents.
#
# Usage:
#   ./import-model.sh --bucket my-bucket --role-arn arn:aws:iam::123456789012:role/BedrockImportRole [options]
#
# Options (each also readable from an environment variable; a flag
# overrides the matching environment variable):
#   --model-id ID     HuggingFace model id to import.
#                      (env MODEL_ID, default: Qwen/Qwen3-4B)
#   --bucket NAME      S3 bucket to stage weights in. Required.
#                      (env S3_BUCKET)
#   --prefix PREFIX    S3 key prefix under the bucket.
#                      (env S3_PREFIX, default: derived from --model-id)
#   --region REGION    AWS region for S3 and Bedrock.
#                      (env AWS_REGION, default: eu-central-1)
#   --role-arn ARN     IAM role ARN Bedrock assumes to read the S3 weights.
#                      Required. (env IMPORT_ROLE_ARN)
#   --job-name NAME    Import job / imported model name.
#                      (env IMPORT_JOB_NAME, default: derived from --model-id)
#   --dry-run          Report whether the model already exists, then stop.
#   -h, --help         Show this help and exit.
#
# ------------------------------------------------------------------------
# COST WARNING: Amazon Bedrock Custom Model Import bills per Custom Model
# Unit (CMU) per minute while your imported model copy is active (serving
# or having recently served traffic), and separately per CMU per month for
# storing the imported model, whether or not it is ever invoked. An active
# copy scales down to zero CMUs after about 5 minutes with no invocations,
# but reactivates (with added latency) on the next call. Check current
# Bedrock pricing for the region you pass before proceeding; this script
# does not estimate cost for you. Importing itself is not charged; the
# storage charge begins once the model exists and runs until it is deleted.
# ------------------------------------------------------------------------

set -euo pipefail

MODEL_ID="${MODEL_ID:-Qwen/Qwen3-4B}"
S3_BUCKET="${S3_BUCKET:-}"
S3_PREFIX="${S3_PREFIX:-}"
REGION="${AWS_REGION:-eu-central-1}"
IMPORT_ROLE_ARN="${IMPORT_ROLE_ARN:-}"
IMPORT_JOB_NAME="${IMPORT_JOB_NAME:-}"
DRY_RUN="false"

log() {
  printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2
}

usage() {
  sed -n '2,66p' "$0" | sed 's/^# \{0,1\}//'
}

sanitize() {
  # Lowercases and replaces anything that is not [a-z0-9-] with '-', for
  # use as an S3 prefix or a Bedrock job/model name.
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9-' '-' | sed 's/-\+/-/g; s/^-//; s/-$//'
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --model-id)
      MODEL_ID="$2"; shift 2 ;;
    --bucket)
      S3_BUCKET="$2"; shift 2 ;;
    --prefix)
      S3_PREFIX="$2"; shift 2 ;;
    --region)
      REGION="$2"; shift 2 ;;
    --role-arn)
      IMPORT_ROLE_ARN="$2"; shift 2 ;;
    --job-name)
      IMPORT_JOB_NAME="$2"; shift 2 ;;
    --dry-run)
      DRY_RUN="true"; shift ;;
    -h|--help)
      usage; exit 0 ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1 ;;
  esac
done

if [[ -z "$S3_BUCKET" ]]; then
  echo "Error: --bucket (or S3_BUCKET) is required." >&2
  exit 1
fi
if [[ -z "$IMPORT_ROLE_ARN" ]]; then
  echo "Error: --role-arn (or IMPORT_ROLE_ARN) is required. This must be an" >&2
  echo "IAM role that trusts bedrock.amazonaws.com and can read the S3" >&2
  echo "bucket. See infra/README.md for the trust and permissions policy." >&2
  exit 1
fi

SANITIZED_MODEL_ID="$(sanitize "$MODEL_ID")"
S3_PREFIX="${S3_PREFIX:-models/${SANITIZED_MODEL_ID}}"
IMPORT_JOB_NAME="${IMPORT_JOB_NAME:-${SANITIZED_MODEL_ID}-import}"

for cmd in hf aws; do
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Error: required command '$cmd' was not found on PATH." >&2
    exit 1
  fi
done

cat >&2 <<EOF

================================================================================
 This will download '$MODEL_ID', upload it to
   s3://${S3_BUCKET}/${S3_PREFIX}/
 in region ${REGION}, and start (or reuse) a Bedrock Custom Model Import job
 named '${IMPORT_JOB_NAME}'.

 Amazon Bedrock Custom Model Import bills per Custom Model Unit (CMU) per
 minute while the resulting model is active, plus a monthly storage charge
 per CMU. Check current Bedrock pricing for ${REGION} before continuing.
================================================================================

EOF

# An existing imported model of this name is the finished product, so report
# it and stop. Creating a second one would leave both in the account, each
# carrying its own monthly per-CMU storage charge, and only one of them would
# be wired into the stack.
# A failed lookup is not the same answer as an empty one. Swallowing an
# AccessDenied here would read as "no model exists" and import a second
# billable copy, so the failure stops the script instead.
if ! LIST_OUTPUT="$(aws bedrock list-imported-models \
  --region "$REGION" \
  --name-contains "$IMPORT_JOB_NAME" \
  --query "modelSummaries[?modelName=='${IMPORT_JOB_NAME}'].modelArn | [0]" \
  --output text 2>&1)"; then
  echo "Could not list imported models in $REGION, so it is not safe to" >&2
  echo "assume none exists. Fix the error below and re-run." >&2
  echo "$LIST_OUTPUT" >&2
  exit 1
fi
EXISTING_MODEL_ARN="$LIST_OUTPUT"
if [[ -n "$EXISTING_MODEL_ARN" && "$EXISTING_MODEL_ARN" != "None" ]]; then
  log "Imported model '$IMPORT_JOB_NAME' already exists, nothing to do."
  echo "$EXISTING_MODEL_ARN"
  exit 0
fi

if [[ "$DRY_RUN" == "true" ]]; then
  log "Dry run: no model exists yet; a real run would download, stage and import."
  exit 0
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
LOCAL_MODEL_DIR="${WORKDIR}/${SANITIZED_MODEL_ID}"

log "Downloading $MODEL_ID to $LOCAL_MODEL_DIR ..."
hf download "$MODEL_ID" --local-dir "$LOCAL_MODEL_DIR"

S3_URI="s3://${S3_BUCKET}/${S3_PREFIX}/"
log "Syncing weights to $S3_URI ..."
# Bedrock expects config.json and the weights at the prefix root. The hf
# metadata cache is not part of the checkpoint, and --delete keeps a
# retry from leaving files behind from an earlier attempt.
aws s3 sync "$LOCAL_MODEL_DIR" "$S3_URI" --region "$REGION" \
  --exclude ".cache/*" --delete

get_status() {
  aws bedrock get-model-import-job \
    --region "$REGION" \
    --job-identifier "$1" \
    --query 'status' \
    --output text 2>/dev/null || true
}

get_field() {
  aws bedrock get-model-import-job \
    --region "$REGION" \
    --job-identifier "$1" \
    --query "$2" \
    --output text 2>/dev/null || true
}

JOB_TO_POLL="$IMPORT_JOB_NAME"
EXISTING_STATUS="$(get_status "$IMPORT_JOB_NAME")"

case "$EXISTING_STATUS" in
  "" )
    log "Starting import job '$IMPORT_JOB_NAME' ..."
    aws bedrock create-model-import-job \
      --region "$REGION" \
      --job-name "$IMPORT_JOB_NAME" \
      --imported-model-name "$IMPORT_JOB_NAME" \
      --role-arn "$IMPORT_ROLE_ARN" \
      --model-data-source "{\"s3DataSource\":{\"s3Uri\":\"${S3_URI}\"}}" \
      >/dev/null
    ;;
  InProgress)
    log "Import job '$IMPORT_JOB_NAME' is already in progress, reusing it."
    ;;
  Completed)
    log "Import job '$IMPORT_JOB_NAME' already completed, skipping straight to the result."
    ;;
  Failed)
    # Job names are unique per account, so a retry needs a fresh one. The
    # imported model name stays fixed: it is the key the existence check above
    # matches on, and letting retries suffix it would produce one more billable
    # imported model per attempt.
    JOB_TO_POLL="${IMPORT_JOB_NAME}-$(date -u +%Y%m%d%H%M%S)"
    log "Import job '$IMPORT_JOB_NAME' previously failed; starting a new job '$JOB_TO_POLL' instead."
    log "Inspect the failed job with: aws bedrock get-model-import-job --region $REGION --job-identifier $IMPORT_JOB_NAME"
    aws bedrock create-model-import-job \
      --region "$REGION" \
      --job-name "$JOB_TO_POLL" \
      --imported-model-name "$IMPORT_JOB_NAME" \
      --role-arn "$IMPORT_ROLE_ARN" \
      --model-data-source "{\"s3DataSource\":{\"s3Uri\":\"${S3_URI}\"}}" \
      >/dev/null
    ;;
  *)
    log "Import job '$IMPORT_JOB_NAME' is in state '$EXISTING_STATUS', polling it as-is."
    ;;
esac

if [[ "$EXISTING_STATUS" != "Completed" ]]; then
  log "Polling job '$JOB_TO_POLL' until it completes (checking every 30s) ..."
  while true; do
    STATUS="$(get_status "$JOB_TO_POLL")"
    case "$STATUS" in
      Completed)
        break ;;
      Failed)
        FAILURE_MESSAGE="$(get_field "$JOB_TO_POLL" 'failureMessage')"
        echo "Import job '$JOB_TO_POLL' failed: $FAILURE_MESSAGE" >&2
        exit 1
        ;;
      "" )
        echo "Error: could not read status for job '$JOB_TO_POLL'." >&2
        exit 1
        ;;
      *)
        log "Status: $STATUS"
        sleep 30
        ;;
    esac
  done
fi

MODEL_ARN="$(get_field "$JOB_TO_POLL" 'importedModelArn')"
log "Import complete."
echo "$MODEL_ARN"
