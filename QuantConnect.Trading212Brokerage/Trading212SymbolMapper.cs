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
using System.Text;
using QuantConnect.Logging;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212
{
    /// <summary>
    /// Provides the mapping between LEAN symbols and Trading 212 instrument tickers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Trading 212 identifies an instrument with a composite ticker of the form
    /// <c>&lt;TICKER&gt;_&lt;COUNTRY&gt;_&lt;INSTRUMENT_TYPE&gt;</c>, for example <c>AAPL_US_EQ</c> for a
    /// US-listed share. The parts are separated from the right, because the ticker itself may contain
    /// underscores for class shares: Berkshire Hathaway's class B shares are <c>BRK_B_US_EQ</c>.
    /// </para>
    /// <para>
    /// The mapper is built from <c>GET /api/v0/equity/metadata/instruments</c>, which is the only
    /// authoritative list of what the account can trade, so it does not guess at tickers. The LEAN
    /// ticker is derived from the first part of the broker ticker, with underscores replaced by dots to
    /// match LEAN's equity ticker convention (LEAN stores Berkshire's class B shares as <c>BRK.B</c>).
    /// </para>
    /// <para>
    /// LEAN only models US-listed equities and ETFs, which are both <see cref="SecurityType.Equity"/> in
    /// LEAN, so instruments on other exchanges are indexed but not mappable.
    /// </para>
    /// </remarks>
    public class Trading212SymbolMapper : ISymbolMapper
    {
        /// <summary>
        /// The security types this mapper supports.
        /// </summary>
        public static readonly HashSet<SecurityType> SupportedSecurityTypes = new() { SecurityType.Equity };

        /// <summary>
        /// The markets this mapper supports.
        /// </summary>
        public static readonly HashSet<string> SupportedMarkets = new() { Market.USA };

        /// <summary>
        /// The country code of the US listings, the only ones LEAN can model.
        /// </summary>
        private const string UnitedStatesCountryCode = "US";

        /// <summary>
        /// The instrument type codes Trading 212 uses for US shares and ETFs, both equities in LEAN.
        /// </summary>
        private static readonly HashSet<string> EquityInstrumentTypeCodes = new(StringComparer.OrdinalIgnoreCase) { "EQ", "ETF" };

        /// <summary>
        /// Guards the maps while the instrument list is refreshed.
        /// </summary>
        private readonly object _lock = new();

        /// <summary>
        /// Maps a LEAN security identifier to the broker ticker.
        /// </summary>
        /// <remarks>
        /// Keyed by identifier rather than by ticker so a ticker change (for example GOOCV to GOOG) does
        /// not poison the cache.
        /// </remarks>
        private Dictionary<SecurityIdentifier, string> _brokerageSymbolBySecurityIdentifier = new();

        /// <summary>
        /// Maps a normalized LEAN ticker to the broker ticker, used for symbols the mapper has not seen.
        /// </summary>
        private Dictionary<string, string> _brokerageSymbolByNormalizedTicker = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Maps a broker ticker to the LEAN symbol.
        /// </summary>
        private Dictionary<string, Symbol> _leanSymbolByBrokerageSymbol = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Every instrument returned by the broker, including the ones LEAN cannot model.
        /// </summary>
        private Dictionary<string, Trading212TradableInstrument> _instrumentByBrokerageSymbol = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the number of instruments currently indexed.
        /// </summary>
        public int InstrumentCount
        {
            get
            {
                lock (_lock)
                {
                    return _instrumentByBrokerageSymbol.Count;
                }
            }
        }

        /// <summary>
        /// Gets the number of instruments that could be mapped to a LEAN symbol.
        /// </summary>
        public int MappedInstrumentCount
        {
            get
            {
                lock (_lock)
                {
                    return _leanSymbolByBrokerageSymbol.Count;
                }
            }
        }

        /// <summary>
        /// Rebuilds the mapping from the broker's instrument metadata.
        /// </summary>
        /// <param name="instruments">The instruments returned by the metadata endpoint.</param>
        /// <returns>The number of instruments that were mapped to a LEAN symbol.</returns>
        public int SetInstruments(IEnumerable<Trading212TradableInstrument> instruments)
        {
            var bySecurityIdentifier = new Dictionary<SecurityIdentifier, string>();
            var byNormalizedTicker = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var leanByBrokerage = new Dictionary<string, Symbol>(StringComparer.OrdinalIgnoreCase);
            var instrumentsByBrokerage = new Dictionary<string, Trading212TradableInstrument>(StringComparer.OrdinalIgnoreCase);

            foreach (var instrument in instruments ?? Enumerable.Empty<Trading212TradableInstrument>())
            {
                if (string.IsNullOrWhiteSpace(instrument?.Ticker))
                {
                    continue;
                }

                instrumentsByBrokerage[instrument.Ticker] = instrument;

                if (!TryParseTicker(instrument.Ticker, out var ticker, out var countryCode, out var instrumentTypeCode))
                {
                    Log.Trace($"{nameof(Trading212SymbolMapper)}.{nameof(SetInstruments)}: Skipping '{instrument.Ticker}' because its format is not '<TICKER>_<COUNTRY>_<TYPE>'.");
                    continue;
                }

                if (!IsSupported(countryCode, instrumentTypeCode))
                {
                    continue;
                }

                if (!TryToLeanTicker(ticker, out var leanTicker))
                {
                    Log.Trace($"{nameof(Trading212SymbolMapper)}.{nameof(SetInstruments)}: Skipping '{instrument.Ticker}' because '{ticker}' is not a common share ticker.");
                    continue;
                }

                var symbol = Symbol.Create(leanTicker, SecurityType.Equity, Market.USA);

                bySecurityIdentifier[symbol.ID] = instrument.Ticker;
                byNormalizedTicker[NormalizeTicker(leanTicker)] = instrument.Ticker;
                leanByBrokerage[instrument.Ticker] = symbol;
            }

            lock (_lock)
            {
                _brokerageSymbolBySecurityIdentifier = bySecurityIdentifier;
                _brokerageSymbolByNormalizedTicker = byNormalizedTicker;
                _leanSymbolByBrokerageSymbol = leanByBrokerage;
                _instrumentByBrokerageSymbol = instrumentsByBrokerage;
            }

            return leanByBrokerage.Count;
        }

        /// <inheritdoc/>
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (TryGetBrokerageSymbol(symbol, out var brokerageSymbol))
            {
                return brokerageSymbol;
            }

            throw new NotSupportedException(
                $"{nameof(Trading212SymbolMapper)}.{nameof(GetBrokerageSymbol)}: The symbol '{symbol}' is not available for trading on Trading 212. " +
                "Only US-listed equities and ETFs returned by the instruments metadata endpoint are supported.");
        }

        /// <summary>
        /// Attempts to map a LEAN symbol to the broker ticker without throwing.
        /// </summary>
        /// <param name="symbol">The LEAN symbol.</param>
        /// <param name="brokerageSymbol">The broker ticker, when the mapping succeeded.</param>
        /// <returns><c>true</c> when the symbol is tradable at Trading 212.</returns>
        public bool TryGetBrokerageSymbol(Symbol symbol, out string brokerageSymbol)
        {
            brokerageSymbol = null;
            if (symbol == null || !SupportedSecurityTypes.Contains(symbol.SecurityType))
            {
                return false;
            }

            lock (_lock)
            {
                if (_brokerageSymbolBySecurityIdentifier.TryGetValue(symbol.ID, out brokerageSymbol))
                {
                    return true;
                }

                // The caller may hold a symbol built elsewhere; fall back to the ticker it trades under today.
                foreach (var ticker in GetCandidateTickers(symbol))
                {
                    if (_brokerageSymbolByNormalizedTicker.TryGetValue(NormalizeTicker(ticker), out brokerageSymbol))
                    {
                        return true;
                    }
                }
            }

            brokerageSymbol = null;
            return false;
        }

        /// <inheritdoc/>
        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market, DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = 0)
        {
            if (TryGetLeanSymbol(brokerageSymbol, out var symbol))
            {
                return symbol;
            }

            throw new NotSupportedException(
                $"{nameof(Trading212SymbolMapper)}.{nameof(GetLeanSymbol)}: The ticker '{brokerageSymbol}' is not a supported Trading 212 instrument. " +
                "Only US-listed equities and ETFs returned by the instruments metadata endpoint are supported.");
        }

        /// <summary>
        /// Attempts to map a broker ticker to a LEAN symbol without throwing.
        /// </summary>
        /// <param name="brokerageSymbol">The broker ticker.</param>
        /// <param name="symbol">The LEAN symbol, when the mapping succeeded.</param>
        /// <returns><c>true</c> when the ticker could be mapped.</returns>
        public bool TryGetLeanSymbol(string brokerageSymbol, out Symbol symbol)
        {
            symbol = null;
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                return false;
            }

            lock (_lock)
            {
                if (_leanSymbolByBrokerageSymbol.TryGetValue(brokerageSymbol, out symbol))
                {
                    return true;
                }

                // The instrument was not in the metadata snapshot, but its ticker may still be well formed.
                if (TryParseTicker(brokerageSymbol, out var ticker, out var countryCode, out var instrumentTypeCode)
                    && IsSupported(countryCode, instrumentTypeCode)
                    && TryToLeanTicker(ticker, out var leanTicker))
                {
                    symbol = Symbol.Create(leanTicker, SecurityType.Equity, Market.USA);
                    _leanSymbolByBrokerageSymbol[brokerageSymbol] = symbol;
                    _brokerageSymbolBySecurityIdentifier[symbol.ID] = brokerageSymbol;
                    _brokerageSymbolByNormalizedTicker[NormalizeTicker(symbol.Value)] = brokerageSymbol;
                    return true;
                }
            }

            symbol = null;
            return false;
        }

        /// <summary>
        /// Gets the broker's metadata for a ticker, including instruments LEAN cannot model.
        /// </summary>
        /// <param name="brokerageSymbol">The broker ticker.</param>
        /// <param name="instrument">The instrument metadata, when it was found.</param>
        /// <returns><c>true</c> when the ticker is in the instrument list.</returns>
        public bool TryGetInstrument(string brokerageSymbol, out Trading212TradableInstrument instrument)
        {
            instrument = null;
            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                return false;
            }

            lock (_lock)
            {
                return _instrumentByBrokerageSymbol.TryGetValue(brokerageSymbol, out instrument);
            }
        }

        /// <summary>
        /// Returns whether a broker ticker is an instrument LEAN can model.
        /// </summary>
        /// <param name="brokerageSymbol">The broker ticker.</param>
        /// <returns><c>true</c> when the instrument is a US-listed common share or ETF.</returns>
        public bool IsSupported(string brokerageSymbol)
        {
            return TryParseTicker(brokerageSymbol, out var ticker, out var countryCode, out var instrumentTypeCode)
                && IsSupported(countryCode, instrumentTypeCode)
                && TryToLeanTicker(ticker, out _);
        }

        /// <summary>
        /// Returns whether a country and instrument type code pair is supported.
        /// </summary>
        /// <param name="countryCode">The country code, for example <c>US</c>.</param>
        /// <param name="instrumentTypeCode">The instrument type code, for example <c>EQ</c>.</param>
        /// <returns><c>true</c> when the pair is a US-listed equity or ETF.</returns>
        private static bool IsSupported(string countryCode, string instrumentTypeCode)
        {
            return string.Equals(countryCode, UnitedStatesCountryCode, StringComparison.OrdinalIgnoreCase)
                && EquityInstrumentTypeCodes.Contains(instrumentTypeCode);
        }

        /// <summary>
        /// Splits a Trading 212 ticker into its ticker, country and instrument type parts.
        /// </summary>
        /// <remarks>
        /// The parts are taken from the right, so tickers containing underscores (class shares) are handled.
        /// </remarks>
        /// <param name="brokerageSymbol">The broker ticker.</param>
        /// <param name="ticker">The instrument's own ticker part.</param>
        /// <param name="countryCode">The country code part.</param>
        /// <param name="instrumentTypeCode">The instrument type code part.</param>
        /// <returns><c>true</c> when the ticker has at least three parts.</returns>
        public static bool TryParseTicker(string brokerageSymbol, out string ticker, out string countryCode, out string instrumentTypeCode)
        {
            ticker = null;
            countryCode = null;
            instrumentTypeCode = null;

            if (string.IsNullOrWhiteSpace(brokerageSymbol))
            {
                return false;
            }

            var parts = brokerageSymbol.Split('_');
            if (parts.Length < 3)
            {
                return false;
            }

            instrumentTypeCode = parts[^1];
            countryCode = parts[^2];
            ticker = string.Join("_", parts.Take(parts.Length - 2));

            return !string.IsNullOrEmpty(ticker) && !string.IsNullOrEmpty(countryCode) && !string.IsNullOrEmpty(instrumentTypeCode);
        }

        /// <summary>
        /// Converts the instrument's own ticker part into a LEAN equity ticker, rejecting the instruments that are
        /// not common shares.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trading 212 writes a share class with a separator instead of LEAN's dot: Berkshire's class B shares are
        /// <c>BRK_B</c> and its class A shares are <c>BRK/A</c>, where LEAN uses <c>BRK.B</c> and <c>BRK.A</c>.
        /// </para>
        /// <para>
        /// An underscore is not always a share class. Across the 6,916 US instruments the account can trade, only
        /// 19 ticker parts contain a separator: 17 are warrants written <c>&lt;TICKER&gt;_WAR</c> and one is a
        /// malformed <c>AVAV_</c>. Mapping a warrant to <c>DSX.WAR</c> would invent a LEAN symbol that never had
        /// data, so a separator is only treated as a share class when it is followed by exactly one letter.
        /// </para>
        /// </remarks>
        /// <param name="brokerTicker">The instrument's own ticker part.</param>
        /// <param name="leanTicker">The LEAN ticker, when the instrument is a common share.</param>
        /// <returns><c>true</c> when the instrument is a common share.</returns>
        public static bool TryToLeanTicker(string brokerTicker, out string leanTicker)
        {
            leanTicker = null;
            if (string.IsNullOrEmpty(brokerTicker))
            {
                return false;
            }

            var separatorIndex = Math.Max(brokerTicker.LastIndexOf('_'), brokerTicker.LastIndexOf('/'));
            if (separatorIndex < 0)
            {
                leanTicker = brokerTicker.ToUpperInvariant();
                return true;
            }

            // A share class is a single letter after the separator: BRK_B, BRK/A.
            var baseTicker = brokerTicker.Substring(0, separatorIndex);
            var shareClass = brokerTicker.Substring(separatorIndex + 1);
            if (baseTicker.Length == 0 || shareClass.Length != 1 || !char.IsLetter(shareClass[0]))
            {
                return false;
            }

            leanTicker = $"{baseTicker.ToUpperInvariant()}.{char.ToUpperInvariant(shareClass[0])}";
            return true;
        }

        /// <summary>
        /// Normalizes a LEAN ticker so it can be compared with a broker ticker part.
        /// </summary>
        /// <param name="leanTicker">The LEAN ticker.</param>
        /// <returns>The normalized ticker.</returns>
        public static string NormalizeTicker(string leanTicker)
        {
            if (string.IsNullOrEmpty(leanTicker))
            {
                return leanTicker;
            }

            var builder = new StringBuilder(leanTicker.Length);
            foreach (var character in leanTicker)
            {
                switch (character)
                {
                    case '.':
                    case '-':
                    case '/':
                    case ' ':
                        builder.Append('_');
                        break;
                    default:
                        builder.Append(char.ToUpperInvariant(character));
                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Returns the tickers a LEAN symbol might be trading under, most current first.
        /// </summary>
        /// <param name="symbol">The LEAN symbol.</param>
        /// <returns>The candidate tickers.</returns>
        private static IEnumerable<string> GetCandidateTickers(Symbol symbol)
        {
            var current = TryResolveCurrentTicker(symbol);
            if (!string.IsNullOrEmpty(current))
            {
                yield return current;
            }

            if (!string.IsNullOrEmpty(symbol.Value) && !string.Equals(symbol.Value, current, StringComparison.Ordinal))
            {
                yield return symbol.Value;
            }
        }

        /// <summary>
        /// Resolves the ticker a symbol trades under today, tolerating a missing data folder.
        /// </summary>
        /// <param name="symbol">The LEAN symbol.</param>
        /// <returns>The current ticker, or <c>null</c> when it could not be resolved.</returns>
        private static string TryResolveCurrentTicker(Symbol symbol)
        {
            try
            {
                return SecurityIdentifier.Ticker(symbol, DateTime.UtcNow);
            }
            catch (Exception exception)
            {
                // Resolving the ticker needs the map files; fall back to the symbol's own value.
                Log.Trace($"{nameof(Trading212SymbolMapper)}.{nameof(TryResolveCurrentTicker)}: Could not resolve the current ticker for '{symbol}': {exception.Message}");
                return symbol.Value;
            }
        }
    }
}
