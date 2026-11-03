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

using System.IO;
using System.Linq;
using System.Net;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Api;
using QuantConnect.Brokerages.Trading212.ToolBox;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    [TestFixture]
    public class Trading212InstrumentListDownloaderTests
    {
        [Test]
        public void WritesTheBrokerTickerToLeanSymbolMapping()
        {
            var handler = new FakeHttpMessageHandler();
            handler.EnqueueJson(HttpStatusCode.OK, Trading212TestData.Instruments);

            var outputPath = Path.Combine(Path.GetTempPath(), $"trading212-instruments-{System.Guid.NewGuid():N}.csv");

            try
            {
                using (var client = new Trading212ApiClient("https://demo.trading212.com", "key", "secret", handler, messageReceived: null))
                {
                    new Trading212InstrumentListDownloader().Run(outputPath, client);
                }

                var lines = File.ReadAllLines(outputPath);

                Assert.That(lines[0], Is.EqualTo("brokerage-ticker,lean-ticker,supported,instrument-type,currency,name,isin"));
                Assert.That(lines, Has.Length.EqualTo(8));

                var rows = lines.Skip(1).ToDictionary(line => line.Split(',')[0]);
                Assert.That(rows["AAPL_US_EQ"], Does.StartWith("AAPL_US_EQ,AAPL,true,Stock,USD,"));
                Assert.That(rows["BRK_B_US_EQ"], Does.StartWith("BRK_B_US_EQ,BRK.B,true,Stock,USD,"));
                Assert.That(rows["VOD_GB_EQ"], Does.StartWith("VOD_GB_EQ,,false,Stock,GBP,"));
            }
            finally
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
            }
        }
    }
}
