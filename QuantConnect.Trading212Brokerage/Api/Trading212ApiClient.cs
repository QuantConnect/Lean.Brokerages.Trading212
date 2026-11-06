/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Logging;
using QuantConnect.Util;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Api
{
    /// <summary>
    /// The HTTP client for the Trading 212 Public API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every request is authenticated with HTTP Basic authentication, using the API key as the user name
    /// and the API secret as the password. See
    /// <see href="https://docs.trading212.com/api/section/authentication"/>.
    /// </para>
    /// <para>
    /// Requests are paced with the documented per-endpoint rate limits and retried when the API answers
    /// <c>429 Too Many Requests</c>, honouring the <c>Retry-After</c> and <c>x-ratelimit-reset</c> headers.
    /// </para>
    /// <para>
    /// Errors are never swallowed: a non-success response throws a <see cref="Trading212ApiException"/>
    /// carrying the broker's own message and the raw body.
    /// </para>
    /// </remarks>
    public class Trading212ApiClient : IDisposable
    {
        /// <summary>
        /// The base URL of the live (real money) environment.
        /// </summary>
        public const string LiveBaseUrl = "https://live.trading212.com";

        /// <summary>
        /// The base URL of the demo (paper trading) environment.
        /// </summary>
        public const string DemoBaseUrl = "https://demo.trading212.com";

        /// <summary>
        /// The path prefix shared by every endpoint of the Public API.
        /// </summary>
        private const string ApiPathPrefix = "/api/v0/equity";

        /// <summary>
        /// The maximum number of times a request is retried after a retryable response.
        /// </summary>
        private const int MaxRetries = 3;

        /// <summary>
        /// The JSON settings used for every request and response.
        /// </summary>
        private static readonly JsonSerializerSettings SerializerSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        /// <summary>
        /// The underlying HTTP client.
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// The base URL every resource is appended to.
        /// </summary>
        private readonly string _baseUrl;

        /// <summary>
        /// The per-endpoint rate gates, created on first use.
        /// </summary>
        private readonly ConcurrentDictionary<string, RateGate> _rateGates = new();

        /// <summary>
        /// Whether the documented rate limits are enforced. Disabled by the unit tests so they do not
        /// have to wait for the real pacing.
        /// </summary>
        private readonly bool _rateLimitingEnabled;

        /// <summary>
        /// Raised when the API reports a condition the user should be aware of.
        /// </summary>
        private event EventHandler<BrokerageMessageEvent> MessageReceived;

        /// <summary>
        /// Initializes a new instance of the <see cref="Trading212ApiClient"/> class.
        /// </summary>
        /// <param name="apiKey">The API key, used as the Basic authentication user name.</param>
        /// <param name="apiSecret">The API secret, used as the Basic authentication password.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="messageReceived">The handler notified of conditions the user should be aware of.</param>
        public Trading212ApiClient(string apiKey, string apiSecret, Trading212Environment environment, EventHandler<BrokerageMessageEvent> messageReceived)
            : this(GetBaseUrl(environment), apiKey, apiSecret, new HttpClientHandler(), messageReceived, rateLimitingEnabled: true)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Trading212ApiClient"/> class against an explicit
        /// base URL and transport. Intended for unit testing, so a fake handler can be injected.
        /// </summary>
        /// <param name="baseUrl">The base URL to use.</param>
        /// <param name="apiKey">The API key, used as the Basic authentication user name.</param>
        /// <param name="apiSecret">The API secret, used as the Basic authentication password.</param>
        /// <param name="handler">The transport to send requests through.</param>
        /// <param name="messageReceived">The handler notified of conditions the user should be aware of.</param>
        /// <param name="rateLimitingEnabled">Whether to enforce the documented rate limits.</param>
        internal Trading212ApiClient(string baseUrl, string apiKey, string apiSecret, HttpMessageHandler handler,
            EventHandler<BrokerageMessageEvent> messageReceived, bool rateLimitingEnabled = false)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient(CreateHandler(apiKey, apiSecret, handler ?? new HttpClientHandler()))
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _rateLimitingEnabled = rateLimitingEnabled;
            MessageReceived += messageReceived;
        }

        /// <summary>
        /// Returns the base URL of the requested environment.
        /// </summary>
        /// <param name="environment">The environment.</param>
        /// <returns>The base URL, without a trailing slash.</returns>
        public static string GetBaseUrl(Trading212Environment environment)
        {
            return environment == Trading212Environment.Demo ? DemoBaseUrl : LiveBaseUrl;
        }

        /// <summary>
        /// Wraps a transport so the Basic authentication header is applied to every request.
        /// </summary>
        /// <param name="apiKey">The API key.</param>
        /// <param name="apiSecret">The API secret.</param>
        /// <param name="innerHandler">The transport requests are delegated to.</param>
        /// <returns>The configured handler.</returns>
        private static HttpMessageHandler CreateHandler(string apiKey, string apiSecret, HttpMessageHandler innerHandler)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{apiSecret}"));
            return new AuthenticatedHandler(innerHandler, new AuthenticationHeaderValue("Basic", credentials));
        }

        #region Endpoints

        /// <summary>
        /// Gets the account summary, including the cash breakdown and the primary account currency.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The account summary.</returns>
        public Task<Trading212AccountSummary> GetAccountSummaryAsync(CancellationToken cancellationToken = default)
        {
            return RequestAsync<Trading212AccountSummary>(HttpMethod.Get, $"{ApiPathPrefix}/account/summary",
                Trading212RateLimits.AccountSummary, cancellationToken);
        }

        /// <summary>
        /// Gets all open positions.
        /// </summary>
        /// <param name="ticker">An optional ticker filter.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The open positions.</returns>
        public async Task<List<Trading212Position>> GetPositionsAsync(string ticker = null, CancellationToken cancellationToken = default)
        {
            var resource = $"{ApiPathPrefix}/positions";
            if (!string.IsNullOrEmpty(ticker))
            {
                resource += $"?ticker={Uri.EscapeDataString(ticker)}";
            }

            return await RequestListAsync<Trading212Position>(HttpMethod.Get, resource,
                Trading212RateLimits.Positions, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets every instrument the account can trade. This is the source of the symbol mapping.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The tradable instruments.</returns>
        public async Task<List<Trading212TradableInstrument>> GetInstrumentsAsync(CancellationToken cancellationToken = default)
        {
            return await RequestListAsync<Trading212TradableInstrument>(HttpMethod.Get,
                $"{ApiPathPrefix}/metadata/instruments", Trading212RateLimits.Instruments, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets every exchange and its working schedules.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The exchanges.</returns>
        public async Task<List<Trading212Exchange>> GetExchangesAsync(CancellationToken cancellationToken = default)
        {
            return await RequestListAsync<Trading212Exchange>(HttpMethod.Get,
                $"{ApiPathPrefix}/metadata/exchanges", Trading212RateLimits.Exchanges, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets every pending order: orders that have not yet been filled, cancelled or expired.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pending orders.</returns>
        public async Task<List<Trading212Order>> GetPendingOrdersAsync(CancellationToken cancellationToken = default)
        {
            return await RequestListAsync<Trading212Order>(HttpMethod.Get,
                $"{ApiPathPrefix}/orders", Trading212RateLimits.PendingOrders, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a single pending order by its identifier.
        /// </summary>
        /// <param name="orderId">The broker order identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The pending order.</returns>
        public Task<Trading212Order> GetOrderByIdAsync(long orderId, CancellationToken cancellationToken = default)
        {
            return RequestAsync<Trading212Order>(HttpMethod.Get, $"{ApiPathPrefix}/orders/{orderId}",
                Trading212RateLimits.OrderById, cancellationToken);
        }

        /// <summary>
        /// Requests the cancellation of a pending order.
        /// </summary>
        /// <remarks>
        /// A successful response means the cancellation request was accepted, not that the order is
        /// already cancelled. The order's final state is observed by the order update poller.
        /// </remarks>
        /// <param name="orderId">The broker order identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the request has been accepted.</returns>
        public Task CancelOrderAsync(long orderId, CancellationToken cancellationToken = default)
        {
            return RequestNoContentAsync(HttpMethod.Delete, $"{ApiPathPrefix}/orders/{orderId}",
                Trading212RateLimits.CancelOrder, cancellationToken);
        }

        /// <summary>
        /// Places a market order.
        /// </summary>
        /// <param name="request">The order request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The placed order as the broker recorded it.</returns>
        public Task<Trading212Order> PlaceMarketOrderAsync(Trading212MarketOrderRequest request, CancellationToken cancellationToken = default)
        {
            return PlaceOrderAsync($"{ApiPathPrefix}/orders/market", request, cancellationToken);
        }

        /// <summary>
        /// Places a limit order.
        /// </summary>
        /// <param name="request">The order request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The placed order as the broker recorded it.</returns>
        public Task<Trading212Order> PlaceLimitOrderAsync(Trading212LimitOrderRequest request, CancellationToken cancellationToken = default)
        {
            return PlaceOrderAsync($"{ApiPathPrefix}/orders/limit", request, cancellationToken);
        }

        /// <summary>
        /// Places a stop order.
        /// </summary>
        /// <param name="request">The order request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The placed order as the broker recorded it.</returns>
        public Task<Trading212Order> PlaceStopOrderAsync(Trading212StopOrderRequest request, CancellationToken cancellationToken = default)
        {
            return PlaceOrderAsync($"{ApiPathPrefix}/orders/stop", request, cancellationToken);
        }

        /// <summary>
        /// Places a stop-limit order.
        /// </summary>
        /// <param name="request">The order request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The placed order as the broker recorded it.</returns>
        public Task<Trading212Order> PlaceStopLimitOrderAsync(Trading212StopLimitOrderRequest request, CancellationToken cancellationToken = default)
        {
            return PlaceOrderAsync($"{ApiPathPrefix}/orders/stop_limit", request, cancellationToken);
        }

        /// <summary>
        /// Gets the historical orders, newest first, following the cursor until <paramref name="maxPages"/>
        /// pages have been read or the last page is reached.
        /// </summary>
        /// <remarks>
        /// The endpoint exposes no time filter, only a ticker filter and a cursor, so callers that look
        /// for a specific order bound the work with <paramref name="maxPages"/>.
        /// </remarks>
        /// <param name="maxPages">The maximum number of pages to read. Must be at least 1.</param>
        /// <param name="ticker">An optional ticker filter.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The historical orders and their fills.</returns>
        public async Task<List<Trading212HistoricalOrder>> GetHistoricalOrdersAsync(int maxPages = 1, string ticker = null, CancellationToken cancellationToken = default)
        {
            var results = new List<Trading212HistoricalOrder>();
            var resource = $"{ApiPathPrefix}/history/orders?limit=50";
            if (!string.IsNullOrEmpty(ticker))
            {
                resource += $"&ticker={Uri.EscapeDataString(ticker)}";
            }

            for (var page = 0; page < Math.Max(1, maxPages); page++)
            {
                var response = await RequestAsync<Trading212PaginatedResponse<Trading212HistoricalOrder>>(HttpMethod.Get, resource,
                    Trading212RateLimits.HistoricalOrders, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    results.AddRange(response.Items);
                }

                if (string.IsNullOrEmpty(response?.NextPagePath))
                {
                    break;
                }

                resource = response.NextPagePath;
            }

            return results;
        }

        #endregion

        #region HTTP plumbing

        /// <summary>
        /// Sends an order placement request and returns the order as the broker recorded it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A placement is only retried on <c>429 Too Many Requests</c>, where the request is known not to have
        /// been processed. A timeout, a 408 or a 5xx from a gateway says nothing about whether the order was
        /// created, so retrying could place the same order twice; those outcomes are reported as a
        /// <see cref="Trading212OrderPlacementException"/> for the caller to reconcile against the broker's
        /// order list.
        /// </para>
        /// </remarks>
        /// <typeparam name="TRequest">The order request type.</typeparam>
        /// <param name="resource">The relative resource.</param>
        /// <param name="request">The order request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The placed order.</returns>
        private async Task<Trading212Order> PlaceOrderAsync<TRequest>(string resource, TRequest request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendWithRetriesAsync(HttpMethod.Post, resource, request, Trading212RateLimits.PlaceOrder,
                    cancellationToken, retryServerErrors: false).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // No response at all: the order may still have reached the broker.
                throw new Trading212OrderPlacementException(resource, statusCode: null,
                    $"The order request to [{resource}] failed before a response was received: {exception.Message}", exception);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (IsAmbiguousOutcome(response.StatusCode))
                    {
                        throw new Trading212OrderPlacementException(resource, response.StatusCode,
                            $"Trading 212 answered {(int)response.StatusCode} ({response.StatusCode}) for [{resource}], " +
                            "so the order may or may not have been placed.",
                            CreateException(response.StatusCode, resource, body));
                    }

                    throw CreateException(response.StatusCode, resource, body);
                }

                return DeserializeBody<Trading212Order>(body, HttpMethod.Post, resource);
            }
        }

        /// <summary>
        /// Sends a request and deserializes the response body.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the response into.</typeparam>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource, including any query string.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The deserialized response.</returns>
        private Task<T> RequestAsync<T>(HttpMethod method, string resource, string rateLimitKey, CancellationToken cancellationToken)
        {
            return RequestAsync<T>(method, resource, jsonBody: null, rateLimitKey, cancellationToken);
        }

        /// <summary>
        /// Sends a request with a JSON body and deserializes the response body.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the response into.</typeparam>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource.</param>
        /// <param name="jsonBody">The object to serialize as the request body.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The deserialized response.</returns>
        private async Task<T> RequestAsync<T>(HttpMethod method, string resource, object jsonBody, string rateLimitKey, CancellationToken cancellationToken)
        {
            var body = await SendAndReadAsync(method, resource, jsonBody, rateLimitKey, cancellationToken).ConfigureAwait(false);

            return DeserializeBody<T>(body, method, resource);
        }

        /// <summary>
        /// Deserializes a response body.
        /// </summary>
        /// <typeparam name="T">The type to deserialize into.</typeparam>
        /// <param name="body">The raw response body.</param>
        /// <param name="method">The HTTP method, for the error message.</param>
        /// <param name="resource">The relative resource, for the error message.</param>
        /// <returns>The deserialized response.</returns>
        private static T DeserializeBody<T>(string body, HttpMethod method, string resource)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return default;
            }

            try
            {
                return JsonConvert.DeserializeObject<T>(body, SerializerSettings);
            }
            catch (JsonException exception)
            {
                Log.Error($"{nameof(Trading212ApiClient)}.{nameof(RequestAsync)}: Failed to deserialize the response of [{method}]({resource}): {exception.Message}. Body: {body}");
                throw;
            }
        }

        /// <summary>
        /// Sends a request whose response is a list of items and deserializes it.
        /// </summary>
        /// <remarks>
        /// The published schema notation for the list endpoints is ambiguous: the <c>items</c> key in the
        /// OpenAPI description is the JSON-Schema element definition, not an object property. In practice
        /// <c>positions</c>, <c>orders</c>, <c>metadata/instruments</c> and <c>metadata/exchanges</c> answer a
        /// bare JSON array, while the <c>history</c> endpoints answer <c>{"items":[...],"nextPagePath":...}</c>.
        /// Both shapes are accepted so a change on either side does not break the plugin.
        /// </remarks>
        /// <typeparam name="T">The type of the items.</typeparam>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The items, never <c>null</c>.</returns>
        private async Task<List<T>> RequestListAsync<T>(HttpMethod method, string resource, string rateLimitKey, CancellationToken cancellationToken)
        {
            var body = await SendAndReadAsync(method, resource, jsonBody: null, rateLimitKey, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return new List<T>();
            }

            try
            {
                var token = JToken.Parse(body);
                var items = token switch
                {
                    JArray array => array,
                    JObject json when json["items"] is JArray array => array,
                    _ => throw new JsonSerializationException(
                        $"Expected a JSON array or an object with an 'items' array, but got: {token.Type}")
                };

                return items.ToObject<List<T>>(JsonSerializer.Create(SerializerSettings)) ?? new List<T>();
            }
            catch (JsonException exception)
            {
                Log.Error($"{nameof(Trading212ApiClient)}.{nameof(RequestListAsync)}: Failed to deserialize the response of [{method}]({resource}): {exception.Message}. Body: {body}");
                throw;
            }
        }

        /// <summary>
        /// Sends a request and returns the response body, throwing when the request failed.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource.</param>
        /// <param name="jsonBody">The object to serialize as the request body, if any.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The raw response body.</returns>
        private async Task<string> SendAndReadAsync(HttpMethod method, string resource, object jsonBody, string rateLimitKey, CancellationToken cancellationToken)
        {
            using var response = await SendWithRetriesAsync(method, resource, jsonBody, rateLimitKey, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateException(response.StatusCode, resource, body);
            }

            return body;
        }

        /// <summary>
        /// Sends a request whose success response has no body.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the request has succeeded.</returns>
        private async Task RequestNoContentAsync(HttpMethod method, string resource, string rateLimitKey, CancellationToken cancellationToken)
        {
            using var response = await SendWithRetriesAsync(method, resource, jsonBody: null, rateLimitKey, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw CreateException(response.StatusCode, resource, body);
            }
        }

        /// <summary>
        /// Sends a request, pacing it against the endpoint's rate limit and retrying the responses that
        /// indicate a transient condition.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="resource">The relative resource.</param>
        /// <param name="jsonBody">The object to serialize as the request body, if any.</param>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The response message, which the caller disposes.</returns>
        private async Task<HttpResponseMessage> SendWithRetriesAsync(HttpMethod method, string resource, object jsonBody, string rateLimitKey, CancellationToken cancellationToken, bool retryServerErrors = true)
        {
            for (var attempt = 0; ; attempt++)
            {
                await WaitForRateLimitAsync(rateLimitKey, cancellationToken).ConfigureAwait(false);

                using var request = new HttpRequestMessage(method, $"{_baseUrl}{resource}");
                if (jsonBody != null)
                {
                    var payload = JsonConvert.SerializeObject(jsonBody, SerializerSettings);
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Log.Error($"{nameof(Trading212ApiClient)}.{nameof(SendWithRetriesAsync)}: Request [{method}]({resource}) failed: {exception.Message}");
                    throw;
                }

                if (!IsRetryable(response.StatusCode, retryServerErrors) || attempt >= MaxRetries)
                {
                    return response;
                }

                var delay = GetRetryDelay(response);
                Log.Trace($"{nameof(Trading212ApiClient)}.{nameof(SendWithRetriesAsync)}: [{method}]({resource}) returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0.###}s (attempt {attempt + 1}/{MaxRetries}).");
                response.Dispose();

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits until the endpoint's documented rate limit allows another request.
        /// </summary>
        /// <param name="rateLimitKey">The rate limit key of the endpoint.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when a request may be sent.</returns>
        private async Task WaitForRateLimitAsync(string rateLimitKey, CancellationToken cancellationToken)
        {
            if (!_rateLimitingEnabled)
            {
                return;
            }

            var gate = _rateGates.GetOrAdd(rateLimitKey, key =>
                new RateGate(Trading212RateLimits.GetOccurrences(key), Trading212RateLimits.GetPeriod(key)));

            // RateGate blocks; poll with a short timeout so cancellation is still honoured.
            while (!gate.WaitToProceed(TimeSpan.FromMilliseconds(200)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        /// <summary>
        /// Returns whether a status code indicates a transient condition worth retrying.
        /// </summary>
        /// <remarks>
        /// A <c>429</c> is always worth retrying: the request was rejected before it was processed. The other
        /// codes are only retried for requests that are safe to repeat, because a 408 or a gateway error on a
        /// mutating request does not say whether the broker applied it.
        /// </remarks>
        /// <param name="statusCode">The status code.</param>
        /// <param name="retryServerErrors">Whether server-side failures may be retried.</param>
        /// <returns><c>true</c> when the request should be retried.</returns>
        private static bool IsRetryable(HttpStatusCode statusCode, bool retryServerErrors)
        {
            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                return true;
            }

            if (!retryServerErrors)
            {
                return false;
            }

            switch (statusCode)
            {
                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.InternalServerError:
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.GatewayTimeout:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns whether a status code leaves it unknown whether a mutating request was applied.
        /// </summary>
        /// <param name="statusCode">The status code.</param>
        /// <returns><c>true</c> when the outcome is unknown.</returns>
        private static bool IsAmbiguousOutcome(HttpStatusCode statusCode)
        {
            switch (statusCode)
            {
                case HttpStatusCode.RequestTimeout:
                case HttpStatusCode.InternalServerError:
                case HttpStatusCode.BadGateway:
                case HttpStatusCode.ServiceUnavailable:
                case HttpStatusCode.GatewayTimeout:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns how long to wait before retrying, using the <c>Retry-After</c> header, the
        /// <c>x-ratelimit-reset</c> header, or a one second default.
        /// </summary>
        /// <param name="response">The response that asked for a retry.</param>
        /// <returns>The delay before the next attempt.</returns>
        private static TimeSpan GetRetryDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue)
                {
                    return Clamp(retryAfter.Delta.Value);
                }

                if (retryAfter.Date.HasValue)
                {
                    return Clamp(retryAfter.Date.Value - DateTimeOffset.UtcNow);
                }
            }

            if (response.Headers.TryGetValues("x-ratelimit-reset", out var resetValues))
            {
                var raw = resetValues.FirstOrDefault();
                if (long.TryParse(raw, out var reset))
                {
                    // The header is documented as a Unix timestamp in seconds; accept milliseconds too.
                    var resetTime = reset > 100000000000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(reset)
                        : DateTimeOffset.FromUnixTimeSeconds(reset);
                    return Clamp(resetTime - DateTimeOffset.UtcNow);
                }
            }

            return TimeSpan.FromSeconds(1);
        }

        /// <summary>
        /// Keeps a retry delay inside a sane range.
        /// </summary>
        /// <param name="delay">The proposed delay.</param>
        /// <returns>The clamped delay.</returns>
        private static TimeSpan Clamp(TimeSpan delay)
        {
            if (delay <= TimeSpan.Zero)
            {
                return TimeSpan.FromSeconds(1);
            }

            return delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay;
        }

        /// <summary>
        /// Builds the exception for a non-success response, preserving the broker's own message.
        /// </summary>
        /// <param name="statusCode">The HTTP status code.</param>
        /// <param name="resource">The relative resource that was requested.</param>
        /// <param name="body">The raw response body.</param>
        /// <returns>The exception to throw.</returns>
        private Trading212ApiException CreateException(HttpStatusCode statusCode, string resource, string body)
        {
            Trading212Error error = null;
            if (!string.IsNullOrWhiteSpace(body))
            {
                try
                {
                    error = JsonConvert.DeserializeObject<Trading212Error>(body, SerializerSettings);
                }
                catch (JsonException)
                {
                    // The body was not JSON; the raw body is still surfaced below.
                }
            }

            var fallback = $"Trading 212 returned {(int)statusCode} ({statusCode}) for [{resource}]. Body: {body}";
            var message = error?.GetMessage(fallback) ?? fallback;

            var exception = new Trading212ApiException(statusCode, resource, body, error, message);
            Log.Error($"{nameof(Trading212ApiClient)}: {message}");

            // Some conditions are worth telling the algorithm about even when the caller handles the
            // exception, for example a rate limit that could not be satisfied within the retry budget.
            if (statusCode == HttpStatusCode.TooManyRequests)
            {
                MessageReceived?.Invoke(this, new BrokerageMessageEvent(BrokerageMessageType.Warning, "RateLimitExceeded", message));
            }

            return exception;
        }

        #endregion

        /// <summary>
        /// Disposes the underlying HTTP client.
        /// </summary>
        public void Dispose()
        {
            _httpClient?.DisposeSafely();
            foreach (var gate in _rateGates.Values)
            {
                gate.DisposeSafely();
            }

            _rateGates.Clear();
        }

        /// <summary>
        /// A message handler that applies a fixed header, used for the Basic authentication header.
        /// </summary>
        private sealed class AuthenticatedHandler : DelegatingHandler
        {
            /// <summary>
            /// The header applied to every outgoing request.
            /// </summary>
            private readonly AuthenticationHeaderValue _authorization;

            /// <summary>
            /// Initializes a new instance of the <see cref="AuthenticatedHandler"/> class.
            /// </summary>
            /// <param name="innerHandler">The handler requests are delegated to.</param>
            /// <param name="authorization">The authorization header to apply.</param>
            public AuthenticatedHandler(HttpMessageHandler innerHandler, AuthenticationHeaderValue authorization)
                : base(innerHandler)
            {
                _authorization = authorization;
            }

            /// <inheritdoc/>
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Authorization = _authorization;
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
