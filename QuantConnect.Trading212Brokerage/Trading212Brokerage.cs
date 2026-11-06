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
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using QuantConnect.Api;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Util;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.Models;
using TimeInForce = QuantConnect.Orders.TimeInForce;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("QuantConnect.Brokerages.Trading212.Tests")]

namespace QuantConnect.Brokerages.Trading212
{
    /// <summary>
    /// The Trading 212 live brokerage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trading 212's Public API is order and account only: it serves no market data and no history, so the
    /// brokerage implements no data feed and the LEAN launcher must be configured with
    /// <c>QuantConnect.Lean.Engine.DataFeeds.Queues.LiveDataQueue</c> and
    /// <c>SubscriptionDataReaderHistoryProvider</c>.
    /// </para>
    /// <para>
    /// The API offers no streaming or webhook for order updates, so order state is observed by polling the
    /// pending orders and historical orders endpoints on a background task.
    /// </para>
    /// <para>
    /// Accounts are long-only cash accounts. A broker rejection is never swallowed: it is surfaced to the
    /// algorithm as an <see cref="OrderEvent"/> with <see cref="OrderStatus.Invalid"/> carrying the broker's
    /// own message.
    /// </para>
    /// </remarks>
    [BrokerageFactory(typeof(Trading212BrokerageFactory))]
    public class Trading212Brokerage : Brokerage
    {
        /// <summary>
        /// How far before a placement request a broker order may have been created and still be treated as the
        /// lost order, in minutes.
        /// </summary>
        private const int PlacementReconciliationWindowMinutes = 5;

        /// <summary>
        /// The default number of historical order pages read on each poll while resolving finished orders.
        /// </summary>
        private const int DefaultOrderHistoryPages = 2;

        /// <summary>
        /// The API client, or <c>null</c> when the brokerage was created without credentials.
        /// </summary>
        private Trading212ApiClient _apiClient;

        /// <summary>
        /// The symbol mapper, built from the instruments metadata endpoint.
        /// </summary>
        private Trading212SymbolMapper _symbolMapper;

        /// <summary>
        /// Cancels the order update poller.
        /// </summary>
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        /// <summary>
        /// Signals that the order update poller has stopped.
        /// </summary>
        private readonly ManualResetEvent _orderUpdateEndEvent = new(false);

        /// <summary>
        /// The broker order ids that are still expected to reach a final state.
        /// </summary>
        private readonly ConcurrentDictionary<string, byte> _trackedOrderIds = new();

        /// <summary>
        /// The last LEAN status emitted for a LEAN order id, so repeated polls do not re-emit it.
        /// </summary>
        private readonly ConcurrentDictionary<int, OrderStatus> _lastOrderStatusByLeanOrderId = new();

        /// <summary>
        /// The cumulative filled value already reported for a LEAN order id, in the instrument's currency. The
        /// pending orders endpoint reports a cumulative quantity and value but no execution price, so the price
        /// of an incremental fill is the change in value over the change in quantity.
        /// </summary>
        private readonly ConcurrentDictionary<int, decimal> _reportedFillValueByLeanOrderId = new();

        /// <summary>
        /// The broker ticker of each tracked order, so an order that has left the pending orders and the
        /// recent history pages can be looked up with the endpoint's ticker filter.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _trackedOrderTickers = new();

        /// <summary>
        /// How many times the historical orders endpoint has been read looking for a tracked order.
        /// </summary>
        private readonly ConcurrentDictionary<string, int> _historyLookupAttempts = new();

        /// <summary>
        /// How many polls to keep looking for a tracked order in the historical orders before giving up.
        /// </summary>
        private const int MaxHistoryLookupAttempts = 30;

        /// <summary>
        /// The cumulative filled quantity already reported for a LEAN order id. LEAN needs the incremental
        /// quantity on each event, while the broker reports a cumulative one.
        /// </summary>
        private readonly ConcurrentDictionary<int, decimal> _reportedFillQuantityByLeanOrderId = new();

        /// <summary>
        /// How often the order update poller runs.
        /// </summary>
        private TimeSpan _orderUpdateInterval;

        /// <summary>
        /// How many historical order pages the poller reads while resolving finished orders.
        /// </summary>
        private int _orderHistoryPages;

        /// <summary>
        /// The background task polling for order updates.
        /// </summary>
        private Task _orderUpdateTask;

        /// <summary>
        /// Whether the brokerage is connected.
        /// </summary>
        private bool _isConnected;

        /// <summary>
        /// The primary account currency reported by the broker, for example <c>GBP</c>.
        /// </summary>
        private string _accountCurrency;

        /// <summary>
        /// Ensures the "no history" warning is only logged once.
        /// </summary>
        private bool _historyNotSupportedWarningSent;

        /// <summary>
        /// Guards against running the initialization twice.
        /// </summary>
        private bool _isInitialized;

        /// <summary>
        /// The order provider, used to resolve the LEAN orders a broker order belongs to.
        /// </summary>
        protected IOrderProvider OrderProvider { get; private set; }

        /// <summary>
        /// The symbol mapper, exposed for tests.
        /// </summary>
        internal Trading212SymbolMapper SymbolMapper => _symbolMapper;

        /// <summary>
        /// The primary account currency, exposed for tests.
        /// </summary>
        internal string AccountCurrency => _accountCurrency;

        /// <summary>
        /// The API client, exposed for tests.
        /// </summary>
        internal Trading212ApiClient ApiClient => _apiClient;

        /// <inheritdoc/>
        public override bool IsConnected => _isConnected;

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="orderProvider">The order provider.</param>
        public Trading212Brokerage(string apiKey, string apiSecret, Trading212Environment environment,
            IOrderProvider orderProvider)
            : base("Trading212")
        {
            Initialize(apiKey, apiSecret, environment, orderProvider);
        }

        /// <summary>
        /// Creates a new instance, taking the order provider from the algorithm.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="algorithm">The algorithm instance.</param>
        public Trading212Brokerage(string apiKey, string apiSecret, Trading212Environment environment, IAlgorithm algorithm)
            : this(apiKey, apiSecret, environment, algorithm?.Portfolio?.Transactions)
        {
        }

