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
