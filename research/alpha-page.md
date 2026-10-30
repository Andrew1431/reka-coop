# Alpha release page: outline (not published)

Discussed 2026-10-29. What the mod page (Thunderstore/Nexus/Discord post) covers when the alpha goes out.

## 1. What this is
- Co-op for REKA: 2 players tested, built for up to 4. One player hosts their own save; friends join over Steam.
- The story runs on the host's game; everyone sees the same world, talks, weather and quests.
- Alpha: expect rough edges. Back up your saves before playing (the mod keeps co-op saves apart, but still).
- Everyone needs the same mod version; the game says so if not.

## 2. Roadmap / tested progress
A table kept up to date from playtests: each area marked **Tested and working**, **Works, known issues** or **Not yet**.
- Tested: joining over Steam, names, movement and gear, shared pack, pickups and chopping, building, carrying, tutorial
  through the chicken-leg ritual and the first trip beyond the fog, talks (whoever starts one leads), weather and time,
  the hut (host drives, others ride along).
- Known issues (link to the list): popups on the host only, the hut's walk animation on guests, trees around the hut's
  base on guests, the first talk after travelling.
- Not yet: later maps and quests beyond the first trip, the building tutorial (always skipped in co-op), shared map
  markers, a proper join/host menu.
- Next up / later: menu-based join and host flow, Steam rich presence and friend lobby list.

## 3. Made with AI
- Plain statement: the mod's code was written by an AI (Claude, by Anthropic), directed, playtested and reviewed by
  the author. Say what that means in practice: every feature is playtested before it's marked tested, bugs are real
  and reported the same way.
- Not affiliated with the game's developers.

## 4. Automated usage reports (alpha only)
- During the alpha the mod sends a short report from each session: this is what lets us find and fix problems from
  everyone's play, not just ours. On by default, with a notice on first launch and a setting to turn it off any time.
- What's sent: the mod's own log lines, its out-of-sync reports, mod/game/protocol versions, a random tester id.
- Not sent: saves, raw Steam ids (hashed), Windows user names, IP addresses kept with the report, Steam names
  (replaced with "player N").
- Where it goes and for how long (our own storage; say the retention period once decided).
- This reporting is for the alpha only and will not be part of the polished co-op release.
- A "report a problem" key that sends a note with both players' logs, if built by then.

## 5. Install, join, report
- Install steps (BepInEx 6 IL2CPP + the mod), how to host and invite, how to join.
- Where to report problems and talk (the Discord).
