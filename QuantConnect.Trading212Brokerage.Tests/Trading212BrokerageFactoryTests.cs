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

using NUnit.Framework;
using QuantConnect.Brokerages;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212BrokerageFactoryTests
    {
        [Test]
        public void InitializesFactoryFromComposer()
        {
            using var factory = Composer.Instance.Single<IBrokerageFactory>(instance => instance.BrokerageType == typeof(Trading212Brokerage));

            Assert.That(factory, Is.Not.Null);
            Assert.That(factory, Is.InstanceOf<Trading212BrokerageFactory>());
        }

        [Test]
        public void ExposesTheExpectedConfigurationKeys()
        {
            var factory = new Trading212BrokerageFactory();

            Assert.That(factory.BrokerageData, Does.ContainKey("trading212-api-key"));
            Assert.That(factory.BrokerageData, Does.ContainKey("trading212-api-secret"));
            Assert.That(factory.BrokerageData, Does.ContainKey("trading212-environment"));
        }

        [Test]
        public void ReturnsTheTrading212BrokerageModel()
        {
            var factory = new Trading212BrokerageFactory();

            Assert.That(factory.GetBrokerageModel(orderProvider: null), Is.InstanceOf<Trading212BrokerageModel>());
        }
    }
}
