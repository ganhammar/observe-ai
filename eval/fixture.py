"""Synthetic log-triage fixture: stack traces labelled bug vs external.

Three bands. `clear_*` rows are solvable by an exception-type lookup table and
exist as a floor check. `ambiguous` rows test whether a model can separate
cause from symptom: the exception type points one way and the causal story
points the other.
"""

import json

from ambiguous import AMBIGUOUS
from heldout import HELDOUT
from tree import BASELINE

# (id, band, label, service, runtime, level, message, stack)
ROWS = [
    # ---------- clear external ----------
    ("dn-01", "clear_external", "external", "checkout-api", "dotnet", "ERROR",
     "System.Net.Http.HttpRequestException: Connection refused (payments.internal:8443)",
     """System.Net.Http.HttpRequestException: Connection refused (payments.internal:8443)
 ---> System.Net.Sockets.SocketException (111): Connection refused
   at System.Net.Http.ConnectHelper.ConnectAsync(Func`3 callback, DnsEndPoint endPoint, HttpRequestMessage requestMessage, CancellationToken cancellationToken)
   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port, HttpRequestMessage initialRequest, Boolean async, CancellationToken cancellationToken)
   at Checkout.Clients.PaymentsClient.ChargeAsync(ChargeRequest request, CancellationToken ct)
   at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)
   at Checkout.Api.CheckoutController.Post(CheckoutRequest body, CancellationToken ct)"""),

    ("dn-02", "clear_external", "external", "order-worker", "java", "ERROR",
     "java.net.SocketTimeoutException: Read timed out",
     """java.net.SocketTimeoutException: Read timed out
\tat java.base/sun.nio.ch.NioSocketImpl.timedRead(NioSocketImpl.java:288)
\tat okhttp3.internal.http2.Http2Stream.waitForIo(Http2Stream.java:673)
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)
\tat java.base/java.util.concurrent.ThreadPoolExecutor.runWorker(ThreadPoolExecutor.java:1144)"""),

    ("dn-03", "clear_external", "external", "search-api", "python", "ERROR",
     "elasticsearch.exceptions.ConnectionError: ConnectionTimeout caused by ReadTimeoutError",
     """Traceback (most recent call last):
  File "/app/search/handlers.py", line 62, in query
    resp = self.es.search(index=index, body=body)
  File "/usr/local/lib/python3.12/site-packages/elasticsearch/_sync/client/utils.py", line 446, in wrapped
    return api(*args, **kwargs)
elasticsearch.exceptions.ConnectionError: ConnectionTimeout caused by - ReadTimeoutError(HTTPSConnectionPool(host='es.internal', port=9243): Read timed out. (read timeout=10))"""),

    ("dn-04", "clear_external", "external", "billing-sync", "dotnet", "ERROR",
     "Npgsql.NpgsqlException: The connection pool has been exhausted",
     """Npgsql.NpgsqlException (0x80004005): The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)
   at Npgsql.ConnectorPool.RentAsync(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)
   at Billing.Data.LedgerRepository.GetOpenEntriesAsync(Guid accountId, CancellationToken ct)
   at Billing.Sync.LedgerSyncJob.RunAsync(CancellationToken ct)"""),

    ("dn-05", "clear_external", "external", "notify-fn", "node", "ERROR",
     "Error: getaddrinfo EAI_AGAIN smtp.provider.example",
     """Error: getaddrinfo EAI_AGAIN smtp.provider.example
    at GetAddrInfoReqWrap.onlookupall [as oncomplete] (node:dns:120:26)
    at SMTPConnection._onError (/app/node_modules/nodemailer/lib/smtp-connection/index.js:772:20)
    at sendDigest (/app/src/notify/digest.js:58:11)
    at async handler (/app/src/index.js:22:5)"""),

    ("dn-06", "clear_external", "external", "checkout-api", "dotnet", "ERROR",
     "Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open",
     """Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open and is not allowing calls.
   at Polly.CircuitBreaker.CircuitStateController`1.OnActionPreExecute()
   at Checkout.Clients.ShippingClient.QuoteAsync(QuoteRequest request, CancellationToken ct)
   at Checkout.Handlers.QuoteHandler.HandleAsync(QuoteCommand cmd, CancellationToken ct)"""),

    ("dn-07", "clear_external", "external", "ingest-worker", "go", "ERROR",
     "rpc error: code = Unavailable desc = connection error",
     """rpc error: code = Unavailable desc = connection error: desc = "transport: Error while dialing dial tcp 10.4.2.19:50051: i/o timeout"
goroutine 412 [running]:
main.(*Ingestor).push(0xc0000b4000, 0xc0001a2100)
\t/app/ingest/push.go:77 +0x1d4
main.(*Ingestor).Run(0xc0000b4000)
\t/app/ingest/run.go:38 +0x9c"""),

    ("dn-08", "clear_external", "external", "auth-api", "java", "ERROR",
     "org.springframework.web.client.HttpServerErrorException$ServiceUnavailable: 503",
     """org.springframework.web.client.HttpServerErrorException$ServiceUnavailable: 503 Service Unavailable: "upstream connect error or disconnect/reset before headers"
\tat org.springframework.web.client.DefaultResponseErrorHandler.handleError(DefaultResponseErrorHandler.java:187)
\tat com.acme.auth.client.DirectoryClient.lookup(DirectoryClient.java:71)
\tat com.acme.auth.LoginService.authenticate(LoginService.java:118)"""),

    ("dn-09", "clear_external", "external", "report-fn", "python", "ERROR",
     "botocore.exceptions.ClientError: ThrottlingException: Rate exceeded",
     """Traceback (most recent call last):
  File "/var/task/report/export.py", line 91, in write_batch
    self.ddb.batch_write_item(RequestItems=payload)
botocore.exceptions.ClientError: An error occurred (ThrottlingException) when calling the BatchWriteItem operation (reached max retries: 4): Rate exceeded"""),

    ("dn-10", "clear_external", "external", "order-worker", "java", "ERROR",
     "org.apache.kafka.common.errors.TimeoutException: Topic not present in metadata after 60000 ms",
     """org.apache.kafka.common.errors.TimeoutException: Topic orders.v2 not present in metadata after 60000 ms.
\tat org.apache.kafka.clients.producer.KafkaProducer.waitOnMetadata(KafkaProducer.java:1088)
\tat com.acme.orders.OrderPublisher.publish(OrderPublisher.java:52)"""),

    # ---------- clear bug ----------
    ("bg-01", "clear_bug", "bug", "pricing-api", "dotnet", "ERROR",
     "System.IndexOutOfRangeException: Index was outside the bounds of the array.",
     """System.IndexOutOfRangeException: Index was outside the bounds of the array.
   at Pricing.Tiers.TierResolver.Resolve(Decimal amount, Tier[] tiers)
   at Pricing.Quote.QuoteBuilder.Build(QuoteRequest request)
   at Pricing.Api.QuoteController.Post(QuoteRequest body)"""),

    ("bg-02", "clear_bug", "bug", "catalog-api", "java", "ERROR",
     "java.lang.ArithmeticException: / by zero",
     """java.lang.ArithmeticException: / by zero
\tat com.acme.catalog.pricing.Discount.percentOff(Discount.java:44)
\tat com.acme.catalog.pricing.PriceCalculator.apply(PriceCalculator.java:88)
\tat com.acme.catalog.ProductService.enrich(ProductService.java:203)"""),

    ("bg-03", "clear_bug", "bug", "report-fn", "python", "ERROR",
     "KeyError: 'total_amount'",
     """Traceback (most recent call last):
  File "/var/task/report/summarise.py", line 47, in build_row
    total = record["total_amount"]
KeyError: 'total_amount'"""),

    ("bg-04", "clear_bug", "bug", "web-bff", "node", "ERROR",
     "TypeError: Cannot read properties of undefined (reading 'map')",
     """TypeError: Cannot read properties of undefined (reading 'map')
    at buildFacets (/app/src/bff/facets.js:31:28)
    at renderSearch (/app/src/bff/search.js:112:19)
    at async /app/src/bff/routes.js:64:20"""),

    ("bg-05", "clear_bug", "bug", "ledger-api", "dotnet", "ERROR",
     "System.InvalidOperationException: Sequence contains no elements",
     """System.InvalidOperationException: Sequence contains no elements
   at System.Linq.Enumerable.First[TSource](IEnumerable`1 source)
   at Ledger.Posting.PostingRules.SelectRule(PostingContext context)
   at Ledger.Posting.PostingService.Post(PostingRequest request)"""),

    ("bg-06", "clear_bug", "bug", "ingest-worker", "go", "ERROR",
     "panic: runtime error: index out of range [5] with length 3",
     """panic: runtime error: index out of range [5] with length 3
goroutine 88 [running]:
main.parseColumns(0xc000124060, 0x3)
\t/app/ingest/parse.go:118 +0x2a4
main.(*Ingestor).handleLine(0xc0000b4000, 0xc000130040)
\t/app/ingest/run.go:64 +0x110"""),

    ("bg-07", "clear_bug", "bug", "auth-api", "java", "ERROR",
     "java.lang.StackOverflowError",
     """java.lang.StackOverflowError
\tat com.acme.auth.policy.PolicyResolver.expand(PolicyResolver.java:77)
\tat com.acme.auth.policy.PolicyResolver.expand(PolicyResolver.java:81)
\tat com.acme.auth.policy.PolicyResolver.expand(PolicyResolver.java:81)
\tat com.acme.auth.policy.PolicyResolver.expand(PolicyResolver.java:81)"""),

    ("bg-08", "clear_bug", "bug", "checkout-api", "dotnet", "ERROR",
     "System.FormatException: The input string '' was not in a correct format.",
     """System.FormatException: The input string '' was not in a correct format.
   at System.Number.ThrowOverflowOrFormatException(ParsingStatus status, TypeCode type)
   at Checkout.Coupons.CouponParser.ParsePercentage(String raw)
   at Checkout.Coupons.CouponService.Apply(Cart cart, String code)"""),

    ("bg-09", "clear_bug", "bug", "search-api", "python", "ERROR",
     "AttributeError: 'NoneType' object has no attribute 'lower'",
     """Traceback (most recent call last):
  File "/app/search/normalise.py", line 28, in normalise_term
    return term.lower().strip()
AttributeError: 'NoneType' object has no attribute 'lower'"""),

    ("bg-10", "clear_bug", "bug", "web-bff", "node", "ERROR",
     "RangeError: Maximum call stack size exceeded",
     """RangeError: Maximum call stack size exceeded
    at deepMerge (/app/src/bff/config.js:19:22)
    at deepMerge (/app/src/bff/config.js:24:14)
    at deepMerge (/app/src/bff/config.js:24:14)""")
]


