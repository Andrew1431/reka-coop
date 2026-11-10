#if DEVBUILD
using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using RekaCoop.Sync;
using UnityEngine;

namespace RekaCoop.Dev;

// Measurement for research/dialogue-one-session.md (section 5). Nothing here runs unless a dev command switches it on:
//   dlgtrace on|off        log the dialogue box and the dialogue camera whenever either changes (one line per change)
//   dlgone on|off          host: dialogues go to the other copies whole (one session) or line by line
//   dlgcamstream on|off    this copy: a watcher shows the starter's camera (on), or its own dialogue camera (off)
//   dlgstate               both, once, with the game's constants
//   dlghold on|off         keep the action the box would run on continue (the next line, or the last line's end) and give
//                          the box one that does nothing
//   dlghold rearm          the same, and the stand-in action starts a new wait when the player presses continue
//   dlgclosepatch          log the watchdog's recover and the box closing (to see what closes a line nobody waits on)
//   dlgadvance held|queue|stop|branch|skip [cam2]   move the local dialogue on: the held action, QueueNextLine(false),
//                          StopTalkingAndCloseUI, RunCompletionThenBranch(no-op), or skip the typing; cam2: first propose
//                          another speaker to the camera in the same frame
//   dlgstart <n> [cam] [branch] [stay] [passive] [alt] [tag=X]   a local n-line dialogue with no callbacks; cam: the
//                          camera is given the speaker in the same frame
//   dlgchain <n> [grace] [cam]   at a held last line: run the held action and start a new dialogue in the same frame
//   dlghead <scene path>|sync    the speaker head for cam (sync: the one the host's talk last used here)
//   dlgpress [frames] [action]   the continue button reads as pressed for a few frames (test windows take no input)
//   dlgdup                 the queue and duplicate rules, read while a dialogue is up
//   camrules               the camera's pure session rules as tables
//   campatch [2]           log the camera's own decisions (Activate, line started, solve, refuse); 2: Commit and Release too
//   camsuspend on|off      host: the camera's entry points see the host's own player while acting for a guest
internal static class DialogueProbe
{
    private static bool _trace, _hold, _holdPatched, _camPatched, _camPatched2, _suspend;
    private static string _last = "";
    private static DialogueCamera _cam;
    private static Harmony _harmony;
    private static Il2CppSystem.Action _held;
    private static (int gen, int index) _heldFor = (-1, -1);
    private static Transform _headOverride, _lastActivated;
    private static string _lastActivatedName;
    private static int _starts;

    private static readonly Il2CppSystem.Action Nothing = (System.Action)(() => { });
    private static readonly Il2CppSystem.Action NoOpBranch = (System.Action)(() => Plugin.Logger.LogInfo($"[dlgprobe] f={Time.frameCount} the no-op branch action ran"));

