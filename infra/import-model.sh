#!/usr/bin/env bash
#
# import-model.sh - one-time helper that gets a Qwen3 model into Amazon
# Bedrock Custom Model Import so infra/template.yaml has a ModelArn to
# deploy against. Run this by hand, once per model, before the first
# deploy; it is not part of the CI/CD pipeline.
#
# Verified constraint: Bedrock Custom Model Import supports only the
# Qwen3ForCausalLM and Qwen3MoeForCausalLM architectures for the Qwen3
# family. Qwen3.5 checkpoints report a different architecture string and
# will NOT import. Use a Qwen3 (dense or MoE) checkpoint, such as the
# default below.
#
# What this does, in order:
#   1. Downloads the model weights from HuggingFace with huggingface-cli.
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
# Prerequisites this script does NOT create for you:
#   - The S3 bucket (create it in the same region you pass with --region).
#   - An IAM role that Bedrock can assume to read that bucket. It needs a
#     trust policy for the bedrock.amazonaws.com service principal and
#     s3:GetObject / s3:ListBucket permissions on the bucket. See
#     infra/README.md for the exact policy documents.
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
#   --yes, -y          Skip the interactive cost confirmation.
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
# does not estimate cost for you and requires explicit confirmation below.
# ------------------------------------------------------------------------

set -euo pipefail

MODEL_ID="${MODEL_ID:-Qwen/Qwen3-4B}"
S3_BUCKET="${S3_BUCKET:-}"
S3_PREFIX="${S3_PREFIX:-}"
REGION="${AWS_REGION:-eu-central-1}"
IMPORT_ROLE_ARN="${IMPORT_ROLE_ARN:-}"
IMPORT_JOB_NAME="${IMPORT_JOB_NAME:-}"
AUTO_YES="false"

log() {
  printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2
}

usage() {
  sed -n '2,64p' "$0" | sed 's/^# \{0,1\}//'
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
    --yes|-y)
      AUTO_YES="true"; shift ;;
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

for cmd in huggingface-cli aws; do
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

if [[ "$AUTO_YES" != "true" ]]; then
  read -r -p "Type 'yes' to continue: " CONFIRM
  if [[ "$CONFIRM" != "yes" ]]; then
    echo "Aborted, nothing was changed." >&2
    exit 1
  fi
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT
LOCAL_MODEL_DIR="${WORKDIR}/${SANITIZED_MODEL_ID}"

log "Downloading $MODEL_ID to $LOCAL_MODEL_DIR ..."
huggingface-cli download "$MODEL_ID" --local-dir "$LOCAL_MODEL_DIR"

S3_URI="s3://${S3_BUCKET}/${S3_PREFIX}/"
log "Syncing weights to $S3_URI ..."
aws s3 sync "$LOCAL_MODEL_DIR" "$S3_URI" --region "$REGION"

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
    JOB_TO_POLL="${IMPORT_JOB_NAME}-$(date -u +%Y%m%d%H%M%S)"
    log "Import job '$IMPORT_JOB_NAME' previously failed; starting a new job '$JOB_TO_POLL' instead."
    log "Inspect the failed job with: aws bedrock get-model-import-job --region $REGION --job-identifier $IMPORT_JOB_NAME"
    aws bedrock create-model-import-job \
      --region "$REGION" \
      --job-name "$JOB_TO_POLL" \
      --imported-model-name "$JOB_TO_POLL" \
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
