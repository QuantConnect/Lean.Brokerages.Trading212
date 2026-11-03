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
using System.Globalization;
using System.IO;
using System.Linq;
using QuantConnect.Logging;
using QuantConnect.Configuration;
using QuantConnect.Util;
using QuantConnect.Brokerages.Trading212;
using QuantConnect.Brokerages.Trading212.Api;

namespace QuantConnect.Brokerages.Trading212.ToolBox
{
    /// <summary>
    /// Writes the mapping between the instruments the Trading 212 account can trade and their LEAN symbols.
    /// </summary>
    /// <remarks>
    /// Trading 212's public API serves no market data, so there is nothing to download and no exchange info to
    /// generate. What the API does provide is the instrument list, which is the only authoritative source of
    /// the ticker mapping, so this tool dumps it to make a mapping problem easy to diagnose.
    /// </remarks>
    public class Trading212InstrumentListDownloader
    {
        /// <summary>
        /// The header written before the instrument rows.
        /// </summary>
        private const string Header = "brokerage-ticker,lean-ticker,supported,instrument-type,currency,name,isin";

        /// <summary>
        /// Downloads the instrument list using the configured credentials and writes it as CSV.
        /// </summary>
        /// <param name="outputPath">The path of the CSV file to write.</param>
        public void Run(string outputPath)
        {
            var apiKey = Config.Get("trading212-api-key");
            var apiSecret = Config.Get("trading212-api-secret");
            var environment = Trading212Brokerage.ParseEnvironment(Config.Get("trading212-environment"));

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(apiSecret))
            {
                throw new ArgumentException("The 'trading212-api-key' and 'trading212-api-secret' settings are required.");
            }

            using var client = new Trading212ApiClient(apiKey, apiSecret, environment, messageReceived: null);
            Run(outputPath, client);
        }

        /// <summary>
        /// Writes the instrument list of an already constructed client as CSV. Intended for unit testing.
        /// </summary>
        /// <param name="outputPath">The path of the CSV file to write.</param>
        /// <param name="client">The API client to read the instruments from.</param>
        public void Run(string outputPath, Trading212ApiClient client)
        {
            var instruments = client.GetInstrumentsAsync().SynchronouslyAwaitTaskResult();

            var symbolMapper = new Trading212SymbolMapper();
            var mapped = symbolMapper.SetInstruments(instruments);
            Log.Trace($"{nameof(Trading212InstrumentListDownloader)}: {mapped} of {instruments.Count} instruments are supported.");

            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputPath);
            writer.WriteLine(Header);

            foreach (var instrument in instruments.OrderBy(instrument => instrument.Ticker, StringComparer.Ordinal))
            {
                var supported = symbolMapper.TryGetLeanSymbol(instrument.Ticker, out var symbol);
                var row = string.Join(",",
                    Escape(instrument.Ticker),
                    Escape(symbol?.Value ?? string.Empty),
                    supported ? "true" : "false",
                    Escape(instrument.Type.ToStringInvariant()),
                    Escape(instrument.CurrencyCode),
                    Escape(instrument.Name),
                    Escape(instrument.Isin));
                writer.WriteLine(row);
            }
        }

        /// <summary>
        /// Quotes a CSV field when it contains a separator, a quote or a newline.
        /// </summary>
        /// <param name="value">The raw field.</param>
        /// <returns>The field, quoted when necessary.</returns>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
