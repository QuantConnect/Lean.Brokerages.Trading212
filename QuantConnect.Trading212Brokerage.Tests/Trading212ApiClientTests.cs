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
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212ApiClientTests
    {
        private FakeHttpMessageHandler _handler;
        private Trading212ApiClient _client;

        [SetUp]
        public void SetUp()
        {
            _handler = new FakeHttpMessageHandler();
            _client = new Trading212ApiClient("https://demo.trading212.com", "my-key", "my-secret", _handler, messageReceived: null);
        }

        [TearDown]
        public void TearDown()
        {
            _client.Dispose();
        }

        [Test]
        public void SendsBasicAuthenticationHeader()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);

            _client.GetAccountSummaryAsync().GetAwaiter().GetResult();

            var authorization = _handler.Authorizations[0];
            Assert.That(authorization, Is.Not.Null);
            Assert.That(authorization.Scheme, Is.EqualTo("Basic"));
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Parameter)), Is.EqualTo("my-key:my-secret"));
        }

        [Test]
        public void UsesTheEnvironmentBaseUrl()
        {
            Assert.That(Trading212ApiClient.GetBaseUrl(Trading212Environment.Live), Is.EqualTo("https://live.trading212.com"));
            Assert.That(Trading212ApiClient.GetBaseUrl(Trading212Environment.Demo), Is.EqualTo("https://demo.trading212.com"));
        }

        [Test]
        public void DeserializesAccountSummary()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);

            var summary = _client.GetAccountSummaryAsync().GetAwaiter().GetResult();

            Assert.That(summary.Currency, Is.EqualTo("GBP"));
            Assert.That(summary.Cash.AvailableToTrade, Is.EqualTo(500m));
            Assert.That(summary.Cash.ReservedForOrders, Is.EqualTo(50m));
            Assert.That(_handler.Requests[0].RequestUri.AbsolutePath, Is.EqualTo(Trading212TestData.AccountSummaryPath));
        }

        [Test]
        public void DeserializesPendingOrders()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.PendingMarketOrder);

            var orders = _client.GetPendingOrdersAsync().GetAwaiter().GetResult();

            Assert.That(orders, Has.Count.EqualTo(1));
            Assert.That(orders[0].Id, Is.EqualTo(987654321L));
            Assert.That(orders[0].Ticker, Is.EqualTo("AAPL_US_EQ"));
            Assert.That(orders[0].Type, Is.EqualTo(Trading212OrderType.Market));
            Assert.That(orders[0].Status, Is.EqualTo(Trading212OrderStatus.New));
            Assert.That(orders[0].Side, Is.EqualTo(Trading212OrderSide.Buy));
            Assert.That(orders[0].TimeInForce, Is.EqualTo(Trading212TimeValidity.Day));
            Assert.That(orders[0].CreatedAtUtc, Is.EqualTo(new DateTime(2025, 1, 2, 15, 0, 0, DateTimeKind.Utc)));
        }

        [Test]
        public void AcceptsTheCreationTimeAliasForTheOrderTimestamp()
        {
            // The published OpenAPI description names the field createdAt, while a response captured in
            // the Trading 212 community forum names it creationTime. Both are accepted.
            _handler.EnqueueJson(HttpStatusCode.OK, """
            [ { "id": 1, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "BUY", "quantity": 1, "creationTime": "2025-03-04T10:11:12Z" } ]
            """);

            var orders = _client.GetPendingOrdersAsync().GetAwaiter().GetResult();

            Assert.That(orders[0].CreatedAtUtc, Is.EqualTo(new DateTime(2025, 3, 4, 10, 11, 12, DateTimeKind.Utc)));
        }

        [Test]
        public void PlacesAMarketOrderWithTheSignedQuantity()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, """
            { "id": 42, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "SELL", "quantity": -3.5 }
            """);

            var order = _client.PlaceMarketOrderAsync(new Trading212MarketOrderRequest
            {
                Ticker = "AAPL_US_EQ",
                Quantity = -3.5m,
                ExtendedHours = false
            }).GetAwaiter().GetResult();

            Assert.That(order.Id, Is.EqualTo(42L));
            Assert.That(_handler.Requests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(_handler.Requests[0].RequestUri.AbsolutePath, Is.EqualTo("/api/v0/equity/orders/market"));
            Assert.That(_handler.RequestBodies[0], Does.Contain("\"quantity\":-3.5"));
            Assert.That(_handler.RequestBodies[0], Does.Contain("\"ticker\":\"AAPL_US_EQ\""));
        }

        [Test]
        public void SerializesTimeValidityInTheWireFormat()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, """{ "id": 7, "ticker": "AAPL_US_EQ", "type": "LIMIT", "status": "NEW", "side": "BUY", "quantity": 1 }""");

            _client.PlaceLimitOrderAsync(new Trading212LimitOrderRequest
            {
                Ticker = "AAPL_US_EQ",
                Quantity = 1m,
                LimitPrice = 100.5m,
                TimeValidity = Trading212TimeValidity.GoodTillCancel
            }).GetAwaiter().GetResult();

            Assert.That(_handler.RequestBodies[0], Does.Contain("\"timeValidity\":\"GOOD_TILL_CANCEL\""));
        }

        [Test]
        public void SurfacesTheBrokerMessageOfARejectedOrder()
        {
            // The order rejection payload carries the broker's reason in code and clarification.
            _handler.EnqueueJson(HttpStatusCode.BadRequest, """{ "code": "SellingEquityNotOwned", "clarification": "SellingEquityNotOwned" }""");

            var exception = Assert.Throws<Trading212ApiException>(() =>
                _client.PlaceMarketOrderAsync(new Trading212MarketOrderRequest { Ticker = "AAPL_US_EQ", Quantity = -1m }).GetAwaiter().GetResult());

            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(exception.ErrorCode, Is.EqualTo("SellingEquityNotOwned"), "the code field is reported as-is");
            Assert.That(exception.Message, Does.Contain("SellingEquityNotOwned"));
            Assert.That(exception.RawBody, Does.Contain("SellingEquityNotOwned"));
        }

        [Test]
        public void SurfacesAnRfc7807Problem()
        {
            _handler.EnqueueJson(HttpStatusCode.BadRequest,
                """{ "type": "/api-errors/invalid-request", "title": "The request is invalid", "status": 400, "detail": "Invalid payload", "traceId": "abc123" }""");

            var exception = Assert.Throws<Trading212ApiException>(() => _client.GetPendingOrdersAsync().GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("The request is invalid"));
            Assert.That(exception.Message, Does.Contain("Invalid payload"));
            Assert.That(exception.Message, Does.Contain("abc123"));
            Assert.That(exception.ErrorCode, Is.EqualTo("invalid-request"), "the reason identifier comes from the type slug");
        }

        [Test]
        public void SurfacesARealOrderRejection()
        {
            // Observed against the live API when selling shares the account does not own.
            _handler.EnqueueJson(HttpStatusCode.BadRequest,
                """{"type":"/api-errors/selling-equity-not-owned","title":"Error while placing the order","status":400,"detail":"Selling more equities than owned, owned: 0.0","traceId":"a4745b9aa7f001021eb7fd5b1e7e644c"}""");

            var exception = Assert.Throws<Trading212ApiException>(() => _client.PlaceMarketOrderAsync(
                new Trading212MarketOrderRequest { Ticker = "AAPL_US_EQ", Quantity = -100m }).GetAwaiter().GetResult());

            Assert.That(exception.ErrorCode, Is.EqualTo("selling-equity-not-owned"));
            Assert.That(exception.Message, Does.Contain("Selling more equities than owned, owned: 0.0"));
            Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public void SurfacesAWrappedRfc7807Problem()
        {
            _handler.EnqueueJson(HttpStatusCode.BadRequest,
                """{ "success": false, "error": { "type": "/api-errors/quantity-precision-mismatch", "title": "Error while placing the order", "status": 400, "detail": "invalid quantity precision 4" } }""");

            var exception = Assert.Throws<Trading212ApiException>(() => _client.GetPendingOrdersAsync().GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Error while placing the order"));
            Assert.That(exception.Message, Does.Contain("invalid quantity precision 4"));
        }

        [Test]
        public void KeepsTheRawBodyWhenTheErrorIsNotJson()
        {
            _handler.EnqueueJson(HttpStatusCode.Forbidden, "<html>Forbidden</html>");

            var exception = Assert.Throws<Trading212ApiException>(() => _client.GetPendingOrdersAsync().GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Forbidden"));
            Assert.That(exception.RawBody, Is.EqualTo("<html>Forbidden</html>"));
        }

        [Test]
        public void RetriesAfterATooManyRequestsResponse()
        {
            _handler.EnqueueJson(HttpStatusCode.TooManyRequests, "", response => response.Headers.Add("Retry-After", "0"));
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);

            var summary = _client.GetAccountSummaryAsync().GetAwaiter().GetResult();

            Assert.That(summary.Currency, Is.EqualTo("GBP"));
            Assert.That(_handler.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void RetriesAPlacementAfterATooManyRequestsResponse()
        {
            // A 429 is the only response that is safe to retry for a placement: the request was rejected
            // before the broker processed it.
            _handler.EnqueueJson(HttpStatusCode.TooManyRequests, "", response => response.Headers.Add("Retry-After", "0"));
            _handler.EnqueueJson(HttpStatusCode.OK, """{ "id": 43, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "BUY", "quantity": 1 }""");

            var order = _client.PlaceMarketOrderAsync(new Trading212MarketOrderRequest { Ticker = "AAPL_US_EQ", Quantity = 1m })
                .GetAwaiter().GetResult();

            Assert.That(order.Id, Is.EqualTo(43L));
            Assert.That(_handler.Requests, Has.Count.EqualTo(2));
        }

        [TestCase(HttpStatusCode.GatewayTimeout)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.RequestTimeout)]
        public void DoesNotRetryAPlacementAfterAnAmbiguousResponse(HttpStatusCode statusCode)
        {
            // A gateway error does not say whether the order was accepted, so retrying could place it twice.
            _handler.EnqueueJson(statusCode, """{ "title": "upstream error" }""");

            var exception = Assert.Throws<Trading212OrderPlacementException>(() =>
                _client.PlaceMarketOrderAsync(new Trading212MarketOrderRequest { Ticker = "AAPL_US_EQ", Quantity = 1m })
                    .GetAwaiter().GetResult());

            Assert.That(exception.StatusCode, Is.EqualTo(statusCode));
            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void RetriesAReadAfterAGatewayError()
        {
            // Reads are safe to repeat, so they keep the wider retry policy.
            _handler.EnqueueJson(HttpStatusCode.BadGateway, """{ "title": "upstream error" }""");
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);

            var summary = _client.GetAccountSummaryAsync().GetAwaiter().GetResult();

            Assert.That(summary.Currency, Is.EqualTo("GBP"));
            Assert.That(_handler.Requests, Has.Count.EqualTo(2));
        }

        [Test]
        public void DoesNotRetryAClientError()
        {
            _handler.EnqueueJson(HttpStatusCode.BadRequest, """{ "code": "InstrumentNotFound" }""");

            Assert.Throws<Trading212ApiException>(() => _client.GetPendingOrdersAsync().GetAwaiter().GetResult());

            Assert.That(_handler.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void DeserializesABareArrayResponse()
        {
            // positions, orders, metadata/instruments and metadata/exchanges answer a bare JSON array, not
            // {"items":[...]} as the published schema notation suggests.
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.PendingMarketOrder);

            var orders = _client.GetPendingOrdersAsync().GetAwaiter().GetResult();

            Assert.That(orders, Has.Count.EqualTo(1));
            Assert.That(orders[0].Ticker, Is.EqualTo("AAPL_US_EQ"));
        }

        [Test]
        public void DeserializesAnItemsEnvelopeResponse()
        {
            // The history endpoints do wrap their items. Being permissive means a change on either side of
            // the API does not break the plugin.
            _handler.EnqueueJson(HttpStatusCode.OK, """{ "items": [ { "id": 1, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "quantity": 1 } ], "nextPagePath": null }""");

            var orders = _client.GetPendingOrdersAsync().GetAwaiter().GetResult();

            Assert.That(orders, Has.Count.EqualTo(1));
        }

        [Test]
        public void DeserializesAnEmptyArrayResponse()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.EmptyArray);

            Assert.That(_client.GetPositionsAsync().GetAwaiter().GetResult(), Is.Empty);
        }

        [Test]
        public void SurfacesTheBrokerMessageOfARateLimitedRequest()
        {
            // Observed against the live API: a rate limited request answers this envelope, which carries its
            // message in errorMessage and context.type rather than in title/detail.
            // A 429 is retried, so queue one response per attempt: the initial call plus the retry budget.
            for (var attempt = 0; attempt <= 3; attempt++)
            {
                _handler.EnqueueJson(HttpStatusCode.TooManyRequests, Trading212TestData.TooManyRequestsError);
            }

            var exception = Assert.Throws<Trading212ApiException>(() => _client.GetInstrumentsAsync().GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("too many requests"));
            Assert.That(exception.Message, Does.Contain("TooManyRequests"));
        }

        [Test]
        public void CancelsAnOrderWithDelete()
        {
            _handler.EnqueueEmpty(HttpStatusCode.OK);

            _client.CancelOrderAsync(987654321L).GetAwaiter().GetResult();

            Assert.That(_handler.Requests[0].Method, Is.EqualTo(HttpMethod.Delete));
            Assert.That(_handler.Requests[0].RequestUri.AbsolutePath, Is.EqualTo("/api/v0/equity/orders/987654321"));
        }

        [Test]
        public void FollowsThePaginationCursorOfTheHistoricalOrders()
        {
            _handler.EnqueueJson(HttpStatusCode.OK, """
            { "items": [ { "order": { "id": 1, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "FILLED", "quantity": 1, "filledQuantity": 1 } } ], "nextPagePath": "/api/v0/equity/history/orders?limit=50&cursor=111" }
            """);
            _handler.EnqueueJson(HttpStatusCode.OK, """
            { "items": [ { "order": { "id": 2, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "FILLED", "quantity": 1, "filledQuantity": 1 } } ], "nextPagePath": null }
            """);

            var history = _client.GetHistoricalOrdersAsync(maxPages: 2).GetAwaiter().GetResult();

            Assert.That(history, Has.Count.EqualTo(2));
            Assert.That(_handler.Requests, Has.Count.EqualTo(2));
            Assert.That(_handler.Requests[1].RequestUri.PathAndQuery, Is.EqualTo("/api/v0/equity/history/orders?limit=50&cursor=111"));
        }
    }
}
