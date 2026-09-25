#!/usr/bin/env bash
#
# Imports a Qwen3 checkpoint into Amazon Bedrock Custom Model Import and prints
# the imported model ARN. The Import Model workflow runs it with the bucket and
# role from infra/bootstrap.yaml.
#
# Usage: import-model.sh --bucket NAME --role-arn ARN [--model-id Qwen/Qwen3-4B]
#        [--job-name NAME] [--prefix PREFIX] [--region eu-central-1]
#
# Files are staged one at a time (download, upload, delete), since a checkpoint
# is split into shards of about 4 GB and a GitHub runner has about 14 GB free.
# Custom Model Import accepts Qwen3ForCausalLM and Qwen3MoeForCausalLM only;
# Qwen3.5 checkpoints do not import.
#
# Cost: Bedrock bills per Custom Model Unit per minute while the model is active
# and per CMU per month for storage from the moment the model exists. The import
# itself is free. An existing model of the same name is reused, never duplicated.

set -euo pipefail

MODEL_ID="Qwen/Qwen3-4B"
S3_BUCKET=""
S3_PREFIX=""
REGION="eu-central-1"
IMPORT_ROLE_ARN=""
IMPORT_JOB_NAME=""

log() { printf '[%s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }
usage() { sed -n '3,17p' "$0" | sed 's/^# \{0,1\}//'; }
sanitize() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9-' '-' | sed 's/-\+/-/g; s/^-//; s/-$//'; }
human() { numfmt --to=iec --suffix=B "$1" 2>/dev/null || echo "$1 bytes"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --model-id) MODEL_ID="$2"; shift 2 ;;
    --bucket) S3_BUCKET="$2"; shift 2 ;;
    --prefix) S3_PREFIX="$2"; shift 2 ;;
    --region) REGION="$2"; shift 2 ;;
    --role-arn) IMPORT_ROLE_ARN="$2"; shift 2 ;;
    --job-name) IMPORT_JOB_NAME="$2"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ -z "$S3_BUCKET" || -z "$IMPORT_ROLE_ARN" ]]; then
  echo "Error: --bucket and --role-arn are required." >&2
  exit 1
fi
for cmd in hf aws curl python3; do
  command -v "$cmd" >/dev/null 2>&1 || { echo "Error: '$cmd' not found on PATH." >&2; exit 1; }
done

SANITIZED_MODEL_ID="$(sanitize "$MODEL_ID")"
S3_PREFIX="${S3_PREFIX:-models/${SANITIZED_MODEL_ID}}"
IMPORT_JOB_NAME="${IMPORT_JOB_NAME:-${SANITIZED_MODEL_ID}-import}"
S3_URI="s3://${S3_BUCKET}/${S3_PREFIX}/"

job_field() {
  aws bedrock get-model-import-job --region "$REGION" --job-identifier "$1" --query "$2" --output text 2>/dev/null || true
}

start_job() {
  aws bedrock create-model-import-job \
    --region "$REGION" \
    --job-name "$1" \
    --imported-model-name "$IMPORT_JOB_NAME" \
    --role-arn "$IMPORT_ROLE_ARN" \
    --model-data-source "{\"s3DataSource\":{\"s3Uri\":\"${S3_URI}\"}}" \
    >/dev/null
}

# A failed listing must stop the script: reading it as "no model" would import a second billable copy.
if ! EXISTING_MODEL_ARN="$(aws bedrock list-imported-models \
  --region "$REGION" \
  --name-contains "$IMPORT_JOB_NAME" \
  --query "modelSummaries[?modelName=='${IMPORT_JOB_NAME}'].modelArn | [0]" \
  --output text 2>&1)"; then
  echo "Could not list imported models in $REGION: $EXISTING_MODEL_ARN" >&2
  exit 1
fi
if [[ -n "$EXISTING_MODEL_ARN" && "$EXISTING_MODEL_ARN" != "None" ]]; then
  log "Imported model '$IMPORT_JOB_NAME' already exists."
  echo "$EXISTING_MODEL_ARN"
  exit 0
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

