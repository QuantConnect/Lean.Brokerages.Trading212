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
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// A minimal <see cref="IOrderProvider"/> that only resolves orders by broker id, which is all the
    /// brokerage needs while reporting order updates.
    /// </summary>
    public sealed class FakeOrderProvider : IOrderProvider
    {
        private readonly List<Order> _orders = new();

        /// <inheritdoc/>
        public int OrdersCount => _orders.Count;

        /// <summary>
        /// Registers an order so it can be resolved by its broker id.
        /// </summary>
        /// <param name="order">The order to register.</param>
        public void Add(Order order)
        {
            _orders.Add(order);
        }

        /// <inheritdoc/>
        public Order GetOrderById(int orderId) => _orders.FirstOrDefault(order => order.Id == orderId);

        /// <inheritdoc/>
        public List<Order> GetOrdersByBrokerageId(string brokerageId)
        {
            return _orders.Where(order => order.BrokerId.Contains(brokerageId)).ToList();
        }

        /// <inheritdoc/>
        public IEnumerable<OrderTicket> GetOrderTickets(Func<OrderTicket, bool> filter = null) => Enumerable.Empty<OrderTicket>();

        /// <inheritdoc/>
        public IEnumerable<OrderTicket> GetOpenOrderTickets(Func<OrderTicket, bool> filter = null) => Enumerable.Empty<OrderTicket>();

        /// <inheritdoc/>
        public OrderTicket GetOrderTicket(int orderId) => null;

        /// <inheritdoc/>
        public IEnumerable<Order> GetOrders(Func<Order, bool> filter = null) => filter == null ? _orders : _orders.Where(filter);

        /// <inheritdoc/>
        public List<Order> GetOpenOrders(Func<Order, bool> filter = null)
        {
            var open = _orders.Where(order => !order.Status.IsClosed());
            return (filter == null ? open : open.Where(filter)).ToList();
        }

        /// <inheritdoc/>
        public ProjectedHoldings GetProjectedHoldings(Security security) => new(0m, 0m);
    }
}
