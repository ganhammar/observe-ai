"""Synthetic log-triage fixture: stack traces labelled bug vs downstream.

Three bands. `clear_*` rows are solvable by an exception-type lookup table and
exist as a floor check. `ambiguous` rows are the ones that decide whether a
model earns its place: the exception type points one way and the causal story
points the other.
"""

import json

# (id, band, label, service, runtime, level, message, stack)
ROWS = [
    # ---------- clear downstream ----------
    ("dn-01", "clear_downstream", "downstream", "checkout-api", "dotnet", "ERROR",
     "System.Net.Http.HttpRequestException: Connection refused (payments.internal:8443)",
     """System.Net.Http.HttpRequestException: Connection refused (payments.internal:8443)
 ---> System.Net.Sockets.SocketException (111): Connection refused
   at System.Net.Http.ConnectHelper.ConnectAsync(Func`3 callback, DnsEndPoint endPoint, HttpRequestMessage requestMessage, CancellationToken cancellationToken)
   at System.Net.Http.HttpConnectionPool.ConnectToTcpHostAsync(String host, Int32 port, HttpRequestMessage initialRequest, Boolean async, CancellationToken cancellationToken)
   at Checkout.Clients.PaymentsClient.ChargeAsync(ChargeRequest request, CancellationToken ct)
   at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)
   at Checkout.Api.CheckoutController.Post(CheckoutRequest body, CancellationToken ct)"""),

    ("dn-02", "clear_downstream", "downstream", "order-worker", "java", "ERROR",
     "java.net.SocketTimeoutException: Read timed out",
     """java.net.SocketTimeoutException: Read timed out
\tat java.base/sun.nio.ch.NioSocketImpl.timedRead(NioSocketImpl.java:288)
\tat okhttp3.internal.http2.Http2Stream.waitForIo(Http2Stream.java:673)
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)
\tat java.base/java.util.concurrent.ThreadPoolExecutor.runWorker(ThreadPoolExecutor.java:1144)"""),

    ("dn-03", "clear_downstream", "downstream", "search-api", "python", "ERROR",
     "elasticsearch.exceptions.ConnectionError: ConnectionTimeout caused by ReadTimeoutError",
     """Traceback (most recent call last):
  File "/app/search/handlers.py", line 62, in query
    resp = self.es.search(index=index, body=body)
  File "/usr/local/lib/python3.12/site-packages/elasticsearch/_sync/client/utils.py", line 446, in wrapped
    return api(*args, **kwargs)
elasticsearch.exceptions.ConnectionError: ConnectionTimeout caused by - ReadTimeoutError(HTTPSConnectionPool(host='es.internal', port=9243): Read timed out. (read timeout=10))"""),

    ("dn-04", "clear_downstream", "downstream", "billing-sync", "dotnet", "ERROR",
     "Npgsql.NpgsqlException: The connection pool has been exhausted",
     """Npgsql.NpgsqlException (0x80004005): The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)
   at Npgsql.ConnectorPool.RentAsync(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)
   at Billing.Data.LedgerRepository.GetOpenEntriesAsync(Guid accountId, CancellationToken ct)
   at Billing.Sync.LedgerSyncJob.RunAsync(CancellationToken ct)"""),

    ("dn-05", "clear_downstream", "downstream", "notify-fn", "node", "ERROR",
     "Error: getaddrinfo EAI_AGAIN smtp.provider.example",
     """Error: getaddrinfo EAI_AGAIN smtp.provider.example
    at GetAddrInfoReqWrap.onlookupall [as oncomplete] (node:dns:120:26)
    at SMTPConnection._onError (/app/node_modules/nodemailer/lib/smtp-connection/index.js:772:20)
    at sendDigest (/app/src/notify/digest.js:58:11)
    at async handler (/app/src/index.js:22:5)"""),

    ("dn-06", "clear_downstream", "downstream", "checkout-api", "dotnet", "ERROR",
     "Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open",
     """Polly.CircuitBreaker.BrokenCircuitException: The circuit is now open and is not allowing calls.
   at Polly.CircuitBreaker.CircuitStateController`1.OnActionPreExecute()
   at Checkout.Clients.ShippingClient.QuoteAsync(QuoteRequest request, CancellationToken ct)
   at Checkout.Handlers.QuoteHandler.HandleAsync(QuoteCommand cmd, CancellationToken ct)"""),

    ("dn-07", "clear_downstream", "downstream", "ingest-worker", "go", "ERROR",
     "rpc error: code = Unavailable desc = connection error",
     """rpc error: code = Unavailable desc = connection error: desc = "transport: Error while dialing dial tcp 10.4.2.19:50051: i/o timeout"
goroutine 412 [running]:
main.(*Ingestor).push(0xc0000b4000, 0xc0001a2100)
\t/app/ingest/push.go:77 +0x1d4
main.(*Ingestor).Run(0xc0000b4000)
\t/app/ingest/run.go:38 +0x9c"""),

    ("dn-08", "clear_downstream", "downstream", "auth-api", "java", "ERROR",
     "org.springframework.web.client.HttpServerErrorException$ServiceUnavailable: 503",
     """org.springframework.web.client.HttpServerErrorException$ServiceUnavailable: 503 Service Unavailable: "upstream connect error or disconnect/reset before headers"
\tat org.springframework.web.client.DefaultResponseErrorHandler.handleError(DefaultResponseErrorHandler.java:187)
\tat com.acme.auth.client.DirectoryClient.lookup(DirectoryClient.java:71)
\tat com.acme.auth.LoginService.authenticate(LoginService.java:118)"""),

    ("dn-09", "clear_downstream", "downstream", "report-fn", "python", "ERROR",
     "botocore.exceptions.ClientError: ThrottlingException: Rate exceeded",
     """Traceback (most recent call last):
  File "/var/task/report/export.py", line 91, in write_batch
    self.ddb.batch_write_item(RequestItems=payload)
botocore.exceptions.ClientError: An error occurred (ThrottlingException) when calling the BatchWriteItem operation (reached max retries: 4): Rate exceeded"""),

    ("dn-10", "clear_downstream", "downstream", "order-worker", "java", "ERROR",
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
    at deepMerge (/app/src/bff/config.js:24:14)"""),

    # ---------- ambiguous ----------
    # Exception type screams "code bug" but the trigger is a dependency response.
    ("am-01", "ambiguous", "downstream", "checkout-api", "dotnet", "ERROR",
     "System.NullReferenceException after payments returned an empty 200 body",
     """System.NullReferenceException: Object reference not set to an instance of an object.
   at Checkout.Clients.PaymentsClient.<ChargeAsync>d__12.MoveNext()
--- End of stack trace from previous location ---
   at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)
Context: PaymentsClient received HTTP 200 with Content-Length: 0 from payments.internal; deserialised response was null."""),

    # Looks like a downstream 400 but we sent a malformed request.
    ("am-02", "ambiguous", "bug", "order-worker", "java", "ERROR",
     "HttpClientErrorException$BadRequest: 400 from inventory service",
     """org.springframework.web.client.HttpClientErrorException$BadRequest: 400 Bad Request: {"error":"quantity must be a positive integer, got -1"}
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)
Context: quantity was computed as requested - allocated, which underflows when allocated exceeds requested."""),

    # Timeout, but caused by our own N+1 query loop.
    ("am-03", "ambiguous", "bug", "report-fn", "python", "ERROR",
     "psycopg.OperationalError: statement timeout after 30s",
     """Traceback (most recent call last):
  File "/var/task/report/summarise.py", line 88, in load_lines
    cur.execute(SELECT_LINE, (order_id,))