QUESTION = BASELINE["question"]
OPTIONS = BASELINE["options"]


def build(include_evidence: bool = True, held_out: bool = False):
    """Return SemIf rows and their labels.

    include_evidence controls whether ambiguous rows carry their surrounding
    signals. Withholding it asks whether the trace alone is enough; including
    it asks whether the signals a log pipeline already has close the gap.
    held_out selects the ambiguous rows written after the tree was fixed, in
    place of the clear bands and the rows it was tuned on.
    """
    rows, labels = [], {}
    for rid, band, label, service, runtime, level, message, stack in ([] if held_out else ROWS):
        rows.append({
            "id": rid,
            "state": {
                "service": service,
                "runtime": runtime,
                "level": level,
                "message": message,
                "stack_trace": stack,
            },
            "question": QUESTION,
            "options": OPTIONS,
        })
        labels[rid] = {"label": label, "band": band, "runtime": runtime}
    for rid, label, service, runtime, message, stack, evidence in (HELDOUT if held_out else AMBIGUOUS):
        state = {
            "service": service,
            "runtime": runtime,
            "level": "ERROR",
            "message": message,
            "stack_trace": stack,
        }
        if include_evidence:
            state["evidence"] = evidence
        rows.append({
            "id": rid,
            "state": state,
            "question": QUESTION,
            "options": OPTIONS,
        })
        labels[rid] = {"label": label, "band": "held_out" if held_out else "ambiguous", "runtime": runtime}
    return rows, labels


