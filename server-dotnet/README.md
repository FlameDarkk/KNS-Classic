# KnS server (.NET 8)

The portable `.NET 8` backend serving the game gateway (`/1mhero/gateway.php`), platform endpoints, standalone ID/password accounts (`/account/*`), real-time WebSocket chat (`/chat`), Admin Studio API (`/admin/*`), static web hosting (`KNS_WEB_ROOT`), and the addition-file CDN.

## Run

From the repository root (`work/` is relative to it):

```sh
dotnet run --project server-dotnet/src/KnS.Server -- 8080
dotnet test server-dotnet
```

Environment variables:

| Variable | Effect |
|---|---|
| `KNS_PORT` / `PORT` / `WEBSITES_PORT` | Port when no CLI argument is given (default `8080`). |
| `KNS_BIND` | Listen address (default `0.0.0.0`; use `127.0.0.1` behind a reverse proxy). |
| `KNS_PUBLIC_BASE` | Public URL prefix (`/` or a subpath such as `/kns/`). |
| `KNS_WEB_ROOT` | Packaged web root directory (serves `/` game and `/studio`, moves API under `<base>api/`). |
| `KNS_TRUSTED_PROXIES` | Comma-separated reverse-proxy IPs/CIDRs trusted for `X-Forwarded-For` / `X-Forwarded-Proto` (loopback trusted by default). |
| `KNS_DB` | SQLite file (default `work/kns.sqlite`). |
| `KNS_DATABASE_URL` | `mysql://user:password@host:3306/db`: use MySQL instead (TLS, CA verified). |
| `KNS_DB_POOL` | Most pooled MySQL connections (default `16`). |
| `KNS_SEED_DIR` | Master-data CSVs (default: `seed/` next to the binaries, copied from `server-dotnet/seed`). |
| `KNS_QUIET` | Disables `work/server.log` / `work/requests.jsonl` / traffic log (hosted servers). |
| `KNS_LOG_DIR` | Folder of those logs (default `work`). |
| `KNS_TRAFFIC_LOG` | Write the traffic log even when quiet (bounded by retention). |
| `KNS_TRAFFIC_DAYS` | Days of traffic files kept, today included (default `7`). |
| `KNS_BREAK_AU` | Plain ActiveUser reply for the encrypted `gateway.php`. |
| `KNS_GW_DELAY`, `KNS_BILLING_DELAY` | Seconds of extra latency on gateway / check2 replies. |
| `KNS_SERVE_ADDITION` | List the archived addition files in protocol 48. |
| `KNS_EMPTY_HERO`, `KNS_MUTE`, `KNS_DROP_ALL` | Test switches (no hero, no audio, every drop hits). |
| `KNS_TESTERS_ONLY` | Only testers in the `tester` table (email, attached device, revoke flag) may use the game gateway; others get an error popup and appear in the refused list. Unset = open. |
| `KNS_ADMIN_KEY` | Secret for `/admin/*` endpoints (header `X-Admin-Key`). Unset = admin endpoints return 404. |
| `KNS_RNG_SEED` | Deterministic random draws for reproducible testing. |

## Traffic log

When not running with `KNS_QUIET`, requests and replies are recorded to `work/traffic/<yyyy-MM-dd>.jsonl` (passwords and account tokens are excluded before logging). Game gateway entries are decrypted and include `user_no`, `protocol`, `name`, `errno`, plus diagnostic `unread`, `notes`, and `warnings` fields.

On a hosted server (`KNS_QUIET=1`), set `KNS_TRAFFIC_LOG=1` and point `KNS_LOG_DIR` at persistent storage to enable bounded daily traffic logs.

## Layout

| Project | Holds |
|---|---|
| `KnS.Protocol` | Wire level: the two AES ciphers, lenient base64, client-JSON reading rules (`RequestParameters`, `LenientJson`), and form bodies. No database, no game rules. |
| `KnS.Data` | `GameDatabase` / `DataSession` (one connection + transaction per request), SQLite and MySQL dialects, schema creation and master-data seeding, and repositories. |
| `KnS.Game` | `GameGateway` (decrypt → handler → flat envelope → encrypt), one `IProtocolHandler` per protocol under `Protocols/`, shared services (`Players/`, `Inventory/`, `Fellows/`, `Stages/`, `Chat/`), rules (`Rules/`), and RNG sources. |
| `KnS.Platform` | Platform endpoints, standalone ID/password accounts, and session tokens. |
| `KnS.Server` | ASP.NET Core host: environment settings, `RequestRouter`, `WebFiles`, `/health`, `/addition/`, `/chat`, `/account/*`, `/admin/*`, and request diagnostics. |
| `KnS.Tests` | xUnit unit and in-memory integration tests covering protocol codecs, gameplay lifecycles, chat, accounts, admin endpoints, and web hosting. |

A protocol handler is registered as a keyed service under its protocol number (`GameServiceCollectionExtensions`). Adding a protocol = a value in `GameProtocol`, a handler class with its reply record, and one `AddHandler` line.

## Tester allowlist

Set `KNS_TESTERS_ONLY=1` and `KNS_ADMIN_KEY=<secret>`. The game never sends an email, so a tester is an
email row with a device id attached (`hub_did`, stable per install). Manage it with the admin endpoint
(plain http: the key travels in the clear, so rotate it after use on an open network):

```sh
H="X-Admin-Key: <secret>"; U=http://<host>/admin/testers
curl -H "$H" $U                                                    # testers + refused devices (newest first)
curl -H "$H" -d '{"email":"a@b.com","note":"alpha"}' $U            # add
curl -H "$H" -d '{"email":"a@b.com","device_id":"<hub_did>"}' $U/attach   # after their first (refused) try
curl -H "$H" -d '{"email":"a@b.com"}' $U/revoke                    # cut off (and /restore)
```

A refused device is listed with its attempt count (200 kept); attaching it removes it from that list.
Revoking takes effect on the next request: the player keeps their save and gets it back on `/restore`.

## Feature flags

The `feature_flag` table (`feature_key`, `enabled`, `created_date`, `update_date`; unix seconds) holds
server-side switches, read on every request so a change applies at once. A missing row falls back to the
flag default. Flags: `fourth_fellow` (default on): the rebuild fourth dungeon ally slot; off, a request
`party_slots: 4` is ignored and replies keep the original three positions.

```sh
curl -H "X-Admin-Key: <secret>" http://<host>/admin/flags                                     # list
curl -H "X-Admin-Key: <secret>" -d '{"feature_key":"fourth_fellow","enabled":false}' http://<host>/admin/flags
```
