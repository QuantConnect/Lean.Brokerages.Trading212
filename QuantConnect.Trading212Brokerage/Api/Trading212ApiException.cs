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
using System.Net;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Api
{
    /// <summary>
    /// Represents an error returned by the Trading 212 Public API.
    /// </summary>
    /// <remarks>
    /// The broker's own wording is preserved in <see cref="Exception.Message"/> and in
    /// <see cref="RawBody"/> so a rejection can be surfaced to the algorithm unmodified.
    /// </remarks>
    public class Trading212ApiException : Exception
    {
        /// <summary>
        /// The HTTP status code of the response.
        /// </summary>
        public HttpStatusCode StatusCode { get; }

        /// <summary>
        /// The broker's machine-readable rejection reason, when the payload carried one.
        /// </summary>
        /// <remarks>
        /// An order rejection observed against the live API carries its reason as the last segment of the
        /// RFC 7807 <c>type</c>, for example <c>selling-equity-not-owned</c>. The published OpenAPI description
        /// does not enumerate the reasons, so this integration never matches on a specific value; it is exposed
        /// for callers that want to.
        /// </remarks>
        public string ErrorCode { get; }

        /// <summary>
        /// The parsed error payload, when the body was valid JSON.
        /// </summary>
        public Trading212Error Error { get; }

        /// <summary>
        /// The raw response body exactly as the broker sent it.
        /// </summary>
        public string RawBody { get; }

        /// <summary>
        /// The relative resource that was requested.
        /// </summary>
        public string Resource { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Trading212ApiException"/> class.
        /// </summary>
        /// <param name="statusCode">The HTTP status code of the response.</param>
        /// <param name="resource">The relative resource that was requested.</param>
        /// <param name="rawBody">The raw response body.</param>
        /// <param name="error">The parsed error payload, when available.</param>
        /// <param name="message">The message to surface.</param>
        public Trading212ApiException(HttpStatusCode statusCode, string resource, string rawBody, Trading212Error error, string message)
            : base(message)
        {
            StatusCode = statusCode;
            Resource = resource;
            RawBody = rawBody;
            Error = error;
            ErrorCode = error?.GetReasonIdentifier();
        }
    }
}
