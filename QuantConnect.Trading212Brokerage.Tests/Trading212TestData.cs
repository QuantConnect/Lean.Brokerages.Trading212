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
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// JSON fixtures shaped like the responses documented in the Trading 212 Public API OpenAPI description.
    /// </summary>
    public static class Trading212TestData
    {
        /// <summary>
        /// An account summary in GBP.
        /// </summary>
        public const string AccountSummary = """
        {
          "id": 12345678,
          "currency": "GBP",
          "totalValue": 1000.0,
          "cash": { "availableToTrade": 500.0, "inPies": 0.0, "reservedForOrders": 50.0 },
          "investments": { "currentValue": 500.0, "realizedProfitLoss": 0.0, "totalCost": 450.0, "unrealizedProfitLoss": 50.0 }
        }
        """;

        /// <summary>
        /// An instruments list containing a US share, a US ETF, a US class share and a non-US listing.
        /// </summary>
        public const string Instruments = """
        [
            { "ticker": "AAPL_US_EQ", "type": "STOCK", "currencyCode": "USD", "name": "Apple Inc", "shortName": "Apple", "isin": "US0378331005", "extendedHours": true, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "VUSA_US_ETF", "type": "ETF", "currencyCode": "USD", "name": "Vanguard S&P 500", "shortName": "VUSA", "isin": "IE00B3XXRP09", "extendedHours": true, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "BRK_B_US_EQ", "type": "STOCK", "currencyCode": "USD", "name": "Berkshire Hathaway Inc Class B", "shortName": "Berkshire B", "isin": "US0846707026", "extendedHours": true, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "VOD_GB_EQ", "type": "STOCK", "currencyCode": "GBP", "name": "Vodafone Group", "shortName": "Vodafone", "isin": "GB00BH4HKS39", "extendedHours": false, "maxOpenQuantity": 100000, "workingScheduleId": 1, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "BRK/A_US_EQ", "type": "STOCK", "currencyCode": "USD", "name": "Berkshire Hathaway Inc Class A", "shortName": "Berkshire A", "isin": "US0846701086", "extendedHours": true, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "DSX_WAR_US_EQ", "type": "STOCK", "currencyCode": "USD", "name": "Diana Shipping warrant", "shortName": "DSX WAR", "isin": "US2527421179", "extendedHours": false, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" },
            { "ticker": "AVAV__US_EQ", "type": "STOCK", "currencyCode": "USD", "name": "AeroVironment", "shortName": "AVAV", "isin": "US0080731088", "extendedHours": true, "maxOpenQuantity": 100000, "workingScheduleId": 4, "addedOn": "2020-01-01T00:00:00Z" }
        ]
        """;

        /// <summary>
        /// A single pending market order.
        /// </summary>
        public const string PendingMarketOrder = """
        [
            {
              "id": 987654321,
              "ticker": "AAPL_US_EQ",
              "type": "MARKET",
              "status": "NEW",
              "side": "BUY",
              "quantity": 10.0,
              "filledQuantity": 0.0,
              "currency": "USD",
              "extendedHours": false,
              "initiatedFrom": "API",
              "strategy": "QUANTITY",
              "timeInForce": "DAY",
              "createdAt": "2025-01-02T15:00:00Z",
              "instrument": { "ticker": "AAPL_US_EQ", "currency": "USD", "name": "Apple Inc", "isin": "US0378331005" }
            }
        ]
        """;

        /// <summary>
        /// The account summary endpoint's route.
        /// </summary>
        public const string AccountSummaryPath = "/api/v0/equity/account/summary";

        /// <summary>
        /// The instruments endpoint's route.
        /// </summary>
        public const string InstrumentsPath = "/api/v0/equity/metadata/instruments";

        /// <summary>
        /// The pending orders endpoint's route.
        /// </summary>
        public const string OrdersPath = "/api/v0/equity/orders";

        /// <summary>
        /// The historical orders endpoint's route prefix.
        /// </summary>
        public const string HistoryOrdersPath = "/api/v0/equity/history/orders";

        /// <summary>
        /// Builds an instruments response from raw instruments.
        /// </summary>
        /// <param name="instruments">The instruments to serialize.</param>
        /// <returns>The JSON body.</returns>
        public static string InstrumentsResponse(IEnumerable<Trading212TradableInstrument> instruments)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(instruments);
        }

        /// <summary>
        /// An empty list response, which the API sends as a bare array.
        /// </summary>
        public const string EmptyArray = "[]";

        /// <summary>
        /// An empty paginated response, which the history endpoints send as an object.
        /// </summary>
        public const string EmptyPage = """{ "items": [], "nextPagePath": null }""";

        /// <summary>
        /// The error envelope of a rate limited request, observed against the live API.
        /// </summary>
        public const string TooManyRequestsError = """
        {"code":"BusinessException","context":{"type":"TooManyRequests"},"errorMessage":"too many requests"}
        """;
    }
}