    private static DialogueCamera Cam => _cam != null ? _cam : _cam = UnityEngine.Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include);
    private static Harmony H => _harmony ??= new Harmony("rekacoop.dev.dialogueprobe");

    private static string Try(Func<string> f) { try { return f(); } catch (Exception e) { return "!" + e.GetType().Name; } }
    private static string P(Transform t) => t == null ? "null" : $"{t.name}@({t.position.x:0.0},{t.position.y:0.0},{t.position.z:0.0})";
    private static string At(Il2CppStringArray a, int i) => a != null && i >= 0 && i < a.Length && a[i] != null ? a[i] : "";
    private static string Cut(string s, int n) => s == null ? "" : s.Length <= n ? s : s.Substring(0, n);

    // A Nullable with no value boxes to null, and its wrapper then throws on any read.
    private static string Branch(DialogueUILogic ui)
    {
        try { return ui.pendingAfterDialogue.HasValue.ToString(); }
        catch (NullReferenceException) { return "False"; }
        catch (Exception e) { return "!" + e.GetType().Name; }
    }

    // Nullable<DialogueBranch>(value) through the wrapper's constructor gave a branch whose action crashed the game
    // when run (the boxed pointer went in as the value). Written field by field instead, and read back.
    private static string _branchNote = "";
    private static Il2CppSystem.Nullable<DialogueBranch> MakeBranch(Il2CppSystem.Action action)
    {
        var n = new Il2CppSystem.Nullable<DialogueBranch>();
        var klass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Il2CppSystem.Nullable<DialogueBranch>>.NativeClassPtr;
        int header = 2 * IntPtr.Size, has = 0, val = IntPtr.Size;
        string names = "";
        try
        {
            var iter = IntPtr.Zero;
            IntPtr f;
            while ((f = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != IntPtr.Zero)
            {
                string fn = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_name(f));
                int off = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f) - header;
                names += $"{fn}@{off} ";
                if (fn != null && fn.ToLowerInvariant().Contains("hasvalue")) has = off;
                else if (fn == "value") val = off;
            }
        }
        catch (Exception e) { names += "!" + e.Message; }
        var p = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(n.Pointer);
        System.Runtime.InteropServices.Marshal.WriteByte(p, has, 1);
        System.Runtime.InteropServices.Marshal.WriteIntPtr(p, val, action.Pointer);
        _branchNote = $" [branch fields {names}-> has@{has} value@{val}; reads back HasValue={Try(() => n.HasValue.ToString())} action ok={Try(() => (n.Value.action.Pointer == action.Pointer).ToString())}]";
        return n;
    }

    private static string Ui(bool withLine = true)
    {
        var ui = DialogueUILogic.Instance;
        if (ui == null) return "no ui";
        int i = ui.indexInDialogue;
        string s = $"talk={ui.IsTalking} passive={ui.currentDialogueIsPassive} gen={ui.DialogueGeneration} idx={i}/{ui.currentDialogue?.Length ?? 0}"
            + $" branch={Branch(ui)} grace={ui.continuationGraceUntilFrame} root={Try(() => ui.IsUiRootVisible.ToString())}"
            + $" opts={DialogueOptionsPanel.IsShowing} q={ui.queue?.Count} cor={ui.coroutinesActive} reveal={Try(() => ui.talker.IsRevealing.ToString())} onFin={(ui.onCurrentDialogueFinished != null)}";
        if (withLine) s += $" who=\"{At(ui.namesOfPersonTalking, i)}\" text=\"{Cut(At(ui.currentDialogue, i), 32)}\"";
        return s;
    }

    private static string CamState()
    {
        var c = Cam;
        if (c == null) return "no cam";
        return $"live={c._live} sgen={c._sessionGeneration} onPlayer={c._onPlayerCamera} shot=\"{c._shotName}\" reason=\"{c._shotReason}\" trans=\"{c._lastTransitionDescription}\""
            + $" spk=\"{c._sessionSpeakerName}\" head={(c._sessionHead != null ? c._sessionHead.name : "null")} prop={c._hasProposal} retry={c._retryPending} setup={Try(() => c.CurrentSetupIndex.ToString())} side={c._lockedSide}";
    }

    private static string Ctx(Transform head = null)
    {
        var c = Cam;
        if (c == null) return "no cam";
        head ??= c._sessionHead;
        return $"f={Time.frameCount} acting={ActingPlayer.Current} inAi={ActingPlayer.InAi} playerHead={Try(() => P(c.playerHead?.transform))} rekaHead={Try(() => P(c.RekaHead()))}"
            + $" playerRoot={Try(() => P(DialogueCamera.PlayerRoot()))} pm={Try(() => P(DialogueCamera.FindPlayerMovement()?.transform))} rekaRoot={Try(() => c.RekaRootPosition().ToString("0.0"))}"
            + $" rootAtSolve={c._rekaRootAtSolve.ToString("0.0")} head={P(head)} outOfRange={Try(() => { if (head == null) return "?"; bool o = c.SpeakerOutOfRange(head, out float d); return $"{o} d={d:0.0}"; })}";
    }

    public static void Tick()
    {
        if (!_trace) return;
        string s = Try(() => Ui() + " | " + CamState());
        if (s == _last) return;
        _last = s;
        Plugin.Logger.LogInfo($"[dlgtrace] f={Time.frameCount} t={Time.unscaledTime:0.00} {s}");
    }

    // --- patches (applied by command, never at load) ---------------------------------------------------------------

    private static HarmonyMethod M(string name, int priority = -1)
    {
        var m = new HarmonyMethod(typeof(DialogueProbe).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        if (priority >= 0) m.priority = priority;
        return m;
    }

    private static bool _rearm, _closePatched;
    private static readonly System.Collections.Generic.HashSet<IntPtr> StandIns = new();
    private static readonly System.Collections.Generic.List<Il2CppSystem.Action> Keep = new();

    private static void PatchClose()
    {
        if (_closePatched) return;
        _closePatched = true;
        H.Patch(AccessTools.Method(typeof(DialogueUILogic), nameof(DialogueUILogic.WatchdogForceRecover)), prefix: M(nameof(WatchdogPre)));
        H.Patch(AccessTools.Method(typeof(DialogueUILogic), nameof(DialogueUILogic.StopTalkingAndCloseUI)), prefix: M(nameof(StopClosePre)));
    }

    private static void WatchdogPre(DialogueUILogic __instance) =>
        Plugin.Logger.LogInfo($"[dlgclose] f={Time.frameCount} WatchdogForceRecover | {Try(() => Ui(false))} wdAgo={Try(() => (Time.unscaledTime - __instance.watchdogLastProgressUnscaled).ToString("0.00"))}s wdFrames={Try(() => (Time.frameCount - __instance.watchdogLastProgressFrame).ToString())}");
    private static void StopClosePre(DialogueUILogic __instance) =>
        Plugin.Logger.LogInfo($"[dlgclose] f={Time.frameCount} StopTalkingAndCloseUI | {Try(() => Ui(false))}");

    private static void PatchHold()
    {
        if (_holdPatched) return;
        _holdPatched = true;
        H.Patch(AccessTools.Method(typeof(DialogueUILogic), nameof(DialogueUILogic.WaitForPlayerInputThenDoSth)), prefix: M(nameof(HoldPre), Priority.First));
    }

    private static string Describe(Il2CppSystem.Action a) =>
        a == null ? "null" : Try(() => { var m = a.Method; return $"{m?.DeclaringType?.Name}.{m?.Name}"; });

    private static void HoldPre(DialogueUILogic __instance, ref Il2CppSystem.Action __0)
    {
        try
        {
            if (_trace || _hold)
                Plugin.Logger.LogInfo($"[dlgwait] f={Time.frameCount} gen={__instance.DialogueGeneration} idx={__instance.indexInDialogue}/{__instance.currentDialogue?.Length ?? 0} waits for input to run {Describe(__0)}{(_hold ? " (held)" : "")}");
            if (!_hold) return;
            if (__0 != null && StandIns.Contains(__0.Pointer)) return;   // our own stand-in, waiting again
            _held = __0;
            _heldFor = (__instance.DialogueGeneration, __instance.indexInDialogue);
            var line = _heldFor;
            Il2CppSystem.Action standIn = null;
            standIn = (Il2CppSystem.Action)(System.Action)(() =>
            {
                var box = DialogueUILogic.Instance;
                bool again = _rearm && box != null && box.IsTalking && (box.DialogueGeneration, box.indexInDialogue) == line;
                Plugin.Logger.LogInfo($"[dlghold] f={Time.frameCount} the box ran the stand-in action of line {line} (its wait routine got its input); box now at ({box?.DialogueGeneration}, {box?.indexInDialogue}){(again ? "; waiting again" : "")}");
                if (again) box.StartCoroutine(box.WaitForPlayerInputThenDoSth(standIn));
            });
            StandIns.Add(standIn.Pointer);
            Keep.Add(standIn);
            __0 = standIn;
        }
        catch (Exception e) { Plugin.Logger.LogInfo($"[dlgwait] {e.Message}"); }
    }

    // The continue button as the game reads it (Rewired), pressed for a few frames by command.
    private static bool _pressPatched;
    private static string _pressAction = "AdvanceDialogue";
    private static int _pressId = -1, _pressFrom = -1, _pressTo = -1, _pressReads;

    private static void PatchPress()
    {
        if (_pressPatched) return;
        _pressPatched = true;
        foreach (var m in typeof(Rewired.Player).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if ((m.Name == "GetButton" || m.Name == "GetButtonDown" || m.Name == "GetButtonUp") && m.ReturnType == typeof(bool)
                && m.GetParameters() is { Length: 1 } p && (p[0].ParameterType == typeof(int) || p[0].ParameterType == typeof(string)))
                H.Patch(m, postfix: M(nameof(PressPost), Priority.First));
    }

    private static void PressPost(object __0, MethodBase __originalMethod, ref bool __result)
    {
        int f = Time.frameCount;
        if (f < _pressFrom || f > _pressTo) return;
        if (!(__0 is string name ? name == _pressAction : __0 is int id && id == _pressId)) return;
        string n = __originalMethod.Name;
        bool v = n == "GetButton" ? f < _pressTo : n == "GetButtonDown" ? f == _pressFrom : f == _pressTo;
        if (v && !__result) { __result = true; _pressReads++; }
    }

    private static void PatchCam(bool second)
    {
        var t = typeof(DialogueCamera);
        if (!_camPatched)
        {
            _camPatched = true;
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.Activate), new[] { typeof(Transform), typeof(string), typeof(DialogueShotFamilyOverride), typeof(Transform) }),
                prefix: M(nameof(ActivatePre), Priority.First), finalizer: M(nameof(SuspendPost)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.OnDialogueLineStarted)), prefix: M(nameof(LineStartedPre)), finalizer: M(nameof(SuspendPost)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.SolveAndApply)), prefix: M(nameof(SolvePre)), postfix: M(nameof(SolvePost)), finalizer: M(nameof(SuspendPost)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.RefuseUnvalidated)), prefix: M(nameof(RefusePre)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.EnterPlayerCamera)), prefix: M(nameof(EnterPlayerPre)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.BeginSession)), prefix: M(nameof(BeginSessionPre)));
        }
        if (second && !_camPatched2)
        {
            _camPatched2 = true;
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.Commit)), prefix: M(nameof(CommitPre)));
            H.Patch(AccessTools.Method(t, nameof(DialogueCamera.Release)), prefix: M(nameof(ReleasePre)));
        }
    }

    private static void Log(string s) { try { Plugin.Logger.LogInfo("[dlgcam] " + s); } catch { } }

    private static void ActivatePre(DialogueCamera __instance, Transform __0, string __1, DialogueShotFamilyOverride __2, Transform __3)
    {
        try
        {
            _cam = __instance;
            _lastActivated = __0;
            _lastActivatedName = __1;
            Log($"Activate head={ScenePath.Of(__0)} name=\"{__1}\" family={__2} listener={P(__3)} uigen={DialogueUILogic.Instance?.DialogueGeneration} | {Ctx(__0)}");
        }
        catch (Exception e) { Log("Activate " + e.Message); }
        SuspendPre();
    }

    private static void LineStartedPre(DialogueCamera __instance, string __0, string __1, bool __2, int __3)
    {
        try
        {
            var c = __instance;
            bool hp = c._hasProposal;
            int pg = -1; string pn = null;
            if (hp) { var p = c._proposal; pg = p.generationAtProposal; pn = p.speakerName; }
            var outcome = DialogueCameraSessionRules.Classify(hp, pg, pn, c._live, c._sessionGeneration, __2, __3, __0);
            Log($"LineStarted who=\"{__0}\" passive={__2} gen={__3} text=\"{Cut(__1, 32)}\" -> {outcome} (proposal={hp} pgen={pg} pname=\"{pn}\" live={c._live} sgen={c._sessionGeneration}) f={Time.frameCount} acting={ActingPlayer.Current} inAi={ActingPlayer.InAi}");
        }
        catch (Exception e) { Log("LineStarted " + e.Message); }
        SuspendPre();
    }

    private static void SolvePre(DialogueCamera __instance, string __0, bool __2, bool __3)
    {
        Log($"SolveAndApply \"{__0}\" sessionStart={__2} newSegment={__3} | {Try(() => Ctx())}");
        SuspendPre();
    }

    private static void SolvePost(DialogueCamera __instance, bool __result) =>
        Log($"SolveAndApply -> {__result} shot=\"{Try(() => __instance._shotName)}\" reason=\"{Try(() => __instance._shotReason)}\" onPlayer={Try(() => __instance._onPlayerCamera.ToString())} | after: {Try(() => Ctx())}");

    private static void RefusePre(DialogueCamera __instance, string __0) => Log($"RefuseUnvalidated \"{__0}\" | {Try(() => Ctx())}");
    private static void EnterPlayerPre(DialogueCamera __instance, string __0) => Log($"EnterPlayerCamera \"{__0}\" f={Time.frameCount}");
    private static void BeginSessionPre(DialogueCamera __instance, Transform __1) => Log($"BeginSession head={P(__1)} f={Time.frameCount} uigen={DialogueUILogic.Instance?.DialogueGeneration}");
    private static void CommitPre(DialogueCamera __instance, int __0) => Log($"Commit gen={__0} f={Time.frameCount}");
    private static void ReleasePre(DialogueCamera __instance, string __0) => Log($"Release \"{__0}\" f={Time.frameCount} | ui: {Try(() => Ui(false))}");

    // Host: while the camera's entry points run, the game's player references are the host's own again.
    private static readonly BindingFlags Priv = BindingFlags.Static | BindingFlags.NonPublic;
    private static int _susDepth, _susWho, _susCount;
    private static bool _susDid;

    private static void PointActing(Transform body, Transform head) =>
        typeof(ActingPlayer).GetMethod("Point", Priv).Invoke(null, new object[] { body, head });

    private static void SuspendPre()
    {
        if (!_suspend) return;
        try
        {
            if (_susDepth++ > 0) return;
            _susDid = false;
            if (!(bool)typeof(ActingPlayer).GetField("_swapped", Priv).GetValue(null)) return;
            _susWho = ActingPlayer.Current;
            PointActing((Transform)typeof(ActingPlayer).GetField("_body", Priv).GetValue(null), (Transform)typeof(ActingPlayer).GetField("_head", Priv).GetValue(null));
            _susDid = true;
            _susCount++;
            Log($"suspended the acting player ({_susWho}) f={Time.frameCount}");
        }
        catch (Exception e) { Log("suspend " + e.Message); }
    }

    private static void SuspendPost()
    {
        if (!_suspend) return;
        try
        {
            if (--_susDepth > 0) return;
            _susDepth = 0;
            if (!_susDid) return;
            _susDid = false;
            var puppet = PlayerSync.PuppetRoot(_susWho);
            if (puppet == null) return;
            var head = (Transform)typeof(ActingPlayer).GetMethod("HeadOf", Priv).Invoke(null, new object[] { puppet });
            PointActing(puppet, head);
        }
        catch (Exception e) { Log("resume " + e.Message); }
    }

    // --- commands --------------------------------------------------------------------------------------------------

    private static Transform Head(out string name)
    {
        name = "Prober";
        if (_headOverride != null) { name = _headOverride.name; return _headOverride; }
        var h = typeof(DialogueSync).GetField("_speakerHead", Priv)?.GetValue(null) as Transform;
        if (h != null) { name = typeof(DialogueSync).GetField("_speaker", Priv)?.GetValue(null) as string ?? "Prober"; return h; }
        if (_lastActivated != null) { name = _lastActivatedName ?? "Prober"; return _lastActivated; }
        return null;
    }

    private static Transform OtherHead()
    {
        var c = Cam;
        var mine = c != null ? c.RekaHead() : null;
        var puppet = PlayerSync.NearestPuppetTransform(PlayerSync.LocalFeet ?? Vector3.zero);
        if (puppet != null && mine != null)
            foreach (var t in puppet.GetComponentsInChildren<Transform>(true)) if (t.name == mine.name) return t;
        return mine;
    }

    private static string Start(DialogueUILogic ui, int n, bool cam, bool branch, bool stay, bool passive, bool alt, string tag, bool other = false)
    {
        string who = "Prober";
        var c = Cam;
        string camNote = "";
        if (cam)
        {
            var head = other ? OtherHead() : Head(out who);
            if (other) who = "Other";
            if (head == null || c == null) return "no speaker head here (dlghead <path>, or watch a host talk first)";
            c.Activate(head, who, DialogueShotFamilyOverride.Auto, null);
            camNote = $" camera given {ScenePath.Of(head)} as \"{who}\" ({Vector3.Distance(head.position, PlayerSync.LocalFeet ?? head.position):0.0} m)";
        }
        var lines = new string[n]; var names = new string[n]; var empty = new string[n];
        for (int i = 0; i < n; i++)
        {
            lines[i] = $"Probe {tag}: line {i + 1} of {n}, long enough to take a moment to type out.";
            names[i] = alt && i % 2 == 1 ? "Other" : who;
            empty[i] = "";
        }
        _branchNote = "";
        var after = branch ? MakeBranch(NoOpBranch) : new Il2CppSystem.Nullable<DialogueBranch>();
        int genBefore = ui.DialogueGeneration;
        ui.RequestStartDialogue(new Il2CppStringArray(lines), new Il2CppStringArray(names), new Il2CppStringArray(empty), null, after, passive, stay, new Il2CppStringArray(empty), null);
        return $"f={Time.frameCount} gen {genBefore} -> {ui.DialogueGeneration}{camNote}{_branchNote} | {Ui()} | {CamState()}";
    }

    public static string Run(string[] a)
    {
        var ui = DialogueUILogic.Instance;
        switch (a[0])
        {
            case "dlgtrace":
                _trace = a.Length < 2 || a[1] != "off";
                _last = "";
                if (_trace) PatchHold();
                return $"trace {(_trace ? "on" : "off")}";
            case "dlgone":
                DialogueSync.OneSession = a.Length < 2 || a[1] != "off";
                return $"one session {(DialogueSync.OneSession ? "on" : "off")} (the host decides)";
            case "dlgcamstream":
                TalkCamera.StreamTalks = a.Length < 2 || a[1] != "off";
                return $"talk camera stream {(TalkCamera.StreamTalks ? "on: watchers see the starter's camera" : "off here: this copy neither sends its talk camera nor shows the starter's")}";
            case "dlgstate":
                return $"f={Time.frameCount} t={Time.unscaledTime:0.00} {Ui()} | {CamState()}"
                    + $"\n  held={Describe(_held)} for {_heldFor} hold={_hold} wdAgo={Try(() => (Time.unscaledTime - ui.watchdogLastProgressUnscaled).ToString("0.0"))}s wdFrames={Try(() => (Time.frameCount - ui.watchdogLastProgressFrame).ToString())} heartbeatFrames={Try(() => (Time.frameCount - ui.lastLoopHeartbeatFrame).ToString())}"
                    + $"\n  consts: CONTINUATION_GRACE_FRAMES={Try(() => DialogueUILogic.CONTINUATION_GRACE_FRAMES.ToString())} WATCHDOG_TIMEOUT_SECONDS={Try(() => DialogueUILogic.WATCHDOG_TIMEOUT_SECONDS.ToString())} WATCHDOG_TIMEOUT_FRAMES={Try(() => DialogueUILogic.WATCHDOG_TIMEOUT_FRAMES.ToString())}"
                    + $" AdvanceInputCooldown={Try(() => DialogueUILogic.AdvanceInputCooldown.ToString())} AutoAdvanceForTesting={Try(() => DialogueUILogic.AutoAdvanceForTesting.ToString())} QueuedBehindPassiveTotal={Try(() => DialogueUILogic.QueuedBehindPassiveTotal.ToString())}"
                    + $" isTalkingOrInQueue={Try(() => DialogueUILogic.isTalkingOrInQueue.ToString())} passiveOnScreen={Try(() => DialogueUILogic.isPassiveDialogueOnScreen.ToString())} delayForPassive={Try(() => ui.delayForPassiveDialogue.ToString())}"
                    + $" releaseGraceSeconds={Try(() => Cam.settings.releaseGraceSeconds.ToString())} camUi: exists={Try(() => DialogueCamera.UiExists.ToString())} talking={Try(() => DialogueCamera.UiIsTalking.ToString())} gen={Try(() => DialogueCamera.UiGeneration.ToString())}"
                    + $"\n  ctx: {Try(() => Ctx())}\n  sync: {DialogueSync.Describe()} suspend={_suspend} suspensions={_susCount}";
            case "dlghold":
                _hold = a.Length < 2 || a[1] != "off";
                _rearm = a.Length > 1 && a[1] == "rearm";
                if (_hold) PatchHold();
                else { _held = null; _heldFor = (-1, -1); }
                return $"hold {(_hold ? "on" : "off")}{(_rearm ? ", the stand-in waits again after a press" : "")}";
            case "dlgclosepatch":
                PatchClose();
                return "logging WatchdogForceRecover and StopTalkingAndCloseUI";
            case "dlgpress":
            {
                PatchPress();
                string was = $"last press was read {_pressReads} times";
                _pressAction = a.Length > 2 ? a[2] : "AdvanceDialogue";
                _pressId = Rewired.ReInput.mapping?.GetAction(_pressAction)?.id ?? -1;
                _pressFrom = Time.frameCount + 1;
                _pressTo = _pressFrom + (a.Length > 1 && int.TryParse(a[1], out int fr) ? fr : 3);
                _pressReads = 0;
                return $"pressing {_pressAction} (id {_pressId}) for frames {_pressFrom}..{_pressTo - 1}; {was} | {Ui(false)}";
            }
            case "dlghead":
            {
                if (a.Length < 2 || a[1] == "sync") { _headOverride = null; var h = Head(out string n); return $"head {(h != null ? ScenePath.Of(h) : "none")} as \"{n}\""; }
                _headOverride = ScenePath.Resolve(string.Join(" ", a.Skip(1)));
                return _headOverride != null ? $"head {ScenePath.Of(_headOverride)}" : "not found here";
            }
            case "dlgstart":
            {
                if (ui == null) return "no ui";
                int n = a.Length > 1 && int.TryParse(a[1], out int v) ? v : 3;
                string tag = a.FirstOrDefault(x => x.StartsWith("tag="))?.Substring(4) ?? ((char)('A' + _starts++ % 26)).ToString();
                return Start(ui, n, a.Contains("cam"), a.Contains("branch"), a.Contains("stay"), a.Contains("passive"), a.Contains("alt"), tag, a.Contains("other"));
            }
            case "dlgadvance":
            {
                if (ui == null) return "no ui";
                string how = a.Length > 1 ? a[1] : "held";
                string before = $"{Ui(false)} | {CamState()}";
                string note = "";
                if (a.Contains("cam2"))
                {
                    var c = Cam; var other = OtherHead();
                    if (c == null || other == null) return "no other head";
                    bool hp0 = c._hasProposal;
                    c.Activate(other, "Other", DialogueShotFamilyOverride.Auto, null);
                    var p = c._hasProposal ? c._proposal : null;
                    note = $" cam2: proposed {P(other)} as \"Other\" (had proposal {hp0}, now {c._hasProposal} pgen={p?.generationAtProposal}); rules for the next line here: same gen/Other -> "
                        + $"{DialogueCameraSessionRules.Classify(c._hasProposal, p?.generationAtProposal ?? -1, p?.speakerName, c._live, c._sessionGeneration, false, ui.DialogueGeneration, "Other")}";
                }
                switch (how)
                {
                    case "held":
                        if (_held == null) return $"nothing held (hold={_hold}, last held for {_heldFor}) | {before}";
                        if (_heldFor != (ui.DialogueGeneration, ui.indexInDialogue)) note += $" [held for {_heldFor}, box is at ({ui.DialogueGeneration}, {ui.indexInDialogue})]";
                        var go = _held; string what = Describe(go);
                        _held = null;
                        go.Invoke();
                        note += $" ran {what}";
                        break;
                    case "queue": ui.QueueNextLine(false); break;
                    case "stop": ui.StopTalkingAndCloseUI(); break;
                    case "stopfin": ui.StopTalkAndCallOnFinished(); break;
                    case "branch": ui.RunCompletionThenBranch(NoOpBranch); break;
                    case "end": ui.EndCurrentDialogue(); break;
                    case "force": ui.ForceCloseDialogue(); break;
                    case "skip": ui.talker.typewriter.SkipTypewriter(); break;
                    default: return "held|queue|stop|stopfin|branch|end|force|skip";
                }
                return $"f={Time.frameCount} {how}{note}\n  before: {before}\n  after:  {Ui()} | {CamState()}";
            }
            case "dlgchain":
            {
                if (ui == null) return "no ui";
                int n = a.Length > 1 && int.TryParse(a[1], out int v) ? v : 2;
                string before = $"{Ui(false)} | {CamState()}";
                string ran = "nothing held";
                if (_held != null) { var go = _held; ran = Describe(go); _held = null; go.Invoke(); }
                string mid = $"{Ui(false)} | {CamState()}";
                if (a.Contains("grace")) ui.continuationGraceUntilFrame = Time.frameCount + DialogueUILogic.CONTINUATION_GRACE_FRAMES;
                string started = Start(ui, n, a.Contains("cam"), a.Contains("branch"), a.Contains("stay"), false, false, "chain" + _starts++, a.Contains("other"));
                return $"f={Time.frameCount} ran {ran}, then started\n  before: {before}\n  between: {mid}\n  after:  {started}";
            }
            case "dlgdup":
            {
                if (ui == null) return "no ui";
                var cur = ui.currentDialogue; var names = ui.namesOfPersonTalking;
                string s = $"before: {Ui(false)} QueuedBehindPassiveTotal={DialogueUILogic.QueuedBehindPassiveTotal}";
                if (cur != null) s += $"\n  IsDuplicateDialogue(current lines, names)={Try(() => ui.IsDuplicateDialogue(cur, names).ToString())}";
                var other = new Il2CppStringArray(new[] { "Probe dup: a different line." }); var who = new Il2CppStringArray(new[] { "Prober" }); var e = new Il2CppStringArray(new[] { "" });
                s += $" IsDuplicateDialogue(other)={Try(() => ui.IsDuplicateDialogue(other, who).ToString())}";
                if (a.Contains("same") && cur != null)
                {
                    var em = ui.emotionsOfLines ?? new Il2CppStringArray(cur.Length);
                    ui.RequestStartDialogue(cur, names, ui.titlesOfPersonTalking, null, new Il2CppSystem.Nullable<DialogueBranch>(), ui.currentDialogueIsPassive, false, em, null);
                    s += $"\n  after requesting the same lines again: {Ui(false)}";
                }
                if (a.Contains("other"))
                {
                    ui.RequestStartDialogue(other, who, e, null, new Il2CppSystem.Nullable<DialogueBranch>(), a.Contains("passive"), false, e, null);
                    s += $"\n  after requesting another dialogue (passive={a.Contains("passive")}): {Ui(false)} QueuedBehindPassiveTotal={DialogueUILogic.QueuedBehindPassiveTotal}";
                }
                return s;
            }
            case "camrules":
            {
                var sb = new System.Text.StringBuilder("Classify(hasProposal, proposalGen, proposalName, live, sessionGen, passive, lineGen=10, lineName=\"A\"); outcomes for passive=False / passive=True");
                string C(bool hp, int pg, string pn, bool live, int sg) =>
                    $"{DialogueCameraSessionRules.Classify(hp, pg, pn, live, sg, false, 10, "A")}/{DialogueCameraSessionRules.Classify(hp, pg, pn, live, sg, true, 10, "A")}";
                foreach (bool live in new[] { false, true })
                    foreach (int sg in live ? new[] { 9, 10 } : new[] { 9 })
                    {
                        sb.Append($"\n  live={live} sessionGen={(sg == 10 ? "line's" : "older")}: no proposal {C(false, -1, null, live, sg)}");
                        foreach (int pg in new[] { 8, 9, 10 })
                            foreach (string pn in new[] { "A", "B" })
                                sb.Append($"; p(gen {(pg == 10 ? "same" : pg == 9 ? "-1" : "-2")},{(pn == "A" ? "same name" : "other name")}) {C(true, pg, pn, live, sg)}");
                    }
                sb.Append("\nProposalCommits(proposalGen, name, passive, lineGen=10, \"A\"):");
                foreach (int pg in new[] { 8, 9, 10, 11 })
                    foreach (string pn in new[] { "A", "B", "" })
                        sb.Append($" ({pg},\"{pn}\") {DialogueCameraSessionRules.ProposalCommits(pg, pn, false, 10, "A")}/{DialogueCameraSessionRules.ProposalCommits(pg, pn, true, 10, "A")}");
                float grace = Try(() => Cam.settings.releaseGraceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } g && float.TryParse(g, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gv) ? gv : 0.5f;
                sb.Append($"\nShouldRelease(uiExists=True, talking, options, uiGen, sessionGen, hasPending, idle, grace={grace}); answers for idle=0 / idle=grace+1:");
                foreach (bool talking in new[] { true, false })
                    foreach (bool opts in new[] { false, true })
                        foreach (bool sameGen in new[] { true, false })
                            foreach (bool pending in new[] { false, true })
                                sb.Append($"\n  talking={talking} options={opts} uiGen{(sameGen ? "==" : "!=")}session pending={pending}: "
                                    + $"{DialogueCameraSessionRules.ShouldRelease(true, talking, opts, sameGen ? 10 : 11, 10, pending, 0f, grace)}/{DialogueCameraSessionRules.ShouldRelease(true, talking, opts, sameGen ? 10 : 11, 10, pending, grace + 1f, grace)}");
                sb.Append($"\n  no ui: {DialogueCameraSessionRules.ShouldRelease(false, false, false, 10, 10, false, 0f, grace)}");
                return sb.ToString();
            }
            case "campatch":
                PatchCam(a.Length > 1 && a[1] == "2");
                return $"camera log patched (first {_camPatched}, second {_camPatched2})";
            case "camsuspend":
                _suspend = a.Length < 2 || a[1] != "off";
                _susDepth = 0; _susDid = false;
                if (_suspend) PatchCam(false);
                return $"suspend {(_suspend ? "on" : "off")}";
        }
        return null;
    }
}
#endif
