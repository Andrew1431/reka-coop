# Talks as one session on every copy (design, 2026-11-10)

Read from interop stubs (signatures, fields, compiler-generated closure/coroutine classes) in
`scratchpad/acs-1032/`, the older dumps beside it, `Sync/DialogueSync.cs`, `TalkCamera.cs`, `ActingPlayer.cs`,
NOTES.md. Method bodies are native: everything marked **(inf)** is inferred from names/fields and must be probed (section 5).

## 1. How the game runs a dialogue

### Entry points (DialogueUILogic)

| Method | Params | Role |
|---|---|---|
| `RequestStartDialogue` | lines, names, titles, `Action onFinished`, `Nullable<DialogueBranch> afterDialogue`, isPassive, isStayInUI, emotions, `Action onStarted` | Public entry. Closure `<>c__DisplayClass65_0 { Possession lootToAward }` → it wraps a loot award around the finish **(inf)**. Queues into `queue` (8-tuple of exactly these args) while talking / behind a passive line; `IsDuplicateDialogue(lines,names)` drops repeats **(inf)**. |
| `StartDialogue` (9 args) | same as above | Stores `onCurrentDialogueFinished`, `pendingAfterDialogue` **(inf)**, then the 7-arg one. |
| `CloseUIAndStartDialogue` / `StartDialogue` (7 args) | lines, names, titles, onFinished, isPassive, emotions, onStarted | Start without branch. |
| `SetDialogue` | lines, names, titles, isPassive, emotions | Writes `currentDialogue`, `namesOfPersonTalking`, `titlesOfPersonTalking`, `emotionsOfLines`, `currentDialogueIsPassive`; resets `indexInDialogue`; bumps `dialogueGeneration` **(inf)**. |
| `ActivateTalking(onStarted)` | | Requests InGameUI (NOTES: this is what stops walking), shows root, fires `OnStartDialogue` UnityEvent, `onStarted` **(inf)**. |
| `TryPopQueue` / `TryPopQueueOnGameModeChanged` | | Next queued request after an end **(inf)**. |
| `DialogueFunctions.StartDefaultDialogue` | (String or LocalizedString) text, action, isPassive, afterDialogue, dontAutoStopTalkingVisuals, isSecondaryCharacter, lineEmotions, onStarted | NPC-side wrapper: closure keeps all args; calls `DialogueCamera.Activate(...)` then `RequestStartDialogue` **(inf, NOTES: Activate seen right before the line)**. |
| `NPC_DialogueManager.ContinueDialogue(index, List<LocalizedString> dialogue, onFinished)` | closure `{index, dialogue, onFinished}` | A rumor answer is said **line by line, each as its own dialogue, chained by onFinished** **(inf, strong: closure shape)**. So one "talk" may be several generations. |
| `SayLineActively(line, onFinished, representsDialogueOptionsAfter)` / `SayLinePassively` | | One line, then (optionally) back to the options via the branch. |

### Per line

1. `QueueNextLine(isPassive)`: `indexInDialogue++`, closure `<>c__DisplayClass84_0 { gen, whenRevealed }` → `talker.Reveal(text, onFinished, isPassive, name, title)` (DialogTextRevealer, Febucci typewriter, `WatchForSkip` coroutine). The closure's `gen` drops a reveal that finished after the dialogue changed **(inf)**.
2. Revealed, not last: active → `WaitForPlayerInputToQueueNextLine` → coroutine `WaitForPlayerInputThenDoSth(action=QueueNextLine)` (fields `hash`, `gen`, `t`, `continueHeldTimeout`: waits for AdvanceDialogue past `AdvanceInputCooldown`, aborts if gen/hash changed, honours `AutoAdvanceForTesting`) **(inf)**. Passive → `WaitForDelayToQueueNextLine` → `WaitDelayThenDoSth(action, holdWhileInGameUI)` (`countdown` = `delayForPassiveDialogue`).
3. Last line revealed: `OnLastLineRevealed(isPassive)`, closure `<>c__DisplayClass87_0 { isPassive, branchAction }` + `87_1 { gen }`: waits (input or delay), then `RunCompletionThenBranch(branchAction)` when a branch is pending, else `StopTalkAndCallOnFinished` **(inf; NOTES confirms the branch path ends in `RepresentsDialogueOptions`)**.
4. Watchdog: `WatchdogNoteProgress`, `watchdogLastProgressUnscaled/Frame`, `lastLoopHeartbeatFrame`, `WATCHDOG_TIMEOUT_SECONDS/FRAMES` → `WatchdogForceRecover` (closes a stuck talk) **(inf)**.

### End and callbacks

| Exit | Callbacks |
|---|---|
| `StopTalkAndCallOnFinished` → `StopTalkingAndCloseUI` + `CallOnFinished` | `onCurrentDialogueFinished` (incl. loot wrapper) |
| `RunCompletionThenBranch(branch)` → `CallOnFinished` then branch, UI kept | onFinished + `DialogueBranch.action` (options again, follow-up talk) |
| `EndCurrentDialogue`, `SkipToLastLine` | unknown whether they call onFinished |
| `ForceCloseDialogue` | none expected **(inf; mod uses it, no side effects reported)**; bumps generation (measured +2 per mod line = Set + ForceClose) |
| `DialogueOptionsPanel.OnOptionPicked(i)` / `CloseAndEndDialogue` | option callback / goodbye |
| `continuationGraceUntilFrame`, `CONTINUATION_GRACE_FRAMES` | a dialogue started within N frames of the last one's end keeps the UI (and camera) as a continuation **(inf)** |

### Camera (DialogueCamera)

- `Activate(head, speakerName, family, listenerHead)` stores a **proposal** (`SpeakerProposal { head, listenerHead, speakerName, family, generationAtProposal, proposedAt }`); `DropStaleProposal` ages it out.
- `OnDialogueLineStarted(speakerName, lineText, isPassive, dialogueGeneration)` per line → `DialogueCameraSessionRules.Classify(hasProposal, proposalGen, proposalName, sessionLive, sessionGeneration, isPassive, lineGeneration, lineSpeakerName)` → `Ignore | Commit | Boundary | Foreign`.
  - Commit → `Commit(gen, text, emotion)` → `BeginSession(hint, head)` → `SolveAndApply("session start", sessionStart:true)` = **opening shot**.
  - Same generation, live → `OnOwnLine` / `OnDialogueBeat` / `ChangeSetup` / `TryReactionShot`: **the shot progression** (`DialogueShotStep` Loose→Medium→Close→Tight, `WidestStepForLine(opening)`, `StartLinePerformances(..., sessionLine)`) **(inf)**.
  - New generation → Boundary (new session, opening again) unless continuation **(inf)**. This is fault 2: every mod line is a new generation.
