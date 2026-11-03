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
using System.Net;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Trading 212's public API serves no historical data, so every history request must be refused with a
    /// <c>null</c> result rather than an empty one or an exception.
    /// </summary>
    [TestFixture]
    public class Trading212BrokerageHistoryProviderTests
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

        [TestCase(Resolution.Minute, TickType.Trade, typeof(TradeBar))]
        [TestCase(Resolution.Minute, TickType.Quote, typeof(QuoteBar))]
        [TestCase(Resolution.Daily, TickType.Trade, typeof(TradeBar))]
        [TestCase(Resolution.Tick, TickType.Trade, typeof(Tick))]
        public void GetHistoryReturnsNull(Resolution resolution, TickType tickType, Type dataType)
        {
            var symbol = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var marketHours = MarketHoursDatabase.FromDataFolder();
            var now = DateTime.UtcNow;

            var request = new HistoryRequest(
                now.AddDays(-10),
                now,
                dataType,
                symbol,
                resolution,
                marketHours.GetExchangeHours(symbol.ID.Market, symbol, symbol.SecurityType),
                marketHours.GetDataTimeZone(symbol.ID.Market, symbol, symbol.SecurityType),
                resolution,
                false,
                false,
                DataNormalizationMode.Adjusted,
                tickType);

            var messages = new List<BrokerageMessageEvent>();
            _brokerage.Message += (_, e) => messages.Add(e);

            var history = _brokerage.GetHistory(request);

            Assert.That(history, Is.Null);
            Assert.That(messages, Has.Count.EqualTo(1));
            Assert.That(messages[0].Code, Is.EqualTo("HistoryNotSupported"));
        }
    }
}
