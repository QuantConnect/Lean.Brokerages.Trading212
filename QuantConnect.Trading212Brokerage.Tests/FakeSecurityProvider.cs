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
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// A minimal <see cref="ISecurityProvider"/> backed by equities built on demand, so the brokerage can
    /// price an order fee the way LEAN does in a live algorithm.
    /// </summary>
    public sealed class FakeSecurityProvider : ISecurityProvider
    {
        private readonly Dictionary<Symbol, Security> _securities = new();

        /// <summary>
        /// Creates an equity for a symbol, quoted in the instrument's currency.
        /// </summary>
        /// <param name="symbol">The equity symbol.</param>
        /// <param name="quoteCurrency">The instrument's currency, for example <c>USD</c>.</param>
        /// <returns>The security that was created.</returns>
        public Security AddEquity(Symbol symbol, string quoteCurrency)
        {
            var hours = SecurityExchangeHours.AlwaysOpen(TimeZones.NewYork);
            var cash = new Cash(quoteCurrency, 1m, 1m);
            var properties = SymbolProperties.GetDefault(quoteCurrency);

            var equity = new Equity(
                symbol,
                hours,
                cash,
                properties,
                ErrorCurrencyConverter.Instance,
                new RegisteredSecurityDataTypesProvider(),
                new SecurityCache());

            _securities[symbol] = equity;
            return equity;
        }

        /// <inheritdoc/>
        public Security GetSecurity(Symbol symbol)
        {
            return _securities.TryGetValue(symbol, out var security) ? security : null;
        }
    }
}
