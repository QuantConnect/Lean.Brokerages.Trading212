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
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.Models;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Covers the order fee the brokerage reports on a fill.
    /// </summary>
    /// <remarks>
    /// Trading 212 reports no commission, but a trade is not free: the history endpoint's fill carries the
    /// taxes that were actually charged in <c>walletImpact.taxes</c>. Those are reported as they are, so a
    /// live fill carries the broker's real cost rather than a model of it.
    /// </remarks>
    [TestFixture]
    public class Trading212BrokerageFeeTests
    {
        private static readonly Symbol Apple = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

        private FakeHttpMessageHandler _handler;
        private FakeOrderProvider _orderProvider;
        private Trading212Brokerage _brokerage;

        [SetUp]
        public void SetUp()
        {
            _handler = new FakeHttpMessageHandler();
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.Instruments);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.EmptyArray);

            _orderProvider = new FakeOrderProvider();
            var client = new Trading212ApiClient("https://demo.trading212.com", "key", "secret", _handler, messageReceived: null);
            _brokerage = new Trading212Brokerage(client, _orderProvider);
            _brokerage.Connect();
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage?.Disconnect();
            _brokerage?.Dispose();
        }

        [Test]
        public void ChargesTheFeesTheBrokerReported()
        {
            // The account is GBP, so a USD tax is converted with the fill's FX rate while a GBP tax is not.
            Assert.That(_brokerage.AccountCurrency, Is.EqualTo("GBP"));

            var fee = _brokerage.GetOrderFee(OrderStatus.Filled, FillWithTaxes(
                ("FX fee", "USD", 0.15m),
                ("PTM levy", "GBP", 0.01m)));

            Assert.That(fee.Value.Amount, Is.EqualTo(0.15m * 0.79m + 0.01m).Within(0.000001m));
            Assert.That(fee.Value.Currency, Is.EqualTo(Currencies.GBP));
        }

        [Test]
        public void ReportsABrokerFeeInTheAccountCurrency()
        {
            var fill = FillWithTaxes(("SEC fee", "GBP", 0.23m));

            Assert.That(_brokerage.GetOrderFee(OrderStatus.Filled, fill).Value.Currency, Is.EqualTo(Currencies.GBP));
        }

        [Test]
        public void ChargesNothingWhenTheFillCarriesNoTaxes()
        {
            var fill = new Trading212Fill
            {
                Price = 100m,
                Quantity = 1m,
                WalletImpact = new Trading212FillWalletImpact { Currency = "USD", FxRate = 0.79m }
            };

            Assert.That(_brokerage.GetOrderFee(OrderStatus.Filled, fill), Is.EqualTo(OrderFee.Zero));
        }

        [Test]
        public void ChargesNothingWithoutAFill()
        {
            Assert.That(_brokerage.GetOrderFee(OrderStatus.Filled, fill: null), Is.EqualTo(OrderFee.Zero));
            Assert.That(_brokerage.GetOrderFee(OrderStatus.Submitted, FillWithTaxes(("FX fee", "USD", 0.15m))), Is.EqualTo(OrderFee.Zero));
            Assert.That(_brokerage.GetOrderFee(OrderStatus.PartiallyFilled, FillWithTaxes(("FX fee", "USD", 0.15m))), Is.EqualTo(OrderFee.Zero));
        }

        [Test]
        public void ReportsTheBrokerFeeOnTheFilledOrderEvent()
        {
            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            order.BrokerId.Add("950");
            _orderProvider.Add(order);

            var events = new List<OrderEvent>();
            _brokerage.OrdersStatusChanged += (_, orderEvents) => events.AddRange(orderEvents);

            _brokerage.ApplyBrokerageOrder(new Trading212Order
            {
                Id = 950,
                Ticker = "AAPL_US_EQ",
                Type = Trading212OrderType.Market,
                Status = Trading212OrderStatus.Filled,
                Side = Trading212OrderSide.Buy,
                Quantity = 1m,
                FilledQuantity = 1m
            }, FillWithTaxes(("FX fee", "USD", 0.15m), ("SEC fee", "GBP", 0.02m)));

            var orderEvent = events.Single();
            Assert.That(orderEvent.Status, Is.EqualTo(OrderStatus.Filled));
            Assert.That(orderEvent.OrderFee.Value.Amount, Is.EqualTo(0.15m * 0.79m + 0.02m).Within(0.000001m));
            Assert.That(orderEvent.OrderFee.Value.Currency, Is.EqualTo(Currencies.GBP));
        }

        /// <summary>
        /// Builds a fill that carries the given taxes.
        /// </summary>
        /// <param name="taxes">The name, currency and amount of each tax.</param>
        /// <returns>The fill.</returns>
        private static Trading212Fill FillWithTaxes(params (string Name, string Currency, decimal Amount)[] taxes)
        {
            var fill = new Trading212Fill
            {
                Price = 100m,
                Quantity = 1m,
                WalletImpact = new Trading212FillWalletImpact
                {
                    Currency = "USD",
                    FxRate = 0.79m,
                    Taxes = new List<Trading212Tax>()
                }
            };

            foreach (var (name, currency, amount) in taxes)
            {
                fill.WalletImpact.Taxes.Add(new Trading212Tax { Name = name, Currency = currency, Quantity = amount });
            }

            return fill;
        }
    }
}
