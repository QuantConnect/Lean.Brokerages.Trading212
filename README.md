# Lean.Brokerages.Trading212

[![Build Status](https://github.com/nimakalantar/Lean.Brokerages.Trading212/workflows/Build%20%26%20Test/badge.svg)](https://github.com/nimakalantar/Lean.Brokerages.Trading212/actions?query=workflow%3A%22Build+%26+Test%22)

Trading 212 brokerage plugin for [LEAN](https://github.com/QuantConnect/Lean).

This repository implements `Trading212Brokerage`, the live brokerage that places and manages orders
through the [Trading 212 Public API](https://docs.trading212.com/api).

## Status

| Capability | State |
| --- | --- |
| Account cash balance | Implemented |
| Account holdings | Implemented |
| Open orders | Implemented |
| Place market / limit / stop / stop-limit orders | Implemented |
| Cancel order | Implemented |
| Order fill and status events | Implemented, by polling (the API has no streaming) |
| Update (amend) order | **Not possible**: the Public API has no replace endpoint |
| Market data | **Not provided by the API** |
| Historical data | **Not provided by the API** |

## Dependency on the LEAN brokerage model

This plugin expects the `Trading212BrokerageModel` added to LEAN by
[QuantConnect/Lean#9982](https://github.com/QuantConnect/Lean/pull/9982) (and its follow-ups
[#9983](https://github.com/QuantConnect/Lean/pull/9983) and
[#9985](https://github.com/QuantConnect/Lean/pull/9985), tracked by
[#9981](https://github.com/QuantConnect/Lean/issues/9981)).

`Trading212BrokerageFactory.GetBrokerageModel` returns that model, so the plugin compiles against a LEAN
that contains it. The plugin also needs a `live-trading212` block in `Launcher/config.json`; see
[LEAN launcher configuration](#lean-launcher-configuration).

## Supported accounts and instruments

The Public API is enabled for **Invest and Stocks ISA** accounts only, and it executes orders in the
account's primary currency only. Trading 212's equity accounts are **long only cash accounts**: there is
no short selling and no margin, which `Trading212BrokerageModel` enforces.

The plugin supports **US-listed equities and ETFs**, which are both `SecurityType.Equity` in LEAN.
The symbol mapping is built from `GET /api/v0/equity/metadata/instruments`, which is the only
authoritative list of what the account can trade, so instruments on other exchanges are reported and
skipped rather than guessed at.

The API is documented as available for the General Invest and Stocks & Shares ISA accounts only, and
Trading 212 states the Public API is **not available for SIPP accounts**.

### Ticker conventions

Trading 212 writes a share class with a separator where LEAN uses a dot, and the separator is not always
an underscore:

| Trading 212 | LEAN |
| --- | --- |
| `AAPL_US_EQ` | `AAPL` |
| `BRK_B_US_EQ` | `BRK.B` |
| `BRK/A_US_EQ` | `BRK.A` |

An underscore is **not** always a share class. Across the 18,559 instruments a real account sees, only 19
US tickers contain a separator: 17 are warrants written `<TICKER>_WAR` and one is a malformed `AVAV_`.
Mapping a warrant to `DSX.WAR` would invent a LEAN symbol that never had data, so a separator is only
treated as a share class when it is followed by exactly one letter. With that rule 6,898 of the 18,559
instruments are supported.

## Configuration

Add the following settings to `config.json` (or the LEAN CLI `config.json`, or the live wizard):

| Key | Required | Description |
| --- | --- | --- |
| `trading212-api-key` | Yes | The API key, used as the Basic authentication user name. |
| `trading212-api-secret` | Yes | The API secret, used as the Basic authentication password. |
| `trading212-environment` | No | `live` (default) or `demo`. Selects `https://live.trading212.com` or `https://demo.trading212.com`. |
| `trading212-order-update-interval-seconds` | No | How often the order update poller runs. Default `10`. The pending orders endpoint allows 1 request per 5 s, so polling faster only produces `429`s. |
| `trading212-order-history-pages` | No | How many historical order pages the poller reads while resolving finished orders. Default `2`. |

Generate the API key and secret from the Trading 212 app; see
[How to get your Trading 212 API key](https://helpcentre.trading212.com/hc/en-us/articles/14584770928157-Trading-212-API-key).
Never commit credentials: `Tests/config.json` only carries empty placeholders.

Two things to watch when creating a key:

- **The environments use different key pairs.** Trading 212 documents that "API Key versions may differ
  between real and demo accounts", so a real-account key is rejected with `401` by `demo.trading212.com`
  and vice versa. Generate one pair from the practice account and another from the real account.
- **The key carries granular permissions** (account data, history, orders, portfolio). Trading needs the
  orders permission; without it an order is refused with `403`.

The keys can optionally be restricted to a set of IP addresses in the Trading 212 account settings, in
which case the plugin can only run from those addresses.

## LEAN launcher configuration

Trading 212's Public API serves no market data and no history, so LEAN must use its own data feed. Add this
block to `Launcher/config.json`:

```jsonc
// defines the 'live-trading212' environment
"live-trading212": {
  "live-mode": true,

  // real brokerage implementations require the BrokerageTransactionHandler
  "live-mode-brokerage": "Trading212Brokerage",
  // Trading 212 serves no market data and no history, so Lean uses its own data feed
  "data-queue-handler": [ "QuantConnect.Lean.Engine.DataFeeds.Queues.LiveDataQueue" ],
  "history-provider": [ "SubscriptionDataReaderHistoryProvider" ],
  "setup-handler": "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
  "result-handler": "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
  "data-feed-handler": "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
  "real-time-handler": "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
  "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler"
},
```

This block must stack on `QuantConnect/Lean#9982`, because the live wizard resolves the brokerage model
through `BrokerageName.Trading212`.

## Order updates

The Public API has no WebSocket, no webhook and no order-update stream, so the plugin observes order state
by polling:

- `GET /api/v0/equity/orders` for the orders that are still working, and
- `GET /api/v0/equity/history/orders` for the fills of the orders that are no longer pending.

Because the historical orders endpoint exposes only a cursor and a ticker filter (no time filter), the
poller reads the most recent `trading212-order-history-pages` pages on each pass. An order that fills but is
not covered by those pages is picked up on a later pass.

## Rejections are never swallowed

A broker rejection reaches the algorithm as an `OrderEvent` with `OrderStatus.Invalid` carrying the broker's
own message, and it is logged. The published OpenAPI description does not enumerate the rejection reasons, so
the plugin never matches on a specific one; it surfaces whatever the broker sends.

Three error envelopes have been observed, and all three are parsed:

```jsonc
// an order rejection, captured from the API: the reason is the type slug, the text is in detail
{ "type": "/api-errors/selling-equity-not-owned", "title": "Error while placing the order", "status": 400,
  "detail": "Selling more equities than owned, owned: 0.0", "traceId": "..." }

// a rate limited request: the message is in errorMessage, the category in context.type
{ "code": "BusinessException", "context": { "type": "TooManyRequests" }, "errorMessage": "too many requests" }

// an older envelope, still handled
{ "code": "SellingEquityNotOwned", "clarification": "SellingEquityNotOwned" }
```

`Trading212ApiException.ErrorCode` reports the reason identifier (`selling-equity-not-owned`) for
diagnostics. `Trading212ApiException.RawBody` always holds the body exactly as sent.

## Order fees

Trading 212 charges no commission, but a trade is not free: there is a 0.15% FX fee on the converted notional
and, on US sales, the SEC and FINRA fees. `Trading212FeeModel` models those, and this plugin reuses it on a
fill, so a live order is priced the same way a backtest is. The fee is reported once, on the fill that
completes the order, because the model prices the whole order quantity.

Verified on the paper account: a 1-share AMZN buy filling at 253.58 in a GBP account reported a fee of
0.38037 USD, which is 0.15% of the notional. The fee is applied to the portfolio in the account currency
(`AccountCurrencyOrderFee`), so it never leaves a balance in the instrument's currency.

## Rate limits

Every endpoint has a documented rate limit, and limits are applied per account, across all keys and IPs.
The client paces requests with one `RateGate` per documented limit and retries `429 Too Many Requests`,
honouring `Retry-After` and `x-ratelimit-reset`.

Some limits are restrictive enough to shape the design:

| Endpoint | Limit |
| --- | --- |
| `GET /equity/metadata/instruments` | 1 req / 50 s |
| `GET /equity/account/summary` | 1 req / 5 s |
| `GET /equity/orders` | 1 req / 5 s |
| order placement | 1 req / 2 s |
| `GET /equity/history/orders` | 20 req / 1 min |

That is why the order update poll interval defaults to 10 s rather than something tighter.

## Development

The solution builds against a LEAN checkout that is a sibling of this repository:

```
dev/
  Lean/
  Lean.Brokerages.Trading212/
```

```bash
dotnet build QuantConnect.Trading212Brokerage.sln
dotnet test QuantConnect.Trading212Brokerage.Tests/bin/Release/QuantConnect.Brokerages.Trading212.Tests.dll
```

The offline tests use a fake HTTP handler, so they need no credentials and no network.

### Online tests

`Trading212BrokerageOnlineTests` runs against the real API and is marked `[Explicit]`, so it never runs in
CI. It needs credentials in the environment, or in a `.env` file at the repository root:

```bash
# practice (paper) account keys, used for the demo environment
TRADING212_PRACTICE_API_KEY_ID=
TRADING212_PRACTICE_SECRET_KEY=

# real account keys, used for the live environment
TRADING212_LIVE_API_KEY_ID=
TRADING212_LIVE_SECRET_KEY=
```

`.env` is gitignored. Select the environment with `TRADING212_ENVIRONMENT` (`demo`, the default, or
`live`):

```bash
TRADING212_ENVIRONMENT=demo dotnet test QuantConnect.Trading212Brokerage.Tests/bin/Release/QuantConnect.Brokerages.Trading212.Tests.dll \
  --filter "FullyQualifiedName~Trading212BrokerageOnlineTests"
```

**Tests that place orders only run against `demo`**, which is paper money; against `live` they self-ignore.
The exception is the rejection test: it sells shares the account does not own, which the broker refuses, so
no order is created and nothing changes. It asserts the account holds none of the instrument first.

The key is IP restricted, so the tests only run from an address the key allows. Expect the run to take
several minutes, because the instruments endpoint allows one request every 50 seconds.

### Toolbox

Trading 212 serves no market data, so there is no downloader and no exchange info to generate. The one
toolbox app dumps the broker ticker to LEAN symbol mapping, which makes a symbol mapping problem easy to
diagnose:

```bash
dotnet run --project QuantConnect.Trading212Brokerage.ToolBox -- \
  --app trading212-instruments --trading212-instruments-output trading212-instruments.csv
```

## Known limitations

- **No market data or history.** `CanSubscribe` is always `false` and `GetHistory` always returns `null`.
  The launcher must be configured with `LiveDataQueue` and `SubscriptionDataReaderHistoryProvider`.
- **No order amending.** `UpdateOrder` returns `false` and raises a `UpdateOrderNotSupported` message,
  because the Public API has no replace endpoint. Cancel the order and place a new one.
- **US-listed equities and ETFs only.** Other exchanges, and warrants, are reported and skipped.
- **Extended hours trading is not exposed.** The API accepts an `extendedHours` flag on market orders but
  the plugin always sends `false`.
- **A fee is only reported on a completed fill.** A partially filled order that is then cancelled reports
  no fee, because the fee model prices the whole order quantity rather than a single execution.
- **Settled cash is not modelled from the broker.** The launcher's T+1 settlement comes from the
  `Trading212BrokerageModel` and its `AccountCurrencyDelayedSettlementModel` (`QuantConnect/Lean#9985`).
- **Subscription validation is not implemented.** `ValidateSubscription` needs a QuantConnect product id,
  which has not been assigned for this module yet.
