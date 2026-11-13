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
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Trading212.Models
{
    /// <summary>
    /// The response of <c>GET /api/v0/equity/account/summary</c>.
    /// </summary>
    /// <remarks>
    /// All monetary values are in the account's primary currency.
    /// </remarks>
    public class Trading212AccountSummary
    {
        /// <summary>
        /// The primary trading account number.
        /// </summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>
        /// The primary account currency in ISO 4217 format, for example <c>GBP</c>.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// The value of all investments in the account's primary currency.
        /// </summary>
        [JsonProperty("totalValue")]
        public decimal TotalValue { get; set; }

        /// <summary>
        /// The cash breakdown of the account.
        /// </summary>
        [JsonProperty("cash")]
        public Trading212Cash Cash { get; set; }

        /// <summary>
        /// The investment metrics of the account.
        /// </summary>
        [JsonProperty("investments")]
        public Trading212Investments Investments { get; set; }
    }

    /// <summary>
    /// The cash breakdown of a Trading 212 account.
    /// </summary>
    public class Trading212Cash
    {
        /// <summary>
        /// Funds available for investing, in the account's primary currency.
        /// </summary>
        [JsonProperty("availableToTrade")]
        public decimal AvailableToTrade { get; set; }

        /// <summary>
        /// The sum of the cash inside all pies that is not yet invested.
        /// </summary>
        [JsonProperty("inPies")]
        public decimal InPies { get; set; }

        /// <summary>
        /// The amount of cash reserved for pending orders. This cash is not available for new trades.
        /// </summary>
        [JsonProperty("reservedForOrders")]
        public decimal ReservedForOrders { get; set; }
    }

    /// <summary>
    /// The investment metrics of a Trading 212 account.
    /// </summary>
    public class Trading212Investments
    {
        /// <summary>
        /// The current value of all investments.
        /// </summary>
        [JsonProperty("currentValue")]
        public decimal CurrentValue { get; set; }

        /// <summary>
        /// The all-time realised profit and loss of all executed trades.
        /// </summary>
        [JsonProperty("realizedProfitLoss")]
        public decimal RealizedProfitLoss { get; set; }

        /// <summary>
        /// The cost basis of the currently held investments.
        /// </summary>
        [JsonProperty("totalCost")]
        public decimal TotalCost { get; set; }

        /// <summary>
        /// The unrealised profit and loss of the currently held investments.
        /// </summary>
        [JsonProperty("unrealizedProfitLoss")]
        public decimal UnrealizedProfitLoss { get; set; }
    }

    /// <summary>
    /// An open position, as returned by <c>GET /api/v0/equity/positions</c>.
    /// </summary>
    public class Trading212Position
    {
        /// <summary>
        /// The average price paid per share, in the instrument's currency.
        /// </summary>
        [JsonProperty("averagePricePaid")]
        public decimal AveragePricePaid { get; set; }

        /// <summary>
        /// When the position was opened.
        /// </summary>
        [JsonProperty("createdAt")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// The current price of a single share, in the instrument's currency.
        /// </summary>
        [JsonProperty("currentPrice")]
        public decimal CurrentPrice { get; set; }

        /// <summary>
        /// The instrument the position is held in.
        /// </summary>
        [JsonProperty("instrument")]
        public Trading212Instrument Instrument { get; set; }

        /// <summary>
        /// The total quantity of shares owned.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The quantity of shares available for trading, excluding shares reserved for pending orders.
        /// </summary>
        [JsonProperty("quantityAvailableForTrading")]
        public decimal QuantityAvailableForTrading { get; set; }

        /// <summary>
        /// The quantity of shares currently used in a pie.
        /// </summary>
        [JsonProperty("quantityInPies")]
        public decimal QuantityInPies { get; set; }

        /// <summary>
        /// The financial impact of the position on the account's wallet.
        /// </summary>
        [JsonProperty("walletImpact")]
        public Trading212PositionWalletImpact WalletImpact { get; set; }
    }

    /// <summary>
    /// The financial impact of a position on the account's wallet.
    /// </summary>
    public class Trading212PositionWalletImpact
    {
        /// <summary>
        /// The currency all the wallet impact values are expressed in.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// The current market value of the position.
        /// </summary>
        [JsonProperty("currentValue")]
        public decimal CurrentValue { get; set; }

        /// <summary>
        /// The impact on the position's value due to currency rate changes, when the instrument
        /// currency differs from the account currency.
        /// </summary>
        [JsonProperty("fxImpact")]
        public decimal? FxImpact { get; set; }

        /// <summary>
        /// The total amount paid for the position.
        /// </summary>
        [JsonProperty("totalCost")]
        public decimal TotalCost { get; set; }

        /// <summary>
        /// The unrealised profit and loss of the position.
        /// </summary>
        [JsonProperty("unrealizedProfitLoss")]
        public decimal UnrealizedProfitLoss { get; set; }
    }

    /// <summary>
    /// An instrument as embedded in positions and orders.
    /// </summary>
    public class Trading212Instrument
    {
        /// <summary>
        /// The instrument currency in ISO 4217 format.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// The ISIN of the instrument.
        /// </summary>
        [JsonProperty("isin")]
        public string Isin { get; set; }

        /// <summary>
        /// The name of the instrument.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }
    }

    /// <summary>
    /// A tradable instrument, as returned by <c>GET /api/v0/equity/metadata/instruments</c>.
    /// </summary>
    public class Trading212TradableInstrument
    {
        /// <summary>
        /// When the instrument was added to the platform.
        /// </summary>
        [JsonProperty("addedOn")]
        public DateTime? AddedOn { get; set; }

        /// <summary>
        /// The instrument currency in ISO 4217 format, for example <c>USD</c>.
        /// </summary>
        [JsonProperty("currencyCode")]
        public string CurrencyCode { get; set; }

        /// <summary>
        /// Whether the instrument is eligible for extended hours trading.
        /// </summary>
        [JsonProperty("extendedHours")]
        public bool ExtendedHours { get; set; }

        /// <summary>
        /// The ISIN of the instrument.
        /// </summary>
        [JsonProperty("isin")]
        public string Isin { get; set; }

        /// <summary>
        /// The maximum open quantity allowed for the instrument.
        /// </summary>
        [JsonProperty("maxOpenQuantity")]
        public decimal? MaxOpenQuantity { get; set; }

        /// <summary>
        /// The full name of the instrument.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The short name of the instrument.
        /// </summary>
        [JsonProperty("shortName")]
        public string ShortName { get; set; }

        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The type of the instrument.
        /// </summary>
        [JsonProperty("type")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212InstrumentType>))]
        public Trading212InstrumentType Type { get; set; }

        /// <summary>
        /// The identifier of the working schedule of the instrument's exchange, as returned by
        /// <c>GET /api/v0/equity/metadata/exchanges</c>.
        /// </summary>
        [JsonProperty("workingScheduleId")]
        public long? WorkingScheduleId { get; set; }
    }

    /// <summary>
    /// An exchange and its working schedules, as returned by <c>GET /api/v0/equity/metadata/exchanges</c>.
    /// </summary>
    public class Trading212Exchange
    {
        /// <summary>
        /// The exchange identifier.
        /// </summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>
        /// The name of the exchange.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The working schedules of the exchange.
        /// </summary>
        [JsonProperty("workingSchedules")]
        public List<Trading212WorkingSchedule> WorkingSchedules { get; set; }
    }

    /// <summary>
    /// A working schedule of an exchange.
    /// </summary>
    public class Trading212WorkingSchedule
    {
        /// <summary>
        /// The working schedule identifier.
        /// </summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>
        /// The open and close events of the schedule.
        /// </summary>
        [JsonProperty("timeEvents")]
        public List<Trading212TimeEvent> TimeEvents { get; set; }
    }

    /// <summary>
    /// A single open or close event of an exchange's working schedule.
    /// </summary>
    public class Trading212TimeEvent
    {
        /// <summary>
        /// The moment the event occurs.
        /// </summary>
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        /// <summary>
        /// The kind of event. Kept as a string because the API is in beta and may add event types.
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }
    }

    /// <summary>
    /// An order as returned by the Trading 212 Public API.
    /// </summary>
    public class Trading212Order
    {
        /// <summary>
        /// The unique, system-generated identifier of the order. This is the value LEAN stores as the
        /// order's brokerage id.
        /// </summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The type of the order.
        /// </summary>
        [JsonProperty("type")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212OrderType>))]
        public Trading212OrderType Type { get; set; }

        /// <summary>
        /// The current state of the order in its lifecycle.
        /// </summary>
        [JsonProperty("status")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212OrderStatus>))]
        public Trading212OrderStatus Status { get; set; }

        /// <summary>
        /// Whether the order is a buy or a sell.
        /// </summary>
        [JsonProperty("side")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212OrderSide>))]
        public Trading212OrderSide Side { get; set; }

        /// <summary>
        /// The total number of shares requested, signed: positive for a buy, negative for a sell.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The number of shares that have been executed so far.
        /// </summary>
        [JsonProperty("filledQuantity")]
        public decimal FilledQuantity { get; set; }

        /// <summary>
        /// The total monetary value of the executed portion, for orders placed by value.
        /// </summary>
        [JsonProperty("filledValue")]
        public decimal? FilledValue { get; set; }

        /// <summary>
        /// The total monetary value of the order, for orders placed by value.
        /// </summary>
        [JsonProperty("value")]
        public decimal? Value { get; set; }

        /// <summary>
        /// The limit price, for limit and stop-limit orders.
        /// </summary>
        [JsonProperty("limitPrice")]
        public decimal? LimitPrice { get; set; }

        /// <summary>
        /// The stop price, for stop and stop-limit orders.
        /// </summary>
        [JsonProperty("stopPrice")]
        public decimal? StopPrice { get; set; }

        /// <summary>
        /// When the order was created, as documented by the OpenAPI description (<c>createdAt</c>).
        /// </summary>
        [JsonProperty("createdAt")]
        public DateTime? CreatedAt { get; set; }

        /// <summary>
        /// When the order was created, as observed in a live response (<c>creationTime</c>).
        /// </summary>
        /// <remarks>
        /// The published OpenAPI description names this field <c>createdAt</c> while a response
        /// captured in the Trading 212 community forum names it <c>creationTime</c>. Both are read and
        /// <see cref="CreatedAtUtc"/> prefers <c>createdAt</c>.
        /// </remarks>
        [JsonProperty("creationTime")]
        public DateTime? CreationTime { get; set; }

        /// <summary>
        /// When the order was created, preferring the documented <c>createdAt</c> field.
        /// </summary>
        [JsonIgnore]
        public DateTime? CreatedAtUtc => CreatedAt ?? CreationTime;

        /// <summary>
        /// The currency used for the order in ISO 4217 format.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// Whether the order is eligible for execution outside regular trading hours.
        /// </summary>
        [JsonProperty("extendedHours")]
        public bool ExtendedHours { get; set; }

        /// <summary>
        /// How the order was initiated.
        /// </summary>
        [JsonProperty("initiatedFrom")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212InitiatedFrom>))]
        public Trading212InitiatedFrom InitiatedFrom { get; set; }

        /// <summary>
        /// Whether the order was sized by quantity or by value.
        /// </summary>
        [JsonProperty("strategy")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212OrderStrategy>))]
        public Trading212OrderStrategy Strategy { get; set; }

        /// <summary>
        /// How long the order remains active.
        /// </summary>
        [JsonProperty("timeInForce")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212TimeValidity>))]
        public Trading212TimeValidity TimeInForce { get; set; }

        /// <summary>
        /// The instrument the order is for.
        /// </summary>
        [JsonProperty("instrument")]
        public Trading212Instrument Instrument { get; set; }
    }

    /// <summary>
    /// A historical order together with its fill, as returned by <c>GET /api/v0/equity/history/orders</c>.
    /// </summary>
    public class Trading212HistoricalOrder
    {
        /// <summary>
        /// The order the fill belongs to.
        /// </summary>
        [JsonProperty("order")]
        public Trading212Order Order { get; set; }

        /// <summary>
        /// The execution of the order. Absent while the order has not executed.
        /// </summary>
        [JsonProperty("fill")]
        public Trading212Fill Fill { get; set; }
    }

    /// <summary>
    /// The execution of an order.
    /// </summary>
    public class Trading212Fill
    {
        /// <summary>
        /// The unique identifier of the fill.
        /// </summary>
        [JsonProperty("id")]
        public long Id { get; set; }

        /// <summary>
        /// When the fill occurred.
        /// </summary>
        [JsonProperty("filledAt")]
        public DateTime? FilledAt { get; set; }

        /// <summary>
        /// The execution price, in the instrument's currency.
        /// </summary>
        [JsonProperty("price")]
        public decimal Price { get; set; }

        /// <summary>
        /// The executed quantity.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// How the fill was executed. Kept as a string because the API is in beta.
        /// </summary>
        [JsonProperty("tradingMethod")]
        public string TradingMethod { get; set; }

        /// <summary>
        /// The kind of fill. Kept as a string because the API returns many corporate-action values.
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>
        /// The financial impact of the fill on the account's wallet.
        /// </summary>
        [JsonProperty("walletImpact")]
        public Trading212FillWalletImpact WalletImpact { get; set; }
    }

    /// <summary>
    /// The financial impact of a fill on the account's wallet.
    /// </summary>
    public class Trading212FillWalletImpact
    {
        /// <summary>
        /// The currency of the wallet impact values.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// The foreign exchange rate applied to the fill.
        /// </summary>
        [JsonProperty("fxRate")]
        public decimal FxRate { get; set; }

        /// <summary>
        /// The net value of the fill.
        /// </summary>
        [JsonProperty("netValue")]
        public decimal NetValue { get; set; }

        /// <summary>
        /// The realised profit and loss of the fill.
        /// </summary>
        [JsonProperty("realisedProfitLoss")]
        public decimal RealizedProfitLoss { get; set; }

        /// <summary>
        /// The taxes applied to the fill.
        /// </summary>
        [JsonProperty("taxes")]
        public List<Trading212Tax> Taxes { get; set; }
    }

    /// <summary>
    /// A tax applied to a fill.
    /// </summary>
    public class Trading212Tax
    {
        /// <summary>
        /// The name of the tax.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// The quantity of the tax.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The currency the tax is charged in.
        /// </summary>
        [JsonProperty("currency")]
        public string Currency { get; set; }

        /// <summary>
        /// When the tax was charged.
        /// </summary>
        [JsonProperty("chargedAt")]
        public DateTime? ChargedAt { get; set; }
    }

    /// <summary>
    /// The <c>context</c> object some Trading 212 error envelopes carry.
    /// </summary>
    public class Trading212ErrorContext
    {
        /// <summary>
        /// The error category, for example <c>TooManyRequests</c>.
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }
    }

    /// <summary>
    /// A page of results from a cursor-paginated Trading 212 endpoint.
    /// </summary>
    /// <remarks>
    /// Only the <c>history</c> endpoints return this envelope. The other list endpoints
    /// (<c>positions</c>, <c>orders</c>, <c>metadata/instruments</c>, <c>metadata/exchanges</c>, <c>pies</c>)
    /// return a bare JSON array, despite what the published schema notation suggests; the API client accepts
    /// both shapes.
    /// </remarks>
    /// <typeparam name="T">The type of the items in the page.</typeparam>
    public class Trading212PaginatedResponse<T>
    {
        /// <summary>
        /// The items in this page.
        /// </summary>
        [JsonProperty("items")]
        public List<T> Items { get; set; }

        /// <summary>
        /// The path of the next page, or <c>null</c> when this is the last page.
        /// </summary>
        [JsonProperty("nextPagePath")]
        public string NextPagePath { get; set; }
    }

    /// <summary>
    /// The body of <c>POST /api/v0/equity/orders/market</c>.
    /// </summary>
    public class Trading212MarketOrderRequest
    {
        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The number of shares, signed: positive for a buy, negative for a sell.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Whether the order may execute outside the standard trading session.
        /// </summary>
        [JsonProperty("extendedHours")]
        public bool ExtendedHours { get; set; }
    }

    /// <summary>
    /// The body of <c>POST /api/v0/equity/orders/limit</c>.
    /// </summary>
    public class Trading212LimitOrderRequest
    {
        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The number of shares, signed: positive for a buy, negative for a sell.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The limit price.
        /// </summary>
        [JsonProperty("limitPrice")]
        public decimal LimitPrice { get; set; }

        /// <summary>
        /// How long the order remains active.
        /// </summary>
        [JsonProperty("timeValidity")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212TimeValidity>))]
        public Trading212TimeValidity TimeValidity { get; set; }
    }

    /// <summary>
    /// The body of <c>POST /api/v0/equity/orders/stop</c>.
    /// </summary>
    public class Trading212StopOrderRequest
    {
        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The number of shares, signed: positive for a buy, negative for a sell.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The stop price.
        /// </summary>
        [JsonProperty("stopPrice")]
        public decimal StopPrice { get; set; }

        /// <summary>
        /// How long the order remains active.
        /// </summary>
        [JsonProperty("timeValidity")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212TimeValidity>))]
        public Trading212TimeValidity TimeValidity { get; set; }
    }

    /// <summary>
    /// The body of <c>POST /api/v0/equity/orders/stop_limit</c>.
    /// </summary>
    public class Trading212StopLimitOrderRequest
    {
        /// <summary>
        /// The unique instrument identifier, for example <c>AAPL_US_EQ</c>.
        /// </summary>
        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        /// <summary>
        /// The number of shares, signed: positive for a buy, negative for a sell.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// The stop price.
        /// </summary>
        [JsonProperty("stopPrice")]
        public decimal StopPrice { get; set; }

        /// <summary>
        /// The limit price.
        /// </summary>
        [JsonProperty("limitPrice")]
        public decimal LimitPrice { get; set; }

        /// <summary>
        /// How long the order remains active.
        /// </summary>
        [JsonProperty("timeValidity")]
        [JsonConverter(typeof(Trading212EnumConverter<Trading212TimeValidity>))]
        public Trading212TimeValidity TimeValidity { get; set; }
    }
}
