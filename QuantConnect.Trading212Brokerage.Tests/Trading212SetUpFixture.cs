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
using QuantConnect.Tests;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Prepares the test environment before any fixture runs.
    /// </summary>
    /// <remarks>
    /// The symbol mapper builds LEAN symbols with
    /// <see cref="Symbol.Create(string, SecurityType, string, string, System.Type)"/>, which resolves the
    /// security's first ticker date through the map files, so the data folder and the file providers have to
    /// be configured the same way LEAN's own test assembly does it.
    /// </remarks>
    [SetUpFixture]
    public class Trading212SetUpFixture
    {
        /// <summary>
        /// Loads the configuration and initializes the data providers.
        /// </summary>
        [OneTimeSetUp]
        public void RunBeforeAnyTests()
        {
            TestSetup.ReloadConfiguration();
            TestGlobals.Initialize();

            // SecurityIdentifier resolves its map file provider from the Composer, which is not the
            // instance TestGlobals initializes, so register the initialized one.
            Composer.Instance.AddPart<IMapFileProvider>(TestGlobals.MapFileProvider);
        }
    }
}