log "Fetching file list for $MODEL_ID ..."
API_URL="https://huggingface.co/api/models/${MODEL_ID}?blobs=true"
MODEL_INFO="$(curl -sfL "$API_URL")" || { echo "Error: could not fetch $API_URL" >&2; exit 1; }

# Bedrock needs the safetensors shards, the shard index and the config and tokenizer files.
# Output is "size<TAB>filename" per line.
SELECTED="$(python3 -c '
import json, sys

data = json.loads(sys.stdin.read())
allowed = {
    "config.json", "generation_config.json", "tokenizer.json",
    "tokenizer_config.json", "tokenizer.model", "special_tokens_map.json",
    "vocab.json", "merges.txt",
}
for sibling in data.get("siblings", []):
    name = sibling.get("rfilename", "")
    if name.startswith("original/"):
        continue
    if name.endswith(".safetensors") or name.endswith(".index.json") or name in allowed:
        print(f"{sibling.get(\"size\", 0)}\t{name}")
' <<<"$MODEL_INFO")"
[[ -n "$SELECTED" ]] || { echo "Error: no matching files found for $MODEL_ID." >&2; exit 1; }

TOTAL_SIZE=0; MAX_SIZE=0; FILE_COUNT=0
while IFS=$'\t' read -r size name; do
  TOTAL_SIZE=$((TOTAL_SIZE + size))
  (( size > MAX_SIZE )) && MAX_SIZE=$size
  FILE_COUNT=$((FILE_COUNT + 1))
done <<<"$SELECTED"
AVAILABLE_BYTES="$(df -B1 --output=avail "$WORKDIR" | tail -n1 | tr -d ' ')"
log "Selected $FILE_COUNT files, $(human "$TOTAL_SIZE") total; $(human "$AVAILABLE_BYTES") free at $WORKDIR."
if (( MAX_SIZE > AVAILABLE_BYTES )); then
  echo "Error: the largest file ($(human "$MAX_SIZE")) does not fit in the space available." >&2
  exit 1
fi

log "Clearing $S3_URI so a retry cannot mix in an earlier attempt's files ..."
aws s3 rm "$S3_URI" --recursive --region "$REGION"

INDEX=0
while IFS=$'\t' read -r size name; do
  INDEX=$((INDEX + 1))
  log "[$INDEX/$FILE_COUNT] $name ($(human "$size"))"
  hf download "$MODEL_ID" "$name" --local-dir "$WORKDIR"
  aws s3 cp "${WORKDIR}/${name}" "${S3_URI}${name}" --region "$REGION"
  rm -f "${WORKDIR}/${name}"
done <<<"$SELECTED"

JOB_TO_POLL="$IMPORT_JOB_NAME"
case "$(job_field "$IMPORT_JOB_NAME" status)" in
  "") log "Starting import job '$IMPORT_JOB_NAME' ..."; start_job "$IMPORT_JOB_NAME" ;;
  InProgress) log "Import job '$IMPORT_JOB_NAME' is in progress, reusing it." ;;
  Completed) log "Import job '$IMPORT_JOB_NAME' already completed." ;;
  Failed)
    # Job names are unique per account; the model name stays fixed so the existence check above still matches.
    JOB_TO_POLL="${IMPORT_JOB_NAME}-$(date -u +%Y%m%d%H%M%S)"
    log "Import job '$IMPORT_JOB_NAME' failed earlier; starting '$JOB_TO_POLL'."
    start_job "$JOB_TO_POLL" ;;
  *) log "Import job '$IMPORT_JOB_NAME' is in an unexpected state, polling it." ;;
esac

log "Polling job '$JOB_TO_POLL' every 30s ..."
while true; do
  case "$(job_field "$JOB_TO_POLL" status)" in
    Completed) break ;;
    Failed) echo "Import job '$JOB_TO_POLL' failed: $(job_field "$JOB_TO_POLL" failureMessage)" >&2; exit 1 ;;
    "") echo "Error: could not read status for job '$JOB_TO_POLL'." >&2; exit 1 ;;
    *) sleep 30 ;;
  esac
done

log "Import complete."
job_field "$JOB_TO_POLL" importedModelArn
