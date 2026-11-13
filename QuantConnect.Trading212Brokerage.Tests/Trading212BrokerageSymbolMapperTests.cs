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
using NUnit.Framework;
using Newtonsoft.Json;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212BrokerageSymbolMapperTests
    {
        private Trading212SymbolMapper _symbolMapper;

        [SetUp]
        public void SetUp()
        {
            _symbolMapper = new Trading212SymbolMapper();
            // The instruments endpoint answers a bare JSON array.
            var instruments = JsonConvert.DeserializeObject<List<Trading212TradableInstrument>>(Trading212TestData.Instruments);
            _symbolMapper.SetInstruments(instruments);
        }

        [TestCase("AAPL", "AAPL_US_EQ")]
        [TestCase("VUSA", "VUSA_US_ETF")]
        public void ReturnsCorrectBrokerageSymbol(string leanTicker, string expectedBrokerageSymbol)
        {
            var symbol = Symbol.Create(leanTicker, SecurityType.Equity, Market.USA);

            Assert.That(_symbolMapper.GetBrokerageSymbol(symbol), Is.EqualTo(expectedBrokerageSymbol));
        }

        [TestCase("AAPL_US_EQ", "AAPL")]
        [TestCase("VUSA_US_ETF", "VUSA")]
        public void ReturnsCorrectLeanSymbol(string brokerageSymbol, string expectedTicker)
        {
            var symbol = _symbolMapper.GetLeanSymbol(brokerageSymbol, SecurityType.Equity, Market.USA);

            Assert.That(symbol.Value, Is.EqualTo(expectedTicker));
            Assert.That(symbol.SecurityType, Is.EqualTo(SecurityType.Equity));
            Assert.That(symbol.ID.Market, Is.EqualTo(Market.USA));
        }

        [Test]
        public void MapsClassSharesToTheLeanDotSeparatedTicker()
        {
            // Trading 212 writes Berkshire's class B shares as BRK_B_US_EQ; LEAN stores them as BRK.B.
            var symbol = _symbolMapper.GetLeanSymbol("BRK_B_US_EQ", SecurityType.Equity, Market.USA);

            Assert.That(symbol.Value, Is.EqualTo("BRK.B"));
            Assert.That(_symbolMapper.GetBrokerageSymbol(symbol), Is.EqualTo("BRK_B_US_EQ"));
        }

        [Test]
        public void SkipsInstrumentsLeanCannotModel()
        {
            // Everything is indexed, but only the common shares are mappable: the non-US listing, the warrant
            // and the malformed ticker are not.
            Assert.That(_symbolMapper.InstrumentCount, Is.EqualTo(7));
            Assert.That(_symbolMapper.MappedInstrumentCount, Is.EqualTo(4));

            Assert.That(_symbolMapper.TryGetInstrument("VOD_GB_EQ", out var instrument), Is.True);
            Assert.That(instrument.Name, Is.EqualTo("Vodafone Group"));
            Assert.That(_symbolMapper.IsSupported("VOD_GB_EQ"), Is.False);

            Assert.That(_symbolMapper.TryGetLeanSymbol("VOD_GB_EQ", out _), Is.False);
            Assert.Throws<NotSupportedException>(() => _symbolMapper.GetLeanSymbol("VOD_GB_EQ", SecurityType.Equity, "lse"));
        }

        [Test]
        public void ThrowsForASymbolTheBrokerDoesNotList()
        {
            var symbol = Symbol.Create("TSLA", SecurityType.Equity, Market.USA);

            Assert.That(_symbolMapper.TryGetBrokerageSymbol(symbol, out _), Is.False);
            Assert.Throws<NotSupportedException>(() => _symbolMapper.GetBrokerageSymbol(symbol));
        }

        [Test]
        public void RejectsUnsupportedSecurityTypes()
        {
            var option = Symbol.CreateOption(Symbol.Create("AAPL", SecurityType.Equity, Market.USA),
                Market.USA, OptionStyle.American, OptionRight.Call, 100m, new DateTime(2026, 1, 16));

            Assert.That(_symbolMapper.TryGetBrokerageSymbol(option, out _), Is.False);
            Assert.Throws<NotSupportedException>(() => _symbolMapper.GetBrokerageSymbol(option));
        }

        [Test]
        public void MapsASymbolWhoseTickerChangedToTheCurrentTicker()
        {
            // A Symbol can carry an old ticker in Value while sharing the SecurityIdentifier of the current one.
            var current = Symbol.Create("AAPL", SecurityType.Equity, Market.USA);
            var historical = new Symbol(current.ID, "AAPLL");

            Assert.That(historical, Is.EqualTo(current));
            Assert.That(historical.Value, Is.Not.EqualTo(current.Value));

            Assert.That(_symbolMapper.GetBrokerageSymbol(historical), Is.EqualTo("AAPL_US_EQ"));
            Assert.That(_symbolMapper.GetBrokerageSymbol(current), Is.EqualTo("AAPL_US_EQ"));
        }

        [Test]
        public void MapsClassASharesWrittenWithASlash()
        {
            // Trading 212 writes Berkshire's class A shares as BRK/A_US_EQ; LEAN stores them as BRK.A.
            var symbol = _symbolMapper.GetLeanSymbol("BRK/A_US_EQ", SecurityType.Equity, Market.USA);

            Assert.That(symbol.Value, Is.EqualTo("BRK.A"));
            Assert.That(_symbolMapper.GetBrokerageSymbol(symbol), Is.EqualTo("BRK/A_US_EQ"));
        }

        [Test]
        public void DoesNotInventSymbolsForWarrants()
        {
            // Trading 212 writes a warrant as <TICKER>_WAR. Mapping it to DSX.WAR would create a LEAN symbol
            // that never had data, so it is not supported.
            Assert.That(_symbolMapper.IsSupported("DSX_WAR_US_EQ"), Is.False);
            Assert.That(_symbolMapper.TryGetLeanSymbol("DSX_WAR_US_EQ", out _), Is.False);
            Assert.That(_symbolMapper.TryGetInstrument("DSX_WAR_US_EQ", out _), Is.True);
        }

        [Test]
        public void DoesNotInventSymbolsForMalformedTickers()
        {
            Assert.That(_symbolMapper.IsSupported("AVAV__US_EQ"), Is.False);
            Assert.That(_symbolMapper.TryGetLeanSymbol("AVAV__US_EQ", out _), Is.False);
        }

        [TestCase("AAPL", true, "AAPL")]
        [TestCase("BRK_B", true, "BRK.B")]
        [TestCase("BRK/A", true, "BRK.A")]
        [TestCase("brk_b", true, "BRK.B")]
        [TestCase("DSX_WAR", false, null)]
        [TestCase("AVAV_", false, null)]
        [TestCase("_B", false, null)]
        [TestCase("", false, null)]
        public void ConvertsOnlyCommonShareTickers(string brokerTicker, bool expected, string expectedLeanTicker)
        {
            var actual = Trading212SymbolMapper.TryToLeanTicker(brokerTicker, out var leanTicker);

            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(leanTicker, Is.EqualTo(expectedLeanTicker));
        }

        [Test]
        public void FallsBackToParsingWhenTheInstrumentListHasNotSeenTheTicker()
        {
            var mapper = new Trading212SymbolMapper();

            Assert.That(mapper.TryGetLeanSymbol("MSFT_US_EQ", out var symbol), Is.True);
            Assert.That(symbol.Value, Is.EqualTo("MSFT"));
            Assert.That(mapper.GetBrokerageSymbol(symbol), Is.EqualTo("MSFT_US_EQ"));
        }

        [TestCase("AAPL_US_EQ", "AAPL", "US", "EQ")]
        [TestCase("BRK_B_US_EQ", "BRK_B", "US", "EQ")]
        [TestCase("VUSA_US_ETF", "VUSA", "US", "ETF")]
        [TestCase("VOD_GB_EQ", "VOD", "GB", "EQ")]
        public void ParsesTheCompositeTicker(string brokerageSymbol, string ticker, string country, string instrumentType)
        {
            Assert.That(Trading212SymbolMapper.TryParseTicker(brokerageSymbol, out var parsedTicker, out var parsedCountry, out var parsedType), Is.True);
            Assert.That(parsedTicker, Is.EqualTo(ticker));
            Assert.That(parsedCountry, Is.EqualTo(country));
            Assert.That(parsedType, Is.EqualTo(instrumentType));
        }

        [TestCase("AAPL")]
        [TestCase("AAPL_US")]
        [TestCase("")]
        public void RejectsMalformedTickers(string brokerageSymbol)
        {
            Assert.That(Trading212SymbolMapper.TryParseTicker(brokerageSymbol, out _, out _, out _), Is.False);
        }

        [TestCase("BRK.B", "BRK_B")]
        [TestCase("AAPL", "AAPL")]
        [TestCase("BF-B", "BF_B")]
        public void NormalizesLeanTickers(string leanTicker, string expected)
        {
            Assert.That(Trading212SymbolMapper.NormalizeTicker(leanTicker), Is.EqualTo(expected));
        }
    }
}
