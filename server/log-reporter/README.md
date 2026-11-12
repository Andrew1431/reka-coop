# Log reporter (deployed 2026-11-12: https://reka-coop-logs.andrew-hartwig1.workers.dev)

A Cloudflare Worker that takes the mod's playtest reports (gzip JSON, see `mod/RekaCoop/Diagnostics/Bundle.cs`) and keeps them in Workers KV for `KEEP_DAYS` (90). Free tier: 100k requests/day, 1 GB, 1000 writes/day; an upload past the day's writes gets 503 and the mod sends it on a later start. (Written for R2 first; the account has no R2.)

- `src/index.js`: `POST /v1/report` (upload key header, 1 MB body cap, 8 MB unpacked cap, per-IP rate limit 6/min, schema and version check, `MIN_VERSION`/`MIN_PROTOCOL` answer 426); `GET /v1/list`, `GET /v1/object/<key>` for the pull script (Bearer `ADMIN_TOKEN`). Client IPs are used for the rate limit only, never stored.
- Stored at `reports/<mod version>/<day>/<kind>/<time>-<random>.json.gz`, metadata: mod, protocol, kind, tester, report.
- `pull.py`: `pull --url ...` (token in `REKA_LOGS_TOKEN`) downloads new reports to `local/reports/`; `group` prints counts per version and kind, the commonest warnings and errors (numbers folded), desync keys and repairs, and F8 reports; `show <report id>` prints every player's log for one report.

## Test locally (no wrangler needed)
```
node test/test.mjs                       # handler tests against mock KV and rate limiter
node test/serve-local.mjs 8787           # local server, bucket in .local-bucket/, admin token local-admin-token-0123456789
```
Point a test copy at it with `Endpoint = http://127.0.0.1:8787` and `UploadLogs = true` in its `BepInEx/config/com.andrew1431.rekacoop.cfg`.
`npx wrangler dev` also works once wrangler is installed (it simulates KV and the rate limiter).

## Deploy (this publishes a public URL on your Cloudflare account)
```
npx wrangler login
npx wrangler kv namespace create REPORTS   # put the id it prints into wrangler.toml
npx wrangler deploy                        # prints https://reka-coop-logs.<account>.workers.dev
npx wrangler secret bulk secrets.json      # {"ADMIN_TOKEN": "<a long random string>"}; keep it out of git
```
(Piping the token into `wrangler secret put` on Windows stored something else: use `secret bulk` with a file.)
The admin token in use is in `local/reka-logs-admin-token.txt` (not in git). `Reporter.DefaultEndpoint` in the mod is that URL. Raise `MIN_VERSION` in `wrangler.toml` to turn old builds away.

## Text for the mod page
> **Log reports (pre-alpha).** To help find bugs, the mod sends a report to its developer when you quit, when the game crashes and when you press F8 to report a problem: the mod's own log lines, this session's desync log, the mod/game/protocol versions, session details (host or guest, player count, ping, scene, frame rate) and a random tester id. Player and Steam names, Steam ids, your Windows user name, PC name and IP addresses are removed before sending; saves are never sent. Reports go to a Cloudflare server run by the developer and are used only to fix the mod. A notice on first start lets you turn it off; later, set `UploadLogs = false` in `BepInEx\config\com.andrew1431.rekacoop.cfg`.