        /// <summary>
        /// Creates a new instance backed by an already constructed API client.
        /// </summary>
        /// <remarks>
        /// Intended for unit testing so a fake HTTP handler can be injected, and so the symbol mapping can be
        /// exercised without a live account.
        /// </remarks>
        /// <param name="apiClient">The API client to use.</param>
        /// <param name="orderProvider">The order provider used to resolve LEAN orders by broker id.</param>
        internal Trading212Brokerage(Trading212ApiClient apiClient, IOrderProvider orderProvider = null)
            : base("Trading212")
        {
            _isInitialized = true;
            _apiClient = apiClient;
            _symbolMapper = new Trading212SymbolMapper();
            OrderProvider = orderProvider;
            _orderUpdateInterval = TimeSpan.FromSeconds(5);
            _orderHistoryPages = 1;
        }

        /// <summary>
        /// Initializes the brokerage. Runs at most once per instance.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="orderProvider">The order provider.</param>
        protected void Initialize(string apiKey, string apiSecret, Trading212Environment environment, IOrderProvider orderProvider)
        {
            if (_isInitialized)
            {
                return;
            }

            _isInitialized = true;
            OrderProvider = orderProvider;
            _symbolMapper = new Trading212SymbolMapper();

            _orderUpdateInterval = TimeSpan.FromSeconds(Config.GetInt("trading212-order-update-interval-seconds", 10));
            _orderHistoryPages = Math.Max(1, Config.GetInt("trading212-order-history-pages", DefaultOrderHistoryPages));

            _apiClient = CreateApiClient(apiKey, apiSecret, environment);

            ValidateSubscription();
        }

        /// <summary>
        /// Creates the API client, or returns <c>null</c> when no credentials were supplied.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <returns>The API client, or <c>null</c>.</returns>
        private Trading212ApiClient CreateApiClient(string apiKey, string apiSecret, Trading212Environment environment)
        {
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            {
                return null;
            }

            return new Trading212ApiClient(apiKey, apiSecret, environment, OnBrokerageMessageEventHandler);
        }

