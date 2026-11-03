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

using QuantConnect.Orders;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212
{
    /// <summary>
    /// Converts between Trading 212 concepts and LEAN concepts.
    /// </summary>
    public static class Trading212BrokerageExtensions
    {
        /// <summary>
        /// Converts a Trading 212 order status into a LEAN <see cref="OrderStatus"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Trading212OrderStatus.Unknown"/> returns <c>null</c> so an unrecognized status
        /// leaves the LEAN order untouched instead of being reported as a state change.
        /// </remarks>
        /// <param name="status">The broker order status.</param>
        /// <returns>The matching LEAN status, or <c>null</c> when it cannot be mapped.</returns>
        public static OrderStatus? ToLeanOrderStatus(this Trading212OrderStatus status)
        {
            switch (status)
            {
                case Trading212OrderStatus.Local:
                case Trading212OrderStatus.Unconfirmed:
                case Trading212OrderStatus.Confirmed:
                case Trading212OrderStatus.New:
                case Trading212OrderStatus.Cancelling:
                case Trading212OrderStatus.Replacing:
                case Trading212OrderStatus.Replaced:
                    return OrderStatus.Submitted;
                case Trading212OrderStatus.PartiallyFilled:
                    return OrderStatus.PartiallyFilled;
                case Trading212OrderStatus.Filled:
                    return OrderStatus.Filled;
                case Trading212OrderStatus.Cancelled:
                    return OrderStatus.Canceled;
                case Trading212OrderStatus.Rejected:
                    return OrderStatus.Invalid;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns whether a broker order status means the order is still working and may still fill.
        /// </summary>
        /// <param name="status">The broker order status.</param>
        /// <returns><c>true</c> when the order is still working.</returns>
        public static bool IsWorking(this Trading212OrderStatus status)
        {
            switch (status)
            {
                case Trading212OrderStatus.Local:
                case Trading212OrderStatus.Unconfirmed:
                case Trading212OrderStatus.Confirmed:
                case Trading212OrderStatus.New:
                case Trading212OrderStatus.Cancelling:
                case Trading212OrderStatus.PartiallyFilled:
                case Trading212OrderStatus.Replacing:
                case Trading212OrderStatus.Replaced:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Converts a LEAN <see cref="TimeInForce"/> into the Trading 212 time validity.
        /// </summary>
        /// <remarks>
        /// The Public API only accepts <c>DAY</c> and <c>GOOD_TILL_CANCEL</c>, so a good-till-date order
        /// is not representable.
        /// </remarks>
        /// <param name="timeInForce">The LEAN time in force.</param>
        /// <param name="timeValidity">The Trading 212 time validity.</param>
        /// <returns><c>true</c> when the time in force can be represented.</returns>
        public static bool TryGetTimeValidity(this TimeInForce timeInForce, out Trading212TimeValidity timeValidity)
        {
            if (ReferenceEquals(timeInForce, TimeInForce.Day) || timeInForce is DayTimeInForce)
            {
                timeValidity = Trading212TimeValidity.Day;
                return true;
            }

            if (ReferenceEquals(timeInForce, TimeInForce.GoodTilCanceled) || timeInForce is GoodTilCanceledTimeInForce)
            {
                timeValidity = Trading212TimeValidity.GoodTillCancel;
                return true;
            }

            timeValidity = Trading212TimeValidity.Unknown;
            return false;
        }
    }
}
