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

namespace QuantConnect.Brokerages.Trading212.Api
{
    /// <summary>
    /// Raised when an order placement request failed in a way that does not say whether the broker accepted
    /// the order.
    /// </summary>
    /// <remarks>
    /// A gateway error or a timeout on a POST to the order endpoints is not a rejection: the order may have
    /// been created and only the response lost. Placement is therefore never retried on these responses, and
    /// the caller reconciles against the broker's order list before reporting the order as invalid.
    /// </remarks>
    public class Trading212OrderPlacementException : Exception
    {
        /// <summary>
        /// The HTTP status code of the response, or <c>null</c> when no response was received.
        /// </summary>
        public HttpStatusCode? StatusCode { get; }

        /// <summary>
        /// The relative resource that was requested.
        /// </summary>
        public string Resource { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Trading212OrderPlacementException"/> class.
        /// </summary>
        /// <param name="resource">The relative resource that was requested.</param>
        /// <param name="statusCode">The HTTP status code, or <c>null</c> when no response was received.</param>
        /// <param name="message">The message to surface.</param>
        /// <param name="innerException">The underlying failure, if any.</param>
        public Trading212OrderPlacementException(string resource, HttpStatusCode? statusCode, string message, Exception innerException = null)
            : base(message, innerException)
        {
            Resource = resource;
            StatusCode = statusCode;
        }
    }
}
