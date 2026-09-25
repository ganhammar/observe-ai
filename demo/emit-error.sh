#!/usr/bin/env bash
# Writes one unhandled-exception event into /aws/lambda/pricing-demo, in the
# exact shape the Python Lambda runtime logs, so the pipeline can be exercised
# without deploying the demo function. Line numbers match handler.py.
#
# Usage: demo/emit-error.sh [tier]   (default: enterprise)
set -euo pipefail

tier="${1:-enterprise}"
group="/aws/lambda/pricing-demo"
stream="demo-$(date +%s)"
region="${AWS_REGION:-${AWS_DEFAULT_REGION:-eu-central-1}}"

# The runtime joins the traceback's lines with a carriage return, not a newline.
cr=$'\r'
message="[ERROR] KeyError: '$tier'${cr}Traceback (most recent call last):${cr}  File \"/var/task/handler.py\", line 26, in handler${cr}    total = price_for(customer, quantity)${cr}  File \"/var/task/handler.py\", line 19, in price_for${cr}    unit = TIERS[customer[\"tier\"]]"

aws logs create-log-group --region "$region" --log-group-name "$group" 2>/dev/null || true
aws logs create-log-stream --region "$region" --log-group-name "$group" --log-stream-name "$stream"

python3 - "$message" > "${TMPDIR:-/tmp}/emit-error.json" <<'PY'
import json, sys, time
print(json.dumps([{"timestamp": int(time.time() * 1000), "message": sys.argv[1]}]))
PY

aws logs put-log-events --region "$region" --log-group-name "$group" --log-stream-name "$stream" \
  --log-events "file://${TMPDIR:-/tmp}/emit-error.json" >/dev/null

echo "Wrote one KeyError event to $group/$stream"