- `ShouldRelease(uiExists, isTalking, optionsShowing, uiGeneration, sessionGeneration, hasPending, idleHeld, grace)`: an open options panel holds the session; idle past grace releases.
- Solve inputs: `playerHead` (a `TransformReference`, the same `PlayerHead` asset ActingPlayer re-points), `RekaHead()`, static `PlayerRoot()`, static `FindPlayerMovement()`, `RekaRootPosition()`, `BuildListenerFrame()`, `SpeakerOutOfRange(head, out d)`. Failure → `RefuseUnvalidated` → `EnterPlayerCamera` ("nothing validated"), `_retryPending` → `RetryPendingSolve`.
- **Host "nothing validated" when acting for a guest (inf):** `StartDefaultDialogue` runs inside the swapped AI update, so `Activate`/first `OnDialogueLineStarted`/`SolveAndApply` read `playerHead` = guest puppet head, while `FindPlayerMovement()`/`RekaRootPosition()` read the host's body (not swapped, and not yet gathered: Gather runs at the host's next Tick). Listener frame and Reka root disagree by metres → no candidate validates. Retries later see the host body beside a speaker who is facing the puppet.

Note: the pure statics `DialogueCameraSessionRules.Classify/ShouldRelease/ProposalCommits` can be called from a dev command to read their answers directly.

## 2. Design

### Principle

A **talk** on the host = one or more host dialogues (generations). For each host generation, every other copy runs **one local dialogue with all of its lines**, started through `RequestStartDialogue` with no callbacks, and moves it on only when the host's index moves. Host-chained generations (rumor lines, branch follow-ups) are replayed as the game itself chains them: finish the previous via its own last-line action, start the next in the same frame (continuation).

### Messages (MsgType.Dialogue, protocol 59)

| Kind | Dir | Payload | When |
|---|---|---|---|
| `Start` (new, replaces Line + Passive) | H→all | talkId (= host `dialogueGeneration`), leader, flags (passive, hasBranch = `pendingAfterDialogue.HasValue`, continues = started while `_talking` / within grace), from (first index included), n, n × (text, name, title, emotion), speaker (headPath, name, family, listenerPath or "") | Host Tick sees a new generation (read from `currentDialogue`, `namesOfPersonTalking`, `titlesOfPersonTalking`, `emotionsOfLines`; no new hook). Re-sent to a joiner with from = current index. |
| `Index` (new) | H→all | talkId, index | Host `indexInDialogue` changes. |
| `Next` | leader guest→H | talkId, index | Leader's continue on its own box. (Same as now, talkId = host gen.) |
| `Camera` | H→all | talkId, index, headPath, name, family, listenerPath | Host `DialogueCamera.Activate` postfix (no head-only dedupe: a speaker change mid-generation must go out). |
| `Options` / `Chosen` / `Pick` / `Hover` / `Skip` | as now | + talkId on Options and Skip | unchanged logic |
| `End` | H→all | talkId, reason (finished, branch, closed, forced) | host talk over (0.5 s quiet rule kept) |
| `State` | H→all | phase, leader, talkId, index | 1 Hz, reconcile |
| `Gather` | H→all | as now | **moved earlier**: at `Admit`/`NoteRequest` time for guest-led talks (before the line starts), still at Begin for host-led |
| `Cam` | | hut drive only (and dev fallback) | |

Size: a 10-line generation ≈ 2-4 KB, ReliableOrdered (LiteNetLib fragments).

### Each copy

