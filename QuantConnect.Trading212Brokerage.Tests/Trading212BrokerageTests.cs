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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.Models;
using QuantConnect.Orders;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212BrokerageTests
    {
        private static readonly Symbol Apple = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

        private FakeHttpMessageHandler _handler;
        private FakeOrderProvider _orderProvider;
        private Trading212Brokerage _brokerage;

        [TearDown]
        public void TearDown()
        {
            if (_brokerage == null)
            {
                return;
            }

            _brokerage.Disconnect();
            _brokerage.Dispose();
            _brokerage = null;
        }

        /// <summary>
        /// Creates a brokerage connected to a fake API, queuing the responses <see cref="Trading212Brokerage.Connect"/>
        /// consumes: the account summary, the instruments and the pending orders.
        /// </summary>
        private Trading212Brokerage CreateConnectedBrokerage(string pendingOrders = Trading212TestData.EmptyArray)
        {
            _handler = new FakeHttpMessageHandler();
            _orderProvider = new FakeOrderProvider();

            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.Instruments);
            _handler.EnqueueJson(HttpStatusCode.OK, pendingOrders);

            var client = new Trading212ApiClient("https://demo.trading212.com", "key", "secret", _handler, messageReceived: null);
            _brokerage = new Trading212Brokerage(client, _orderProvider);
            _brokerage.Connect();

            return _brokerage;
        }

        [Test]
        public void ConnectsAndLoadsTheInstrumentMapping()
        {
            var brokerage = CreateConnectedBrokerage();

            Assert.That(brokerage.IsConnected, Is.True);
            Assert.That(brokerage.SymbolMapper.InstrumentCount, Is.EqualTo(7));
            Assert.That(brokerage.SymbolMapper.MappedInstrumentCount, Is.EqualTo(4));
        }

        [Test]
        public void GetCashBalanceAddsTheCashReservedForPendingOrders()
        {
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);

            var cash = brokerage.GetCashBalance();

            Assert.That(cash, Has.Count.EqualTo(1));
            Assert.That(cash[0].Currency, Is.EqualTo("GBP"));
            Assert.That(cash[0].Amount, Is.EqualTo(550m));
            Assert.That(brokerage.AccountBaseCurrency, Is.EqualTo("GBP"));
        }

        [Test]
        public void GetAccountHoldingsMapsAPosition()
        {
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueJson(HttpStatusCode.OK, """
            [
                {
                  "averagePricePaid": 100.0,
                  "currentPrice": 110.0,
                  "quantity": 5.0,
                  "quantityAvailableForTrading": 4.0,
                  "quantityInPies": 0.0,
                  "createdAt": "2025-01-01T00:00:00Z",
                  "instrument": { "ticker": "AAPL_US_EQ", "currency": "USD", "name": "Apple Inc", "isin": "US0378331005" },
                  "walletImpact": { "currency": "GBP", "currentValue": 440.0, "totalCost": 400.0, "unrealizedProfitLoss": 40.0 }
                }
            ]
            """);

            var holdings = brokerage.GetAccountHoldings();

            Assert.That(holdings, Has.Count.EqualTo(1));
            var holding = holdings[0];
            Assert.That(holding.Symbol, Is.EqualTo(Apple));
            Assert.That(holding.Quantity, Is.EqualTo(5m));
            Assert.That(holding.AveragePrice, Is.EqualTo(100m));
            Assert.That(holding.MarketPrice, Is.EqualTo(110m));
            Assert.That(holding.MarketValue, Is.EqualTo(440m));
            Assert.That(holding.CurrencySymbol, Is.EqualTo("USD"));
            Assert.That(holding.ConversionRate, Is.EqualTo(0.8m));
            Assert.That(holding.UnrealizedPnL, Is.EqualTo(40m));
            Assert.That(holding.UnrealizedPnLPercent, Is.EqualTo(10m));
        }

        [Test]
        public void GetAccountHoldingsSkipsInstrumentsLeanCannotModel()
        {
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueJson(HttpStatusCode.OK, """
            [
                {
                  "averagePricePaid": 1.0, "currentPrice": 1.0, "quantity": 1.0, "quantityAvailableForTrading": 1.0, "quantityInPies": 0.0,
                  "instrument": { "ticker": "VOD_GB_EQ", "currency": "GBP", "name": "Vodafone", "isin": "GB00BH4HKS39" },
                  "walletImpact": { "currency": "GBP", "currentValue": 1.0, "totalCost": 1.0, "unrealizedProfitLoss": 0.0 }
                }
            ]
            """);

            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);

            var holdings = brokerage.GetAccountHoldings();

            Assert.That(holdings, Is.Empty);
            Assert.That(messages.Any(message => message.Code == "UnsupportedPosition"), Is.True);
        }

        [Test]
        public void GetOpenOrdersConvertsAPendingMarketOrder()
        {
            var brokerage = CreateConnectedBrokerage(Trading212TestData.PendingMarketOrder);
            // Connect already consumed the seeding response.
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.PendingMarketOrder);

            var openOrders = brokerage.GetOpenOrders();

            Assert.That(openOrders, Has.Count.EqualTo(1));
            var order = openOrders[0];
            Assert.That(order, Is.InstanceOf<MarketOrder>());
            Assert.That(order.Symbol, Is.EqualTo(Apple));
            Assert.That(order.Quantity, Is.EqualTo(10m));
            Assert.That(order.BrokerId.Single(), Is.EqualTo("987654321"));
            Assert.That(order.Status, Is.EqualTo(OrderStatus.Submitted));
        }

        [TestCase("LIMIT", 100.5, null, typeof(LimitOrder))]
        [TestCase("STOP", null, 99.0, typeof(StopMarketOrder))]
        public void GetOpenOrdersConvertsAPendingWorkingOrder(string type, double? limitPrice, double? stopPrice, Type expectedType)
        {
            var pendingOrders = $$"""
            [
                {
                  "id": 11, "ticker": "AAPL_US_EQ", "type": "{{type}}", "status": "NEW", "side": "BUY", "quantity": 2.0, "filledQuantity": 0.0,
                  "limitPrice": {{(limitPrice.HasValue ? limitPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},
                  "stopPrice": {{(stopPrice.HasValue ? stopPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},
                  "createdAt": "2025-01-02T15:00:00Z"
                }
            ]
            """;

            var brokerage = CreateConnectedBrokerage(pendingOrders);
            _handler.EnqueueJson(HttpStatusCode.OK, pendingOrders);

            var openOrders = brokerage.GetOpenOrders();

            Assert.That(openOrders, Has.Count.EqualTo(1));
            Assert.That(openOrders[0], Is.InstanceOf(expectedType));
        }

        [Test]
        public void PlaceOrderSendsTheOrderAndReportsItAsSubmitted()
        {
            var brokerage = CreateConnectedBrokerage();
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            _handler.EnqueueJson(HttpStatusCode.OK, """
            { "id": 555, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "BUY", "quantity": 3.0 }
            """);

            var order = new MarketOrder(Apple, 3m, DateTime.UtcNow);
            var placed = brokerage.PlaceOrder(order);

            Assert.That(placed, Is.True);
            Assert.That(order.BrokerId.Single(), Is.EqualTo("555"));
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Status, Is.EqualTo(OrderStatus.Submitted));
            Assert.That(_handler.Requests.Last().RequestUri.AbsolutePath, Is.EqualTo("/api/v0/equity/orders/market"));
            Assert.That(_handler.RequestBodies.Last(), Does.Contain("\"quantity\":3"));
        }

        [Test]
        public void PlaceOrderSendsASignedQuantityForASell()
        {
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueJson(HttpStatusCode.OK, """
            { "id": 556, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "SELL", "quantity": -3.0 }
            """);

            brokerage.PlaceOrder(new MarketOrder(Apple, -3m, DateTime.UtcNow));

            Assert.That(_handler.RequestBodies.Last(), Does.Contain("\"quantity\":-3"));
        }

        [Test]
        public void PlaceOrderSurfacesABrokerRejectionAsAnInvalidOrderEvent()
        {
            // A broker rejection must never be swallowed: it reaches the algorithm as an OrderEvent
            // carrying the broker's own message.
            var brokerage = CreateConnectedBrokerage();
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            _handler.EnqueueJson(HttpStatusCode.BadRequest, """{ "code": "SellingEquityNotOwned", "clarification": "SellingEquityNotOwned" }""");

            var order = new MarketOrder(Apple, -1m, DateTime.UtcNow);
            var placed = brokerage.PlaceOrder(order);

            Assert.That(placed, Is.True);
            Assert.That(order.BrokerId, Is.Empty);
            Assert.That(events, Has.Count.EqualTo(1));
            Assert.That(events[0].Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(events[0].Message, Does.Contain("SellingEquityNotOwned"));
        }

        [Test]
        public void PlaceOrderSurfacesAnInsufficientFundsRejection()
        {
            var brokerage = CreateConnectedBrokerage();
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            _handler.EnqueueJson(HttpStatusCode.BadRequest, """{ "code": "InsufficientValueForStocksSell", "clarification": "InsufficientValueForStocksSell" }""");

            brokerage.PlaceOrder(new LimitOrder(Apple, -5m, 100m, DateTime.UtcNow));

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(events.Single().Message, Does.Contain("InsufficientValueForStocksSell"));
        }

        [Test]
        public void PlaceOrderRejectsASymbolTheBrokerDoesNotList()
        {
            var brokerage = CreateConnectedBrokerage();
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            var tesla = Symbol.Create("TSLA", SecurityType.Equity, Market.USA);
            brokerage.PlaceOrder(new MarketOrder(tesla, 1m, DateTime.UtcNow));

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(events.Single().Message, Does.Contain("TSLA"));
            Assert.That(_handler.Requests.Any(request => request.Method == HttpMethod.Post), Is.False);
        }

        [Test]
        public void PlaceOrderRejectsAGoodTilDateOrder()
        {
            var brokerage = CreateConnectedBrokerage();
            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            var order = new LimitOrder(Apple, 1m, 100m, DateTime.UtcNow);
            order.Properties.TimeInForce = TimeInForce.GoodTilDate(DateTime.UtcNow.AddDays(1));

            brokerage.PlaceOrder(order);

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(_handler.Requests.Any(request => request.Method == HttpMethod.Post), Is.False);
        }

        [Test]
        public void UpdateOrderIsNotSupportedByTheApi()
        {
            var brokerage = CreateConnectedBrokerage();
            var messages = new List<BrokerageMessageEvent>();
            brokerage.Message += (_, e) => messages.Add(e);

            var order = new LimitOrder(Apple, 1m, 100m, DateTime.UtcNow);
            order.BrokerId.Add("42");

            Assert.That(brokerage.UpdateOrder(order), Is.False);
            Assert.That(messages.Single().Code, Is.EqualTo("UpdateOrderNotSupported"));
        }

        [Test]
        public void CancelOrderSendsADelete()
        {
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueEmpty(HttpStatusCode.OK);

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            order.BrokerId.Add("77");

            Assert.That(brokerage.CancelOrder(order), Is.True);
            Assert.That(_handler.Requests.Last().Method, Is.EqualTo(HttpMethod.Delete));
            Assert.That(_handler.Requests.Last().RequestUri.AbsolutePath, Is.EqualTo("/api/v0/equity/orders/77"));
        }

        [Test]
        public void CancelOrderTreatsAMissingOrderAsCancelled()
        {
            // The order is no longer pending, so the DELETE answers 404. That is not a failure: the
            // order update poller reports its final state.
            var brokerage = CreateConnectedBrokerage();
            _handler.EnqueueJson(HttpStatusCode.NotFound, """{ "title": "Order not found", "status": 404 }""");

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            order.BrokerId.Add("78");

            Assert.That(brokerage.CancelOrder(order), Is.True);
        }

        [Test]
        public void ApplyBrokerageOrderReportsIncrementalFills()
        {
            var brokerage = CreateConnectedBrokerage();

            var order = new MarketOrder(Apple, 10m, DateTime.UtcNow);
            order.BrokerId.Add("900");
            _orderProvider.Add(order);

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            // The broker reports a cumulative filled quantity; LEAN needs the incremental one. A pending order
            // carries no execution price, only the cumulative filled value, so the price of each incremental
            // fill is the change in value over the change in quantity.
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.New, 0m), fill: null);
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.PartiallyFilled, 4m, filledValue: 400m), fill: null);
            // An unchanged poll must not produce another event.
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.PartiallyFilled, 4m, filledValue: 400m), fill: null);
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.Filled, 10m, filledValue: 1010m), new Trading212Fill { Price = 101m, Quantity = 6m });

            Assert.That(events.Select(e => e.Status), Is.EqualTo(new[]
            {
                OrderStatus.Submitted,
                OrderStatus.PartiallyFilled,
                OrderStatus.Filled
            }));
            Assert.That(events.Select(e => e.FillQuantity), Is.EqualTo(new[] { 0m, 4m, 6m }));
            Assert.That(events[1].FillPrice, Is.EqualTo(100m), "a partial fill must be priced from filledValue, not reported at zero");
            Assert.That(events[2].FillPrice, Is.EqualTo(101m));
        }

        [Test]
        public void ApplyBrokerageOrderPricesAPartialFillFromTheChangeInFilledValue()
        {
            var brokerage = CreateConnectedBrokerage();

            var order = new MarketOrder(Apple, 10m, DateTime.UtcNow);
            order.BrokerId.Add("903");
            _orderProvider.Add(order);

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            // The second fill is at a different price, so only the change in value applies to the new shares.
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.PartiallyFilled, 2m, id: 903, filledValue: 200m), fill: null);
            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.PartiallyFilled, 5m, id: 903, filledValue: 530m), fill: null);

            Assert.That(events.Select(e => e.FillQuantity), Is.EqualTo(new[] { 2m, 3m }));
            Assert.That(events[0].FillPrice, Is.EqualTo(100m));
            Assert.That(events[1].FillPrice, Is.EqualTo(110m), "3 shares filled for a further 330.00");
            Assert.That(events.All(e => e.FillPrice > 0m), Is.True);
        }

        [Test]
        public void PlaceOrderDoesNotPlaceTwiceWhenTheResponseIsAmbiguous()
        {
            var brokerage = CreateConnectedBrokerage();

            // The placement request fails with a gateway error, then the broker reports the order it did create.
            _handler.EnqueueJson(HttpStatusCode.GatewayTimeout, """{ "title": "upstream error" }""");
            var createdAt = DateTime.UtcNow.AddMinutes(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            _handler.EnqueueJson(HttpStatusCode.OK, $$"""
            [
                { "id": 4242, "ticker": "AAPL_US_EQ", "type": "MARKET", "status": "NEW", "side": "BUY", "quantity": 1, "filledQuantity": 0, "createdAt": "{{createdAt}}" }
            ]
            """);

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            _orderProvider.Add(order);

            Assert.That(brokerage.PlaceOrder(order), Is.True);

            Assert.That(_handler.Requests.Count(request => request.Method == HttpMethod.Post), Is.EqualTo(1), "placement must not be retried");
            Assert.That(order.BrokerId, Is.EqualTo(new List<string> { "4242" }));
            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Submitted));
        }

        [Test]
        public void PlaceOrderReportsAnUnknownOutcomeWhenTheOrderCannotBeFound()
        {
            var brokerage = CreateConnectedBrokerage();

            // The placement request fails and the broker holds no such order.
            _handler.EnqueueJson(HttpStatusCode.BadGateway, """{ "title": "upstream error" }""");
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.EmptyArray);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.EmptyPage);

            var messages = new List<BrokerageMessageEvent>();
            var events = new List<OrderEvent>();
            brokerage.Message += (_, message) => messages.Add(message);
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            _orderProvider.Add(order);

            Assert.That(brokerage.PlaceOrder(order), Is.True);

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Invalid));
            Assert.That(order.BrokerId, Is.Empty);
            Assert.That(messages.Single(message => message.Code == "OrderPlacementUnknown").Type, Is.EqualTo(BrokerageMessageType.Error));
        }

        [Test]
        public void ApplyBrokerageOrderReportsANegativeFillForASell()
        {
            var brokerage = CreateConnectedBrokerage();

            var order = new MarketOrder(Apple, -10m, DateTime.UtcNow);
            order.BrokerId.Add("901");
            _orderProvider.Add(order);

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.Filled, 10m, id: 901, quantity: -10m), new Trading212Fill { Price = 100m, Quantity = 10m });

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Filled));
            Assert.That(events.Single().FillQuantity, Is.EqualTo(-10m));
        }

        [Test]
        public void ApplyBrokerageOrderReportsARejection()
        {
            var brokerage = CreateConnectedBrokerage();

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            order.BrokerId.Add("902");
            _orderProvider.Add(order);

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.Rejected, 0m, id: 902), fill: null);

            Assert.That(events.Single().Status, Is.EqualTo(OrderStatus.Invalid));
        }

        [Test]
        public void ApplyBrokerageOrderIgnoresOrdersTheAlgorithmDidNotPlace()
        {
            var brokerage = CreateConnectedBrokerage();

            var events = new List<OrderEvent>();
            brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            brokerage.ApplyBrokerageOrder(NewOrder(Trading212OrderStatus.Filled, 1m, id: 12345), new Trading212Fill { Price = 1m, Quantity = 1m });

            Assert.That(events, Is.Empty);
        }

        /// <summary>
        /// Builds a broker order for the fill tests.
        /// </summary>
        /// <param name="status">The broker status.</param>
        /// <param name="filledQuantity">The cumulative filled quantity.</param>
        /// <param name="id">The broker order id.</param>
        /// <param name="quantity">The order quantity.</param>
        /// <param name="filledValue">The cumulative filled value, which the pending orders endpoint reports.</param>
        /// <returns>The broker order.</returns>
        private static Trading212Order NewOrder(Trading212OrderStatus status, decimal filledQuantity, long id = 900, decimal quantity = 10m, decimal? filledValue = null)
        {
            return new Trading212Order
            {
                Id = id,
                Ticker = "AAPL_US_EQ",
                Type = Trading212OrderType.Market,
                Status = status,
                Side = quantity < 0 ? Trading212OrderSide.Sell : Trading212OrderSide.Buy,
                Quantity = quantity,
                FilledQuantity = filledQuantity,
                FilledValue = filledValue
            };
        }
    }
}
