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
using System.Threading;
using NUnit.Framework;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Exercises the brokerage against a live Trading 212 account.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These tests talk to the real API, so they are marked <see cref="ExplicitAttribute"/> and never run in
    /// CI. They require credentials; see <see cref="Trading212OnlineCredentials"/>.
    /// </para>
    /// <para>
    /// The environment is selected with the <c>TRADING212_ENVIRONMENT</c> variable, <c>demo</c> (the default)
    /// or <c>live</c>. The two environments use different API key pairs. Tests that place orders only run
    /// against <c>demo</c>, because that is paper money; against <c>live</c> only the read-only checks and a
    /// guaranteed broker rejection run.
    /// </para>
    /// <para>
    /// The API key is IP restricted, so the tests can only run from a host the key allows.
    /// </para>
    /// </remarks>
    [TestFixture, Explicit("Calls the real Trading 212 API and requires API keys.")]
    public class Trading212BrokerageOnlineTests
    {
        /// <summary>
        /// How long to wait for the brokerage to report an order's final state.
        /// </summary>
        private static readonly TimeSpan OrderTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Liquid US instruments to prefer when picking something to trade, so a test does not fail on an
        /// illiquid or restricted listing.
        /// </summary>
        private static readonly string[] PreferredTickers =
        {
            "AAPL_US_EQ", "MSFT_US_EQ", "GOOGL_US_EQ", "AMZN_US_EQ", "NVDA_US_EQ", "TSLA_US_EQ", "VUSA_US_ETF"
        };

        private Trading212ApiClient _apiClient;
        private Trading212Brokerage _brokerage;

        /// <summary>
        /// Holds the orders the brokerage resolves by broker id while reporting updates, which in LEAN is the
        /// transaction handler's order provider.
        /// </summary>
        private FakeOrderProvider _orderProvider;

        /// <summary>
        /// Supplies the securities the brokerage prices order fees against, which in LEAN is the algorithm's
        /// portfolio.
        /// </summary>
        private FakeSecurityProvider _securityProvider;

        /// <summary>
        /// The environment the tests run against.
        /// </summary>
        private Trading212Environment _environment;

        /// <summary>
        /// The variable that selects the environment, <c>demo</c> (default) or <c>live</c>.
        /// </summary>
        public const string EnvironmentVariable = "TRADING212_ENVIRONMENT";

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _environment = Trading212Brokerage.ParseEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariable));

            if (!Trading212OnlineCredentials.IsConfigured(_environment))
            {
                Assert.Ignore($"No {_environment} credentials configured. Set " +
                    $"{(_environment == Trading212Environment.Live ? Trading212OnlineCredentials.LiveApiKeyVariable : Trading212OnlineCredentials.PracticeApiKeyVariable)} and " +
                    $"{(_environment == Trading212Environment.Live ? Trading212OnlineCredentials.LiveApiSecretVariable : Trading212OnlineCredentials.PracticeApiSecretVariable)}, " +
                    $"or add them to a .env file, and set {EnvironmentVariable}={_environment.ToString().ToLowerInvariant()} to run the online tests.");
            }

            Log.Trace($"Online tests run against the {_environment} environment.");

            // Poll more often than the production default so the tests do not sit idle.
            Config.Set("trading212-order-update-interval-seconds", 6);
        }

        [SetUp]
        public void SetUp()
        {
            var apiKey = Trading212OnlineCredentials.GetApiKey(_environment);
            var apiSecret = Trading212OnlineCredentials.GetApiSecret(_environment);

            _apiClient = new Trading212ApiClient(apiKey, apiSecret, _environment, messageReceived: null);

            _orderProvider = new FakeOrderProvider();
            _securityProvider = new FakeSecurityProvider();
            _brokerage = new Trading212Brokerage(
                apiKey,
                apiSecret,
                _environment,
                orderProvider: _orderProvider,
                securityProvider: _securityProvider);

            _brokerage.Connect();
        }

        [TearDown]
        public void TearDown()
        {
            _brokerage?.Disconnect();
            _brokerage?.Dispose();
            _apiClient?.Dispose();
        }

        [Test]
        public void ConnectsToTheAccount()
        {
            Assert.That(_brokerage.IsConnected, Is.True);
            Assert.That(_brokerage.AccountBaseCurrency, Is.Not.Null.And.Not.Empty);
            Assert.That(_brokerage.SymbolMapper.InstrumentCount, Is.GreaterThan(0));
            Assert.That(_brokerage.SymbolMapper.MappedInstrumentCount, Is.GreaterThan(0));

            Log.Trace($"Account currency: {_brokerage.AccountBaseCurrency}. " +
                $"{_brokerage.SymbolMapper.MappedInstrumentCount} of {_brokerage.SymbolMapper.InstrumentCount} instruments are supported.");

            // The paper account starts funded, but assert only that the balance is readable so the test
            // does not depend on the starting balance.
            var cash = _brokerage.GetCashBalance();
            Assert.That(cash, Has.Count.EqualTo(1));
            Assert.That(cash[0].Currency, Is.EqualTo(_brokerage.AccountBaseCurrency));

            foreach (var holding in _brokerage.GetAccountHoldings())
            {
                Log.Trace($"Holding: {holding.Symbol} qty={holding.Quantity} avg={holding.AveragePrice} price={holding.MarketPrice} value={holding.MarketValue}");
            }
        }

        /// <summary>
        /// Verifies the cash composition assumption: that <c>cash.inPies</c> is already part of
        /// <c>cash.availableToTrade</c>, so cash does not have to be added twice.
        /// </summary>
        [Test]
        public void AccountSummaryCashFieldsAreConsistent()
        {
            var summary = _apiClient.GetAccountSummaryAsync().SynchronouslyAwaitTaskResult();

            Log.Trace($"Account summary: {JsonConvert.SerializeObject(summary)}");

            Assert.That(summary, Is.Not.Null);
            Assert.That(summary.Cash, Is.Not.Null);
            Assert.That(summary.Currency, Is.Not.Null.And.Not.Empty);

            // An uninvested pie balance must not exceed the tradeable balance if it is a subset of it.
            Assert.That(summary.Cash.InPies, Is.LessThanOrEqualTo(summary.Cash.AvailableToTrade + 0.01m),
                "cash.inPies exceeds cash.availableToTrade, so the two must not be added together in GetCashBalance().");
        }

        /// <summary>
        /// Verifies that a sell of shares the account does not own is rejected by the broker and that the
        /// broker's own message reaches the caller unmodified.
        /// </summary>
        /// <remarks>
        /// This is safe against a real account: the broker rejects the sell, so no order is created and nothing
        /// changes. The test asserts the account holds none of the instrument before attempting the sell.
        /// </remarks>
        [Test]
        public void RejectedSellSurfacesTheBrokerMessage()
        {
            var ticker = PickTradableTicker(excludeHeldTickers: true);
            Assert.That(ticker, Is.Not.Null, "No tradable instrument was available to test a rejection.");

            // Guard the real-account path: never sell something that is actually held.
            var heldQuantity = _apiClient.GetPositionsAsync().SynchronouslyAwaitTaskResult()
                .Where(position => string.Equals(position.Instrument?.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                .Sum(position => position.Quantity);
            Assert.That(heldQuantity, Is.EqualTo(0m), $"The account holds {heldQuantity} of {ticker}, so the rejection test must not run on it.");

            // First hit the API directly, to capture the raw rejection payload this client parses.
            var exception = Assert.Throws<Trading212ApiException>(() =>
                _apiClient.PlaceMarketOrderAsync(new Trading212MarketOrderRequest { Ticker = ticker, Quantity = -100m })
                    .SynchronouslyAwaitTaskResult());

            Assert.That(exception, Is.Not.Null);
            Assert.That(exception.RawBody, Is.Not.Null.And.Not.Empty);
            Log.Trace($"Broker rejection raw body: {exception.RawBody}");
            Log.Trace($"Parsed rejection code: {exception.ErrorCode ?? "<none>"}");
            Log.Trace($"Rejection message surfaced by the client: {exception.Message}");

            // Then through the brokerage, to prove the rejection is not swallowed.
            var symbol = _brokerage.SymbolMapper.GetLeanSymbol(ticker, SecurityType.Equity, Market.USA);
            var order = new MarketOrder(symbol, -100m, DateTime.UtcNow);
            var events = CaptureOrderEvents();

            _orderProvider.Add(order);
            var placed = _brokerage.PlaceOrder(order);
            Assert.That(placed, Is.True);

            var invalid = WaitForStatus(events, order.Id, OrderStatus.Invalid, TimeSpan.FromSeconds(30));
            Assert.That(invalid, Is.Not.Null, "The broker rejection was not surfaced as an Invalid order event.");
            Assert.That(invalid.Message, Is.Not.Null.And.Not.Empty);
            Assert.That(order.BrokerId, Is.Empty, "A rejected order must not keep a broker id.");
            Log.Trace($"Broker rejection message as an order event: {invalid.Message}");
        }

        /// <summary>
        /// Places a market buy, waits for the fill, verifies the reported fill, then sells the position back.
        /// </summary>
        [Test]
        public void PlacesMarketOrderAndObservesTheFill()
        {
            // This test trades, so it is only meaningful (and only safe) on the paper account.
            if (_environment != Trading212Environment.Demo)
            {
                Assert.Ignore($"Placing orders is only exercised against the paper account. Set {EnvironmentVariable}=demo and use demo API keys.");
            }

            var ticker = PickTradableTicker(excludeHeldTickers: true);
            Assert.That(ticker, Is.Not.Null, "No tradable instrument was available to place an order on.");

            var symbol = _brokerage.SymbolMapper.GetLeanSymbol(ticker, SecurityType.Equity, Market.USA);
            _securityProvider.AddEquity(symbol, GetInstrumentCurrency(ticker));
            var order = new MarketOrder(symbol, 1m, DateTime.UtcNow);

            var events = CaptureOrderEvents();

            // LEAN registers the order before submitting it, so the update poller can resolve it by broker
            // id as soon as PlaceOrder assigns one.
            _orderProvider.Add(order);
            _brokerage.PlaceOrder(order);

            Assert.That(order.BrokerId, Has.Count.EqualTo(1), "The placed order did not receive a broker id.");
            Log.Trace($"Placed buy order {order.Id} as broker order {order.BrokerId[0]} on {ticker}.");

            var finalEvent = WaitForFinalStatus(events, order.Id, OrderTimeout);
            Assert.That(finalEvent, Is.Not.Null, $"The order did not reach a final state within {OrderTimeout}.");

            Log.Trace($"Final event: status={finalEvent.Status} fill={finalEvent.FillQuantity}@{finalEvent.FillPrice} fee={finalEvent.OrderFee}");

            // A market order placed while the venue is open fills; while it is closed it stays working.
            // Accept either outcome, but never a rejection.
            Assert.That(finalEvent.Status, Is.Not.EqualTo(OrderStatus.Invalid),
                $"The buy was rejected: {finalEvent.Message}");

            if (finalEvent.Status != OrderStatus.Filled)
            {
                Log.Trace($"The order is still {finalEvent.Status}, so the venue was probably closed. Cancelling it.");
                _brokerage.CancelOrder(order);
                return;
            }

            Assert.That(finalEvent.FillPrice, Is.GreaterThan(0m));
            Assert.That(finalEvent.FillQuantity, Is.GreaterThan(0m));

            // The fill must carry a priced fee, not OrderFee.Zero: Trading 212 charges an FX fee on the
            // converted notional and the US regulatory fees, which Trading212FeeModel computes.
            Assert.That(finalEvent.OrderFee.Value.Amount, Is.GreaterThan(0m), "The fill reported a zero fee.");
            // The fee is computed in the instrument's quote currency and applied to the portfolio in the
            // account currency, so it never leaves a balance in the quote currency.
            Assert.That(finalEvent.OrderFee, Is.InstanceOf<AccountCurrencyOrderFee>(),
                "The fee must settle in the account currency.");
            Log.Trace($"Fee on a {GetInstrumentCurrency(ticker)} buy in a {_brokerage.AccountBaseCurrency} account: {finalEvent.OrderFee}");

            var holding = _brokerage.GetAccountHoldings().FirstOrDefault(holding => holding.Symbol == symbol);
            Assert.That(holding, Is.Not.Null, "The bought position is not reported as a holding.");
            Assert.That(holding.Quantity, Is.GreaterThan(0m));
            Log.Trace($"Holding after the buy: qty={holding.Quantity} avg={holding.AveragePrice} value={holding.MarketValue}");

            // Sell the shares back so the account is left the way it was found.
            var sell = new MarketOrder(symbol, -1m, DateTime.UtcNow);
            var sellEvents = CaptureOrderEvents();
            _orderProvider.Add(sell);
            _brokerage.PlaceOrder(sell);

            var sellFinal = WaitForFinalStatus(sellEvents, sell.Id, OrderTimeout);
            Assert.That(sellFinal, Is.Not.Null, "The sell order did not reach a final state.");
            Assert.That(sellFinal.Status, Is.Not.EqualTo(OrderStatus.Invalid), $"The sell was rejected: {sellFinal.Message}");
            Log.Trace($"Sell final event: status={sellFinal.Status} fill={sellFinal.FillQuantity}@{sellFinal.FillPrice}");
        }

        /// <summary>
        /// Verifies the instrument naming conventions against the account's real instrument list.
        /// </summary>
        /// <remarks>
        /// Trading 212 separates a share class with an underscore or a slash (BRK_B, BRK/A) where LEAN uses a
        /// dot, and also uses an underscore for warrants (&lt;TICKER&gt;_WAR). Only a single-letter suffix is a
        /// share class; anything else must not be mapped to a LEAN symbol.
        /// </remarks>
        [Test]
        public void InstrumentNamingConventionsAreHandled()
        {
            var instruments = _apiClient.GetInstrumentsAsync().SynchronouslyAwaitTaskResult();
            var usEquities = instruments.Where(instrument => instrument.Ticker.EndsWith("_US_EQ", StringComparison.OrdinalIgnoreCase)).ToList();

            var withSeparator = usEquities
                .Select(instrument => (Instrument: instrument, TickerPart: instrument.Ticker[..^"_US_EQ".Length]))
                .Where(entry => entry.TickerPart.Contains('_') || entry.TickerPart.Contains('/'))
                .ToList();

            Log.Trace($"{withSeparator.Count} of {usEquities.Count} US instruments have a separator in their ticker part.");

            var classShares = new List<string>();
            foreach (var (instrument, tickerPart) in withSeparator)
            {
                var mapped = Trading212SymbolMapper.TryToLeanTicker(tickerPart, out var leanTicker);
                Log.Trace($"Separator ticker: {instrument.Ticker} -> mapped={mapped}, lean='{leanTicker}'");

                if (mapped)
                {
                    classShares.Add(instrument.Ticker);
                    Assert.That(leanTicker, Does.Contain('.'));
                    Assert.That(_brokerage.SymbolMapper.IsSupported(instrument.Ticker), Is.True);
                }
                else
                {
                    // A warrant or a malformed ticker must not become a LEAN symbol.
                    Assert.That(_brokerage.SymbolMapper.IsSupported(instrument.Ticker), Is.False);
                }
            }

            Log.Trace($"Share classes: {string.Join(", ", classShares)}");

            // Berkshire class B and class A are the documented class shares on the US market.
            foreach (var ticker in new[] { "BRK_B_US_EQ", "BRK/A_US_EQ" })
            {
                if (usEquities.Any(instrument => instrument.Ticker == ticker))
                {
                    Assert.That(_brokerage.SymbolMapper.IsSupported(ticker), Is.True, $"{ticker} should be mapped as a share class.");
                }
            }

            // Warrants are the documented exception: they use an underscore but are not common shares.
            var warrants = withSeparator.Where(entry => entry.TickerPart.EndsWith("_WAR", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.That(warrants.All(entry => !Trading212SymbolMapper.TryToLeanTicker(entry.TickerPart, out _)), Is.True,
                "A warrant ticker must not be mapped to a LEAN symbol.");
        }

        /// <summary>
        /// Captures the order events the brokerage reports.
        /// </summary>
        /// <returns>A thread-safe list that is appended to as events arrive.</returns>
        private List<OrderEvent> CaptureOrderEvents()
        {
            var events = new List<OrderEvent>();
            _brokerage.OrdersStatusChanged += (_, orderEvents) =>
            {
                lock (events)
                {
                    events.AddRange(orderEvents);
                }
            };

            return events;
        }

        /// <summary>
        /// Waits for a specific status of an order.
        /// </summary>
        /// <param name="events">The captured events.</param>
        /// <param name="orderId">The LEAN order id.</param>
        /// <param name="status">The status to wait for.</param>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The matching event, or <c>null</c> on timeout.</returns>
        private static OrderEvent WaitForStatus(List<OrderEvent> events, int orderId, OrderStatus status, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (events)
                {
                    var match = events.FirstOrDefault(orderEvent => orderEvent.OrderId == orderId && orderEvent.Status == status);
                    if (match != null)
                    {
                        return match;
                    }
                }

                Thread.Sleep(200);
            }

            return null;
        }

        /// <summary>
        /// Waits for an order to reach any status LEAN treats as final.
        /// </summary>
        /// <param name="events">The captured events.</param>
        /// <param name="orderId">The LEAN order id.</param>
        /// <param name="timeout">How long to wait.</param>
        /// <returns>The final event, or <c>null</c> on timeout.</returns>
        private static OrderEvent WaitForFinalStatus(List<OrderEvent> events, int orderId, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (events)
                {
                    var match = events.LastOrDefault(orderEvent =>
                        orderEvent.OrderId == orderId &&
                        (orderEvent.Status == OrderStatus.Filled ||
                         orderEvent.Status == OrderStatus.Canceled ||
                         orderEvent.Status == OrderStatus.Invalid));
                    if (match != null)
                    {
                        return match;
                    }
                }

                Thread.Sleep(200);
            }

            return null;
        }

        /// <summary>
        /// Returns the instrument currency of a broker ticker.
        /// </summary>
        /// <param name="ticker">The broker ticker.</param>
        /// <returns>The ISO 4217 currency code.</returns>
        private string GetInstrumentCurrency(string ticker)
        {
            return _brokerage.SymbolMapper.TryGetInstrument(ticker, out var instrument)
                ? instrument.CurrencyCode
                : Currencies.USD;
        }

        /// <summary>
        /// Picks a supported instrument this test can trade.
        /// </summary>
        /// <param name="excludeHeldTickers">
        /// When true, instruments already held are skipped, so a rejection test sells something not owned and
        /// a buy test starts from a flat position.
        /// </param>
        /// <returns>The broker ticker, or <c>null</c> when none was available.</returns>
        private string PickTradableTicker(bool excludeHeldTickers)
        {
            var held = excludeHeldTickers
                ? _apiClient.GetPositionsAsync().SynchronouslyAwaitTaskResult()
                    .Select(position => position.Instrument?.Ticker)
                    .Where(ticker => ticker != null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var candidates = _apiClient.GetInstrumentsAsync().SynchronouslyAwaitTaskResult()
                .Where(instrument => _brokerage.SymbolMapper.IsSupported(instrument.Ticker))
                .Where(instrument => !held.Contains(instrument.Ticker))
                .OrderBy(instrument =>
                {
                    var preferred = Array.IndexOf(PreferredTickers, instrument.Ticker);
                    return preferred < 0 ? int.MaxValue : preferred;
                })
                .ThenBy(instrument => instrument.Ticker, StringComparer.Ordinal)
                .ToList();

            // Dynamics of the paper account are not guaranteed, so log what was considered.
            Log.Trace($"{candidates.Count} tradable instrument(s) available after excluding {held.Count} held position(s).");

            return candidates.FirstOrDefault()?.Ticker;
        }
    }
}
