"""Held-out ambiguous rows, written after the tree was fixed and never used to tune it.

Same shape as ambiguous.py: the exception type points one way, the evidence the other,
and the evidence states facts without naming a cause. Six bug, six external.
"""

# (id, label, service, runtime, message, stack, evidence)
HELDOUT = [
    ("ho-01", "external", "partner-sync", "dotnet",
     "AuthenticationException: The remote certificate is invalid",
     """System.Security.Authentication.AuthenticationException: The remote certificate is invalid according to the validation procedure: RemoteCertificateNotAvailable, RemoteCertificateChainErrors
   at System.Net.Security.SslStream.SendAuthResetSignal(ReadOnlySpan`1 alert, ExceptionDispatchInfo exception)
   at Partner.Sync.Clients.OrdersFeedClient.PullAsync(DateTimeOffset since, CancellationToken ct)
   at Partner.Sync.Workers.FeedWorker.RunAsync(CancellationToken ct)""",
     {"remote_host": "feed.partner-orders.example",
      "remote_certificate": {"not_after": "2026-09-23T23:59:59Z", "issuer": "Partner Orders CA"},
      "observed_at": "2026-09-24T00:04:10Z",
      "our_trust_store_last_changed": "2026-03-02T11:00Z",
      "other_consumers_of_this_host_failing": True,
      "last_deploy": {"service": "partner-sync", "at": "2026-09-10T15:20Z"}}),

    ("ho-02", "bug", "catalog-api", "java",
     "NullPointerException mapping supplier record",
     """java.lang.NullPointerException: Cannot invoke "String.trim()" because the return value of "SupplierRecord.getRegion()" is null
\tat com.acme.catalog.mapping.SupplierMapper.toProduct(SupplierMapper.java:58)
\tat com.acme.catalog.ingest.SupplierIngestor.ingest(SupplierIngestor.java:112)""",
     {"supplier_payload_sample": {"sku": "SP-2231", "price": 12.5, "region": None},
      "supplier_contract": "region is documented as optional since v1 of the feed",
      "records_without_region_last_30d_percent": 4.1,
      "last_deploy": {"service": "catalog-api", "at": "2026-09-24T08:10Z", "change": "use supplier region for tax lookup"},
      "errors_started_at": "2026-09-24T08:12Z"}),

    ("ho-03", "bug", "web-bff", "node",
     "Error: socket hang up (ECONNRESET) calling reviews-api",
     """Error: socket hang up
    at connResetException (node:internal/errors:720:14)
    at Socket.socketOnEnd (node:_http_client:525:23)
    at fetchReviews (/app/src/bff/reviews.js:22:17)
    at renderProduct (/app/src/bff/product.js:88:21)""",
     {"retry_policy": {"attempts": 50, "backoff_ms": 0},
      "outbound_requests_per_second_to_reviews_api": 3900,
      "outbound_requests_per_second_to_reviews_api_7d_avg": 65,
      "reviews_api_error_rate_from_other_callers_percent": 0.1,
      "reviews_api_p50_latency_ms": 41,
      "last_deploy": {"service": "web-bff", "at": "2026-09-24T11:00Z", "change": "add retries to review fetch"},
      "errors_started_at": "2026-09-24T11:03Z"}),

    ("ho-04", "external", "session-store", "python",
     "botocore.errorfactory.ProvisionedThroughputExceededException",
     """Traceback (most recent call last):
  File "/var/task/sessions/store.py", line 41, in put
    self.table.put_item(Item=item)
botocore.errorfactory.ProvisionedThroughputExceededException: An error occurred (ProvisionedThroughputExceededException) when calling the PutItem operation""",
     {"table": "sessions",
      "table_write_capacity_units": 5,
      "prior_table_write_capacity_units": 200,
      "capacity_changed_by": "platform-team infrastructure apply at 2026-09-24T10:00Z",
      "this_service_writes_per_second": 38,
      "this_service_writes_per_second_7d_avg": 41,
      "errors_started_at": "2026-09-24T10:01Z"}),

    ("ho-05", "bug", "metrics-agg", "go",
     "panic: runtime error: integer divide by zero",
     """panic: runtime error: integer divide by zero

goroutine 17 [running]:
metrics/agg.(*Window).Rate(0xc0001c2000)
\t/app/agg/window.go:64 +0x1e
metrics/agg.(*Aggregator).Flush(0xc00010e180)
\t/app/agg/aggregator.go:140 +0x2a4""",
     {"config": {"window_seconds": 0},
      "prior_config": {"window_seconds": 60},
      "config_source": "this service's own config map, changed in the last deploy",
      "last_deploy": {"service": "metrics-agg", "at": "2026-09-24T13:45Z", "change": "make aggregation window configurable"},
      "errors_started_at": "2026-09-24T13:46Z"}),

    ("ho-06", "external", "shipping-api", "dotnet",
     "HttpRequestException: 503 Service Unavailable from carrier rates",
     """System.Net.Http.HttpRequestException: Response status code does not indicate success: 503 (Service Unavailable).
   at System.Net.Http.HttpResponseMessage.EnsureSuccessStatusCode()
   at Shipping.Clients.CarrierRatesClient.QuoteAsync(Parcel parcel, CancellationToken ct)
   at Shipping.Api.RatesController.Post(RateRequest body, CancellationToken ct)""",
     {"carrier_status_page": "incident opened 2026-09-24T15:20Z: degraded rates API, all regions",
      "our_request_shape_last_changed": "2026-07-19",
      "this_service_requests_per_second": 12,
      "this_service_requests_per_second_7d_avg": 11,
      "carrier_error_rate_percent": 87,
      "errors_started_at": "2026-09-24T15:21Z"}),

    ("ho-07", "external", "notify-fn", "node",
     "getaddrinfo ENOTFOUND api.smsgateway.example",
     """Error: getaddrinfo ENOTFOUND api.smsgateway.example
    at GetAddrInfoReqWrap.onlookup [as oncomplete] (node:dns:107:26)
    at sendSms (/app/src/notify/sms.js:15:20)
    at handler (/app/src/notify/index.js:52:14)""",
     {"dns_lookup": "NXDOMAIN from every resolver since 2026-09-24T08:00Z",
      "vendor_notice": "none received; vendor changelog shows domain retired 2026-09-24 without a deprecation period",
      "our_config_for_host_last_changed": "2025-11-02",
      "errors_started_at": "2026-09-24T08:00Z",
      "last_deploy": {"service": "notify-fn", "at": "2026-09-02T14:10Z"}}),

    ("ho-08", "bug", "report-fn", "python",
     "UnicodeDecodeError reading the nightly export",
     """Traceback (most recent call last):
  File "/var/task/report/load.py", line 27, in read_export
    text = handle.read()
UnicodeDecodeError: 'utf-8' codec can't decode byte 0xe9 in position 1042: invalid continuation byte""",
     {"export_file_producer": "report-fn export job (this service)",
      "export_encoding_written": "latin-1",
      "reader_encoding_expected": "utf-8",
      "last_deploy": {"service": "report-fn", "at": "2026-09-23T22:00Z", "change": "switch export writer to platform default encoding"},
      "errors_started_at": "2026-09-24T02:00Z"}),

    ("ho-09", "bug", "checkout-api", "dotnet",
     "TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 2 seconds",
     """System.Threading.Tasks.TaskCanceledException: The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing.
   at System.Net.Http.HttpClient.HandleFailure(Exception e, Boolean telemetryStarted, HttpResponseMessage response, CancellationTokenSource cts, CancellationToken cancellationToken, CancellationTokenSource pendingRequestsCts)
   at Checkout.Clients.FraudClient.ScoreAsync(Order order, CancellationToken ct)
   at Checkout.Handlers.CheckoutHandler.HandleAsync(CheckoutCommand cmd, CancellationToken ct)""",
     {"fraud_api_p50_latency_ms": 3100,
      "fraud_api_p50_latency_ms_7d_avg": 3050,
      "fraud_api_error_rate_percent": 0.0,
      "our_client_timeout_ms": 2000,
      "prior_our_client_timeout_ms": 10000,
      "last_deploy": {"service": "checkout-api", "at": "2026-09-24T09:30Z", "change": "tighten outbound timeouts"},
      "errors_started_at": "2026-09-24T09:31Z"}),

    ("ho-10", "external", "cart-api", "go",
     "context deadline exceeded talking to redis",
     """panic: context deadline exceeded

goroutine 88 [running]:
cart/store.(*RedisStore).Get(0xc000140000, {0xc0002a6000, 0x24})
\t/app/store/redis.go:71 +0x1f2
cart/api.(*Handler).GetCart(0xc00012a0c0, {0x9d3c40, 0xc0001d8000}, 0xc0002c4100)
\t/app/api/cart.go:44 +0x1a5""",
     {"redis_event": "managed cache provider performed primary failover at 2026-09-24T14:02:10Z",
      "errors_started_at": "2026-09-24T14:02Z",
      "errors_ended_at": "2026-09-24T14:05Z",
      "this_service_commands_per_second": 410,
      "this_service_commands_per_second_7d_avg": 395,
      "last_deploy": {"service": "cart-api", "at": "2026-09-15T10:00Z"}}),

    ("ho-11", "external", "thumbnail-fn", "python",
     "PermissionError: [Errno 13] Permission denied: '/tmp/thumb-8812.png'",
     """Traceback (most recent call last):
  File "/var/task/thumbs/render.py", line 33, in render
    with open(path, "wb") as out:
PermissionError: [Errno 13] Permission denied: '/tmp/thumb-8812.png'""",
     {"platform_event": "ops applied a read-only root filesystem policy to every function in the account at 2026-09-24T09:00Z",
      "this_service_temp_writes_per_invocation": 1,
      "this_service_temp_writes_per_invocation_7d_avg": 1,
      "last_deploy": {"service": "thumbnail-fn", "at": "2026-08-30T12:00Z"},
      "errors_started_at": "2026-09-24T09:00Z"}),

    ("ho-12", "bug", "pricing-api", "java",
     "ConcurrentModificationException iterating the tier cache",
     """java.util.ConcurrentModificationException
\tat java.base/java.util.HashMap$HashIterator.nextNode(HashMap.java:1605)
\tat com.acme.pricing.cache.TierCache.snapshot(TierCache.java:47)
\tat com.acme.pricing.QuoteService.quote(QuoteService.java:91)""",
     {"request_concurrency": 14,
      "request_concurrency_7d_avg": 15,
      "last_deploy": {"service": "pricing-api", "at": "2026-09-24T12:20Z", "change": "refresh tier cache on a background thread"},
      "errors_started_at": "2026-09-24T12:41Z",
      "dependencies_reporting_errors": 0}),
]
