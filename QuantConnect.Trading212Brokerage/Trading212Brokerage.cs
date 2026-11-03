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
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Packets;
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
    /// Trading 212's Public API is order and account only: it serves no market data and no history.
    /// <see cref="GetHistory"/> therefore returns <c>null</c>, <c>CanSubscribe</c> is always <c>false</c>,
    /// and the LEAN launcher must be configured with
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
    public class Trading212Brokerage : Brokerage, IDataQueueHandler, IDataQueueUniverseProvider
    {
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
        /// Consolidates subscribed ticks. Trading 212 serves no data, so it is only kept for the
        /// <see cref="IDataQueueHandler"/> contract.
        /// </summary>
        private IDataAggregator _aggregator;

        /// <summary>
        /// The subscription manager backing <see cref="IDataQueueHandler"/>.
        /// </summary>
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;

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
        /// Ensures the "no market data" warning is only logged once.
        /// </summary>
        private bool _marketDataNotSupportedWarningSent;

        /// <summary>
        /// Guards against running the initialization twice.
        /// </summary>
        private bool _isInitialized;

        /// <summary>
        /// The order provider, used to resolve the LEAN orders a broker order belongs to.
        /// </summary>
        protected IOrderProvider OrderProvider { get; private set; }

        /// <summary>
        /// The security provider.
        /// </summary>
        protected ISecurityProvider SecurityProvider { get; private set; }

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
        /// Parameterless constructor, required for brokerages implementing <see cref="IDataQueueHandler"/>.
        /// </summary>
        /// <remarks>
        /// Reads the credentials from the LEAN configuration. Trading 212 serves no market data, so a
        /// brokerage created this way is only usable for its brokerage, not as a data source.
        /// </remarks>
        public Trading212Brokerage()
            : this(Config.Get("trading212-api-key"), Config.Get("trading212-api-secret"),
                ParseEnvironment(Config.Get("trading212-environment")),
                orderProvider: null, securityProvider: null,
                aggregator: Composer.Instance.GetPart<IDataAggregator>())
        {
        }

        /// <summary>
        /// Creates a new instance.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="orderProvider">The order provider.</param>
        /// <param name="securityProvider">The security provider.</param>
        /// <param name="aggregator">The data aggregator, only needed for the <see cref="IDataQueueHandler"/> contract.</param>
        public Trading212Brokerage(string apiKey, string apiSecret, Trading212Environment environment,
            IOrderProvider orderProvider, ISecurityProvider securityProvider, IDataAggregator aggregator = null)
            : base("Trading212")
        {
            Initialize(apiKey, apiSecret, environment, orderProvider, securityProvider, aggregator);
        }

        /// <summary>
        /// Creates a new instance, taking the order and security providers from the algorithm.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="algorithm">The algorithm instance.</param>
        public Trading212Brokerage(string apiKey, string apiSecret, Trading212Environment environment, IAlgorithm algorithm)
            : this(apiKey, apiSecret, environment, algorithm?.Portfolio?.Transactions, algorithm?.Portfolio, Composer.Instance.GetPart<IDataAggregator>())
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
        /// <param name="securityProvider">The security provider used to price order fees.</param>
        internal Trading212Brokerage(Trading212ApiClient apiClient, IOrderProvider orderProvider = null, ISecurityProvider securityProvider = null)
            : base("Trading212")
        {
            _isInitialized = true;
            _apiClient = apiClient;
            _symbolMapper = new Trading212SymbolMapper();
            OrderProvider = orderProvider;
            SecurityProvider = securityProvider;
            _orderUpdateInterval = TimeSpan.FromSeconds(5);
            _orderHistoryPages = 1;
            _aggregator = Composer.Instance.GetPart<IDataAggregator>();
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager
            {
                SubscribeImpl = (symbols, _) => Subscribe(symbols),
                UnsubscribeImpl = (symbols, _) => Unsubscribe(symbols)
            };
        }

        /// <summary>
        /// Initializes the brokerage. Runs at most once per instance.
        /// </summary>
        /// <param name="apiKey">The Trading 212 API key.</param>
        /// <param name="apiSecret">The Trading 212 API secret.</param>
        /// <param name="environment">The environment to connect to.</param>
        /// <param name="orderProvider">The order provider.</param>
        /// <param name="securityProvider">The security provider.</param>
        /// <param name="aggregator">The data aggregator.</param>
        protected void Initialize(string apiKey, string apiSecret, Trading212Environment environment,
            IOrderProvider orderProvider, ISecurityProvider securityProvider, IDataAggregator aggregator)
        {
            if (_isInitialized)
            {
                return;
            }

            _isInitialized = true;
            OrderProvider = orderProvider;
            SecurityProvider = securityProvider;
            _symbolMapper = new Trading212SymbolMapper();

            _orderUpdateInterval = TimeSpan.FromSeconds(Config.GetInt("trading212-order-update-interval-seconds", 10));
            _orderHistoryPages = Math.Max(1, Config.GetInt("trading212-order-history-pages", DefaultOrderHistoryPages));

            _aggregator = aggregator ?? Composer.Instance.GetPart<IDataAggregator>();
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager
            {
                SubscribeImpl = (symbols, _) => Subscribe(symbols),
                UnsubscribeImpl = (symbols, _) => Unsubscribe(symbols)
            };

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
        /// Parses the configured environment name, defaulting to live.
        /// </summary>
        /// <param name="value">The configured value, either <c>live</c> or <c>demo</c>.</param>
        /// <returns>The parsed environment.</returns>
        public static Trading212Environment ParseEnvironment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Trading212Environment.Live;
            }

            if (Enum.TryParse<Trading212Environment>(value, ignoreCase: true, out var environment))
            {
                return environment;
            }

            // Accept the words the Trading 212 documentation uses for the two environments.
            switch (value.Trim().ToLowerInvariant())
            {
                case "paper":
                case "paper-trading":
                case "paper trading":
                    return Trading212Environment.Demo;
                default:
                    return Trading212Environment.Live;
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
                _trackedOrderIds[GetBrokerageOrderId(pending.Id)] = 0;
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

            try
            {
                var placed = PlaceBrokerageOrder(order, ticker, timeValidity);
                if (placed == null || placed.Id == 0)
                {
                    throw new InvalidOperationException("Trading 212 accepted the request but returned no order id.");
                }

                var brokerageId = GetBrokerageOrderId(placed.Id);
                order.BrokerId.Add(brokerageId);
                _trackedOrderIds[brokerageId] = 0;
                _lastOrderStatusByLeanOrderId[order.Id] = OrderStatus.Submitted;

                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "Trading 212 order submitted.")
                {
                    Status = OrderStatus.Submitted
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
                pendingById.Add(GetBrokerageOrderId(pending.Id));
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
                if (attempts == MaxHistoryLookupAttempts)
                {
                    Log.Error($"{nameof(Trading212Brokerage)}.{nameof(PollOrders)}: Gave up looking for Trading 212 order {brokerageId} in the historical orders after {attempts} polls. Its final state was not reported.");
                    _trackedOrderIds.TryRemove(brokerageId, out _);
                    _historyLookupAttempts.TryRemove(brokerageId, out _);
                }
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

            var direction = Math.Sign(brokerageOrder.Quantity);
            if (direction == 0)
            {
                direction = brokerageOrder.Side == Trading212OrderSide.Sell ? -1 : 1;
            }

            var fillPrice = fill?.Price ?? 0m;

            foreach (var leanOrder in leanOrders)
            {
                var previouslyReported = _reportedFillQuantityByLeanOrderId.GetOrAdd(leanOrder.Id, 0m);
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

                if (delta > 0m)
                {
                    _reportedFillQuantityByLeanOrderId[leanOrder.Id] = cumulativeFilled;
                }

                var message = leanStatus.Value == OrderStatus.Invalid
                    ? "Trading 212 rejected the order."
                    : $"Trading 212 order {brokerageId} is {brokerageOrder.Status}.";

                var orderEvent = new OrderEvent(leanOrder, DateTime.UtcNow, GetOrderFee(leanOrder, leanStatus.Value, fillPrice), message)
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
                foreach (var leanOrder in leanOrders)
                {
                    _reportedFillQuantityByLeanOrderId.TryRemove(leanOrder.Id, out _);
                }
            }
        }

        /// <summary>
        /// Computes the fee of an order the way the brokerage's own fee model does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trading 212 is commission free; the costs are the FX fee on the converted notional and the US
        /// regulatory fees, which <see cref="Trading212FeeModel"/> models for backtesting. Reusing that model
        /// here keeps a live order's reported cost consistent with a backtest of the same order.
        /// </para>
        /// <para>
        /// The model is constructed with the account currency this brokerage discovered from the account
        /// summary, which the model cannot discover on its own.
        /// </para>
        /// <para>
        /// The fee is attached once, on the fill that completes the order, because the model prices the whole
        /// order quantity rather than a single execution. A partially filled order that is then cancelled
        /// therefore reports no fee.
        /// </para>
        /// </remarks>
        /// <param name="leanOrder">The LEAN order that changed state.</param>
        /// <param name="status">The LEAN status being reported.</param>
        /// <param name="fillPrice">The price the order filled at, or 0 when it has not filled.</param>
        /// <returns>The order fee, or <see cref="OrderFee.Zero"/> when it cannot be computed.</returns>
        internal OrderFee GetOrderFee(Order leanOrder, OrderStatus status, decimal fillPrice)
        {
            if (status != OrderStatus.Filled || string.IsNullOrEmpty(_accountCurrency))
            {
                return OrderFee.Zero;
            }

            var security = SecurityProvider?.GetSecurity(leanOrder.Symbol);
            if (security == null)
            {
                return OrderFee.Zero;
            }

            try
            {
                // The fee model values a market order from the security's price, not the order's. Seed the
                // security with the traded price when it has none yet, which happens when no data feed is
                // driving it, so the fee is priced on the real notional instead of collapsing to zero.
                if (security.Price <= 0m && fillPrice > 0m)
                {
                    // Note: Tick's three-argument (time, symbol, value) constructor is the open interest
                    // overload, not a trade, so the fields are set explicitly.
                    security.SetMarketPrice(new Tick
                    {
                        Symbol = security.Symbol,
                        Time = DateTime.UtcNow,
                        Value = fillPrice,
                        TickType = TickType.Trade
                    });
                }

                return new Trading212FeeModel(_accountCurrency).GetOrderFee(new OrderFeeParameters(security, leanOrder));
            }
            catch (Exception exception)
            {
                // A fee model failure must not lose the fill event.
                Log.Error($"{nameof(Trading212Brokerage)}.{nameof(GetOrderFee)}: Could not compute the fee of order {leanOrder.Id}: {exception.Message}");
                return OrderFee.Zero;
            }
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

        #region IDataQueueHandler

        /// <summary>
        /// Subscribe to the specified configuration.
        /// </summary>
        /// <remarks>
        /// Trading 212's public API serves no market data, so this always returns <c>null</c>. The LEAN
        /// launcher must be configured with <c>LiveDataQueue</c> instead.
        /// </remarks>
        /// <param name="dataConfig">The subscription configuration.</param>
        /// <param name="newDataAvailableHandler">The handler fired when new data is available.</param>
        /// <returns>Always <c>null</c>.</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol))
            {
                return null;
            }

            var enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);

            return enumerator;
        }

        /// <summary>
        /// Removes the specified configuration.
        /// </summary>
        /// <param name="dataConfig">The subscription configuration to remove.</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator?.Remove(dataConfig);
        }

        /// <summary>
        /// Sets the job the brokerage is subscribing for.
        /// </summary>
        /// <remarks>
        /// Trading 212 serves no market data, so there is nothing to subscribe to.
        /// </remarks>
        /// <param name="job">The job packet.</param>
        public void SetJob(LiveNodePacket job)
        {
            Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(SetJob)}: Trading 212 serves no market data, so there is no data subscription to set up.");
        }

        /// <summary>
        /// Returns whether a symbol can be subscribed to.
        /// </summary>
        /// <remarks>
        /// Trading 212's public API serves neither market data nor history, so no symbol can be subscribed to.
        /// </remarks>
        /// <param name="symbol">The symbol to check.</param>
        /// <returns>Always <c>false</c>.</returns>
        private bool CanSubscribe(Symbol symbol)
        {
            if (!_marketDataNotSupportedWarningSent)
            {
                _marketDataNotSupportedWarningSent = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "MarketDataNotSupported",
                    "Trading 212's public API does not provide market data. Configure the LEAN launcher with " +
                    "QuantConnect.Lean.Engine.DataFeeds.Queues.LiveDataQueue to use another data source."));
            }

            return false;
        }

        /// <summary>
        /// Adds the specified symbols to the subscription.
        /// </summary>
        /// <param name="symbols">The symbols to add.</param>
        /// <returns>Always <c>false</c>, because no data is available.</returns>
        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            return false;
        }

        /// <summary>
        /// Removes the specified symbols from the subscription.
        /// </summary>
        /// <param name="symbols">The symbols to remove.</param>
        /// <returns>Always <c>false</c>, because no data is available.</returns>
        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            return false;
        }

        #endregion

        #region IDataQueueUniverseProvider

        /// <summary>
        /// Returns the symbols available at the data source.
        /// </summary>
        /// <remarks>Trading 212 serves no data, so no symbols are available.</remarks>
        /// <param name="symbol">The symbol to look up.</param>
        /// <param name="includeExpired">Whether expired contracts are included.</param>
        /// <param name="securityCurrency">The expected security currency, if any.</param>
        /// <returns>An empty sequence.</returns>
        public IEnumerable<Symbol> LookupSymbols(Symbol symbol, bool includeExpired, string securityCurrency = null)
        {
            return Enumerable.Empty<Symbol>();
        }

        /// <summary>
        /// Returns whether selection can take place.
        /// </summary>
        /// <returns>Always <c>false</c>, because no data is available.</returns>
        public bool CanPerformSelection()
        {
            return false;
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
        /// <para>
        /// Only runs when the process carries a QuantConnect user token, which the QuantConnect cloud sets
        /// and a local run does not. Without that guard a local or paper-account run would be terminated by
        /// the failure path below.
        /// </para>
        /// <para>
        /// TODO: QuantConnect has not assigned a product id for this module yet, so validation is skipped while
        /// <see cref="ProductId"/> is unset. Set it to the assigned value to enable the check; the rest of the
        /// flow is complete and matches the other brokerage plugins.
        /// </para>
        /// </remarks>
        private static void ValidateSubscription()
        {
            const int ProductId = 0;

            try
            {
                var userId = Globals.UserId;
                var token = Globals.UserToken;
                var organizationId = Globals.OrganizationID;

                if (string.IsNullOrEmpty(token))
                {
                    // Local run: there is no QuantConnect cloud session to validate.
                    return;
                }

                if (ProductId == 0)
                {
                    Log.Trace($"{nameof(Trading212Brokerage)}.{nameof(ValidateSubscription)}: Skipped, no product id has been assigned to this module yet.");
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
                    { "productId", ProductId },
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
