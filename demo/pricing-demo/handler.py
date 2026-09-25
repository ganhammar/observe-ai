"""A pricing endpoint that exists to fail.

The triage pipeline is tested end to end against this function: it throws a
KeyError from application code, the error lands in /aws/lambda/pricing-demo,
and the pipeline should trace it back to the line below and file an issue in
the repository of the same name.
"""

import json

TIERS = {
    "basic": 10.0,
    "pro": 25.0,
}


def price_for(customer, quantity):
    # Every customer record is expected to carry a tier that TIERS knows about.
    unit = TIERS[customer["tier"]]
    return unit * quantity


def handler(event, context):
    customer = {"id": event.get("customer", "anonymous"), "tier": event.get("tier", "basic")}
    quantity = int(event.get("quantity", 1))
    total = price_for(customer, quantity)
    return {"statusCode": 200, "body": json.dumps({"total": total})}