| Copy | Starts | Advances | Continue press |
|---|---|---|---|
| Host, leads | game's own | game's own | game's own; Tick sends Index |
| Host, follower (guest leads) | game's own (with callbacks) | `_waiting.Invoke()` on the leader's Next (existing `Continue()`) | swapped to Nothing (existing) |
| Guest, follower | `Session.Start`: `cam.Activate(copyHead, name, family, listener)` then `ui.RequestStartDialogue(lines[from..], names, titles, onFinished:null, afterDialogue: hasBranch ? DialogueBranch(NoOp) : null, isPassive:false, isStayInUI:hasBranch, emotions, onStarted:null)` **in one frame**; record local gen after the call | on Index k: invoke the held action (below) once per step until local index + from == k | blocked (TalkInputBlock, Nothing action) |
| Guest, leader | same start | same (host's Index drives it) | the held action is **not** run; send `Next(talkId, index)` instead → no double advance |

**Programmatic advance (the core):**
1. **Rank 1 – hold the coroutine's action.** Prefix on `WaitForPlayerInputThenDoSth(ref action)` (exists) on a follower/leader guest: keep `action` with (local gen, index), replace with `Nothing` (follower) or `SendNext` (leader). On Index, invoke the held action: it is the game's own `QueueNextLine` mid-talk and the `OnLastLineRevealed` closure on the last line, so the session ends or branches the game's way. Same mechanism the host already uses for a guest leader (`DialogueSync.Continue`). Same prefix on `WaitDelayThenDoSth` for subtitle-mode copies.
2. **Rank 2 – call it directly.** `ui.QueueNextLine(false)` mid-talk; last line: `ui.StopTalkingAndCloseUI()` or `RunCompletionThenBranch(NoOp)`. Fallback if the held action misbehaves outside its coroutine.
- Line not yet revealed when Index arrives: `talker.typewriter.SkipTypewriter()` then advance once the action is held (same as `_nextFor` on the host). Jump > 1: repeat skip + advance per frame; > 3 behind: restart the generation from the host index (one camera restart, rare).
- While holding: write `ui.watchdogLastProgressUnscaled = Time.unscaledTime; watchdogLastProgressFrame = Time.frameCount` each frame if probe 6 shows the watchdog fires.

**Camera per copy:** `TalkCamera` stream dropped for talks (PLAN: camera is per player). Each copy's own DialogueCamera frames its own Reka (gathered beside the leader) and its copy of the speaker, across one session = the game's normal progression. Mid-generation speaker change: the follower applies the queued `Camera` proposal for index k in the same frame it invokes the advance to k. `TalkCamera` stays for the hut. Keep a dev toggle `dlgcamstream` to re-enable the stream for talks.

**Host "nothing validated" fix (ranked):**
1. ActingPlayer suspend around the camera: prefixes on `DialogueCamera.Activate`, `OnDialogueLineStarted`, `Commit`, `SolveAndApply`, `RetryPendingSolve` restore the host's references (`ActingPlayer.Suspend()`/`Resume()`, depth-safe), so the host camera always frames the host's own Reka. Plus Gather before the talk starts (above), so that Reka is beside the guest and the speaker.
2. If 1 still refuses (speaker turned to the puppet, Reka off-axis): pass the puppet's head as the 4th `Activate` arg (listenerHead) on the host, or fall back to the guest's stream for host-as-follower only (keep `TalkCamera` receive path when `Role == Host && WatchingTalk`).

## 3. DialogueSync pieces

| Fate | Pieces |
|---|---|
| **Delete** | `Follow`, `ShowPending`, `FollowNow`, `CloseLine` (old form), `StopFollowing`, `_pending/_shownAt/_shownGen/_lineToken`, `_passiveDelay` + `delayForPassiveDialogue = 3600`, `SendPassive` + `Kind.Passive`, `Kind.Line` (send in Tick and case in OnMessage), `PullIn`/`LetGo`/`_pulledIn/_speakerHead/_speaker`, `_hostLine`, `TestStuck` (rewrite), `TalkCamera` gating by `LeadingTalk/WatchingTalk` |
| **New** | `FollowSession` (talkId, localGen, from, n, held action + its index, queued camera proposals, mode Active/Subtitle), `SendStart`, `OnStart`, `OnIndex`, `Advance`, `EndLocal(talkId)`, `SendNext`, `ActingPlayer.Suspend/Resume` + 5 camera prefixes, `WaitDelayThenDoSth` prefix |
| **Change** | `Tick` (host: Start on new gen, Index on index change, End with talkId), `OnWaitingForContinue` (guest: hold + Nothing/SendNext), `OnMessage`, `OnRequest.Next` (talkId), `OnCameraActivated` (index, family, listener; no dedupe), `SendState`/`Reconcile` (talkId + index → catch up or close), `ShowOptions` (instead of `CloseLine`: run the held last-line action so the session goes to its no-op branch, then `panel.Show`), `ClosePanel`, `OnSkipped`/`ApplySkip` (key on talkId/local index), `LeadingTalk/WatchingTalk/InTalkHere` (guest: session != null), `Attach.PlayerLeft` (re-arm host continue), `Gather` call site, `CloseStrayLine` (talkId based; HutSync calls it), `Kind` enum |
| **Keep** | `Personal`, `NoteRequest`, `Begin`, `Gather/Ground/MoveHere`, `Admit/SwitchOn`, `DeferDefault/RunPendingDefault/RequestDefaultTalk`, `OnOptionsShown`, `OnOptionPicked`, `WatchLeaving`, `TickHover/Highlighted`, `Continue`, `Show` (only for `ShowLocal` notices), `ShowLocal/ShowAfterTalk`, `Panel`, `Describe`, `Test/TalkToNearest/TestOptions`, all `DialogueHooks` |

Estimate: ~150 lines out, ~220 in; TalkCamera −10, ActingPlayer +25.

## 4. Edge cases

| Case | Behaviour |
|---|---|
| Options | Host: last line → branch → `RepresentsDialogueOptions` → panel (`Options`). Follower: its session sits on the last line with the action held; on `Options` run the held action (→ `RunCompletionThenBranch(NoOp)`, UI kept, camera held by `optionsShowing`), then `ShowOptions` read-only or choosing (as now). |
| Branch follow-up (answer after a pick, rumor line chain) | Host starts a new generation → `Start` with continues=1. Follower: close panel (Chosen), if a session is still held at its last line run that action, then in the same frame `RequestStartDialogue` the new one (set `continuationGraceUntilFrame = frameCount + CONTINUATION_GRACE_FRAMES` first if probe 5 says it is needed). |
| Passive lines (monologues) | `Start` with passive=1: follower runs it passive with its own `delayForPassiveDialogue` timing, no index sync, no camera; skipped when a real talk is on screen (current guard). Ends on `End` or the next `Start`. `IsDuplicateDialogue` stops double copies of guest-local passive StandaloneDialogues **(probe 7)**. |
| Talk ends early (closeoptions, ForceClose, watchdog, leader Pick −1) | Host `End(reason)`; follower `EndLocal`: `queue.Clear()` + `ForceCloseDialogue()` if local gen is that talk's, panel closed. No callbacks exist on followers. |
| Bystander (not gathered: no ground, other interior, > 35 m from its copy of the speaker) | Subtitle mode: same `Start`, run passive with `delayForPassiveDialogue = 3600`, advanced by the held `WaitDelayThenDoSth` action on Index; no `Activate`, no input lock. |
| Join mid-talk | On the joiner's Ready the host sends `Gather` (joiner only), `Start` with from = current index (lines sliced), the last `Camera`, and `Options` if open. Local index + from = host index. |
| Leader leaves | `PlayerLeft`: leader = host; host re-arms its own continue: `ui.StartCoroutine(ui.WaitForPlayerInputThenDoSth(_waiting))` (prefix now keeps the real action), `State` tells guests; an open panel becomes the host's to pick. A guest leader who becomes a follower drops `SendNext` on its next held line. |
| Host leaves | disconnect path: `EndLocal`. |
| Lost/late message | `State` (1 Hz): talkId differs → `EndLocal` + wait for/ask `Start` (re-sent on request kind `Resync`); index ahead → catch up; Idle 1.5 s → `EndLocal`. |

## 5. Probes (most of the design depends on the first)

1. **What a talk is in generations.** Host, log per line: `DialogueGeneration`, `indexInDialogue`, `currentDialogue.Length`, `pendingAfterDialogue.HasValue`, `continuationGraceUntilFrame`, `Time.frameCount`, camera `CurrentShotName`. Run a villager rumor answer (`ContinueDialogue`), the door talk, a StandaloneDialogue chain, options → answer. Look for: one line per generation in rumors, and whether the host's own camera says "session start" at each new generation (if it does not, find what keeps the session: grace frames, same speaker).
2. **Held action advances one session.** Guest, dev `dlgadvance`: start `talktest` (3 lines) with the prefix holding the action, invoke it from the console. Look for: box stays up, `DialogueGeneration` constant, index +1, shot name changes without "session start", last invoke closes with no exception. Repeat with rank 2 (`QueueNextLine(false)`).
3. **Follower camera start and speaker change.** Guest: `Activate(head,name,family,listener)` + `RequestStartDialogue` in one frame → `IsLive`; a second `Activate` with another speaker just before an advance → what `Classify` returns (call `DialogueCameraSessionRules.Classify` with the live values from a dev command). `ShouldRelease` with options showing.
4. **Host "nothing validated".** Host, guest-led door talk: prefix log on `SolveAndApply`/`RefuseUnvalidated`/`EnterPlayerCamera`: `RekaHead()`, `PlayerRoot()`, `FindPlayerMovement()`, `playerHead.transform`, `ActingPlayer.Current`, `SpeakerOutOfRange(head, out d)`. Then try the Suspend prefixes + early Gather.
5. **Continuation without blink.** Guest: dialogue A with `DialogueBranch(NoOp)`; at its last line run the held action and `RequestStartDialogue(B)` in the same frame, with and without writing `continuationGraceUntilFrame`. Look for: root stays visible (`IsUiRootVisible`), camera not released/restarted.
6. **Watchdog.** Hold a follower line 90 s. Read `WATCHDOG_TIMEOUT_SECONDS`; look for `WatchdogForceRecover` (postfix log) and whether writing `watchdogLastProgress*` stops it.
7. **Queue/duplicate rules.** `RequestStartDialogue` while `IsTalking` (queued or dropped?), the same lines twice (`IsDuplicateDialogue`), and while a passive line is up (`QueuedBehindPassiveTotal`).
8. **Text and loot.** Does `currentDialogue` on the host still contain markup the 65_0 loot closure parses? Start a follower dialogue from a loot-giving line and compare the guest's pack before/after.

## 6. Stages (each tested host + guest, regress `door`/`yard` both leaders at the end of each)

1. **Probes 1-4 as dev commands** (`dlgprobe` extensions, `dlgadvance`, `camclassify`, camera solve log). No protocol change.
2. **Host sends `Start`/`Index` beside the old `Line`** (protocol 59). Guest only logs and diffs against the lines it shows. Proves the payload.
3. **Guest follower runs one session** (host leads). Old Line path off for non-leaders. Check: no blink, guest shot names progress, generation constant per host generation.
4. **Guest leader** (`SendNext`, advance on Index). Guest-led door talk: host advances via `Continue`, no double steps.
5. **Options and chained generations** (no-op branch, continuation). Villager talk with options and a rumor answer, both leaders.
6. **Camera per copy**: per-line `Camera` proposals on followers, `TalkCamera` off for talks, host Suspend + early Gather. Guest-led: host log has no "nothing validated".
7. **Passive, subtitle mode, early end, State with talkId.** `closeoptions`, ForceClose on host, monologue at the Lutki.
8. **Join mid-talk, leader leaving; delete the old code** (section 3). Full regress both ways.

## Biggest risk

Probe 1: if villager talks are one line per generation and the game's own camera only stays in one session through a continuation rule we cannot read, then "one session" is really "the same chain of generations as the host, started within the same grace window", and followers must reproduce the game's chaining timing exactly (stage 5); a late message would make the follower's camera restart anyway.

## 7. Probe results (2026-11-10)

Measured in the running game, host `play` + guest `client` on one PC, fixtures `talisman-1.0.32` (twice) and
`pre-door-1.0.32`, game 1.0.32. Tooling: `Dev/DialogueProbe.cs` (dev build only, every patch applied by command, none at
load) and two lines in `Dev/Commands.cs`. No sync behaviour was changed. Commands: `dlgtrace`, `dlgstate`,
`dlghold on|off|rearm`, `dlgadvance held|queue|stop|stopfin|branch|end|force|skip [cam2]`, `dlgstart`, `dlgchain`,
`dlghead`, `dlgpress`, `dlgdup`, `dlgclosepatch`, `camrules`, `campatch [2]`, `camsuspend`. Log prefixes: `[dlgtrace]`
(box + camera on every change), `[dlgwait]` (which action a line waits to run), `[dlgcam]` (the camera's own decisions),
`[dlghold]`, `[dlgclose]`.

### Probe 1: what a talk is in generations

Ran (host log): the four tutorial talks with the old woman, a StandaloneDialogue, and a tutorial villager with options.

| Talk | Started by | Generations | Lines | Branch | Camera |
|---|---|---|---|---|---|
| Door (piece 001) | her AI, for the guest | 1 (gen 1) | 5 | no | Activate once, BeginSession once |
| Yard (piece 002) | interact | 1 (gen 3) | 13 | no | once |
| "I'm in the house" (piece 004) | yard trigger | 1 | 1 | no | none at all (no Activate, `Ignore`) |
| Indoors (piece 005) | interact | 1 (gen 3) | 21 | no | once |
| StandaloneDialogue `DontGoThereWithHouse_01` | dev `standalone` | 1 (gen 5) | 2 | no | none |
| Villager Urszula: greeting | `say` (SaySomething) | 1 (gen 7) | 1 | yes | Activate, BeginSession |
| ... options, pick 0: answer | options callback | 1 (gen 9) | 1 | yes | Activate again, Commit, **no** BeginSession |
| ... Goodbye | options | 1 (gen 15 after 13) | 1 | no | Activate, Commit, no BeginSession |

- Generation numbers: +1 when a dialogue starts, +1 when it ends (`StopTalkAndCallOnFinished`, `ForceCloseDialogue`,
  `StopTalkingAndCloseUI`); +1 only when the next one starts through a finished branch (see probe 5). Villager steps
  therefore go 7, 9 / 11, 13, 15.
- What a line waits to run (`[dlgwait]`): mid-talk `DialogueUILogic.QueueNextLine`; last line without a branch
  `DialogueUILogic.StopTalkAndCallOnFinished` (the method itself, no closure); last line with a branch
  `<>c__DisplayClass87_0.<OnLastLineRevealed>b__1`. `pendingAfterDialogue` is emptied when the last line is revealed
  (it moves into that closure). `continuationGraceUntilFrame` is -1 all through a talk and becomes frame + 4 when a
  branch has run (`CONTINUATION_GRACE_FRAMES` = 4).
- Camera per line, read at `OnDialogueLineStarted` with the live values:
  `LineStarted ... gen=3 -> Boundary (proposal=False live=True sgen=3)` for every line after the first. In the 21-line
  talk the host's shots went `Player camera` (refused, see probe 4), `Opening wide OTS`, `OTS-Medium`, `OTS-Close`,
  `OTS-Medium`, `Single-Close`, `OTS-Medium`, `OTS-Close`, `OTS-Medium`, reasons `policy change` (about every second
  line) and `reaction over`. One `BeginSession`, one `session start`.
- The rules, read from the pure statics (`camrules`), complete for non-passive lines:
  - `Commit` if and only if there is a proposal with the line's speaker name whose generation differs from the line's
    (it was made before `RequestStartDialogue` raised the generation). Live or not, any session generation.
  - else live and session generation = line's: `Boundary`. This is the ordinary next line of the session, not a restart.
  - else live and another generation: `Foreign`. Not live: `Ignore`. Passive lines never commit.
  - `ShouldRelease`: talking with ui generation != session generation: true at once. Not talking, no options, no pending
    proposal: true after `releaseGraceSeconds` = 0.1. Options showing: never. Pending proposal while idle: never.
- A `Commit` on a live session does not restart it: no `BeginSession`, the next solve is `policy change`. Seen on the
  host (villager answer: `OTS-Close "policy change"`), and on a guest in today's code for all 21 one-line dialogues of
  the indoor talk (generation 3, 5, 7 ... 43; `BeginSession` once; shots `Opening Single-Loose`, `Single-Medium`,
  `Single-Close`, `Single-Medium`, `Single-Tight` ...).
- Not found in reach: a rumor answer of several lines (`ContinueDialogue`), a StandaloneDialogue that chains. The
  tutorial villager's answers are one line each. So "one line per generation in rumors" is still unmeasured; what is
  measured is that a new generation of the same speaker keeps the session.

Conclusion: a story talk is one generation with all its lines; a villager talk is one generation per greeting/answer,
joined by the options panel. The camera session survives a new generation as long as the speaker is proposed again in
the frame the new dialogue starts and the box was not idle for 0.1 s (an open options panel does not count as idle).

### Probe 2: the held action advances one session

Ran on the guest: `dlghold on`, `talktest` / `dlgstart n`, `dlgadvance held` per line; again with `dlgadvance queue`.

- Rank 1: `gen=45 idx 0 -> 1 -> 2`, box up (`root=True`) throughout, last call ran `StopTalkAndCallOnFinished`:
  `talk=False gen=46`. No exception in 30+ advances. With a camera: generation constant, `Opening OTS-Close` then
  `OTS-Close "policy change"`, one `BeginSession`.
- Rank 2: `QueueNextLine(false)` does the same mid-talk; called on the last line of a dialogue without a branch it
  ends the dialogue (`idx=3/3 talk=False gen+1`). `RunCompletionThenBranch(noop)` on a branch dialogue's last line does
  what the held closure does.
- Last line of a branch dialogue: the held closure runs the branch action, sets the grace frame and **leaves the box
  up on the last line** (`talk=True root=True`, camera live), for as long as nothing else happens (watched 10 s, with
  and without an options panel). It needs a following dialogue, options, or a close.
- **Crash (guest died, AccessViolation in `il2cpp_runtime_invoke`)**: a branch built as
  `new Il2CppSystem.Nullable<DialogueBranch>(new DialogueBranch(action))` holds a wrong action pointer; the game crashes
  when the last-line closure runs it. Built field by field it is fine: `new Nullable<DialogueBranch>()`, then at the
  unboxed pointer `hasValue` (byte, offset 0) = 1 and `value.action` (pointer, offset 8) = the action (offsets read from
  the class at run time; `DialogueProbe.MakeBranch`). Also: the `pendingAfterDialogue` getter throws
  NullReferenceException when there is no value.
- **Wait routines that were stepped past stay alive.** `coroutinesActive` goes 1, 2, 3 ... per line advanced from
  outside and back to 0 at the end. With stand-in actions that log, one simulated press (`dlgpress`) after two outside
  advances ran the stand-ins of lines 2, 1 and 0 in one frame.
- **A line nobody waits on closes itself.** After a press ran a stand-in that does nothing, `coroutinesActive` = 0 and
  the box closed 2 to 11 frames later (`talk=False gen+1`), not through `WatchdogForceRecover` and not through
  `StopTalkingAndCloseUI` (both logged, neither ran). With `dlghold rearm` (the stand-in starts
  `WaitForPlayerInputThenDoSth(standIn)` again in the same call) the box stayed up through repeated presses.
- Running a held action after its dialogue is gone leaves a broken box (`talk=False root=False reveal=True`, index
  moved). The held action must be checked against (local generation, index) before every use.
- A press while the line is typing only finishes the typing; the wait starts in that frame and does not take the same
  press.

Conclusion: yes, "advance a follower by running the held action" works, and the held action (the `action` argument of
`WaitForPlayerInputThenDoSth`) is the right one because it is also the correct last-line step. `QueueNextLine(false)`
is a safe fallback mid-talk only.

### Probe 3: follower camera start, speaker change, options

Ran on the guest: `dlgstart 4 cam alt` (camera given her head, then `RequestStartDialogue`, one frame; lines named
alternately "Old Woman" / "Other"), `dlgadvance held cam2` (a second `Activate` for another head just before an
advance), `dlgchain 2 cam other`, `talkoptions` on a held branch line.

- `Activate` + `RequestStartDialogue` in one frame: `live=True sgen=50 shot="Opening OTS-Close" reason="session start"`
  in that same frame.
- A line with another speaker name inside the generation: nothing changes (same shot, `spk="Old Woman"`).
- A proposal made inside the generation (`pgen=50`, line gen 50): `Boundary`; the session keeps its head, the proposal
  stays pending (`prop=True`) until the dialogue ends. It never commits.
- A new generation with another head and name: `Commit`, no `BeginSession`,
  `shot="Group" reason="group establish (speaker change, 3 participants)"`, `spk="Other"`.
- A new generation with no proposal: `Release "foreign dialogue on screen"` in that frame; no camera for that dialogue.
- Options: the panel open on a held last line kept `live=True` (6 s); `ShouldRelease` is false for every row with
  options showing. In the real villager talk on a guest leader the session also stayed live across panel and answer.
- End: `Release "dialogue UI idle"` 5 to 7 frames after the box closes. A new start 0.5 s after an end is a new session
  (`BeginSession`, `Opening OTS-Medium "session start"`).

### Probe 4: the host's camera in a guest-led talk

Ran: guest-led door talk (her AI starts it), yard talk and indoor talk (interact intent), villager talk (`say` on the
guest), with `campatch` on the host. Then the villager talk again with `camsuspend on`.

At the session-start solve, inside the swap (`acting=1 inAi=True`), in all four:
`playerHead=nose@(puppet) rekaHead=nose@(puppet) playerRoot=RekaCoop_Puppet_1 pm=null rekaRoot=(puppet position)`.
Everything the camera reads follows the swap together; only `FindPlayerMovement()` is null. The solve validated every
time, framing the guest's puppet as Reka:

| Talk | First solve on the host | Next |
|---|---|---|
| Door, speaker 10.8 m | `Opening wide OTS`, true | line 2: `"Reka moved"` (host gathered) `Held Single-Medium`, true |
| Yard, 2.8 m | `Opening OTS-Medium`, true | `"Reka moved"` `Held OTS-Medium`, then `Single-Medium`, `OTS-Medium` ... |
| Indoors, 4.2 m | `Opening OTS-Close`, true | `"Reka moved"` `Held Single-Medium`; three `policy change` false (shot kept); `Single-Tight` |
| Villager, 1.7 m, host 30 m off | `Opening OTS-Loose`, true | `Single-Medium "policy change"` |

"nothing validated" was seen only twice: (1) **host-led** indoor talk, first frame: `RefuseUnvalidated "session start"`,
`Player camera`, then the game's own `"retry from the player camera"` 51 frames later gave `Opening wide OTS`;
(2) when I put the host on the hut roof mid-talk: refused at `"Reka moved"`, at every `"retry"` (about every 60 frames)
and at every line, shot name unchanged. `SpeakerOutOfRange` was false everywhere, including 29.7 m.

With `camsuspend on`: the prefixes do restore the host's references (`playerHead=nose@(165.1,6.4,-60.1)
pm=Reka_Body_Base_01`), and the first solve then runs from where the host stands before Gather: `Opening wide OTS`
from 29.7 m, then a cut once the host has been moved.

Conclusion: the cause given in section 1 is wrong. The swap is consistent and the host validates. Refusal depends on
where the host's Reka stands relative to the speaker (line of sight), so the place to look for the reported symptom is
the Gather spot (1.6 m to the side, grounded by raycast: indoors that can be behind a wall). I could not reproduce a
whole talk refused. Suspend makes the first shot worse, not better.

### Probe 5: continuation without a blink (guest, `dlgchain`)

- No branch, held `StopTalkAndCallOnFinished` then `RequestStartDialogue` in the same frame: generation +2, camera
  session kept (`Commit`, no `BeginSession`), but `root=False` for about 11 frames (0.18 s). Writing
  `continuationGraceUntilFrame` first changes nothing.
- Branch dialogue, held closure then `RequestStartDialogue` in the same frame: generation +1, **`root=True` throughout**,
  camera kept, next shot `Single-Tight "policy change"`.
- The same 8 to 14 frames of `root=False` open every dialogue, on the host too (also the game's own options answer).
  Within one generation the root stays visible from line to line. Today's guest shows it on every line: that is the blink.

### Probe 6: watchdog

`WATCHDOG_TIMEOUT_SECONDS` = 2, `WATCHDOG_TIMEOUT_FRAMES` = 20, `AdvanceInputCooldown` = 0.15, `delayForPassiveDialogue`
= 2. A held line with its wait routine alive stayed 30 s (`wdAgo=0.0s` every read); host lines held for a guest stay for
minutes. No write to `watchdogLastProgress*` is needed while a wait routine runs. What closes a line with no wait
routine (probe 2) was not identified.

### Probe 7: queue and duplicates (guest, `dlgdup`)

- The lines on screen requested again: dropped (`IsDuplicateDialogue` true, `q=0`).
- Another dialogue while talking: queued (`q=1`); the same one requested again: dropped (the check covers the queue). It
  started by itself when the current one ended.
- An active request while a passive line is up: queued, `QueuedBehindPassiveTotal` 0 -> 1, started after the passive
  line's time ran out. A second passive duplicate: dropped.

### Not measured

Probe 8 (loot). A several-line rumor answer and a chaining StandaloneDialogue (none in reach in the tutorial).
`WaitDelayThenDoSth` held for subtitle mode. Real network delay (both copies are on one PC: a guest leader's next line
arrived within one frame, so its session never idled 0.1 s; over the internet it will). Joining mid-talk. The method
that closes an unwaited line. The reported "host validates nothing for a whole guest-led talk".

### Section 2 against the measurements

Confirmed:
- `Start` (all lines of a host generation) + `Index` fits every talk seen. talkId = host generation works; expect gaps.
- The held action as the advance (rank 1), `Activate` then `RequestStartDialogue` in one frame, no callbacks on
  followers, an open panel holding the camera, the duplicate guard for passive copies.
- Continuation without a blink exists, through the branch path only.

Wrong:
- "A new generation is a Boundary, so the camera opens again on every mod line." `Boundary` is the normal next line.
  A new generation with the speaker proposed again is a `Commit`, and that keeps a live session. The session restarts
  only after 0.1 s idle or a dialogue with no proposal. A watching guest today keeps one session for the whole talk; a
  guest leader loses it when the host's next line takes longer than 0.1 s to arrive after its own line closed. The
  `shot=Opening wide OTS reason=session start` read on every line is also what a session shows when later
  `policy change` solves return false. The blink is real and separate: the box hides for about 0.2 s at every start.
- "Mid-generation speaker change: apply the queued Camera proposal for index k." It never commits. The game changes
  speaker with a new generation. Drop the per-index `Camera` message; the speaker belongs in `Start`.
- The host "nothing validated" explanation, and with it the Suspend prefixes (both ranks of that fix).
- "Set `continuationGraceUntilFrame` if needed": no effect.
- "Write `watchdogLastProgress*` while holding": not needed.
- `afterDialogue: hasBranch ? DialogueBranch(NoOp) : null` as written crashes; build the Nullable by hand (probe 2).

Change:
1. Followers always start with a no-op branch and isStayInUI, whatever the host's dialogue has. At the last line the
   follower does not run the held action when the host's index moves on; it waits for what the host does next:
   `Start` -> run the held closure and `Activate` + `RequestStartDialogue` in the same frame (no blink, session kept);
   `Options` -> run it, then `panel.Show`; `End` -> `ForceCloseDialogue`. A follower whose talk has ended on the host
   therefore shows its last line until `End` arrives (0.5 s quiet rule today; shorten it for the no-options case).
2. Every `Start`, also a continuing one, carries the speaker (head path, name) or says there is none. With a speaker
   the follower calls `Activate` first, every time; without one it must not (the yard line and the standalone
   dialogue have no camera on the host either).
3. Guest leader: the stand-in action (`SendNext`) must start a new `WaitForPlayerInputThenDoSth(standIn)` in the same
   call, or the box closes itself within a few frames while the host's `Index` is still on its way. Stand-ins of earlier
   lines fire once more on the next press: each must compare its (talkId, index) with the current line and do nothing
   otherwise; the host's check on `Next` stays.
4. Check the held action's (local generation, index) before every invoke and drop it when the local generation changes.
5. Host camera: no Suspend. Keep "Gather before the first line" as a small improvement (it removes the `Reka moved`
   re-cut at line 2), and give Gather a spot that sees the speaker; log `RefuseUnvalidated` in dev builds to catch the
   reported case.
6. Stage 6 shrinks to: `TalkCamera` off for talks, speaker in `Start`. "Biggest risk" is settled in our favour: keeping
   the session needs the proposal in the starting frame and no 0.1 s idle, nothing about chaining timing.

Seen on the way (not dialogue design): in the host-led run the guest crossed a Lutki `ObjectDetector_Player` a moment
before the host walked into the yard; the yard talk was given to the guest (`conversation led by regress-guest`) and the
host was moved 100 m to the Lutki (`moved beside the starter (0.3, 5.6, 109.4)`). After dev `closeoptions` on the host
the villager's box stays up (`talk=True`) with nobody to close it.

## 8. Implemented (2026-11-10): story talks as one session (stages 2 to 4, protocol 60)

What runs as one session: a host dialogue that starts a talk and has nothing after it (`pendingAfterDialogue` empty,
no options open). That is every tutorial talk with the old woman and the standalone story lines. The host logs
`Dialogue: one session, generation N, L lines`, sends `Start` (leader, generation, current index, then text, name,
title, emotion per line) once and `Index` (generation, index) per line after that. The speaker still comes from the
existing `Camera` message, which the host sends at `Activate`, before `Start`.

On a guest (`DialogueSync.One`, `StartOne`, `TickOne`, `StandInFor`):
- `Start`: `Activate` (when the host's camera named a speaker) and `RequestStartDialogue` with all lines in one frame,
  no callbacks, no branch. The guest logs `Dialogue: one session N: L lines from 0, leading|watching, camera on X`.
- The continue prefix keeps the box's own action per line. `Index` from the host: `TickOne` runs the kept action (the
  box's `QueueNextLine`), after finishing the typing if the line was still typing; one step per frame.
- Watching: the box gets the do-nothing action; its presses are blocked as before, so its wait stays alive.
- Leading: the box gets a stand-in that sends `Next(generation, index)` and starts the wait again. Stand-ins of earlier
  lines get the same press; each checks it is the current stand-in for the line on screen and does nothing otherwise.
- The last line stays up until the host's `End` (about 0.5 s after the starter's last continue: the host's quiet rule).
  No branch is used, so the Nullable construction that crashed is not in the shipped path.
- If the box is gone while the host is still in the talk, it starts again from the host's line (warning in the log).
  `State` carries the index too, so a lost `Index` is caught up within a second.

Still line by line (the old `Line` / `FollowNow` path, logged on the host as `Dialogue: line by line, generation N
(reason)`): a dialogue with a branch (villager greeting, answers, anything that goes on to options), every later
dialogue of such a talk including its "Goodbye", passive lines (`Passive`), and the rest of a talk after someone joins
mid-talk (`joined mid-talk; the rest of it goes line by line`; guests that were in the session drop to lines at the
next `Line`). Dev: `dlgone off` on the host sends everything line by line. `TalkCamera` is unchanged: a watcher still
shows the starter's stream over its own dialogue camera, and the two did not fight.

Dev `next` on a guest in a session now presses the real continue button (`dlgpress`), so the regression goes through
the stand-in and the re-armed wait.

Measured on the following copy (guest), `dlgtrace` + `campatch 2`:

| Talk | | Generations | Box hidden while talking | BeginSession | Shots |
|---|---|---|---|---|---|
| Door, guest leads | before | 1, 3, 5, 7, 9 | 5 times | 1 | `Opening wide OTS "session start"` on all 5 lines |
| | after | 1 | once (the start) | 1 | `Opening wide OTS`, `Reaction on Reka (Single-Medium)`, `Opening wide OTS "reaction over"` |
| Indoors (21 lines), host leads | before | 3, 5 ... 43 | 21 | 1 | progressed already (`Opening Single-Loose`, `Single-Medium`, `Single-Close` ...) |
| | after | 3 | once | 1 | `Opening Single-Loose`, `Single-Medium`, `Single-Close`, `Single-Medium`, `Single-Tight`, `Single-Medium`, `Single-Close`, `OTS-Medium` |
| Indoors, guest leads | before | 3, 5 ... 43 | 21 | 2 (one release mid-talk) | `Player camera "nothing validated"`, `Opening OTS-Close "retry"`, ..., `Opening wide OTS "session start"` again mid-talk |
| | after | 3 | once | 1 | `Player camera "nothing validated"`, `Opening OTS-Close "retry from the player camera"`, `OTS-Close`, `OTS-Medium`, `Single-Close`, `OTS-Medium`, `OTS-Close`, `Single-Tight`, `OTS-Medium` |

The first-frame `nothing validated` indoors is on whichever copy's player just used her (host-led: the host; guest-led:
the guest) and the game's own retry fixes it within a second. It is not the swap.

Regression (`python mod/regress.py`, 16.1 min): guest-led playthrough PASS; host-led playthrough one failed check,
`the host is inside her house: 30.3, 71.5`, caused by the leader bug below, not by this change (seen with the old code
in section 7; with the guest moved away from the Lutki first, the same host-led step ran clean).

### The leader bug, for a separate fix

Repro: fixture `talisman-1.0.32`, both players at the Lutki. Guest stands still. Host: `tp 60 40 4`, `backport port`,
`walkto 80.6 13.6 8`. The standing Lutki keep walking into the guest, so the host logs a detector for the guest about
once a second, and the yard talk the host walks into is given to the guest:

```
[cmd] walkto 80.6 13.6 8 -> walking to 80.6, 13.6
Detectors: regress-guest walked into TutorialProps_v3(Clone)[0]/FunctionalComponents[0]/POI_Tutorial_TeleportTalisman[5]/Lutki[3]/Lutki_01[1]/ObjectDetector_Player[1]
[piece] RunDialogue Tutorial/C00_Tutorial/BabaJaga/004 wasSet=False didRun=False invalid=False immediate=True
Dialogue: one session, generation 7, 1 lines
Dialogue: conversation led by regress-guest
Dialogue: moved beside the starter (3.4, 5.5, 107.2)
walkto: stopped 120.8 m from the goal
```

Cause as read from the code: the detector calls `DialogueSync.NoteRequest(guest, hold)`; `Begin` gives the next talk to
`_requestedBy` while the request is fresh, whatever started the talk. This talk was started by the host walking into a
trigger (`TutorialDialoguePiece.RunDialogue`, not inside an AI update), so nothing set the host as the requester.

## 9. Implemented (2026-11-10): every dialogue as one session (stages 5, 7, parts of 6 and 8, protocol 61)

Branch `worktree-agent-a2e3ef901c319e0c6`, commits `aa5427f`, `0ca3cd1` and the one that adds this section. Tested on the
second PC (`mod/olga.ps1`), game 1.0.40, host `play` + guest `client`, fixture `talisman-1.0.32`. Test villager: Yullia,
`Tutorial_POIs(Clone)/POI_HangingMan/NPC_03` (dev `facenpc POI_HangingMan`, `say POI_HangingMan`, `next`, `pick n`).

### What the host's talk with her is (measured, `dlgtrace` on the host)

| Step | Generation | Lines | Last line waits to run | Box |
|---|---|---|---|---|
| Greeting | 1 | 1 | `<OnLastLineRevealed>b__1` (branch) | stays up (`talk=True root=True`) under the options panel |
| Pick "Tell me about Kopnik." | 3 | **3** | branch again | `StopTalkingAndCloseUI` at the pick, new dialogue in the same frame, root hidden 9 frames; up under the options again |
| Pick "Goodbye" | 5 | 1 | `StopTalkAndCallOnFinished` | same close and reopen at the pick; closes at the end |

So an answer can be several lines in one generation (this was "not found in reach" in section 7), and the game's own
pick closes the box and opens the next one. One camera session for the whole talk (`Commit` at generations 3 and 5, no
`BeginSession`).

### What changed

- **Host**: every dialogue goes whole (`Start`), whatever follows it; the `line by line` fallback for branches, options
  and mid-talk joins is gone. `Start` now carries a passive flag and the speaker (head path, name), read from the
  game's camera when the dialogue starts: the session it committed for that generation, else the proposal it holds;
  none when the camera was given none. The separate `Camera` message is only sent with `dlgone off`.
- **Guest, start**: every dialogue started here has an empty branch, built field by field (`DialogueSync.BranchTo`:
  an empty `Nullable<DialogueBranch>`, then `value.action` and `hasValue` written at the offsets the class reports;
  falls back to no branch with a warning if the fields aren't found). `isStayInUI` stays false.
- **Guest, next dialogue** (`ChainNext`): a `Start` that arrives while a dialogue of the talk is on the box waits (at
  most 0.4 s) until the box holds its last line's step, runs it (the empty branch) and starts the new dialogue in the
  same frame, after `Activate` when it has a speaker. The box stays up and the camera commits on its live session.
  If the box is not on its last line, or the step doesn't come, the box is closed and the dialogue started plainly.
- **Guest, options**: the box stays on its last line under the panel, as on the host (before: closed).
- **Guest starter's pick**: the game's own `OnOptionPicked` is not run on a guest who leads; the pick goes to the host
  and only the panel closes. The game's own pick closed the box here (measured, first build), the answer then opened a
  new one, and the camera would let go if the answer took longer than 0.1 s to arrive.
- **Passive dialogues** go whole in the same `Start` (flag) and run on the guest's own timing; skipped while a real
  talk is on screen, as before.
- **Join mid-talk**: the host sends the joiner `Start` from the current line and the open options. A guest that is not
  in the world yet keeps the dialogue and starts it when the host's `State` shows the host still on it. The same
  rule replaces the old per-frame "the box closed here; starting again": it now needs the host's word.
- A watcher whose options panel never opened is shown it again from `State` (before: the starter only).
- Dev: `dlgone off` (host) still sends everything by the old `Line` / `Passive` / `Camera` path; `dlgcamstream off`
  (per copy) switches the talk camera stream off there; `kick <id>` (host) drops a guest, who rejoins by itself.
- `mod/regress.py`: scenario `villager` (fixture `talisman-1.0.32`; last in the chained run), with checks read from
  `dlgtrace`: one dialogue here per dialogue on the host, the box never hidden inside a dialogue, the greeting up
  under the options, one camera session.

### Measured on the guest (`dlgtrace`, `campatch 2`), the talk above: greeting, pick 0, three lines, Goodbye

| Leader | Build | Guest's generations (host's) | Box under the options | Box hidden during the talk | BeginSession |
|---|---|---|---|---|---|
| Host | before (`00f05b8`) | 1, 3, 5, 7, 9 (1, 3, 5) | closed (`talk=False`) | at the start, under both panels, and at lines 2 and 3 of the answer (about 12 frames each) | 1 |
| Host | after | 1, 2, 3 (1, 3, 5) | up | once, the 9 frames that open the talk | 1 |
| Guest | before | 11, 14, 16, 18, 21 (11, 13, 15) | closed | at every line | 2 (`Release "dialogue UI idle"` at the Goodbye pick) |
| Guest | after, game's own pick (`aa5427f`) | 5, 7, 9 (7, 9, 11) | up | at each of the 3 dialogue starts (as on a host who leads) | 1 |
| Guest | after (`0ca3cd1`) | 1, 2, 3 (1, 3, 5) | up | once | 1 |

Guest log, host leads, after: `one session 1: 1 lines from 0, watching, camera on Yullia`, `watching from ...`,
`regress chose Tell me about Kopnik.`, `one session 3: 3 lines from 0`, `one session 3 goes on from 1`,
`gen=2 idx=0/3 root=True` ... `idx=2/3`, `watching from ...`, `one session 5 goes on from 3`, `conversation over`.

Other things run by hand on the new path:

| Case | How | Result |
|---|---|---|
| Guest starter leaves the options | `closeoptions` on the guest | `left the options`; the host says its goodbye (a new generation), the guest's box goes on to it; ends on both |
| Talk forced shut on the host | `dlgadvance force` on the host, options open | `conversation over` on the guest, box and panel closed, guest can walk |
| Passive dialogue | `standalone Dialogue_FirstInterruption` on the host | guest: `passive, 1 lines: Old Woman: Yes! Keep going!`, up 2 s, closes by itself |
| Join mid-talk | host at line 2 of the answer, `kick 1`; the guest reconnected by itself (as player 2) | host: `joined mid-talk; sent generation 25 from line 1`; guest: `one session 25: 3 lines from 1`, then followed line 3, the options and the goodbye |
| Old path | `dlgone off`, the same talk | runs line by line as before |

### Stage 6 (camera per copy): not switched on

With `dlgcamstream off` on both copies the watcher has its own dialogue camera for the whole talk. It never refused
(`onPlayer=False`, no `RefuseUnvalidated`, opening shot validated on the watcher in both directions). Guest watching
(one run): `Opening OTS-Medium`, then three later solves returned false and the opening shot stayed for the whole
talk. Host watching a guest-led talk (one run): `Opening OTS-Medium`, `reaction over`, `Single-Close "policy
change"`. So it works, but the watcher's shots are no better than
the starter's stream and sometimes poorer (it stands 1.6 m to the side), and I can't look at the picture from here.
The stream stays on by default; the switch is there to try it by eye.

### Stage 8: the old path stays, as a dev fallback only

No dialogue kind is sent line by line any more unless `dlgone off` is set on the host. The old code (`Kind.Line`,
`Kind.Passive`, `Kind.Camera`, `Follow`, `ShowPending`, `FollowNow`, `PullIn`, the 3600 s passive delay) is not deleted,
because these kinds were not run on the new path:
- an answer said as several dialogues chained by their finish callback (`NPC_DialogueManager.ContinueDialogue`); it
  should arrive as plain new generations, but none was in reach;
- a `StandaloneDialogue` that chains to another one;
- talks with a hand-in or a reward (`TakeItem`, merchants, loot in the finish callback: probe 8);
- the hut's talk that hands over the controls (`HutSync` uses `CloseStrayLine` and `ShowAfterTalk`);
- a join by a guest who is still loading the world (only a reconnect of a guest already in the world was run);
- anything over a real network (both copies are on one PC; a `Start` arrives within a frame or two).
`Show` / `ShowLocal` / `ShowAfterTalk` (the mod's own notices) use the passive box directly and stay in any case.

### Regression

- `olga.ps1 run villager` (each leader alone, build `aa5427f` plus `dlgcamstream`, before the pick change): PASS
  host leads, PASS guest leads, 4.6 min.
- Full chained run (`olga.ps1 run`, build `0ca3cd1`): **not completed**. The owner of the test PC paused testing
  during it. Host-led playthrough: all 102 checks ok (door, yard, lutki, home, villager and the end checks: nobody
  stuck, both can walk, digest equal, no errors in either log). Guest-led playthrough: door and yard all ok, lutki
  ok up to the point it was stopped (46 checks, no failure); guest-led home, villager and end checks did not run.
- With the pick change (`0ca3cd1`), a guest-led villager talk was run by hand only (table above), not by the script.

Deployed and never exercised (code paths no test reached): the start of a dialogue for a guest still loading in
(`Reconcile` starting it from `State`), a watcher's options shown again from `State`, the wait and close in
`ChainNext` when the last line's step doesn't come within 0.4 s, `BranchTo`'s fallback to no branch, a passive
`Start` skipped because a real talk is on screen, a passive dialogue of more than one line.

### Not done, not verified

- Subtitle mode for bystanders (section 4): everyone is still gathered into every talk.
- `End` still comes 0.5 s after the host's box closes; the guest's last line stays up that long.
- A passive dialogue cut short on the host is not cut short on guests (it runs out by itself there).
- Dev `closeoptions` on the host still leaves the host's own box up (`Close()` is not the game's way out of the
  panel; the game's `CloseAndEndDialogue`, which a guest's leaving runs, ends it properly).
- With dev `say` on the host while a guest stands in the villager's detector, the talk is given to the guest (the
  leader bug of section 8); real key presses name the host.