psycopg.errors.QueryCanceled: canceling statement due to statement timeout
Context: load_lines is called once per order inside a loop over 12,400 orders; each call opens its own statement."""),

    # Deserialisation failure from an upstream contract change.
    ("am-04", "ambiguous", "downstream", "catalog-api", "java", "ERROR",
     "JsonMappingException: Cannot deserialize value of type BigDecimal from String \"n/a\"",
     """com.fasterxml.jackson.databind.exc.InvalidFormatException: Cannot deserialize value of type `java.math.BigDecimal` from String "n/a": not a valid representation
\tat com.acme.catalog.client.SupplierClient.fetchPrice(SupplierClient.java:63)
\tat com.acme.catalog.ProductService.enrich(ProductService.java:203)
Context: supplier feed began emitting "n/a" for unpriced SKUs on 2026-09-21; the field was previously always numeric."""),

    # Connection pool exhausted, but by our own leak.
    ("am-05", "ambiguous", "bug", "billing-sync", "dotnet", "ERROR",
     "NpgsqlException: connection pool exhausted during nightly sync",
     """Npgsql.NpgsqlException (0x80004005): The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)
   at Npgsql.ConnectorPool.RentAsync(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)
   at Billing.Sync.LedgerSyncJob.RunAsync(CancellationToken ct)
Context: LedgerSyncJob opens an NpgsqlConnection per account and does not dispose it; pool usage grows monotonically through the run."""),

    # OOM killed, but the upstream payload grew 50x.
    ("am-06", "ambiguous", "downstream", "ingest-worker", "go", "ERROR",
     "fatal error: runtime: out of memory",
     """fatal error: runtime: out of memory
