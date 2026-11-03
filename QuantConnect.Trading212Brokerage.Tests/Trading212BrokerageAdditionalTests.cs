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
using QuantConnect.Interfaces;
using QuantConnect.Util;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212BrokerageAdditionalTests
    {
        [Test]
        public void ParameterlessConstructorComposerUsage()
        {
            var brokerage = Composer.Instance.GetExportedValueByTypeName<IDataQueueHandler>("Trading212Brokerage");

            Assert.That(brokerage, Is.Not.Null);
            Assert.That(brokerage, Is.InstanceOf<Trading212Brokerage>());
        }

        [TestCase("live", Trading212Environment.Live)]
        [TestCase("LIVE", Trading212Environment.Live)]
        [TestCase("demo", Trading212Environment.Demo)]
        [TestCase("Demo", Trading212Environment.Demo)]
        [TestCase("paper", Trading212Environment.Demo)]
        [TestCase("", Trading212Environment.Live)]
        [TestCase(null, Trading212Environment.Live)]
        [TestCase("something-else", Trading212Environment.Live)]
        public void ParsesTheConfiguredEnvironment(string value, Trading212Environment expected)
        {
            Assert.That(Trading212Brokerage.ParseEnvironment(value), Is.EqualTo(expected));
        }
    }
}
