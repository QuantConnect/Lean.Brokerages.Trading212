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

using NUnit.Framework;
using Newtonsoft.Json;
using QuantConnect.Orders;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212BrokerageExtensionsTests
    {
        [TestCase(Trading212OrderStatus.Local, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.Unconfirmed, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.Confirmed, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.New, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.Cancelling, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.Replacing, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.Replaced, OrderStatus.Submitted)]
        [TestCase(Trading212OrderStatus.PartiallyFilled, OrderStatus.PartiallyFilled)]
        [TestCase(Trading212OrderStatus.Filled, OrderStatus.Filled)]
        [TestCase(Trading212OrderStatus.Cancelled, OrderStatus.Canceled)]
        [TestCase(Trading212OrderStatus.Rejected, OrderStatus.Invalid)]
        public void MapsBrokerStatusesToLeanStatuses(Trading212OrderStatus brokerStatus, OrderStatus expected)
        {
            Assert.That(brokerStatus.ToLeanOrderStatus(), Is.EqualTo(expected));
        }

        [Test]
        public void AnUnknownBrokerStatusHasNoLeanEquivalent()
        {
            Assert.That(Trading212OrderStatus.Unknown.ToLeanOrderStatus(), Is.Null);
        }

        [TestCase(Trading212OrderStatus.New, true)]
        [TestCase(Trading212OrderStatus.PartiallyFilled, true)]
        [TestCase(Trading212OrderStatus.Cancelling, true)]
        [TestCase(Trading212OrderStatus.Filled, false)]
        [TestCase(Trading212OrderStatus.Cancelled, false)]
        [TestCase(Trading212OrderStatus.Rejected, false)]
        [TestCase(Trading212OrderStatus.Unknown, false)]
        public void IdentifiesWorkingOrders(Trading212OrderStatus status, bool expected)
        {
            Assert.That(status.IsWorking(), Is.EqualTo(expected));
        }

        [Test]
        public void MapsLeanTimeInForceToTheBrokerTimeValidity()
        {
            Assert.That(TimeInForce.Day.TryGetTimeValidity(out var day), Is.True);
            Assert.That(day, Is.EqualTo(Trading212TimeValidity.Day));

            Assert.That(TimeInForce.GoodTilCanceled.TryGetTimeValidity(out var gtc), Is.True);
            Assert.That(gtc, Is.EqualTo(Trading212TimeValidity.GoodTillCancel));

            Assert.That(TimeInForce.GoodTilDate(new System.DateTime(2026, 1, 1)).TryGetTimeValidity(out _), Is.False);
        }

        [Test]
        public void DeserializesUnknownEnumValuesAsUnknown()
        {
            var order = JsonConvert.DeserializeObject<Trading212Order>(
                """{ "id": 1, "ticker": "AAPL_US_EQ", "type": "TRAILING_STOP", "status": "SOMETHING_NEW" }""");

            Assert.That(order.Type, Is.EqualTo(Trading212OrderType.Unknown));
            Assert.That(order.Status, Is.EqualTo(Trading212OrderStatus.Unknown));
        }

        [Test]
        public void DeserializesScreamingSnakeCaseEnumValues()
        {
            var order = JsonConvert.DeserializeObject<Trading212Order>(
                """{ "id": 1, "ticker": "AAPL_US_EQ", "type": "STOP_LIMIT", "status": "PARTIALLY_FILLED", "side": "SELL", "timeInForce": "GOOD_TILL_CANCEL" }""");

            Assert.That(order.Type, Is.EqualTo(Trading212OrderType.StopLimit));
            Assert.That(order.Status, Is.EqualTo(Trading212OrderStatus.PartiallyFilled));
            Assert.That(order.Side, Is.EqualTo(Trading212OrderSide.Sell));
            Assert.That(order.TimeInForce, Is.EqualTo(Trading212TimeValidity.GoodTillCancel));
        }

        [TestCase("STOCK", Trading212InstrumentType.Stock)]
        [TestCase("ETF", Trading212InstrumentType.Etf)]
        [TestCase("CRYPTOCURRENCY", Trading212InstrumentType.Cryptocurrency)]
        [TestCase("CVR", Trading212InstrumentType.Cvr)]
        [TestCase("CORPACT", Trading212InstrumentType.Corpact)]
        public void DeserializesInstrumentTypes(string wireValue, Trading212InstrumentType expected)
        {
            var instrument = JsonConvert.DeserializeObject<Trading212TradableInstrument>(
                $$"""{ "ticker": "X_US_EQ", "type": "{{wireValue}}" }""");

            Assert.That(instrument.Type, Is.EqualTo(expected));
        }
    }
}
