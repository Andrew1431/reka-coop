# Log reporter for community playtests

Built 2026-10-29 (`mod/RekaCoop/Diagnostics`, `server/log-reporter`; see NOTES.md). Changed by the user: during pre-alpha uploads are on by default, with a one-time notice and a config toggle, instead of opt-in.

Discussed 2026-10-29. Goal: testers' logs reach us automatically, with consent, so issues can be found across everyone's sessions.

## Recommended setup (free)
- A Cloudflare Worker receives gzipped report bundles and stores them in R2. Free tier: Workers 100k requests/day, R2 10 GB. A bundle is ~50-300 KB.
- Pull all reports locally (wrangler / R2 API) and group them by version, warning and desync line.
- Deploying creates a public URL on the user's Cloudflare account: ask before deploying.

## In the mod
- Opt-in prompt on first launch saying exactly what's sent; a config toggle afterwards.
- Bundle: the mod's lines from LogOutput.log, desync.log, mod/game/protocol versions, a random tester id.
- Upload when the session ends or the game crashes; failed uploads stay on disk and retry on the next launch.
- "Report a problem" key (e.g. F8): a one-line note, a timestamp marker, and the host's and guest's logs sent together under one report id.
- Scrub before upload: Steam ids -> hash, Windows user names in paths, IPs, Steam names -> "player N". Saves never, unless a box is ticked.
- Worker: size cap, per-IP rate limit, version check. The upload key in the mod isn't secret; treat every upload as untrusted.

## Alternatives considered
- Sentry free tier: good for crash stacks, but not for reading whole logs. Could be added for crashes only.
- Discord webhook: easiest, but the URL can be pulled out of the mod and spammed, and logs in a chat channel are hard to search.
- Supabase storage: workable, but more setup than a Worker for nothing extra.

The mod page should say what's collected and where it goes.
