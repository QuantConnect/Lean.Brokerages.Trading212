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

using QuantConnect.Configuration;
using static QuantConnect.Configuration.ApplicationParser;

namespace QuantConnect.Brokerages.Trading212.ToolBox
{
    /// <summary>
    /// The Trading 212 toolbox entry point.
    /// </summary>
    /// <remarks>
    /// Trading 212's public API serves no market data, so there is no downloader and no exchange info to
    /// generate. The only tool is the instrument list dump, which makes the broker ticker to LEAN symbol
    /// mapping easy to inspect.
    /// </remarks>
    internal static class Program
    {
        private static void Main(string[] args)
        {
            var optionsObject = ToolboxArgumentParser.ParseArguments(args);
            if (optionsObject.Count == 0)
            {
                PrintMessageAndExit();
            }

            if (!optionsObject.TryGetValue("app", out var targetApp))
            {
                PrintMessageAndExit(1, "ERROR: --app value is required");
            }

            var targetAppName = targetApp.ToString();
            if (targetAppName.Contains("instrument") || targetAppName.Contains("symbol"))
            {
                var outputPath = Config.Get("trading212-instruments-output", "trading212-instruments.csv");
                new Trading212InstrumentListDownloader().Run(outputPath);
            }
            else
            {
                PrintMessageAndExit(1, "ERROR: Unrecognized --app value. Trading 212 serves no market data, so the only " +
                    "supported app is 'trading212-instruments', which writes the broker ticker to LEAN symbol mapping.");
            }
        }
    }
}
