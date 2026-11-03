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
using System.IO;
using NUnit.Framework;
using QuantConnect.Brokerages.Trading212.Models;

namespace QuantConnect.Brokerages.Trading212.Tests
{
    /// <summary>
    /// Reads the paper-account credentials used by the online tests.
    /// </summary>
    /// <remarks>
    /// Credentials come from the environment first, then from a <c>.env</c> file found by walking up from
    /// the test output folder. The file is never committed; the repository's <c>.gitignore</c> excludes it.
    /// </remarks>
    public static class Trading212OnlineCredentials
    {
        /// <summary>
        /// The environment variable holding the live (real money) API key.
        /// </summary>
        public const string LiveApiKeyVariable = "TRADING212_LIVE_API_KEY_ID";

        /// <summary>
        /// The environment variable holding the live (real money) API secret.
        /// </summary>
        public const string LiveApiSecretVariable = "TRADING212_LIVE_SECRET_KEY";

        /// <summary>
        /// The environment variable holding the practice (paper) API key.
        /// </summary>
        public const string PracticeApiKeyVariable = "TRADING212_PRACTICE_API_KEY_ID";

        /// <summary>
        /// The environment variable holding the practice (paper) API secret.
        /// </summary>
        public const string PracticeApiSecretVariable = "TRADING212_PRACTICE_SECRET_KEY";

        /// <summary>
        /// Returns the API key for an environment.
        /// </summary>
        /// <remarks>
        /// The two environments use different key pairs: Trading 212 documents that
        /// "API Key versions may differ between real and demo accounts".
        /// </remarks>
        /// <param name="environment">The environment.</param>
        /// <returns>The API key, or <c>null</c> when it is not configured.</returns>
        public static string GetApiKey(Trading212Environment environment)
        {
            return Read(environment == Trading212Environment.Live ? LiveApiKeyVariable : PracticeApiKeyVariable);
        }

        /// <summary>
        /// Returns the API secret for an environment.
        /// </summary>
        /// <param name="environment">The environment.</param>
        /// <returns>The API secret, or <c>null</c> when it is not configured.</returns>
        public static string GetApiSecret(Trading212Environment environment)
        {
            return Read(environment == Trading212Environment.Live ? LiveApiSecretVariable : PracticeApiSecretVariable);
        }

        /// <summary>
        /// Whether both credentials of an environment are available.
        /// </summary>
        /// <param name="environment">The environment.</param>
        /// <returns><c>true</c> when the pair is configured.</returns>
        public static bool IsConfigured(Trading212Environment environment)
        {
            return !string.IsNullOrEmpty(GetApiKey(environment)) && !string.IsNullOrEmpty(GetApiSecret(environment));
        }

        /// <summary>
        /// Reads a setting from the environment, falling back to the nearest <c>.env</c> file.
        /// </summary>
        /// <param name="name">The setting name.</param>
        /// <returns>The value, or <c>null</c> when it is not configured.</returns>
        private static string Read(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            var dotEnvPath = FindDotEnv();
            if (dotEnvPath == null)
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(dotEnvPath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (trimmed.Substring(0, separator).Trim() != name)
                {
                    continue;
                }

                var raw = trimmed.Substring(separator + 1).Trim();
                return raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0]
                    ? raw.Substring(1, raw.Length - 2)
                    : raw;
            }

            return null;
        }

        /// <summary>
        /// Looks for a <c>.env</c> file in the test output folder and its ancestors.
        /// </summary>
        /// <returns>The path of the file, or <c>null</c> when none was found.</returns>
        private static string FindDotEnv()
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            for (var depth = 0; depth < 8 && directory != null; depth++, directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
