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
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that replays queued responses, so the API client and the
    /// brokerage can be exercised offline.
    /// </summary>
    public sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

        /// <summary>
        /// The requests that were sent, in order.
        /// </summary>
        public List<HttpRequestMessage> Requests { get; } = new();

        /// <summary>
        /// The request bodies that were sent, in order. Entries are <c>null</c> when a request had no body.
        /// </summary>
        public List<string> RequestBodies { get; } = new();

        /// <summary>
        /// The authorization header of each request, in order.
        /// </summary>
        public List<AuthenticationHeaderValue> Authorizations { get; } = new();

        /// <summary>
        /// Queues a responder.
        /// </summary>
        /// <param name="responder">Builds the response for the request it receives.</param>
        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responses.Enqueue(responder);
        }

        /// <summary>
        /// Queues a JSON response.
        /// </summary>
        /// <param name="statusCode">The status code.</param>
        /// <param name="json">The response body.</param>
        /// <param name="configure">An optional callback that can add headers to the response.</param>
        public void EnqueueJson(HttpStatusCode statusCode, string json, Action<HttpResponseMessage> configure = null)
        {
            Enqueue(_ =>
            {
                var response = new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json")
                };
                configure?.Invoke(response);
                return response;
            });
        }

        /// <summary>
        /// Queues an empty response.
        /// </summary>
        /// <param name="statusCode">The status code.</param>
        /// <param name="configure">An optional callback that can add headers to the response.</param>
        public void EnqueueEmpty(HttpStatusCode statusCode, Action<HttpResponseMessage> configure = null)
        {
            Enqueue(_ =>
            {
                var response = new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(string.Empty)
                };
                configure?.Invoke(response);
                return response;
            });
        }

        /// <inheritdoc/>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Authorizations.Add(request.Headers.Authorization);
            RequestBodies.Add(request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException($"No response was queued for [{request.Method}] {request.RequestUri}.");
            }

            return _responses.Dequeue()(request);
        }
    }
}