goroutine 1 [running]:
main.(*Ingestor).readAll(0xc0000b4000)
\t/app/ingest/read.go:41 +0x88
Context: upstream partner switched from paginated batches to a single 4.2 GB payload without notice; readAll buffers the whole response."""),

    # 401 from a dependency: our token refresh is broken.
    ("am-07", "ambiguous", "bug", "auth-api", "java", "ERROR",
     "HttpClientErrorException$Unauthorized: 401 from directory service",
     """org.springframework.web.client.HttpClientErrorException$Unauthorized: 401 Unauthorized
\tat com.acme.auth.client.DirectoryClient.lookup(DirectoryClient.java:71)
\tat com.acme.auth.LoginService.authenticate(LoginService.java:118)
Context: the cached service token is refreshed on a 3600s timer but the issuer reduced token lifetime to 900s; refresh logic ignores the expires_in field."""),

    # NPE, and it really is our bug (no dependency involved).
    ("am-08", "ambiguous", "bug", "pricing-api", "dotnet", "ERROR",
     "System.NullReferenceException in tier resolution",
     """System.NullReferenceException: Object reference not set to an instance of an object.
   at Pricing.Tiers.TierResolver.Resolve(Decimal amount, Tier[] tiers)
   at Pricing.Quote.QuoteBuilder.Build(QuoteRequest request)
Context: tiers is null when a product has no tier configuration; Resolve does not check before indexing."""),

    # Rate limited by a partner: genuinely their limit, our volume is nominal.
    ("am-09", "ambiguous", "downstream", "notify-fn", "node", "ERROR",
     "Error: 429 Too Many Requests from provider",
     """Error: Request failed with status code 429
    at settle (/app/node_modules/axios/lib/core/settle.js:19:12)
    at sendDigest (/app/src/notify/digest.js:58:11)
Context: provider reduced the plan limit from 500/min to 100/min at 02:00 UTC; our send rate is unchanged at 180/min."""),

    # Deadlock, our transaction ordering.
    ("am-10", "ambiguous", "bug", "ledger-api", "dotnet", "ERROR",
     "SqlException: Transaction was deadlocked on lock resources",
     """Microsoft.Data.SqlClient.SqlException (0x80131904): Transaction (Process ID 71) was deadlocked on lock resources with another process and has been chosen as the deadlock victim.
   at Ledger.Posting.PostingService.Post(PostingRequest request)
Context: PostingService locks accounts in request order; concurrent transfers between the same two accounts acquire the locks in opposite order."""),

    # Certificate expiry on the dependency side.
    ("am-11", "ambiguous", "downstream", "checkout-api", "dotnet", "ERROR",
     "AuthenticationException: The remote certificate is invalid",
     """System.Security.Authentication.AuthenticationException: The remote certificate is invalid according to the validation procedure: RemoteCertificateNotYetValid, RemoteCertificateChainErrors
   at System.Net.Security.SslStream.SendAuthResetSignal(ProtocolToken message, ExceptionDispatchInfo exception)
   at Checkout.Clients.ShippingClient.QuoteAsync(QuoteRequest request, CancellationToken ct)
Context: shipping.internal presented a certificate with notBefore 2026-09-24T12:00Z; current time is 2026-09-24T09:14Z."""),

    # Retry storm we caused.
    ("am-12", "ambiguous", "bug", "order-worker", "java", "ERROR",
     "TimeoutException after retry exhaustion against inventory",
     """java.util.concurrent.TimeoutException: Did not observe any item or terminal signal within 30000ms
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)
Context: retry policy is 10 attempts with no backoff and no jitter; inventory reports our service as the sole source of a 40x traffic spike."""),
]

QUESTION = ("Does this error indicate a defect in this service's own source code, "
            "or a failure in an external dependency or infrastructure it calls?")

OPTIONS = [
    {"id": "bug",
     "description": "A defect in this service's own source code. Resolving it requires a change to this repository."},
    {"id": "downstream",
     "description": "A failure in an external dependency or in infrastructure this service calls. This service's own code is behaving correctly."},
]


def build():
    rows, labels = [], {}
    for rid, band, label, service, runtime, level, message, stack in ROWS:
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
    return rows, labels


if __name__ == "__main__":
    import pathlib
    rows, labels = build()
    out = pathlib.Path(__file__).parent
    with (out / "logs.jsonl").open("w") as fh:
        for row in rows:
            fh.write(json.dumps(row, ensure_ascii=False) + "\n")
    (out / "labels.json").write_text(json.dumps(labels, indent=2))
    counts = {}
    for meta in labels.values():
        counts[(meta["band"], meta["label"])] = counts.get((meta["band"], meta["label"]), 0) + 1
    print(f"{len(rows)} rows -> logs.jsonl")
    for key in sorted(counts):
        print(f"  {key[0]:<17} {key[1]:<11} {counts[key]}")
