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
using System.Text;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Trading212.Models
{
    /// <summary>
    /// The Trading 212 Public API environment to connect to.
    /// </summary>
    /// <remarks>
    /// The two environments use different base URLs and different API key pairs; see
    /// <see href="https://docs.trading212.com/api/section/general-information/api-environments"/>.
    /// </remarks>
    public enum Trading212Environment
    {
        /// <summary>
        /// Real money trading: <c>https://live.trading212.com/api/v0</c>
        /// </summary>
        Live,

        /// <summary>
        /// Paper trading: <c>https://demo.trading212.com/api/v0</c>
        /// </summary>
        Demo
    }

    /// <summary>
    /// The type of a tradable instrument, as returned by <c>GET /api/v0/equity/metadata/instruments</c>.
    /// </summary>
    public enum Trading212InstrumentType
    {
        /// <summary>
        /// A value that this integration does not recognise, or that the API did not return.
        /// </summary>
        Unknown,

        /// <summary>An equity (share).</summary>
        Stock,

        /// <summary>An exchange traded fund. LEAN models ETFs as equities.</summary>
        Etf,

        /// <summary>A cryptocurrency.</summary>
        Cryptocurrency,

        /// <summary>A foreign exchange pair.</summary>
        Forex,

        /// <summary>A future.</summary>
        Futures,

        /// <summary>An index.</summary>
        Index,

        /// <summary>A warrant.</summary>
        Warrant,

        /// <summary>A cryptocurrency (alternate code returned by the API).</summary>
        Crypto,

        /// <summary>A contingent value right.</summary>
        Cvr,

        /// <summary>A corporate action.</summary>
        Corpact
    }

    /// <summary>
    /// The type of an order, as accepted by the Trading 212 Public API.
    /// </summary>
    public enum Trading212OrderType
    {
        /// <summary>A value that this integration does not recognise.</summary>
        Unknown,

        /// <summary>Executes immediately at the next available price.</summary>
        Market,

        /// <summary>Executes at the given price or better.</summary>
        Limit,

        /// <summary>Becomes a market order once the stop price is reached.</summary>
        Stop,

        /// <summary>Becomes a limit order once the stop price is reached.</summary>
        StopLimit
    }

    /// <summary>
    /// The side of an order, as returned by the Trading 212 Public API.
    /// </summary>
    public enum Trading212OrderSide
    {
        /// <summary>A value that this integration does not recognise.</summary>
        Unknown,

        /// <summary>A buy (positive quantity).</summary>
        Buy,

        /// <summary>A sell (negative quantity).</summary>
        Sell
    }

    /// <summary>
    /// The state of an order in its lifecycle, as returned by the Trading 212 Public API.
    /// </summary>
    public enum Trading212OrderStatus
    {
        /// <summary>
        /// A value that this integration does not recognise. The API is in beta and may add
        /// states, so unknown values deserialize to this instead of failing.
        /// </summary>
        Unknown,

        /// <summary>The order has not been sent to the venue yet.</summary>
        Local,

        /// <summary>The order is awaiting confirmation.</summary>
        Unconfirmed,

        /// <summary>The order has been confirmed but is not working yet.</summary>
        Confirmed,

        /// <summary>The order is working on the venue.</summary>
        New,

        /// <summary>A cancellation request is in flight.</summary>
        Cancelling,

        /// <summary>The order was cancelled.</summary>
        Cancelled,

        /// <summary>The order was partially executed.</summary>
        PartiallyFilled,

        /// <summary>The order was fully executed.</summary>
        Filled,

        /// <summary>The broker rejected the order.</summary>
        Rejected,

        /// <summary>A replacement request is in flight.</summary>
        Replacing,

        /// <summary>The order was replaced.</summary>
        Replaced
    }

    /// <summary>
    /// How long an order remains active, as accepted by the Trading 212 Public API.
    /// </summary>
    public enum Trading212TimeValidity
    {
        /// <summary>A value that this integration does not recognise.</summary>
        Unknown,

        /// <summary>
        /// The order expires at midnight in the time zone of the instrument's exchange if it has
        /// not executed.
        /// </summary>
        Day,

        /// <summary>The order remains active until it fills or is cancelled.</summary>
        GoodTillCancel
    }

    /// <summary>
    /// The strategy used to size an order, as returned by the Trading 212 Public API.
    /// </summary>
    /// <remarks>
    /// The API currently only accepts orders placed by <see cref="Quantity"/>.
    /// </remarks>
    public enum Trading212OrderStrategy
    {
        /// <summary>A value that this integration does not recognise.</summary>
        Unknown,

        /// <summary>The order was sized by number of shares.</summary>
        Quantity,

        /// <summary>The order was sized by monetary value.</summary>
        Value
    }

    /// <summary>
    /// How an order was initiated, as returned by the Trading 212 Public API.
    /// </summary>
    public enum Trading212InitiatedFrom
    {
        /// <summary>A value that this integration does not recognise.</summary>
        Unknown,

        /// <summary>Placed through the Public API.</summary>
        Api,

        /// <summary>Placed through the iOS application.</summary>
        Ios,

        /// <summary>Placed through the Android application.</summary>
        Android,

        /// <summary>Placed through the web application.</summary>
        Web,

        /// <summary>Placed by Trading 212 itself.</summary>
        System,

        /// <summary>Placed by auto-invest.</summary>
        Autoinvest,

        /// <summary>Placed by instrument auto-invest.</summary>
        InstrumentAutoinvest
    }

    /// <summary>
    /// Converts between the <c>SCREAMING_SNAKE_CASE</c> strings the Trading 212 Public API uses
    /// and the <see cref="PascalCase"/> names of the enums in this namespace.
    /// </summary>
    internal static class Trading212EnumNormalization
    {
        /// <summary>
        /// Converts a wire value such as <c>STOP_LIMIT</c> into the matching enum name, <c>StopLimit</c>.
        /// </summary>
        /// <param name="wireValue">The value as returned by the API.</param>
        /// <returns>The normalized name, or an empty string when the input is empty.</returns>
        public static string ToPascalCase(string wireValue)
        {
            if (string.IsNullOrEmpty(wireValue))
            {
                return wireValue;
            }

            var parts = wireValue.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder(wireValue.Length);
            foreach (var part in parts)
            {
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part.Substring(1).ToLowerInvariant());
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Converts an enum name such as <c>StopLimit</c> into the wire value <c>STOP_LIMIT</c>.
        /// </summary>
        /// <param name="pascalCase">The enum name.</param>
        /// <returns>The value to send to the API.</returns>
        public static string ToWireValue(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase))
            {
                return pascalCase;
            }

            var builder = new StringBuilder(pascalCase.Length + 4);
            for (var i = 0; i < pascalCase.Length; i++)
            {
                var current = pascalCase[i];
                if (char.IsUpper(current) && i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToUpperInvariant(current));
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Deserializes the enums of this namespace from the Trading 212 wire format.
    /// </summary>
    /// <remarks>
    /// The API is in beta and may add enum members without notice. <see cref="Newtonsoft.Json.Converters.StringEnumConverter"/>
    /// throws on an unrecognized value, which would abort order status polling, so unrecognized values
    /// are mapped to the <c>Unknown</c> member instead. Values are written back in the wire format.
    /// </remarks>
    /// <typeparam name="T">The enum type to convert.</typeparam>
    public class Trading212EnumConverter<T> : JsonConverter
        where T : struct, Enum
    {
        /// <inheritdoc/>
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(T) || objectType == typeof(T?);
        }

        /// <inheritdoc/>
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var raw = reader.Value?.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                return default(T);
            }

            var normalized = Trading212EnumNormalization.ToPascalCase(raw);
            return Enum.TryParse<T>(normalized, ignoreCase: true, out var value) ? value : default(T);
        }

        /// <inheritdoc/>
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteValue(Trading212EnumNormalization.ToWireValue(value.ToString()));
        }
    }
}
