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
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Covers the order fee the brokerage reports on a fill.
    /// </summary>
    /// <remarks>
    /// Trading 212 reports no commission, but a trade is not free: there is an FX fee on the converted notional
    /// and, on US sales, the regulatory fees. Those are modelled by <c>Trading212FeeModel</c>, which this plugin
    /// reuses so a live fill is priced the same way a backtest is.
    /// </remarks>
    [TestFixture]
    public class Trading212BrokerageFeeTests
    {
        private static readonly Symbol Apple = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

        private FakeHttpMessageHandler _handler;
        private FakeSecurityProvider _securityProvider;
        private Trading212Brokerage _brokerage;

        [SetUp]
        public void SetUp()
        {
            _handler = new FakeHttpMessageHandler();
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.Instruments);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.EmptyArray);

            _securityProvider = new FakeSecurityProvider();
            var client = new Trading212ApiClient("https://demo.trading212.com", "key", "secret", _handler, messageReceived: null);
            _brokerage = new Trading212Brokerage(client, orderProvider: null, securityProvider: _securityProvider);
            _brokerage.Connect();
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage?.Disconnect();
            _brokerage?.Dispose();
        }

        [Test]
        public void ChargesTheFxFeeOnAForeignCurrencyBuy()
        {
            // The account is GBP and the instrument is USD, so the FX fee applies.
            Assert.That(_brokerage.AccountCurrency, Is.EqualTo("GBP"));
            _securityProvider.AddEquity(Apple, Currencies.USD);

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);
            var fee = _brokerage.GetOrderFee(order, OrderStatus.Filled, fillPrice: 100m);

            Assert.That(fee.Value.Amount, Is.GreaterThan(0m), "A USD buy in a GBP account must pay the FX fee.");
            // 0.15% of the 100.00 notional, computed in the instrument's quote currency.
            Assert.That(fee.Value.Amount, Is.EqualTo(0.15m).Within(0.0001m));
            Assert.That(fee.Value.Currency, Is.EqualTo(Currencies.USD));
            // The fee is applied to the portfolio in the account currency, so it never leaves a USD balance.
            Assert.That(fee, Is.InstanceOf<AccountCurrencyOrderFee>());
        }

        [Test]
        public void ChargesTheRegulatoryFeesOnAUsSale()
        {
            _securityProvider.AddEquity(Apple, Currencies.USD);

            var order = new MarketOrder(Apple, 100m, DateTime.UtcNow);
            var fee = _brokerage.GetOrderFee(order, OrderStatus.Filled, fillPrice: 100m);

            // The FX fee applies to a sale too, and the SEC and FINRA fees are added on top.
            Assert.That(fee.Value.Amount, Is.GreaterThan(0.15m), "A sale must also pay the regulatory fees.");
        }

        [Test]
        public void ChargesNothingWhenTheOrderHasNotFilled()
        {
            _securityProvider.AddEquity(Apple, Currencies.USD);

            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);

            Assert.That(_brokerage.GetOrderFee(order, OrderStatus.Submitted, fillPrice: 0m), Is.EqualTo(OrderFee.Zero));
            Assert.That(_brokerage.GetOrderFee(order, OrderStatus.PartiallyFilled, fillPrice: 100m), Is.EqualTo(OrderFee.Zero));
        }

        [Test]
        public void ChargesNothingWhenTheSecurityIsUnknown()
        {
            // Without a security the fee model cannot be applied; the fill must still be reported.
            var order = new MarketOrder(Apple, 1m, DateTime.UtcNow);

            Assert.That(_brokerage.GetOrderFee(order, OrderStatus.Filled, fillPrice: 100m), Is.EqualTo(OrderFee.Zero));
        }

        [Test]
        public void PricesAMarketFillFromTheTradedPrice()
        {
            // A security that no data feed has priced yet still has to produce a fee for a real fill.
            var security = _securityProvider.AddEquity(Apple, Currencies.USD);
            Assert.That(security.Price, Is.EqualTo(0m));

            var fee = _brokerage.GetOrderFee(new MarketOrder(Apple, 10m, DateTime.UtcNow), OrderStatus.Filled, fillPrice: 50m);

            Assert.That(security.Price, Is.EqualTo(50m), "the fill price should seed an unpriced security");
            // 0.15% of 10 x 50.00.
            Assert.That(fee.Value.Amount, Is.EqualTo(0.75m).Within(0.0001m));
        }
    }
}
