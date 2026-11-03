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

namespace QuantConnect.Brokerages.Trading212.Api
{
    /// <summary>
    /// The documented rate limits of the Trading 212 Public API endpoints this integration calls.
    /// </summary>
    /// <remarks>
    /// Each limit is published in the endpoint's description, for example "**Rate limit:** 1 req / 5s".
    /// See <see href="https://docs.trading212.com/api/section/rate-limiting"/>.
    /// Limits are applied per account, regardless of which API key or IP address is used.
    /// </remarks>
    public static class Trading212RateLimits
    {
        /// <summary>Rate limit key for <c>GET /api/v0/equity/account/summary</c>: 1 req / 5s.</summary>
        public const string AccountSummary = "account-summary";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/positions</c>: 1 req / 1s.</summary>
        public const string Positions = "positions";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/metadata/instruments</c>: 1 req / 50s.</summary>
        public const string Instruments = "instruments";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/metadata/exchanges</c>: 1 req / 30s.</summary>
        public const string Exchanges = "exchanges";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/orders</c>: 1 req / 5s.</summary>
        public const string PendingOrders = "orders";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/orders/{id}</c>: 1 req / 1s.</summary>
        public const string OrderById = "order-by-id";

        /// <summary>Rate limit key for <c>DELETE /api/v0/equity/orders/{id}</c>: 50 req / 1m.</summary>
        public const string CancelOrder = "cancel-order";

        /// <summary>Rate limit key for the order placement endpoints: 1 req / 2s.</summary>
        public const string PlaceOrder = "place-order";

        /// <summary>Rate limit key for <c>GET /api/v0/equity/history/orders</c>: 20 req / 1m.</summary>
        public const string HistoricalOrders = "history-orders";

        /// <summary>
        /// Returns the documented number of occurrences allowed per period for a rate limit key.
        /// </summary>
        /// <param name="rateLimitKey">One of the keys defined by this class.</param>
        /// <returns>The number of requests allowed in the period.</returns>
        public static int GetOccurrences(string rateLimitKey)
        {
            switch (rateLimitKey)
            {
                case AccountSummary:
                    return 1;
                case Positions:
                    return 1;
                case Instruments:
                    return 1;
                case Exchanges:
                    return 1;
                case PendingOrders:
                    return 1;
                case OrderById:
                    return 1;
                case CancelOrder:
                    return 50;
                case PlaceOrder:
                    return 1;
                case HistoricalOrders:
                    return 20;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rateLimitKey), rateLimitKey, "Unknown Trading 212 rate limit key.");
            }
        }

        /// <summary>
        /// Returns the documented period of a rate limit key.
        /// </summary>
        /// <param name="rateLimitKey">One of the keys defined by this class.</param>
        /// <returns>The length of the period.</returns>
        public static TimeSpan GetPeriod(string rateLimitKey)
        {
            switch (rateLimitKey)
            {
                case AccountSummary:
                    return TimeSpan.FromSeconds(5);
                case Positions:
                    return TimeSpan.FromSeconds(1);
                case Instruments:
                    return TimeSpan.FromSeconds(50);
                case Exchanges:
                    return TimeSpan.FromSeconds(30);
                case PendingOrders:
                    return TimeSpan.FromSeconds(5);
                case OrderById:
                    return TimeSpan.FromSeconds(1);
                case CancelOrder:
                    return TimeSpan.FromMinutes(1);
                case PlaceOrder:
                    return TimeSpan.FromSeconds(2);
                case HistoricalOrders:
                    return TimeSpan.FromMinutes(1);
                default:
                    throw new ArgumentOutOfRangeException(nameof(rateLimitKey), rateLimitKey, "Unknown Trading 212 rate limit key.");
            }
        }
    }
}