if __name__ == "__main__":
    import argparse
    import pathlib

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-evidence", action="store_true",
                        help="Omit the evidence field from ambiguous rows")
    parser.add_argument("--held-out", action="store_true",
                        help="Emit the held-out ambiguous rows instead, to logs-heldout*.jsonl and labels-heldout.json")
    parser.add_argument("--output", type=pathlib.Path)
    args = parser.parse_args()

    rows, labels = build(include_evidence=not args.no_evidence, held_out=args.held_out)
    out = pathlib.Path(__file__).parent
    stem = "logs-heldout" if args.held_out else "logs"
    output = args.output or out / f"{stem}{'-no-evidence' if args.no_evidence else ''}.jsonl"
    with output.open("w") as fh:
        for row in rows:
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")
    (out / ("labels-heldout.json" if args.held_out else "labels.json")).write_text(json.dumps(labels, indent=2))
    counts = {}
    for meta in labels.values():
        key = (meta["band"], meta["label"])
        counts[key] = counts.get(key, 0) + 1
    evidence = "without" if args.no_evidence else "with"
    print(f"{len(rows)} rows -> {output} ({evidence} evidence on ambiguous rows)")
    for key in sorted(counts):
        print(f"  {key[0]:<17} {key[1]:<11} {counts[key]}")