        /// <summary>
        /// Parses the configured environment name.
        /// </summary>
        /// <remarks>
        /// There is no default: an unset or unrecognized value throws rather than falling back to the
        /// environment that trades real money.
        /// </remarks>
        /// <param name="value">The configured value, either <c>live</c> or <c>demo</c>.</param>
        /// <returns>The parsed environment.</returns>
        /// <exception cref="ArgumentException">The value is missing or not a known environment.</exception>
        public static Trading212Environment ParseEnvironment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "The Trading 212 environment is not configured. Set 'trading212-environment' to 'live' or 'demo'.",
                    nameof(value));
            }

            if (Enum.TryParse<Trading212Environment>(value.Trim(), ignoreCase: true, out var environment)
                && Enum.IsDefined(environment))
            {
                return environment;
            }

            // Accept the words the Trading 212 documentation uses for the demo environment.
            switch (value.Trim().ToLowerInvariant())
            {
                case "paper":
                case "paper-trading":
                case "paper trading":
                    return Trading212Environment.Demo;
                default:
                    throw new ArgumentException(
                        $"'{value}' is not a Trading 212 environment. Set 'trading212-environment' to 'live' or 'demo'.",
                        nameof(value));
            }
        }

        #region Brokerage

        /// <summary>
        /// Connects the client to the broker's remote servers.
        /// </summary>
        /// <remarks>
        /// The connection is validated by reading the account summary, which also yields the primary
        /// account currency, and the instruments metadata, which builds the symbol mapping.
        /// </remarks>
        public override void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            if (_apiClient == null)
            {
                throw new InvalidOperationException(
                    $"{nameof(Trading212Brokerage)}.{nameof(Connect)}: An API key and secret are required. " +
                    "Set 'trading212-api-key' and 'trading212-api-secret' in the configuration.");
            }

            var summary = _apiClient.GetAccountSummaryAsync().SynchronouslyAwaitTaskResult();
            if (summary == null)
            {
                throw new InvalidOperationException($"{nameof(Trading212Brokerage)}.{nameof(Connect)}: The account summary could not be read; check the API key and secret.");
            }

            _accountCurrency = summary.Currency;
            AccountBaseCurrency = _accountCurrency;

            var instruments = _apiClient.GetInstrumentsAsync().SynchronouslyAwaitTaskResult();
            var mapped = _symbolMapper.SetInstruments(instruments);
            Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(Connect)}: {mapped} of {instruments?.Count ?? 0} instruments are supported.");

            // Seed the orders that are already working so their final state is reported.
            foreach (var pending in _apiClient.GetPendingOrdersAsync().SynchronouslyAwaitTaskResult())
            {
                var brokerageId = GetBrokerageOrderId(pending.Id);
                _trackedOrderIds[brokerageId] = 0;
                if (!string.IsNullOrEmpty(pending.Ticker))
                {
                    _trackedOrderTickers[brokerageId] = pending.Ticker;
                }
            }

            _isConnected = true;
            _orderUpdateTask = Task.Factory.StartNew(OrderUpdateLoop, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Disconnects the client from the broker's remote servers.
        /// </summary>
        public override void Disconnect()
        {
            if (!IsConnected)
            {
                return;
            }

            _isConnected = false;
            _cancellationTokenSource.Cancel();

            if (!_orderUpdateEndEvent.WaitOne(TimeSpan.FromSeconds(5)))
            {
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(Disconnect)}: Timed out waiting for the order update task to stop.");
            }
        }

        /// <summary>
        /// Gets the current cash balance for each currency held in the brokerage account.
        /// </summary>
        /// <remarks>
        /// Trading 212 books every transaction in the primary account currency, so a single balance is
        /// returned. The balance is the cash available to trade plus the cash reserved for pending orders:
        /// the API documents <c>availableToTrade</c> as excluding the reserved amount, and the uninvested
        /// cash inside pies is already part of <c>availableToTrade</c>.
        /// TODO: verify with a live account whether <c>cash.inPies</c> is already included in
        /// <c>cash.availableToTrade</c>; if it is not, it must be added here.
        /// </remarks>
        /// <returns>The current cash balance.</returns>
        public override List<CashAmount> GetCashBalance()
        {
            var summary = _apiClient.GetAccountSummaryAsync().SynchronouslyAwaitTaskResult();
            if (summary == null)
            {
                return new List<CashAmount>();
            }

            _accountCurrency = summary.Currency;
            AccountBaseCurrency = _accountCurrency;

            var cash = summary.Cash ?? new Trading212Cash();
            Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(GetCashBalance)}: availableToTrade={cash.AvailableToTrade}, reservedForOrders={cash.ReservedForOrders}, inPies={cash.InPies}, currency={summary.Currency}");

            return new List<CashAmount>
            {
                new CashAmount(cash.AvailableToTrade + cash.ReservedForOrders, summary.Currency)
            };
        }

        /// <summary>
        /// Gets all holdings for the account.
        /// </summary>
        /// <returns>The current holdings.</returns>
        public override List<Holding> GetAccountHoldings()
        {
            var positions = _apiClient.GetPositionsAsync().SynchronouslyAwaitTaskResult();
            var holdings = new List<Holding>();

            foreach (var position in positions)
            {
                var ticker = position?.Instrument?.Ticker;
                if (!_symbolMapper.TryGetLeanSymbol(ticker, out var symbol))
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedPosition",
                        $"The position in '{ticker}' is not a US-listed equity or ETF and was skipped."));
                    continue;
                }

                var marketValue = position.WalletImpact?.CurrentValue ?? 0m;
                var unrealized = position.WalletImpact?.UnrealizedProfitLoss ?? 0m;
                var totalCost = position.WalletImpact?.TotalCost ?? 0m;

                holdings.Add(new Holding
                {
                    Symbol = symbol,
                    Quantity = position.Quantity,
                    // Prices are quoted in the instrument's currency, the market value in the account currency.
                    AveragePrice = position.AveragePricePaid,
                    MarketPrice = position.CurrentPrice,
                    MarketValue = marketValue,
                    CurrencySymbol = position.Instrument?.Currency,
                    ConversionRate = GetConversionRate(position.Quantity, position.CurrentPrice, marketValue),
                    UnrealizedPnL = unrealized,
                    UnrealizedPnLPercent = totalCost == 0m ? 0m : unrealized / totalCost * 100m
                });
            }

            return holdings;
        }

        /// <summary>
        /// Derives the rate that converts the position's instrument-currency value into the account currency.
        /// </summary>
        /// <param name="quantity">The position quantity.</param>
        /// <param name="price">The position price in the instrument currency.</param>
        /// <param name="marketValue">The position market value in the account currency.</param>
        /// <returns>The conversion rate, defaulting to 1.</returns>
        private static decimal GetConversionRate(decimal quantity, decimal price, decimal marketValue)
        {
            var instrumentValue = quantity * price;
            if (instrumentValue == 0m)
            {
                return 1m;
            }

            return marketValue / instrumentValue;
        }

        /// <summary>
        /// Gets all open orders on the account.
        /// </summary>
        /// <remarks>The returned orders do not have QC order ids, only broker ids.</remarks>
        /// <returns>The open orders.</returns>
        public override List<Order> GetOpenOrders()
        {
            var pending = _apiClient.GetPendingOrdersAsync().SynchronouslyAwaitTaskResult();
            var orders = new List<Order>();

            foreach (var brokerageOrder in pending)
            {
                if (TryConvertToLeanOrder(brokerageOrder, out var leanOrder))
                {
                    orders.Add(leanOrder);
                }
                else
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedOpenOrder",
                        $"The pending order '{brokerageOrder?.Ticker}' could not be converted to a LEAN order and was skipped."));
                }
            }

            return orders;
        }

        /// <summary>
        /// Converts a broker order into the equivalent LEAN order.
        /// </summary>
        /// <param name="brokerageOrder">The broker order.</param>
        /// <param name="leanOrder">The LEAN order, when the conversion succeeded.</param>
        /// <returns><c>true</c> when the order could be converted.</returns>
        internal bool TryConvertToLeanOrder(Trading212Order brokerageOrder, out Order leanOrder)
        {
            leanOrder = null;
            if (brokerageOrder == null || !_symbolMapper.TryGetLeanSymbol(brokerageOrder.Ticker, out var symbol))
            {
                return false;
            }

            var time = brokerageOrder.CreatedAtUtc ?? DateTime.UtcNow;
            var quantity = brokerageOrder.Quantity;

            switch (brokerageOrder.Type)
            {
                case Trading212OrderType.Market:
                    leanOrder = new MarketOrder(symbol, quantity, time);
                    break;
                case Trading212OrderType.Limit when brokerageOrder.LimitPrice.HasValue:
                    leanOrder = new LimitOrder(symbol, quantity, brokerageOrder.LimitPrice.Value, time);
                    break;
                case Trading212OrderType.Stop when brokerageOrder.StopPrice.HasValue:
                    leanOrder = new StopMarketOrder(symbol, quantity, brokerageOrder.StopPrice.Value, time);
                    break;
                case Trading212OrderType.StopLimit when brokerageOrder.StopPrice.HasValue && brokerageOrder.LimitPrice.HasValue:
                    leanOrder = new StopLimitOrder(symbol, quantity, brokerageOrder.StopPrice.Value, brokerageOrder.LimitPrice.Value, time);
                    break;
                default:
                    return false;
            }

            leanOrder.BrokerId.Add(GetBrokerageOrderId(brokerageOrder.Id));
            var status = brokerageOrder.Status.ToLeanOrderStatus();
            if (status.HasValue)
            {
                leanOrder.Status = status.Value;
            }

            return true;
        }

        /// <summary>
        /// Places a new order and assigns a new broker id to the order.
        /// </summary>
        /// <remarks>
        /// A broker rejection is surfaced as an <see cref="OrderEvent"/> with
        /// <see cref="OrderStatus.Invalid"/> carrying the broker's own message. It is never swallowed.
        /// </remarks>
        /// <param name="order">The order to be placed.</param>
        /// <returns><c>true</c>, because the outcome is reported through the order event.</returns>
        public override bool PlaceOrder(Order order)
        {
            if (!IsConnected)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Trading 212 is not connected.")
                {
                    Status = OrderStatus.Invalid,
                    Message = "Trading 212 is not connected."
                });
                return true;
            }

            if (!_symbolMapper.TryGetBrokerageSymbol(order.Symbol, out var ticker))
            {
                var message = $"The symbol '{order.Symbol}' is not a US-listed equity or ETF available on Trading 212.";
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedSymbol", message));
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message)
                {
                    Status = OrderStatus.Invalid,
                    Message = message
                });
                return true;
            }

            if (!order.TimeInForce.TryGetTimeValidity(out var timeValidity))
            {
                var message = $"Trading 212 only supports DAY and GOOD_TILL_CANCEL orders, but the order uses '{order.TimeInForce}'.";
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedTimeInForce", message));
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message)
                {
                    Status = OrderStatus.Invalid,
                    Message = message
                });
                return true;
            }

            var placedAtUtc = DateTime.UtcNow;

            try
            {
                var placed = PlaceBrokerageOrder(order, ticker, timeValidity);
                OnOrderPlaced(order, ticker, placed);
            }
            catch (Trading212OrderPlacementException exception)
            {
                // The broker may have accepted the order and lost the response, so ask what it holds before
                // telling the algorithm the order failed.
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(PlaceOrder)}: The outcome of order {order.Id} is unknown: {exception.Message}");

                var placed = TryReconcilePlacement(order, ticker, placedAtUtc);
                if (placed != null)
                {
                    Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(PlaceOrder)}: Order {order.Id} was found at Trading 212 as order {placed.Id} after an ambiguous response.");
                    OnOrderPlaced(order, ticker, placed);
                    return true;
                }

                var message = $"The order may or may not have been placed at Trading 212 ({exception.Message}). It was not found in the broker's orders, so check the account before placing it again.";
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "OrderPlacementUnknown", message));
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, message)
                {
                    Status = OrderStatus.Invalid,
                    Message = message
                });
            }
            catch (Trading212ApiException exception)
            {
                // The broker refused the order: carry its own message to the algorithm.
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(PlaceOrder)}: Trading 212 rejected the order {order.Id}: {exception.Message}");
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, exception.Message)
                {
                    Status = OrderStatus.Invalid,
                    Message = exception.Message
                });
            }
            catch (Exception exception)
            {
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(PlaceOrder)}: {exception}");
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, exception.Message)
                {
                    Status = OrderStatus.Invalid,
                    Message = exception.Message
                });
            }

            return true;
        }

        /// <summary>
        /// Registers a broker order with LEAN and reports it as submitted.
        /// </summary>
        /// <param name="order">The LEAN order.</param>
        /// <param name="ticker">The broker ticker.</param>
        /// <param name="placed">The order as the broker recorded it.</param>
        private void OnOrderPlaced(Order order, string ticker, Trading212Order placed)
        {
            if (placed == null || placed.Id == 0)
            {
                throw new InvalidOperationException("Trading 212 accepted the request but returned no order id.");
            }

            var brokerageId = GetBrokerageOrderId(placed.Id);
            order.BrokerId.Add(brokerageId);
            _trackedOrderIds[brokerageId] = 0;
            _trackedOrderTickers[brokerageId] = string.IsNullOrEmpty(placed.Ticker) ? ticker : placed.Ticker;
            _lastOrderStatusByLeanOrderId[order.Id] = OrderStatus.Submitted;

            OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Trading 212 order submitted.")
            {
                Status = OrderStatus.Submitted
            });
        }

        /// <summary>
        /// Looks for an order the broker accepted but whose response was lost, so it is not placed twice and is
        /// not reported as failed when it is live.
        /// </summary>
        /// <param name="order">The LEAN order whose placement outcome is unknown.</param>
        /// <param name="ticker">The broker ticker.</param>
        /// <param name="placedAtUtc">When the placement request was sent.</param>
        /// <returns>The matching broker order, or <c>null</c> when it was not found.</returns>
        private Trading212Order TryReconcilePlacement(Order order, string ticker, DateTime placedAtUtc)
        {
            // Only orders this brokerage has not already accounted for can be the lost one.
            var since = placedAtUtc.AddMinutes(-PlacementReconciliationWindowMinutes);

            try
            {
                var pending = _apiClient.GetPendingOrdersAsync().SynchronouslyAwaitTaskResult();
                var match = FindMatchingOrder(pending, order, ticker, since);
                if (match != null)
                {
                    return match;
                }

                // A market order can fill before the next poll, so the working orders alone are not enough.
                var history = _apiClient.GetHistoricalOrdersAsync(_orderHistoryPages, ticker).SynchronouslyAwaitTaskResult();
                var historicalMatch = FindMatchingOrder(history.Where(entry => entry?.Order != null).Select(entry => entry.Order), order, ticker, since);
                return historicalMatch;
            }
            catch (Exception exception)
            {
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(TryReconcilePlacement)}: Could not read the broker orders to resolve order {order.Id}: {exception.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the broker order that matches a LEAN order placement request.
        /// </summary>
        /// <param name="brokerageOrders">The broker orders to search.</param>
        /// <param name="order">The LEAN order.</param>
        /// <param name="ticker">The broker ticker.</param>
        /// <param name="sinceUtc">The earliest creation time a candidate may have.</param>
        /// <returns>The matching order, or <c>null</c>.</returns>
        private Trading212Order FindMatchingOrder(IEnumerable<Trading212Order> brokerageOrders, Order order, string ticker, DateTime sinceUtc)
        {
            var side = order.Quantity < 0 ? Trading212OrderSide.Sell : Trading212OrderSide.Buy;
            var quantity = Math.Abs(order.Quantity);

            return brokerageOrders.FirstOrDefault(candidate =>
                candidate != null
                && !_trackedOrderIds.ContainsKey(GetBrokerageOrderId(candidate.Id))
                && string.Equals(candidate.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
                && candidate.Side == side
                && Math.Abs(candidate.Quantity) == quantity
                && (candidate.CreatedAtUtc == null || candidate.CreatedAtUtc.Value >= sinceUtc));
        }

        /// <summary>
        /// Sends the placement request that matches the LEAN order type.
        /// </summary>
        /// <param name="order">The LEAN order.</param>
        /// <param name="ticker">The broker ticker.</param>
        /// <param name="timeValidity">The broker time validity.</param>
        /// <returns>The order as the broker recorded it.</returns>
        private Trading212Order PlaceBrokerageOrder(Order order, string ticker, Trading212TimeValidity timeValidity)
        {
            switch (order.Type)
            {
                case OrderType.Market:
                    return _apiClient.PlaceMarketOrderAsync(new Trading212MarketOrderRequest
                    {
                        Ticker = ticker,
                        Quantity = order.Quantity,
                        // TODO: expose extended hours trading through an IOrderProperties implementation once
                        // the brokerage model supports it; the API defaults it to false.
                        ExtendedHours = false
                    }).SynchronouslyAwaitTaskResult();

                case OrderType.Limit:
                    var limitOrder = (LimitOrder)order;
                    return _apiClient.PlaceLimitOrderAsync(new Trading212LimitOrderRequest
                    {
                        Ticker = ticker,
                        Quantity = order.Quantity,
                        LimitPrice = limitOrder.LimitPrice,
                        TimeValidity = timeValidity
                    }).SynchronouslyAwaitTaskResult();

                case OrderType.StopMarket:
                    var stopOrder = (StopMarketOrder)order;
                    return _apiClient.PlaceStopOrderAsync(new Trading212StopOrderRequest
                    {
                        Ticker = ticker,
                        Quantity = order.Quantity,
                        StopPrice = stopOrder.StopPrice,
                        TimeValidity = timeValidity
                    }).SynchronouslyAwaitTaskResult();

                case OrderType.StopLimit:
                    var stopLimitOrder = (StopLimitOrder)order;
                    return _apiClient.PlaceStopLimitOrderAsync(new Trading212StopLimitOrderRequest
                    {
                        Ticker = ticker,
                        Quantity = order.Quantity,
                        StopPrice = stopLimitOrder.StopPrice,
                        LimitPrice = stopLimitOrder.LimitPrice,
                        TimeValidity = timeValidity
                    }).SynchronouslyAwaitTaskResult();

                default:
                    throw new NotSupportedException($"The order type '{order.Type}' is not supported by Trading 212.");
            }
        }

        /// <summary>
        /// Updates the order with the same id.
        /// </summary>
        /// <remarks>
        /// The Trading 212 Public API exposes no replace or amend endpoint: an order can only be placed or
        /// cancelled. Amending an order would have to be done as a cancel followed by a new order, which
        /// changes the broker id and can leave the account exposed between the two calls, so it is not done
        /// implicitly. The caller is told to cancel and re-place the order instead.
        /// </remarks>
        /// <param name="order">The new order information.</param>
        /// <returns><c>false</c>, because the API cannot amend an order.</returns>
        public override bool UpdateOrder(Order order)
        {
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateOrderNotSupported",
                $"Trading 212's public API cannot amend an order, so order {order.Id} ({order.Symbol}) was not updated. Cancel it and place a new order instead."));
            return false;
        }

        /// <summary>
        /// Cancels the order with the specified id.
        /// </summary>
        /// <param name="order">The order to cancel.</param>
        /// <returns><c>true</c> when the cancellation request was accepted.</returns>
        public override bool CancelOrder(Order order)
        {
            var brokerageId = order.BrokerId.LastOrDefault();
            if (!long.TryParse(brokerageId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderId))
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelOrderMissingBrokerId",
                    $"The order {order.Id} ({order.Symbol}) has no Trading 212 order id and cannot be cancelled."));
                return false;
            }

            try
            {
                _apiClient.CancelOrderAsync(orderId).SynchronouslyAwaitTask();
                return true;
            }
            catch (Trading212ApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // The order is no longer pending: it either filled or was already cancelled. The poller
                // reports its final state, so this is not a failure.
                Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(CancelOrder)}: Order {orderId} is no longer pending on Trading 212.");
                return true;
            }
            catch (Trading212ApiException exception)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "CancelOrderFailed",
                    $"Failed to cancel order {order.Id} ({order.Symbol}) at Trading 212: {exception.Message}"));
                return false;
            }
            catch (Exception exception)
            {
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(CancelOrder)}: {exception}");
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "CancelOrderFailed",
                    $"Failed to cancel order {order.Id} ({order.Symbol}) at Trading 212: {exception.Message}"));
                return false;
            }
        }

        #endregion

        #region Order updates

        /// <summary>
        /// Polls the broker for order updates until the brokerage disconnects.
        /// </summary>
        private void OrderUpdateLoop()
        {
            try
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    // Wait first: Connect has just read the pending orders, so there is nothing new yet.
                    if (_cancellationTokenSource.Token.WaitHandle.WaitOne(_orderUpdateInterval))
                    {
                        break;
                    }

                    try
                    {
                        PollOrders();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        Log.Error($"{nameof(Trading212Brokerage)}.{nameof(OrderUpdateLoop)}: {exception}");
                    }
                }
            }
            finally
            {
                _orderUpdateEndEvent.Set();
            }
        }

        /// <summary>
        /// Reads the pending and recently finished orders and reports any state change to LEAN.
        /// </summary>
        private void PollOrders()
        {
            var cancellationToken = _cancellationTokenSource.Token;

            var pendingOrders = _apiClient.GetPendingOrdersAsync(cancellationToken).SynchronouslyAwaitTaskResult();
            var pendingById = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pending in pendingOrders)
            {
                var brokerageId = GetBrokerageOrderId(pending.Id);
                pendingById.Add(brokerageId);
                if (!string.IsNullOrEmpty(pending.Ticker))
                {
                    _trackedOrderTickers[brokerageId] = pending.Ticker;
                }

                ApplyBrokerageOrder(pending, fill: null);
            }

            var unresolved = _trackedOrderIds.Keys.Where(id => !pendingById.Contains(id)).ToList();
            if (unresolved.Count == 0)
            {
                return;
            }

            // The API offers no time filter on the historical orders endpoint, only a cursor, so the most
            // recent pages are read and matched by order id.
            var history = _apiClient.GetHistoricalOrdersAsync(_orderHistoryPages, cancellationToken: cancellationToken).SynchronouslyAwaitTaskResult();
            var historyById = new Dictionary<string, Trading212HistoricalOrder>(StringComparer.Ordinal);
            foreach (var entry in history)
            {
                if (entry?.Order == null)
                {
                    continue;
                }

                historyById[GetBrokerageOrderId(entry.Order.Id)] = entry;
            }

            foreach (var brokerageId in unresolved)
            {
                if (historyById.TryGetValue(brokerageId, out var entry))
                {
                    ApplyBrokerageOrder(entry.Order, entry.Fill);
                    continue;
                }

                // The order is not in the pages that were read yet. The endpoint has no time filter, so keep
                // asking for a bounded number of polls and then stop rather than polling for ever.
                var attempts = _historyLookupAttempts.AddOrUpdate(brokerageId, 1, (_, count) => count + 1);
                if (attempts < MaxHistoryLookupAttempts)
                {
                    continue;
                }

                // Last chance: the order may have fallen out of the recent pages, so ask for its ticker only.
                if (_trackedOrderTickers.TryGetValue(brokerageId, out var ticker) && !string.IsNullOrEmpty(ticker))
                {
                    var filtered = _apiClient.GetHistoricalOrdersAsync(1, ticker, cancellationToken).SynchronouslyAwaitTaskResult();
                    var match = filtered.FirstOrDefault(candidate => candidate?.Order != null
                        && GetBrokerageOrderId(candidate.Order.Id) == brokerageId);
                    if (match != null)
                    {
                        ApplyBrokerageOrder(match.Order, match.Fill);
                        continue;
                    }
                }

                // The order vanished: it may have filled. Report it rather than leaving LEAN unaware.
                var message = $"The final state of Trading 212 order {brokerageId} was not reported after {attempts} polls, " +
                    "and the order is no longer in the broker's pending or recent historical orders. The account may hold a position LEAN does not know about; check the account.";
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(PollOrders)}: {message}");
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "OrderStateUnknown", message));
                _trackedOrderIds.TryRemove(brokerageId, out _);
                _historyLookupAttempts.TryRemove(brokerageId, out _);
                _trackedOrderTickers.TryRemove(brokerageId, out _);
            }
        }

        /// <summary>
        /// Reports the state of a broker order to LEAN, emitting an event only when something changed.
        /// </summary>
        /// <param name="brokerageOrder">The broker order.</param>
        /// <param name="fill">The fill, when the order has executed.</param>
        internal void ApplyBrokerageOrder(Trading212Order brokerageOrder, Trading212Fill fill)
        {
            if (brokerageOrder == null)
            {
                return;
            }

            var brokerageId = GetBrokerageOrderId(brokerageOrder.Id);
            var leanStatus = brokerageOrder.Status.ToLeanOrderStatus();
            if (leanStatus == null)
            {
                Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(ApplyBrokerageOrder)}: Unrecognized Trading 212 order status '{brokerageOrder.Status}' for order {brokerageId}.");
                return;
            }

            var leanOrders = OrderProvider?.GetOrdersByBrokerageId(brokerageId);
            if (leanOrders == null || leanOrders.Count == 0)
            {
                // An order placed outside the algorithm, or one LEAN has not registered yet.
                return;
            }

            var cumulativeFilled = Math.Abs(brokerageOrder.FilledQuantity);
            if (cumulativeFilled == 0m && fill != null)
            {
                cumulativeFilled = Math.Abs(fill.Quantity);
            }

            var cumulativeFilledValue = Math.Abs(brokerageOrder.FilledValue ?? 0m);
            if (cumulativeFilledValue == 0m && fill != null && fill.Price > 0m)
            {
                cumulativeFilledValue = fill.Price * Math.Abs(fill.Quantity);
            }

            var direction = Math.Sign(brokerageOrder.Quantity);
            if (direction == 0)
            {
                direction = brokerageOrder.Side == Trading212OrderSide.Sell ? -1 : 1;
            }

            foreach (var leanOrder in leanOrders)
            {
                var previouslyReported = _reportedFillQuantityByLeanOrderId.GetOrAdd(leanOrder.Id, 0m);
                var previouslyReportedValue = _reportedFillValueByLeanOrderId.GetOrAdd(leanOrder.Id, 0m);
                var delta = cumulativeFilled - previouslyReported;
                if (delta < 0m)
                {
                    // The broker reported less than before, which should not happen; resynchronize.
                    delta = 0m;
                }

                var lastStatus = _lastOrderStatusByLeanOrderId.GetOrAdd(leanOrder.Id, OrderStatus.None);
                if (lastStatus == leanStatus.Value && delta == 0m)
                {
                    continue;
                }

                var fillPrice = delta > 0m
                    ? ResolveFillPrice(brokerageOrder, fill, cumulativeFilledValue - previouslyReportedValue, delta)
                    : fill?.Price ?? 0m;

                if (delta > 0m)
                {
                    _reportedFillQuantityByLeanOrderId[leanOrder.Id] = cumulativeFilled;
                    _reportedFillValueByLeanOrderId[leanOrder.Id] = cumulativeFilledValue;
                }

                var message = leanStatus.Value == OrderStatus.Invalid
                    ? "Trading 212 rejected the order."
                    : $"Trading 212 order {brokerageId} is {brokerageOrder.Status}.";

                var orderEvent = new OrderEvent(leanOrder, DateTime.UtcNow, GetOrderFee(leanStatus.Value, fill), message)
                {
                    Status = leanStatus.Value,
                    FillPrice = fillPrice,
                    FillQuantity = delta * direction,
                    Message = message
                };

                OnOrderEvent(orderEvent);
                _lastOrderStatusByLeanOrderId[leanOrder.Id] = leanStatus.Value;
            }

            if (!brokerageOrder.Status.IsWorking())
            {
                _trackedOrderIds.TryRemove(brokerageId, out _);
                _historyLookupAttempts.TryRemove(brokerageId, out _);
                _trackedOrderTickers.TryRemove(brokerageId, out _);
                foreach (var leanOrder in leanOrders)
                {
                    _reportedFillQuantityByLeanOrderId.TryRemove(leanOrder.Id, out _);
                    _reportedFillValueByLeanOrderId.TryRemove(leanOrder.Id, out _);
                }
            }
        }

        /// <summary>
        /// Resolves the price of an incremental fill.
        /// </summary>
        /// <remarks>
        /// A pending order carries no execution price, only a cumulative quantity and value, so the price of the
        /// shares filled between two polls is the change in value divided by the change in quantity. Reporting
        /// zero instead would book the shares at no cost and corrupt the average price, cash and P&amp;L.
        /// </remarks>
        /// <param name="brokerageOrder">The broker order.</param>
        /// <param name="fill">The fill, when the order is finished and the history endpoint reported one.</param>
        /// <param name="filledValueDelta">The change in the cumulative filled value.</param>
        /// <param name="quantityDelta">The change in the cumulative filled quantity.</param>
        /// <returns>The price of the incremental fill.</returns>
        private decimal ResolveFillPrice(Trading212Order brokerageOrder, Trading212Fill fill, decimal filledValueDelta, decimal quantityDelta)
        {
            if (fill?.Price > 0m)
            {
                return fill.Price;
            }

            if (filledValueDelta > 0m && quantityDelta > 0m)
            {
                return filledValueDelta / quantityDelta;
            }

            // Fall back to the order's own price rather than reporting a fill at zero.
            if (brokerageOrder.LimitPrice is > 0m)
            {
                return brokerageOrder.LimitPrice.Value;
            }

            if (brokerageOrder.StopPrice is > 0m)
            {
                return brokerageOrder.StopPrice.Value;
            }

            var message = $"The fill price of Trading 212 order {GetBrokerageOrderId(brokerageOrder.Id)} could not be determined; it is reported as zero.";
            Log.Error($"{nameof(Trading212Brokerage)}.{nameof(ResolveFillPrice)}: {message}");
            OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Error, "FillPriceUnknown", message));
            return 0m;
        }

        /// <summary>
        /// Returns the fee the broker charged on a fill.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trading 212 is commission free, but a trade is not free: the fill carries the costs that were
        /// actually charged in <c>fill.walletImpact.taxes</c>, for example the FX fee on the converted notional
        /// and the US regulatory fees. Those are reported as they are, instead of being modelled, so a live
        /// fill carries the real cost.
        /// </para>
        /// <para>
        /// The taxes are reported in wallet currency; <c>fill.walletImpact.fxRate</c> converts the instrument's
        /// currency into the account currency.
        /// </para>
        /// <para>
        /// The fee is attached once, on the fill that completes the order, which is the only status for which
        /// the history endpoint reports a fill.
        /// </para>
        /// </remarks>
        /// <param name="status">The LEAN status being reported.</param>
        /// <param name="fill">The fill reported by the broker, when there is one.</param>
        /// <returns>The order fee, or <see cref="OrderFee.Zero"/> when the broker charged nothing or the fill is not final.</returns>
        internal OrderFee GetOrderFee(OrderStatus status, Trading212Fill fill)
        {
            var walletImpact = fill?.WalletImpact;
            if (status != OrderStatus.Filled || walletImpact?.Taxes == null || walletImpact.Taxes.Count == 0)
            {
                return OrderFee.Zero;
            }

            var currency = string.IsNullOrEmpty(_accountCurrency) ? walletImpact.Currency : _accountCurrency;
            var amount = walletImpact.Taxes
                .Where(tax => tax != null)
                .Sum(tax => ToAccountCurrency(Math.Abs(tax.Quantity), tax.Currency, walletImpact));

            return amount <= 0m ? OrderFee.Zero : new OrderFee(new CashAmount(amount, currency));
        }

        /// <summary>
        /// Converts an amount charged in the instrument's currency into the account currency.
        /// </summary>
        /// <param name="amount">The amount to convert.</param>
        /// <param name="currency">The currency the amount is charged in.</param>
        /// <param name="walletImpact">The fill's wallet impact, carrying the FX rate.</param>
        /// <returns>The amount in the account currency.</returns>
        private decimal ToAccountCurrency(decimal amount, string currency, Trading212FillWalletImpact walletImpact)
        {
            if (amount == 0m || string.IsNullOrEmpty(_accountCurrency) || string.IsNullOrEmpty(currency)
                || string.Equals(currency, _accountCurrency, StringComparison.OrdinalIgnoreCase))
            {
                return amount;
            }

            if (walletImpact.FxRate > 0m && string.Equals(currency, walletImpact.Currency, StringComparison.OrdinalIgnoreCase))
            {
                return amount * walletImpact.FxRate;
            }

            // The wallet impact carries no rate for this currency, so report the amount as charged.
            return amount;
        }

        /// <summary>
        /// Formats a broker order id the way LEAN stores it in <see cref="Order.BrokerId"/>.
        /// </summary>
        /// <param name="orderId">The broker order id.</param>
        /// <returns>The broker id as a string.</returns>
        private static string GetBrokerageOrderId(long orderId)
        {
            return orderId.ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        /// <summary>
        /// Gets the history for the requested symbols.
        /// </summary>
        /// <remarks>
        /// Trading 212's public API serves no historical data, so this always returns <c>null</c>, which is
        /// what LEAN expects when a history request cannot be served.
        /// </remarks>
        /// <param name="request">The historical data request.</param>
        /// <returns>Always <c>null</c>.</returns>
        public override IEnumerable<BaseData> GetHistory(Data.HistoryRequest request)
        {
            if (!_historyNotSupportedWarningSent)
            {
                _historyNotSupportedWarningSent = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "HistoryNotSupported",
                    "Trading 212's public API does not provide historical data. Configure the LEAN launcher with " +
                    "SubscriptionDataReaderHistoryProvider to use another history source."));
            }

            return null;
        }

        /// <summary>
        /// Forwards a message raised by the API client to the brokerage's subscribers.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="brokerageMessageEvent">The message.</param>
        private void OnBrokerageMessageEventHandler(object sender, BrokerageMessageEvent brokerageMessageEvent)
        {
            OnMessage(brokerageMessageEvent);
        }

        /// <inheritdoc/>
        public override void Dispose()
        {
            _cancellationTokenSource.Cancel();
            _cancellationTokenSource.DisposeSafely();
            _orderUpdateEndEvent.DisposeSafely();
            _apiClient.DisposeSafely();
            base.Dispose();
        }

        /// <summary>
        /// The response of the <c>modules/license/read</c> endpoint used to validate a subscription.
        /// </summary>
        private class ModulesReadLicenseRead : RestResponse
        {
            /// <summary>
            /// The encrypted license payload.
            /// </summary>
            [JsonProperty(PropertyName = "license")]
            public string License;

            /// <summary>
            /// The organization the license belongs to, when the caller did not supply one.
            /// </summary>
            [JsonProperty(PropertyName = "organizationId")]
            public string OrganizationId;
        }

        /// <summary>
        /// Validates that the current user is allowed to use this module.
        /// </summary>
        /// <remarks>
        /// Mirrors the other brokerage plugins: the check runs unconditionally on construction and shuts the
        /// process down when the subscription cannot be validated.
        /// TODO: QuantConnect has not assigned a product id for this module yet, and the request cannot be
        /// made without one, so it is skipped while <see cref="productId"/> is zero. Set it to the assigned
        /// value to enable the check; the rest of the flow is complete.
        /// </remarks>
        private static void ValidateSubscription()
        {
            const int productId = 0;

            try
            {
                var userId = Globals.UserId;
                var token = Globals.UserToken;
                var organizationId = Globals.OrganizationID;

                if (productId == 0)
                {
                    Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(ValidateSubscription)}: No product id has been assigned to this module yet, so the subscription check is skipped.");
                    return;
                }

                // Verify we can authenticate with this user and token
                var api = new ApiConnection(userId, token);
                if (!api.Connected)
                {
                    throw new ArgumentException("Invalid api user id or token, cannot authenticate subscription.");
                }

                // Compile the information we want to send when validating
                var information = new Dictionary<string, object>
                {
                    { "productId", productId },
                    { "machineName", Environment.MachineName },
                    { "userName", Environment.UserName },
                    { "domainName", Environment.UserDomainName },
                    { "os", Environment.OSVersion }
                };

                // IP and MAC Address Information
                try
                {
                    var interfaceDictionary = new List<Dictionary<string, object>>();
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(nic => nic.OperationalStatus == OperationalStatus.Up))
                    {
                        var interfaceInformation = new Dictionary<string, object>();
                        // Get UnicastAddresses
                        var addresses = nic.GetIPProperties().UnicastAddresses
                            .Select(uniAddress => uniAddress.Address)
                            .Where(address => !IPAddress.IsLoopback(address))
                            .Select(x => x.ToString())
                            .ToList();

                        // If this interface has non-loopback addresses, we will include it
                        if (addresses.Count != 0)
                        {
                            interfaceInformation.Add("unicastAddresses", addresses);
                            // Get MAC address
                            interfaceInformation.Add("MAC", nic.GetPhysicalAddress().ToString());
                            // Add Interface name
                            interfaceInformation.Add("name", nic.Name);
                            // Add these to our dictionary
                            interfaceDictionary.Add(interfaceInformation);
                        }
                    }

                    information.Add("networkInterfaces", interfaceDictionary);
                }
                catch (Exception)
                {
                    // NOP, not necessary to crash if fails to extract and add this information
                }

                // Include our OrganizationId if specified
                if (!string.IsNullOrEmpty(organizationId))
                {
                    information.Add("organizationId", organizationId);
                }

                using var request = ApiUtils.CreateJsonPostRequest("modules/license/read", information);
                api.TryRequest(request, out ModulesReadLicenseRead result);
                if (!result.Success)
                {
                    throw new InvalidOperationException($"Request for subscriptions from web failed, Response Errors : {string.Join(',', result.Errors)}");
                }

                var encryptedData = result.License;

                // Decrypt the data we received
                DateTime? expirationDate = null;
                long? stamp = null;
                bool? isValid = null;
                if (encryptedData != null)
                {
                    // Fetch the org id from the response if we are null, we need it to generate our validation key
                    if (string.IsNullOrEmpty(organizationId))
                    {
                        organizationId = result.OrganizationId;
                    }

                    // Create our combination key
                    var password = $"{token}-{organizationId}";
                    var key = SHA256.HashData(Encoding.UTF8.GetBytes(password));

                    // Split the data
                    var info = encryptedData.Split("::");
                    var buffer = Convert.FromBase64String(info[0]);
                    var iv = Convert.FromBase64String(info[1]);

                    // Decrypt our information
                    using var aes = Aes.Create();
                    var decryptor = aes.CreateDecryptor(key, iv);
                    using var memoryStream = new MemoryStream(buffer);
                    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
                    using var streamReader = new StreamReader(cryptoStream);
                    var decryptedData = streamReader.ReadToEnd();

                    if (!string.IsNullOrEmpty(decryptedData))
                    {
                        var jsonInfo = JsonConvert.DeserializeObject<JObject>(decryptedData);
                        expirationDate = jsonInfo["expiration"]?.Value<DateTime>();
                        isValid = jsonInfo["isValid"]?.Value<bool>();
                        stamp = jsonInfo["stamped"]?.Value<int>();
                    }
                }

                // Validate our conditions
                if (!expirationDate.HasValue || !isValid.HasValue || !stamp.HasValue)
                {
                    throw new InvalidOperationException("Failed to validate subscription.");
                }

                var nowUtc = DateTime.UtcNow;
                var timeSpan = nowUtc - Time.UnixTimeStampToDateTime(stamp.Value);
                if (timeSpan > TimeSpan.FromHours(12))
                {
                    throw new InvalidOperationException("Invalid API response.");
                }

                if (!isValid.Value)
                {
                    throw new ArgumentException("Your subscription is not valid, please check your product subscriptions on our website.");
                }

                if (expirationDate < nowUtc)
                {
                    throw new ArgumentException($"Your subscription expired {expirationDate}, please renew in order to use this product.");
                }
            }
            catch (Exception exception)
            {
                Log.Error($"ValidateSubscription(): Failed during validation, shutting down. Error : {exception.Message}");
                Environment.Exit(1);
            }
        }
    }
}
