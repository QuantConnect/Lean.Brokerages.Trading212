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
using System.Text;
using Newtonsoft.Json;

namespace QuantConnect.Brokerages.Trading212.Models
{
    /// <summary>
    /// The error payload the Trading 212 Public API returns when a request is refused.
    /// </summary>
    /// <remarks>
    /// The published OpenAPI description documents only the HTTP status codes for error responses,
    /// so this type captures every shape the API has been observed to return and preserves the raw
    /// body as well:
    /// <list type="bullet">
    /// <item><description>
    /// Order rejections carry the broker's reason in <c>code</c> and <c>clarification</c>, for example
    /// <c>{"code": "SellingEquityNotOwned", "clarification": "SellingEquityNotOwned"}</c>.
    /// </description></item>
    /// <item><description>
    /// Request errors follow RFC 7807 with <c>type</c>, <c>title</c>, <c>status</c>, <c>detail</c> and
    /// <c>traceId</c>, for example <c>{"type": "/api-errors/invalid-request", "title": "The request is
    /// invalid", "status": 400, "detail": "Invalid payload", "traceId": "..."}</c>.
    /// </description></item>
    /// <item><description>
    /// The same RFC 7807 object is sometimes wrapped in <c>{"success": false, "error": {...}}</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public class Trading212Error
    {
        /// <summary>
        /// The broker's machine-readable rejection reason, for example <c>SellingEquityNotOwned</c>.
        /// </summary>
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>
        /// A clarification of <see cref="Code"/>.
        /// </summary>
        [JsonProperty("clarification")]
        public string Clarification { get; set; }

        /// <summary>
        /// The RFC 7807 problem type, for example <c>/api-errors/invalid-request</c>.
        /// </summary>
        [JsonProperty("type")]
        public string Type { get; set; }

        /// <summary>
        /// The RFC 7807 human readable summary.
        /// </summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>
        /// The HTTP status code repeated in the body.
        /// </summary>
        [JsonProperty("status")]
        public int? Status { get; set; }

        /// <summary>
        /// The RFC 7807 human readable explanation of this occurrence.
        /// </summary>
        [JsonProperty("detail")]
        public string Detail { get; set; }

        /// <summary>
        /// The broker's human readable message, sent in some envelopes instead of <see cref="Title"/> and
        /// <see cref="Detail"/>.
        /// </summary>
        /// <remarks>
        /// A rate limited request answers
        /// <c>{"code":"BusinessException","context":{"type":"TooManyRequests"},"errorMessage":"too many requests"}</c>.
        /// </remarks>
        [JsonProperty("errorMessage")]
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Additional context of the error, when the API sends it.
        /// </summary>
        [JsonProperty("context")]
        public Trading212ErrorContext Context { get; set; }

        /// <summary>
        /// The identifier Trading 212 uses to correlate the request with its logs.
        /// </summary>
        [JsonProperty("traceId")]
        public string TraceId { get; set; }

        /// <summary>
        /// The wrapped error object, when the API nests it.
        /// </summary>
        [JsonProperty("error")]
        public Trading212Error Error { get; set; }

        /// <summary>
        /// The success flag of the wrapper envelope, when the API sends one.
        /// </summary>
        [JsonProperty("success")]
        public bool? Success { get; set; }

        /// <summary>
        /// Returns the innermost error, unwrapping the envelope if the API nested one.
        /// </summary>
        /// <returns>The error carrying the actual reason.</returns>
        public Trading212Error Unwrap()
        {
            var current = this;
            var seen = new HashSet<Trading212Error>();
            while (current?.Error != null && seen.Add(current))
            {
                current = current.Error;
            }

            return current;
        }

        /// <summary>
        /// The broker's machine-readable rejection reason, when the payload carried one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Trading 212 is not consistent about where it puts the reason. An order rejection observed against
        /// the live API is an RFC 7807 problem whose <c>type</c> is the slug
        /// <c>/api-errors/selling-equity-not-owned</c> and whose <c>detail</c> is the human text; older captures
        /// show a <c>{"code": "SellingEquityNotOwned", "clarification": "..."}</c> envelope instead.
        /// </para>
        /// <para>
        /// This is the <c>code</c> field only. The published OpenAPI description does not enumerate the reasons,
        /// so this integration never matches on a specific value; use <see cref="GetMessage(string)"/> for what to
        /// show a user.
        /// </para>
        /// </remarks>
        public string ErrorCode => Unwrap()?.Code;

        /// <summary>
        /// Returns the reason identifier of the error, from whichever field the broker populated.
        /// </summary>
        /// <remarks>
        /// For an RFC 7807 problem this is the last segment of <c>type</c>, so
        /// <c>/api-errors/selling-equity-not-owned</c> yields <c>selling-equity-not-owned</c>. This is reported for
        /// diagnostics only and is never matched on.
        /// </remarks>
        /// <returns>The reason identifier, or <c>null</c> when the payload carried none.</returns>
        public string GetReasonIdentifier()
        {
            var error = Unwrap() ?? this;

            if (!string.IsNullOrWhiteSpace(error.Code))
            {
                return error.Code;
            }

            if (string.IsNullOrWhiteSpace(error.Type))
            {
                return null;
            }

            var separator = error.Type.LastIndexOf('/');
            return separator >= 0 ? error.Type.Substring(separator + 1) : error.Type;
        }

        /// <summary>
        /// Builds a single human readable message from every field the API populated, so a broker
        /// rejection reaches the algorithm with the broker's own wording.
        /// </summary>
        /// <param name="fallback">The message to use when the payload carried no usable field.</param>
        /// <returns>The message to surface to the user.</returns>
        public string GetMessage(string fallback = null)
        {
            var error = Unwrap() ?? this;
            var parts = new List<string>(4);

            AddDistinct(parts, error.Code);
            AddDistinct(parts, error.Context?.Type);
            AddDistinct(parts, error.ErrorMessage);
            AddDistinct(parts, error.Clarification);
            AddDistinct(parts, error.Title);
            AddDistinct(parts, error.Detail);
            if (parts.Count == 0)
            {
                AddDistinct(parts, error.Type);
            }

            if (parts.Count == 0)
            {
                return fallback;
            }

            var builder = new StringBuilder();
            builder.Append(string.Join(": ", parts));
            if (error.Status.HasValue)
            {
                builder.Append($" (status {error.Status.Value})");
            }

            if (!string.IsNullOrEmpty(error.TraceId))
            {
                builder.Append($" [traceId {error.TraceId}]");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Appends a value unless it is empty or already present.
        /// </summary>
        /// <param name="parts">The accumulated message parts.</param>
        /// <param name="value">The candidate value.</param>
        private static void AddDistinct(List<string> parts, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !parts.Contains(value))
            {
                parts.Add(value);
            }
        }
    }
}
