"""Ambiguous band: rows where the exception type points away from the answer.

Each row carries `evidence`, which holds the surrounding signals a real log
pipeline already has: adjacent log lines, response metadata, dependency
status, recent deploys. The evidence never names a cause and never uses the
words bug, defect, downstream or dependency. Deciding requires relating the
frames to the signals.

`fixture.py` can emit these rows with or without the evidence field, so the
same twelve cases measure two different questions: whether the model can
triage from a trace alone, and whether extra context helps.
"""

# (id, label, service, runtime, message, stack, evidence)
AMBIGUOUS = [
    ("am-01", "downstream", "checkout-api", "dotnet",
     "System.NullReferenceException in ChargeAsync",
     """System.NullReferenceException: Object reference not set to an instance of an object.
   at Checkout.Clients.PaymentsClient.<ChargeAsync>d__12.MoveNext()
--- End of stack trace from previous location ---
   at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)
   at Checkout.Api.CheckoutController.Post(CheckoutRequest body, CancellationToken ct)""",
     {"upstream_response": {"host": "payments.internal", "status": 200,
                            "content_length": 0, "content_type": "application/json"},
      "recent_rate": "first occurrence 08:14Z, 3140 occurrences since",
      "last_deploy": {"service": "checkout-api", "at": "2026-09-11T10:02Z"}}),

    ("am-02", "bug", "order-worker", "java",
     "HttpClientErrorException$BadRequest: 400 from inventory",
     """org.springframework.web.client.HttpClientErrorException$BadRequest: 400 Bad Request
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)""",
     {"response_body": {"error": "quantity must be a positive integer", "received": -1},
      "request_body": {"sku": "AC-8812", "quantity": -1, "warehouse": "eu-1"},
      "upstream_health": "inventory-api reporting healthy, 0.02% error rate overall"}),

    ("am-03", "bug", "report-fn", "python",
     "psycopg.errors.QueryCanceled: statement timeout",
     """Traceback (most recent call last):
  File "/var/task/report/summarise.py", line 88, in load_lines
    cur.execute(SELECT_LINE, (order_id,))
psycopg.errors.QueryCanceled: canceling statement due to statement timeout""",
     {"query_count_this_invocation": 12400,
      "distinct_statements": 1,
      "mean_statement_ms": 2.4,
      "db_cpu_percent": 31,
      "other_clients_affected": False}),

    ("am-04", "downstream", "catalog-api", "java",
     "InvalidFormatException deserialising supplier price",
     """com.fasterxml.jackson.databind.exc.InvalidFormatException: Cannot deserialize value of type `java.math.BigDecimal` from String "n/a": not a valid representation
\tat com.acme.catalog.client.SupplierClient.fetchPrice(SupplierClient.java:63)
\tat com.acme.catalog.ProductService.enrich(ProductService.java:203)""",
     {"upstream_payload_sample": {"sku": "SP-1190", "price": "n/a", "currency": "EUR"},
      "field_history": "price parsed as numeric on 1.4M records through 2026-09-20, first non-numeric 2026-09-21T00:11Z",
      "last_deploy": {"service": "catalog-api", "at": "2026-08-29T14:40Z"}}),

    ("am-05", "bug", "billing-sync", "dotnet",
     "NpgsqlException: connection pool exhausted",
     """Npgsql.NpgsqlException (0x80004005): The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)
   at Npgsql.ConnectorPool.RentAsync(NpgsqlConnection conn, NpgsqlTimeout timeout, Boolean async, CancellationToken cancellationToken)
   at Billing.Data.LedgerRepository.GetOpenEntriesAsync(Guid accountId, CancellationToken ct)
   at Billing.Sync.LedgerSyncJob.RunAsync(CancellationToken ct)""",
     {"pool_in_use_over_run": [4, 19, 47, 83, 100, 100, 100],
      "connections_opened": 2841, "connections_disposed": 12,
      "db_max_connections": 400, "other_services_connected": 96}),

    ("am-06", "downstream", "ingest-worker", "go",
     "fatal error: runtime: out of memory",
     """fatal error: runtime: out of memory
goroutine 1 [running]:
main.(*Ingestor).readAll(0xc0000b4000)
\t/app/ingest/read.go:41 +0x88""",
     {"response_content_length": 4509715660,
      "prior_7d_max_content_length": 88211004,
      "container_memory_limit_bytes": 2147483648,
      "partner_notice": None,
      "last_deploy": {"service": "ingest-worker", "at": "2026-07-02T09:15Z"}}),

    ("am-07", "bug", "auth-api", "java",
     "HttpClientErrorException$Unauthorized: 401 from directory",
     """org.springframework.web.client.HttpClientErrorException$Unauthorized: 401 Unauthorized
\tat com.acme.auth.client.DirectoryClient.lookup(DirectoryClient.java:71)
\tat com.acme.auth.LoginService.authenticate(LoginService.java:118)""",
     {"token_issued_at": "2026-09-24T06:00:00Z", "token_expires_in_s": 900,
      "token_refresh_interval_s": 3600, "request_at": "2026-09-24T06:41:12Z",
      "failure_pattern": "succeeds for ~15 min after each refresh, then fails until the next",
      "upstream_health": "directory-api reporting healthy"}),

    ("am-08", "bug", "pricing-api", "dotnet",
     "System.NullReferenceException in TierResolver",
     """System.NullReferenceException: Object reference not set to an instance of an object.
   at Pricing.Tiers.TierResolver.Resolve(Decimal amount, Tier[] tiers)
   at Pricing.Quote.QuoteBuilder.Build(QuoteRequest request)
   at Pricing.Api.QuoteController.Post(QuoteRequest body)""",
     {"request_body": {"sku": "PR-4401", "amount": 249.0},
      "affected_skus": "only SKUs with no tier configuration rows",
      "outbound_calls_this_request": 0}),

    ("am-09", "downstream", "notify-fn", "node",
     "Request failed with status code 429",
     """Error: Request failed with status code 429
    at settle (/app/node_modules/axios/lib/core/settle.js:19:12)
    at sendDigest (/app/src/notify/digest.js:58:11)""",
     {"response_headers": {"retry-after": "60", "x-ratelimit-limit": "100",
                           "x-ratelimit-remaining": "0"},
      "our_send_rate_per_min": 180,
      "our_send_rate_per_min_7d_avg": 178,
      "prior_ratelimit_limit": "500 until 2026-09-24T02:00Z"}),

    ("am-10", "bug", "ledger-api", "dotnet",
     "SqlException: deadlocked on lock resources",
     """Microsoft.Data.SqlClient.SqlException (0x80131904): Transaction (Process ID 71) was deadlocked on lock resources with another process and has been chosen as the deadlock victim.
   at Ledger.Posting.PostingService.Post(PostingRequest request)""",
     {"deadlock_pairs": [{"pid": 71, "locks": ["account:A18", "account:B04"]},
                         {"pid": 84, "locks": ["account:B04", "account:A18"]}],
      "both_processes": "ledger-api",
      "db_cpu_percent": 22,
      "occurrences_7d": 417}),

    ("am-11", "downstream", "checkout-api", "dotnet",
     "AuthenticationException: remote certificate is invalid",
     """System.Security.Authentication.AuthenticationException: The remote certificate is invalid according to the validation procedure: RemoteCertificateNotYetValid, RemoteCertificateChainErrors
   at System.Net.Security.SslStream.SendAuthResetSignal(ProtocolToken message, ExceptionDispatchInfo exception)
   at Checkout.Clients.ShippingClient.QuoteAsync(QuoteRequest request, CancellationToken ct)""",
     {"peer": "shipping.internal",
      "peer_certificate": {"not_before": "2026-09-24T12:00:00Z",
                           "not_after": "2027-09-24T12:00:00Z",
                           "issuer": "CN=acme-internal-ca"},
      "observed_at": "2026-09-24T09:14:02Z",
      "last_deploy": {"service": "checkout-api", "at": "2026-09-11T10:02Z"}}),

    ("am-12", "bug", "order-worker", "java",
     "TimeoutException awaiting inventory reserve",
     """java.util.concurrent.TimeoutException: Did not observe any item or terminal signal within 30000ms
\tat com.acme.orders.client.InventoryClient.reserve(InventoryClient.java:94)
\tat com.acme.orders.OrderWorker.process(OrderWorker.java:141)""",
     {"attempt_timestamps_ms": [0, 3, 7, 11, 14, 18, 22, 25, 29, 33],
      "attempts_configured": 10,
      "inbound_rps_from_this_service": 8400,
      "inbound_rps_from_this_service_7d_avg": 210,
      "inventory_error_rate_excluding_this_caller": 0.0003}),
]
