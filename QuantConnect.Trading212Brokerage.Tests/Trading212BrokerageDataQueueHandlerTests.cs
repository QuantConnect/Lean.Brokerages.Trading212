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

using System.Collections.Generic;
using System.Linq;
using System.Net;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Trading 212's public API serves no market data, so the data queue handler must refuse every
    /// subscription and let the LEAN launcher use another data source.
    /// </summary>
    [TestFixture]
    public class Trading212BrokerageDataQueueHandlerTests
    {
        private FakeHttpMessageHandler _handler;
        private Trading212Brokerage _brokerage;

        [SetUp]
        public void SetUp()
        {
            _handler = new FakeHttpMessageHandler();
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.AccountSummary);
            _handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.Instruments);
            _handler.EnqueueJson(HttpStatusCode.OK, """{ "items": [] }""");

            var client = new Trading212ApiClient("https://demo.trading212.com", "key", "secret", _handler, messageReceived: null);
            _brokerage = new Trading212Brokerage(client);
            _brokerage.Connect();
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage.Disconnect();
            _brokerage.Dispose();
        }

        [TestCase(Resolution.Minute, TickType.Trade)]
        [TestCase(Resolution.Second, TickType.Quote)]
        public void SubscribeReturnsNullForEveryConfiguration(Resolution resolution, TickType tickType)
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var config = new SubscriptionDataConfig(
                typeof(TradeBar), symbol, resolution, TimeZones.Utc, TimeZones.Utc, false, false, false, false, tickType: tickType);

            var enumerator = _brokerage.Subscribe(config, (_, _) => { });

            Assert.That(enumerator, Is.Null);
        }

        [Test]
        public void CanPerformSelectionIsFalse()
        {
            Assert.That(_brokerage.CanPerformSelection(), Is.False);
        }

        [Test]
        public void LookupSymbolsReturnsNothing()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);

            Assert.That(_brokerage.LookupSymbols(symbol, includeExpired: false), Is.Empty);
        }

        [Test]
        public void SetJobIsANoOp()
        {
            Assert.DoesNotThrow(() => _brokerage.SetJob(new QuantConnect.Packets.LiveNodePacket()));
        }

        [Test]
        public void UnsubscribeIsSafe()
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var config = new SubscriptionDataConfig(typeof(TradeBar), symbol, Resolution.Minute, TimeZones.Utc, TimeZones.Utc, false, false, false, false);

            Assert.DoesNotThrow(() => _brokerage.Unsubscribe(config));
        }
    }
}
