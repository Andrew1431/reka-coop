using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// One conversation, run once, on the host. Whoever starts it leads it: a guest who talks to a villager (or uses
// anything else that moves the story on, like handing berries to the wood creatures) has the host's copy of that
// thing do it instead, so the story, rewards and quest steps happen once, in the host's world.
//
// The whole group is in every conversation, and the starter steers it. When it starts, everyone else is moved to stand
// beside the starter (and stays there afterwards: the group keeps together). Every line the host's dialogue box shows
// is a real line on every guest's box, and everyone sees the starter's camera (TalkCamera). Only the starter moves it
// on: the host with the game's own input, a guest starter by continuing past the line on their box (which runs the
// host's pending continue: the next line, the options, or the end); everyone else's continue does nothing. The starter
// picks the options; everyone else sees the same panel, read-only, with the starter's highlighted option. When the
// starter skips the rest of a line's typing, everyone's line shows in full too. It ends for everyone when it ends on
// the host.
public static class DialogueSync
{
    private enum Kind : byte
    {
        Line = 1, Options = 2, Chosen = 3, End = 4,  // host -> guests
        Camera = 5,                                    // host -> guests: speaker head path, speaker name
        Pick = 6, Next = 7,                            // guest -> host (8 was Talk: now MsgType.Intent)
        State = 9,                                     // host -> guests, every second: where the conversation is
        Gather = 10,                                   // host -> guests: where each player stands for the talk
        Cam = 11,                                      // the starter's camera pose (a guest's goes through the host)
        Skip = 12,                                     // the starter showed the rest of a line at once (line gen, index)
        Hover = 13,                                    // the option the starter has highlighted
        Passive = 14,                                  // host -> guests: a passive line (a monologue) shown here
        Start = 15,                                    // host -> guests: a whole dialogue (every line of one generation), run as one session
        Index = 16,                                    // host -> guests: the line that dialogue is at now
    }

    // The conversation as the host has it. Guests check theirs against it every second, so a lost or late message
    // can't leave anyone stuck in a line, a camera or an options panel.
    private enum Phase : byte { Idle = 0, Line = 1, Options = 2 }

    private const float RequestWindow = 3f, NextCooldown = 0.3f, StateEvery = 1f;

    private static int _lastGeneration = -1, _lastIndex = -1;
    private static bool _talking;
    private static float _quietSince = -1;  // the options panel can take a frame or two to open or to hand over to the next line
    private static List<string> _offered;

    // Host: who leads the conversation on screen (-1 none), and the last guest request that may be starting one.
    private static int _leader = -1, _requestedBy = -1;
    private static bool _requestAct;
    private static float _requestedAt = -10, _nextAt;
    private static bool _remotePick;
    private static Vector3? _talkAt;   // where the conversation is (what a guest asked for, or the speaker's head)
    private static (int gen, int index) _nextFor = (-1, -1);   // a guest's continue that came while the line was still revealing
    private static float _nextState;

    // Host: what the dialogue box will do when the host presses continue (the next line, or at the last one the options
    // or the end), and for which line. A guest's continue does the same.
    private static Il2CppSystem.Action _waiting;
    private static (int gen, int index) _waitingLine;

    internal static bool Continue()
    {
        _nextFor = (-1, -1);
        var ui = DialogueUILogic.Instance;
        if (ui == null || !ui.IsTalking || ui.currentDialogueIsPassive || DialogueOptionsPanel.IsShowing) return false;
        if (_waiting == null || _waitingLine != (ui.DialogueGeneration, ui.indexInDialogue)) return false;  // still revealing
        var go = _waiting;
        _waiting = null;
        go.Invoke();
        return true;
    }

    // Someone else steers: their own continue input mustn't move the line on, so it gets an action that does nothing
    // (the host keeps the real one for the starter's continue).
    internal static void OnWaitingForContinue(DialogueUILogic ui, ref Il2CppSystem.Action action)
    {
        if (_one is { } o && CoopDriver.Session?.Role == Role.Client && ui.DialogueGeneration == o.LocalGen)
        {
            if (action != null && o.StandIn != null && action.Pointer == o.StandIn.Pointer) return;   // our own, waiting again
            o.Held = action;
            o.HeldFor = (o.LocalGen, ui.indexInDialogue);
            action = _leadingTalk ? o.StandIn = StandInFor(o, ui.indexInDialogue) : Nothing;
            return;
        }
        if (CoopDriver.Session?.Role != Role.Client)
        {
            _waiting = action;
            _waitingLine = (ui.DialogueGeneration, ui.indexInDialogue);
        }
        if (WatchingTalk) action = Nothing;
    }

    private static readonly Il2CppSystem.Action Nothing = (System.Action)(() => { });

    // One session: every dialogue the host's box runs is sent whole when it starts, and every other copy runs it as
    // one dialogue of its own: the box stays up from line to line and the camera keeps its session. Only the line
    // number is synced after that. A talk of several dialogues (a villager's greeting, the options, an answer, the
    // options again, the goodbye) is the same chain here: each next dialogue starts out of the one before it.
    internal static bool OneSession = true;   // dev: off sends every talk line by line (Kind.Line, FollowNow)
    private static int _oneGen = -1, _pathGen = -1;   // host: the generation sent whole; the one a path was chosen for
    private static (Transform head, string name)? _proposed;   // host: the speaker the camera was given for the dialogue about to start
    private static readonly List<int> _joined = new();          // host: players who came in since the last tick

    private static void ChoosePath(DialogueUILogic ui, int gen)
    {
        _oneGen = OneSession ? gen : -1;
        Plugin.Logger.LogInfo(OneSession ? $"Dialogue: one session, generation {gen}, {ui.currentDialogue.Length} lines{(_proposed is { } p ? ", camera on " + p.name : "")}"
            : $"Dialogue: line by line, generation {gen} (one session is switched off)");
    }

    private static void SendIndex(CoopSession session, int gen, int index)
    {
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Index);
        w.Put(gen);
        w.Put(index);
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // to: one player (someone who joined mid-talk), or everyone.
    private static void SendStart(CoopSession session, DialogueUILogic ui, int gen, int index, bool passive, (Transform head, string name)? speaker, int to = -1)
    {
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Start);
        w.Put(_leader);
        w.Put(gen);
        w.Put(index);
        w.Put(passive);
        w.Put(speaker is { head: not null } s ? ScenePath.Of(s.head) : "");
        w.Put(speaker?.name ?? "");
        int n = ui.currentDialogue.Length;
        w.Put(n);
        for (int i = 0; i < n; i++)
        {
            w.Put(At(ui.currentDialogue, i));
            w.Put(At(ui.namesOfPersonTalking, i));
            w.Put(At(ui.titlesOfPersonTalking, i));
            w.Put(At(ui.emotionsOfLines, i));
        }
        if (to >= 0) session.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Host: someone joined while a talk is on: they get the dialogue from the line it is at, and the open options.
    private static void SendToJoined(CoopSession session, DialogueUILogic ui, bool talking)
    {
        if (_joined.Count == 0) return;
        foreach (int id in _joined)
        {
            if (!_talking) continue;
            int gen = ui.DialogueGeneration, index = ui.indexInDialogue;
            bool whole = talking && _oneGen == gen && index >= 0 && index < ui.currentDialogue.Length;
            if (whole) SendStart(session, ui, gen, index, false, _speakerSent, id);
            if (DialogueOptionsPanel.IsShowing && _offered != null) SendOptions(session, id);
            Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(id)} joined mid-talk; sent {(whole ? $"generation {gen} from line {index}" : "no dialogue")}{(DialogueOptionsPanel.IsShowing ? " and the options" : "")}");
        }
        _joined.Clear();
    }

    // Guest: the host's dialogue as it runs here.
    private sealed class One
    {
        public int TalkId, From, LocalGen = -1, HostIndex, Starts;
        public (string text, string name, string title, string emotion)[] Lines;
        public Transform Head;                      // this copy's speaker, when the host's camera was given one for it
        public string Speaker;
        public Il2CppSystem.Action Held, StandIn;   // what the box would run on continue; what it was given instead
        public (int gen, int index) HeldFor;
        public float StartedAt;
        public readonly List<Il2CppSystem.Action> Keep = new();
    }

    // _next: the host's next dialogue, waiting for the box to hold the last line's step of the one on screen.
    private static One _one, _next;
    private static float _nextBy;
    internal static bool InOneSession => _one != null;

    // An empty branch for a dialogue started here. The wrapper's own Nullable<DialogueBranch>(value) constructor stores
    // a wrong action pointer (the game crashes when the last line runs it), so the two fields are written in place.
    private static int _branchHasAt = -1, _branchValueAt;
    private static bool _branchBroken;

    private static Il2CppSystem.Nullable<DialogueBranch> BranchTo(Il2CppSystem.Action action)
    {
        var n = new Il2CppSystem.Nullable<DialogueBranch>();
        if (_branchBroken) return n;
        try
        {
            if (_branchHasAt < 0)
            {
                int header = 2 * System.IntPtr.Size, has = -1, value = -1;
                var klass = Il2CppInterop.Runtime.Il2CppClassPointerStore<Il2CppSystem.Nullable<DialogueBranch>>.NativeClassPtr;
                var iter = System.IntPtr.Zero;
                System.IntPtr f;
                while ((f = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_fields(klass, ref iter)) != System.IntPtr.Zero)
                {
                    string name = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_name(f)) ?? "";
                    int at = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(f) - header;
                    if (name.ToLowerInvariant().Contains("hasvalue")) has = at;
                    else if (name == "value") value = at;
                }
                if (has < 0 || value < 0) throw new System.InvalidOperationException($"fields not found (hasValue@{has}, value@{value})");
                _branchHasAt = has;
                _branchValueAt = value;
            }
            var p = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_unbox(n.Pointer);
            System.Runtime.InteropServices.Marshal.WriteIntPtr(p, _branchValueAt, action.Pointer);
            System.Runtime.InteropServices.Marshal.WriteByte(p, _branchHasAt, 1);
            if (!n.HasValue) throw new System.InvalidOperationException("it reads back empty");
            return n;
        }
        catch (System.Exception e)
        {
            _branchBroken = true;
            Plugin.Logger.LogWarning($"Dialogue: can't build a dialogue branch here ({e.Message}); dialogues that follow each other will blink");
            return new Il2CppSystem.Nullable<DialogueBranch>();
        }
    }

    // chained: the dialogue on screen has just run its last line's step, and this one goes on from it in the same frame
    // (the box stays up and the camera keeps its session).
    private static void StartOne(int from, bool chained = false)
    {
        var ui = DialogueUILogic.Instance;
        var o = _one;
        if (ui == null || o == null || !PlayerSync.InWorld) return;
        if (!chained) CloseLine();
        o.From = from;
        o.Held = o.StandIn = null;
        o.StartedAt = Time.unscaledTime;
        o.Starts++;
        int n = o.Lines.Length - from;
        if (n <= 0) return;
        // The camera takes a speaker only for a dialogue starting in the same frame. A dialogue the host's camera got
        // no speaker for gets none here either (the camera then lets go, as it does there).
        _camera ??= Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include);
        if (o.Head != null && _camera != null) _camera.Activate(o.Head, o.Speaker);
        Il2CppStringArray Col(System.Func<(string text, string name, string title, string emotion), string> f)
        {
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = f(o.Lines[from + i]) ?? "";
            return new Il2CppStringArray(a);
        }
        // Always with a branch that does nothing: the last line then waits (for the host's next dialogue, its options
        // or its end) instead of closing the box, and a next dialogue can go on from it without the box hiding.
        ui.RequestStartDialogue(Col(l => l.text), Col(l => l.name), Col(l => l.title), null, BranchTo(Nothing), false, false, Col(l => l.emotion), null);
        o.LocalGen = ui.DialogueGeneration;
        if (!ui.IsTalking || ui.currentDialogue == null || ui.currentDialogue.Length != n)
            Plugin.Logger.LogWarning($"Dialogue: one session {o.TalkId}: the box didn't take it (talking={ui.IsTalking}, lines={ui.currentDialogue?.Length}, queue={ui.queue?.Count})");
    }

    // Guest: the host started its next dialogue while one of this talk is on the box here. On its last line, the box
    // runs that line's own step (the empty branch) and the next dialogue starts in the same frame; the step is only
    // there once the line has finished typing, so that is waited for, briefly. Anywhere else the box is closed first.
    private static bool ChainNext(DialogueUILogic ui)
    {
        var o = _one;
        bool chained = false;
        if (o != null && ui.IsTalking && ui.DialogueGeneration == o.LocalGen && !ui.currentDialogueIsPassive && ui.currentDialogue != null
            && ui.indexInDialogue == ui.currentDialogue.Length - 1 && Time.unscaledTime < _nextBy)
        {
            if (o.Held == null || o.HeldFor != (o.LocalGen, ui.indexInDialogue))
            {
                if (ui.talker != null && ui.talker.IsRevealing)
                {
                    _applyingSkip = true;
                    try { ui.talker.typewriter?.SkipTypewriter(); }
                    finally { _applyingSkip = false; }
                }
                return false;
            }
            var go = o.Held;
            o.Held = null;
            go.Invoke();
            chained = true;
        }
        _one = _next;
        _next = null;
        Plugin.Logger.LogInfo($"Dialogue: one session {_one.TalkId} {(chained ? "goes on from" : "replaces")} {(o == null ? "nothing" : o.TalkId.ToString())}");
        StartOne(_one.HostIndex, chained);
        return true;
    }

    // Guest: a passive dialogue of the host's (Reka's own thoughts, started by whoever walked up) runs here whole, on
    // this box's own timing.
    private static void StartPassive((string text, string name, string title, string emotion)[] lines, int from)
    {
        var ui = DialogueUILogic.Instance;
        int n = lines.Length - from;
        if (ui == null || n <= 0 || !PlayerSync.InWorld) return;
        Il2CppStringArray Col(System.Func<(string text, string name, string title, string emotion), string> f)
        {
            var a = new string[n];
            for (int i = 0; i < n; i++) a[i] = f(lines[from + i]) ?? "";
            return new Il2CppStringArray(a);
        }
        ui.RequestStartDialogue(Col(l => l.text), Col(l => l.name), Col(l => l.title), null, new Il2CppSystem.Nullable<DialogueBranch>(), true, false, Col(l => l.emotion), null);
        Plugin.Logger.LogInfo($"Dialogue: passive, {n} lines: {lines[from].name}: {lines[from].text}");
    }

    // Guest, every frame: move the box on to the host's line by running what its own continue would run.
    private static void TickOne()
    {
        var o = _one;
        var ui = DialogueUILogic.Instance;
        if (ui == null) return;
        if (_next != null && !ChainNext(ui)) return;
        o = _one;
        if (o == null) return;
        if (!ui.IsTalking || ui.DialogueGeneration != o.LocalGen || ui.currentDialogueIsPassive) return;   // Reconcile starts it again if the host is still on it
        int local = ui.indexInDialogue;
        _hostLine = (o.TalkId, o.From + local);
        if (o.From + local >= o.HostIndex) return;
        if (o.Held != null && o.HeldFor == (o.LocalGen, local))
        {
            var go = o.Held;
            o.Held = null;
            go.Invoke();   // mid-talk this is the box's own next line
        }
        else if (ui.talker != null && ui.talker.IsRevealing)
        {
            _applyingSkip = true;
            try { ui.talker.typewriter?.SkipTypewriter(); }
            finally { _applyingSkip = false; }
        }
    }

    // Guest starter: its continue asks the host for the next line, and waits again: a line nobody waits on closes
    // itself within a few frames, and the host's answer takes longer. Waits left over from earlier lines get the same
    // press (the game doesn't stop them): each knows its line and does nothing once the box has moved on.
    private static Il2CppSystem.Action StandInFor(One o, int index)
    {
        int gen = o.LocalGen;
        Il2CppSystem.Action standIn = null;
        standIn = (System.Action)(() =>
        {
            var ui = DialogueUILogic.Instance;
            if (ui == null || _one != o || o.StandIn == null || o.StandIn.Pointer != standIn.Pointer) return;
            if (!ui.IsTalking || ui.DialogueGeneration != gen || ui.indexInDialogue != index) return;
            var session = CoopDriver.Session;
            if (_leadingTalk && session != null && session.IsConnected)
            {
                var w = session.BeginMessage(MsgType.Dialogue);
                w.Put((byte)Kind.Next);
                w.Put(o.TalkId);
                w.Put(o.From + index);
                session.Send(w, DeliveryMethod.ReliableOrdered);
            }
            ui.StartCoroutine(ui.WaitForPlayerInputThenDoSth(standIn));
        });
        o.Keep.Add(standIn);
        return standIn;
    }

    // The starter skipped the typing: everyone else's line shows in full.
    private static bool _applyingSkip;
    private static (int gen, int index) _skipFor = (-1, -1);

    internal static void OnSkipped(Febucci.TextAnimatorForUnity.TypewriterComponent tw)
    {
        var session = CoopDriver.Session;
        if (_applyingSkip || session == null || !session.IsConnected || !LeadingTalk) return;
        var ui = DialogueUILogic.Instance;
        if (ui == null || ui.talker == null || ui.talker.typewriter == null || ui.talker.typewriter.Pointer != tw.Pointer) return;
        (int gen, int index) line = session.Role == Role.Client ? _hostLine : (ui.DialogueGeneration, ui.indexInDialogue);
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Skip);
        w.Put(line.gen);
        w.Put(line.index);
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // A skip for the line on screen (or about to be: a guest's line starts a moment after the host's message).
    private static void ApplySkip()
    {
        if (_skipFor.gen < 0) return;
        var ui = DialogueUILogic.Instance;
        (int gen, int index) line = CoopDriver.Session?.Role == Role.Client ? _hostLine : ui == null ? (-1, -1) : (ui.DialogueGeneration, ui.indexInDialogue);
        if (line != _skipFor || !WatchingTalk) { if (line.gen > _skipFor.gen || line.index > _skipFor.index && line.gen == _skipFor.gen) _skipFor = (-1, -1); return; }
        if (ui?.talker == null || !ui.talker.IsRevealing || _pending != null) return;
        _skipFor = (-1, -1);
        _applyingSkip = true;
        try { ui.talker.typewriter?.SkipTypewriter(); }
        finally { _applyingSkip = false; }
    }

    // The option the starter has highlighted, and on everyone else's panel the one they're shown.
    private static int _hoverSent = -1, _hoverWanted = -1;

    private static int Highlighted(DialogueOptionsPanel panel)
    {
        var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
        if (sel == null || panel.spawnedRows == null) return -1;
        for (int i = 0; i < panel.spawnedRows.Count; i++)
            if (panel.spawnedRows[i] != null && panel.spawnedRows[i].gameObject.Pointer == sel.Pointer) return i;
        return -1;
    }

    private static void TickHover(CoopSession session)
    {
        var panel = DialogueOptionsPanel.IsShowing ? Panel() : null;
        if (panel == null) { _hoverSent = -1; return; }
        if (LeadingTalk)
        {
            int i = Highlighted(panel);
            if (i < 0 || i == _hoverSent) return;
            _hoverSent = i;
            var w = session.BeginMessage(MsgType.Dialogue);
            w.Put((byte)Kind.Hover);
            w.Put(i);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
        else if (WatchingTalk && _hoverWanted >= 0 && panel.spawnedRows != null && _hoverWanted < panel.spawnedRows.Count && Highlighted(panel) != _hoverWanted)
        {
            var row = panel.spawnedRows[_hoverWanted];
            if (row == null || !row.gameObject.activeInHierarchy) return;
            UnityEngine.EventSystems.EventSystem.current?.SetSelectedGameObject(row.gameObject);
            panel.MoveHighlightToCurrentSelection(false);
        }
    }

    // In a conversation, steering it / watching someone else steer it.
    internal static bool LeadingTalk => CoopDriver.Session?.Role == Role.Client ? InTalkHere && _leadingTalk : _talking && _leader == Me;
    internal static bool WatchingTalk => CoopDriver.Session?.Role == Role.Client ? InTalkHere && !_leadingTalk : _talking && _leader >= 0 && _leader != Me;
    private static bool InTalkHere => _following || _leading || _watching;  // guest: a line or the options of the host's talk
    internal static int Leader => _leader;
    private static (int id, float at) _lastLeader = (-1, -100f);
    // Host: who leads the conversation now, or led the one that ended in the last few seconds (a choice that follows its
    // last line belongs to them); -1 if none.
    internal static int RecentLeader => _talking && _leader >= 0 ? _leader : Time.unscaledTime - _lastLeader.at < 5f ? _lastLeader.id : -1;
    internal static void PutCamKind(NetDataWriter w) => w.Put((byte)Kind.Cam);

    private static Transform _cameraSentFor;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) =>
        {
            if (type != MsgType.Dialogue) return;
            if (session.Role == Role.Client) OnMessage(session, r);
            else if (session.Role == Role.Host) OnRequest(session, from, r);
        };
        session.PlayerLeft += id => { if (_leader == id) _leader = 0; };
        session.PlayerJoined += id => { if (session.Role == Role.Host && _talking) _joined.Add(id); };
    }

    internal static int Me => CoopDriver.Session?.LocalPlayerId ?? 0;

    public static void Tick(CoopSession session)
    {
        if (session.Role == Role.Client && Time.unscaledTime < _strayUntil) CloseStrayLine();
        if (session.Role == Role.Client) { ShowAfterTalkNow(); ShowPending(); TickOne(); WatchLeaving(session); TalkCamera.Tick(session); ApplySkip(); TickHover(session); return; }
        if (session.Role != Role.Host || !session.IsConnected) return;
        var ui = DialogueUILogic.Instance;
        if (ui == null) return;
        RunPendingDefault(ui);
        bool talking = ui.IsTalking && !ui.currentDialogueIsPassive && ui.currentDialogue != null;
        if (_personal)
        {
            if (!talking && !DialogueOptionsPanel.IsShowing) _personal = false;
            return;
        }
        if (talking || DialogueOptionsPanel.IsShowing) _quietSince = -1;
        SendToJoined(session, ui, talking);
        if (_nextFor.gen >= 0 && _waiting != null && _waitingLine == _nextFor) Continue();
        TalkCamera.Tick(session);
        ApplySkip();
        TickHover(session);
        SendState(session, ui, talking);
        SendPassive(session, ui);
        if (talking)
        {
            int gen = ui.DialogueGeneration, index = ui.indexInDialogue;
            if (gen == _lastGeneration && index == _lastIndex) return;
            _lastGeneration = gen;
            _lastIndex = index;
            if (index < 0 || index >= ui.currentDialogue.Length) return;
            bool start = _pathGen != gen;
            if (start) { _pathGen = gen; _speakerSent = SpeakerOf(gen); ChoosePath(ui, gen); }
            Begin(session);
            if (_oneGen == gen)
            {
                if (start) SendStart(session, ui, gen, index, false, _speakerSent);
                else SendIndex(session, gen, index);
                return;
            }
            var w = session.BeginMessage(MsgType.Dialogue);
            w.Put((byte)Kind.Line);
            w.Put(_leader);
            w.Put(gen);
            w.Put(index);
            w.Put(At(ui.namesOfPersonTalking, index));
            w.Put(At(ui.titlesOfPersonTalking, index));
            w.Put(At(ui.currentDialogue, index));
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
        else if (_talking && !DialogueOptionsPanel.IsShowing)
        {
            if (_quietSince < 0) _quietSince = Time.unscaledTime;
            if (Time.unscaledTime - _quietSince < 0.5f) return;
            _talking = false;
            _oneGen = -1;
            _lastIndex = -1;
            _lastLeader = (_leader, Time.unscaledTime);
            _leader = -1;
            _nextFor = (-1, -1);
            _talkAt = null;
            _cameraSentFor = null;
            _proposed = _speakerSent = null;
            TalkCamera.Release();
            var w = session.BeginMessage(MsgType.Dialogue);
            w.Put((byte)Kind.End);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
    }

    // Host: passive lines (Reka's own thoughts at the Lutki, started by whoever walked up; they run here like every
    // story dialogue) are shown on every copy too, or a guest who set one off never sees it.
    private static (int gen, int index) _passiveSent = (-1, -1);

    private static void SendPassive(CoopSession session, DialogueUILogic ui)
    {
        if (!ui.IsTalking || !ui.currentDialogueIsPassive || ui.currentDialogue == null) return;
        var line = (ui.DialogueGeneration, ui.indexInDialogue);
        if (line == _passiveSent || line.Item2 < 0 || line.Item2 >= ui.currentDialogue.Length) return;
        bool first = line.Item1 != _passiveSent.gen;
        _passiveSent = line;
        if (OneSession)
        {
            if (!first) return;   // sent whole; it runs on each copy's own timing
            Plugin.Logger.LogInfo($"Dialogue: passive, generation {line.Item1}, {ui.currentDialogue.Length} lines");
            SendStart(session, ui, line.Item1, line.Item2, true, null);
            return;
        }
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Passive);
        w.Put(At(ui.namesOfPersonTalking, line.Item2));
        w.Put(At(ui.titlesOfPersonTalking, line.Item2));
        w.Put(At(ui.currentDialogue, line.Item2));
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Host: a conversation is starting; it belongs to the guest who just asked for it, else to the host.
    // A talk that only concerns the one talking (the mirror: it opens character customization for whoever uses it) runs
    // on their own copy: a guest's isn't sent to the host, and the host's isn't shared or gathered.
    private static bool _personal;

    internal static bool Personal(Component c)
    {
        if (c == null) return false;
        var t = c.transform;
        while (t != null && t.name != "CharacterCustomization") t = t.parent;  // the mirror block's object; no component of that type
        if (t == null) return false;
        if (CoopDriver.Session?.Role == Role.Host) _personal = true;
        Plugin.Logger.LogInfo($"Dialogue: {ScenePath.Of(c.transform)} is personal, runs here only");
        return true;
    }

    // Host: a talk about to start was set off by this player (they walked into a player detector). The talker may walk
    // over first, so the request holds a while longer than a guest's ask.
    // `act`: something the player did on purpose (picked up, put down, used a tool, pressed interact). While that is
    // fresh, someone merely walking into a detector doesn't take the talk it sets off away from them (seen: the host
    // standing by the old woman led the talk that a guest's chicken in the nest started).
    internal static void NoteRequest(int playerId, float hold, bool act = false)
    {
        if (!act && _requestAct && _requestedBy >= 0 && playerId != _requestedBy && Time.unscaledTime - _requestedAt < RequestWindow) return;
        _requestAct = act;
        _requestedBy = playerId;
        _requestedAt = Time.unscaledTime + hold;
    }

    // Host: an NPC's AI is starting a talk for the player it sees. An AI that sees nobody (a chicken settling in the nest
    // a guest put it in) leaves a guest's fresh request standing: what that guest did set it off.
    internal static void NoteAiStart(int seen)
    {
        if (seen < 0 && _requestedBy >= 0 && Time.unscaledTime - _requestedAt < RequestWindow) return;
        NoteRequest(seen >= 0 ? seen : Me, 0f);
    }

    private static void Begin(CoopSession session)
    {
        if (_talking) return;
        _talking = true;
        _leader = _requestedBy >= 0 && Time.unscaledTime - _requestedAt < RequestWindow ? _requestedBy : Me;
        // While the host drives the hut the guests ride along with their keys off: a talk led by one of them (the hut's
        // clucking when travel is confirmed, which a guest's walk through some detector had "set off") could not be
        // clicked on by anyone. The driver leads.
        if (_leader != Me && HutSync.HostDriving)
        {
            Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(_leader)} is a passenger while the host drives; leading it here");
            _leader = Me;
        }
        // A guest still loading (the arrival talk after travelling starts at once) couldn't click on: nobody could.
        if (_leader != Me && !PlayerSync.InWorldNow(_leader))
        {
            Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(_leader)} isn't in the world yet; leading it here");
            _leader = Me;
        }
        _requestedBy = -1;
        _requestAct = false;
        Plugin.Logger.LogInfo($"Dialogue: conversation led by {NameTags.NameOf(_leader)}");
        Gather(session);
    }

    // Host: everyone but the starter stands in a row beside the starter, facing the conversation (not behind them, where
    // the dialogue camera looks over the starter's shoulder).
    private static void Gather(CoopSession session)
    {
        var lead = _leader == Me ? Find.One<PlayerMovement_2021>()?.transform : PlayerSync.PuppetRoot(_leader);
        if (lead == null) { Plugin.Logger.LogWarning($"Dialogue: no body for {NameTags.NameOf(_leader)}; nobody moved"); return; }
        var at = lead.position;
        var fwd = Vector3.ProjectOnPlane(_talkAt is { } t ? t - at : lead.forward, Vector3.up);
        if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.ProjectOnPlane(lead.forward, Vector3.up);
        fwd.Normalize();
        var right = Vector3.Cross(Vector3.up, fwd);
        var look = _talkAt ?? at + fwd * 3f;
        var ids = new List<int>();
        if (_leader != Me) ids.Add(Me);
        foreach (var p in session.Players.Values) if (p.Ready && p.PlayerId != _leader) ids.Add(p.PlayerId);
        // Everyone comes, however far off. The wrong player leading, which once pulled the
        // host across the map, is handled where the leader is picked (the host's own key press leads).
        var spots = new List<(int id, Vector3 pos, float yaw)>();
        for (int i = 0; i < ids.Count; i++)
        {
            float side = i % 2 == 0 ? 1f : -1f, rank = i / 2 + 1;
            var pos = Ground(at + right * (side * 1.6f * rank) + fwd * 0.2f, at.y);
            spots.Add((ids[i], pos, Quaternion.LookRotation(Vector3.ProjectOnPlane(look - pos, Vector3.up)).eulerAngles.y));
        }
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Gather);
        w.Put(spots.Count);
        foreach (var (id, pos, yaw) in spots) { w.Put(id); w.Put(pos.x); w.Put(pos.y); w.Put(pos.z); w.Put(yaw); }
        session.Send(w, DeliveryMethod.ReliableOrdered);
        foreach (var (id, pos, yaw) in spots) if (id == Me) MoveHere(pos, yaw);
    }

    // The ground under a spot: of the surfaces below 3 m up, the one nearest the starter's height (not a roof or a
    // treetop).
    private static Vector3 Ground(Vector3 p, float nearY)
    {
        var best = p;
        best.y = nearY;
        float bestD = 3f;
        foreach (var hit in Physics.RaycastAll(p + Vector3.up * 3f, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
        {
            float d = Mathf.Abs(hit.point.y - nearY);
            if (d < bestD && hit.collider.GetComponentInParent<PlayerMovement_2021>() == null) { bestD = d; best.y = hit.point.y; }
        }
        best.y += 0.05f;
        return best;
    }

    private static void MoveHere(Vector3 pos, float yaw)
    {
        var me = Find.One<PlayerMovement_2021>();
        if (me == null) return;
        var rb = me.GetComponent<Rigidbody>();
        if (rb != null) { rb.position = pos; rb.linearVelocity = Vector3.zero; }
        me.transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
        Plugin.Logger.LogInfo($"Dialogue: moved beside the starter ({pos.x:0.0}, {pos.y:0.0}, {pos.z:0.0})");
    }

    private static string At(Il2CppStringArray a, int i) => a != null && i < a.Length && a[i] != null ? a[i] : "";

    internal static void OnOptionsShown(Il2CppSystem.Collections.Generic.List<string> labels, Il2CppSystem.Collections.Generic.List<bool> heard, bool allowExit)
    {
        var session = CoopDriver.Session;
        if (session?.Role != Role.Host || !session.IsConnected || labels == null) return;
        Begin(session);  // so guests are told when the panel closes with nobody talking
        _offered = new List<string>();
        _offeredHeard = new List<bool>();
        for (int i = 0; i < labels.Count; i++)
        {
            _offered.Add(labels[i] ?? "");
            _offeredHeard.Add(heard != null && i < heard.Count && heard[i]);
        }
        _offeredExit = allowExit;
        _hoverWanted = _hoverSent = -1;
        SendOptions(session);
        if (_leader != Me) CoopDriver.Notice($"{NameTags.NameOf(_leader)} is choosing what to say.");
    }

    private static List<bool> _offeredHeard;
    private static bool _offeredExit;

    private static void SendOptions(CoopSession session, int to = -1)
    {
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Options);
        w.Put(_leader);
        w.Put(_offeredExit);
        w.Put(_offered.Count);
        for (int i = 0; i < _offered.Count; i++)
        {
            w.Put(_offered[i]);
            w.Put(_offeredHeard[i]);
        }
        if (to >= 0) session.SendTo(to, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // False when the pick isn't this player's to make.
    internal static bool OnOptionPicked(int index)
    {
        var session = CoopDriver.Session;
        if (session?.Role == Role.Client && _watching)
        {
            CoopDriver.Notice($"{NameTags.NameOf(_optionsLeader)} is choosing what to say.");
            return false;
        }
        // Guest starter: the game's own pick would close the box here, and the host's answer would open a new one a
        // moment later (the camera lets go if that moment is longer than its 0.1 s). The pick goes to the host from
        // here instead and only the panel closes: the box stays on its line and the answer goes on from it.
        if (session?.Role == Role.Client && _leading && _one != null && session.IsConnected)
        {
            SendPick(session, index);
            if (DialogueOptionsPanel.IsShowing) Panel()?.Close();
            return false;
        }
        if (session?.Role != Role.Host || !session.IsConnected) return true;
        if (_talking && _leader >= 0 && _leader != Me && !_remotePick)
        {
            CoopDriver.Notice($"{NameTags.NameOf(_leader)} is choosing what to say.");
            return false;
        }
        if (_offered == null || index < 0 || index >= _offered.Count) return true;
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Chosen);
        w.Put(_leader >= 0 ? _leader : Me);
        w.Put(_offered[index]);
        session.Send(w, DeliveryMethod.ReliableOrdered);
        return true;
    }

    // Host: the dialogue camera turned to a speaker; guests turn to their own copy of them.
    internal static void OnCameraActivated(Transform head, string speaker)
    {
        var session = CoopDriver.Session;
        if (session?.Role != Role.Host || !session.IsConnected || head == null) return;
        _proposed = (head, speaker ?? "");
        _talkAt ??= head.position;
        if (OneSession || head == _cameraSentFor) return;   // one session: the speaker goes with the dialogue (Start)
        _cameraSentFor = head;
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Camera);
        w.Put(ScenePath.Of(head));
        w.Put(speaker ?? "");
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Host: the speaker of the dialogue that just started, as the game's camera has it: the session it committed for
    // this generation, or the proposal it still holds for it. None: the camera wasn't given one (a line said from afar).
    private static (Transform head, string name)? _speakerSent;

    private static (Transform head, string name)? SpeakerOf(int gen)
    {
        _camera ??= Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include);
        if (_camera == null) return null;
        if (_camera._live && _camera._sessionGeneration == gen && _camera._sessionHead != null) return (_camera._sessionHead, _camera._sessionSpeakerName ?? "");
        return _camera._hasProposal && _proposed is { head: not null } p ? p : null;
    }

    // Host: the conversation's state, every second and whenever it changes.
    private static (Phase, int, int, int) _stateSent;

    private static void SendState(CoopSession session, DialogueUILogic ui, bool talking)
    {
        var phase = DialogueOptionsPanel.IsShowing && _talking ? Phase.Options : talking && _talking ? Phase.Line : Phase.Idle;
        var state = (phase, _talking ? _leader : -1, talking ? ui.DialogueGeneration : -1, talking ? ui.indexInDialogue : -1);
        if (state == _stateSent && Time.unscaledTime < _nextState) return;
        _stateSent = state;
        _nextState = Time.unscaledTime + StateEvery;
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.State);
        w.Put((byte)state.Item1);
        w.Put(state.Item2);
        w.Put(state.Item3);
        w.Put(state.Item4);
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Host: a guest's request.
    private static void OnRequest(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        switch ((Kind)r.GetByte())
        {
            case Kind.Pick:
            {
                int index = r.GetInt();
                if (from.PlayerId != _leader || !DialogueOptionsPanel.IsShowing) return;
                var panel = Panel();
                if (panel == null) return;
                _remotePick = true;
                try
                {
                    if (index >= 0) panel.OnOptionPicked(index);
                    else panel.CloseAndEndDialogue();  // the leader left the options
                }
                finally { _remotePick = false; }
                break;
            }
            case Kind.Next:
            {
                var line = (r.GetInt(), r.GetInt());
                var ui = DialogueUILogic.Instance;
                if (from.PlayerId != _leader) return;  // only the starter steers
                if (ui == null || line != (ui.DialogueGeneration, ui.indexInDialogue)) return;  // already moved past it
                if (_waiting != null && _waitingLine == line) { if (Time.unscaledTime >= _nextAt && Continue()) _nextAt = Time.unscaledTime + NextCooldown; }
                else _nextFor = line;  // still revealing: continue once it waits
                break;
            }
            case Kind.Cam:
                TalkCamera.OnMessage(session, from, r);
                break;
            case Kind.Skip:
            {
                if (from.PlayerId != _leader) return;
                _skipFor = (r.GetInt(), r.GetInt());
                var w = session.BeginMessage(MsgType.Dialogue);
                w.Put((byte)Kind.Skip);
                w.Put(_skipFor.gen);
                w.Put(_skipFor.index);
                session.Send(w, DeliveryMethod.ReliableOrdered, from.PlayerId);
                break;
            }
            case Kind.Hover:
            {
                if (from.PlayerId != _leader) return;
                _hoverWanted = r.GetInt();
                var w = session.BeginMessage(MsgType.Dialogue);
                w.Put((byte)Kind.Hover);
                w.Put(_hoverWanted);
                session.Send(w, DeliveryMethod.ReliableOrdered, from.PlayerId);
                break;
            }
        }
    }

    // Host: a guest's intent (see Intents) is about to run. Null when it shouldn't.
    internal static Transform Admit(int player, Intents.Kind kind, string what, string path)
    {
        var ui = DialogueUILogic.Instance;
        if (kind != Intents.Kind.Interact && kind != Intents.Kind.Enter && (ui != null && ui.IsTalking && !ui.currentDialogueIsPassive || DialogueOptionsPanel.IsShowing))
        {
            Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(player)} asked for {what} at {path} mid-conversation; ignored");
            return null;
        }
        var t = ScenePath.Resolve(path);
        if (t == null) { Plugin.Logger.LogWarning($"Dialogue: {NameTags.NameOf(player)} asked for {what} at {path}, not found here"); return null; }
        // A detector that is on here sees the guest already (DetectorSync); only one the guest's own copy switched on
        // (picking up the ritual egg switches on the oven's) needs the guest's word for it, or one that also wants a
        // line of sight, which the host's copy can't judge for a puppet.
        if (kind == Intents.Kind.Enter && t.gameObject.activeInHierarchy && !DetectorSync.NeedsWord(t.GetComponent<ObjectDetector>())) return null;
        // Off here: the guest's own state switched it on (the wheel in their arms shows the "found it" talk, which
        // the host's copy only shows while the host carries it), so it's switched on here too. A once-only scene
        // that has already run stays off.
        if (!t.gameObject.activeInHierarchy)
        {
            var once = t.GetComponentInParent<StandaloneDialogue>(true);
            if (once != null && once.onlyActivateOnce && once.hasBeenActivated)
            {
                Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(player)} asked for {what} at {path}, which already ran here; ignored");
                return null;
            }
            // Something that is done here (a stuck Lutki the host has already freed, which the guest's copy still shows
            // for a moment) stays done.
            var station = t.GetComponent<CompletableStation>();
            if (kind == Intents.Kind.Interact && station != null && station.isCompleted)
            {
                Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(player)} asked for {what} at {path}, which is done here; ignored");
                return null;
            }
            Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(player)} asked for {what} at {path}, off here; switched on for them: {SwitchOn(t)}");
        }
        _requestedBy = player;
        _requestedAt = Time.unscaledTime;
        _talkAt = t.position;
        Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(player)} starts {what} at {path}");
        return t;
    }

    // Switches on a path's inactive objects, top down; returns their names.
    private static string SwitchOn(Transform t)
    {
        var off = new List<Transform>();
        for (var x = t; x != null && !x.gameObject.activeInHierarchy; x = x.parent) if (!x.gameObject.activeSelf) off.Add(x);
        off.Reverse();
        foreach (var x in off) x.gameObject.SetActive(true);
        return string.Join(", ", off.Select(x => x.name));
    }

    // The guest's call, minus its two callbacks (they belong to the guest's copy of whoever called it). Those callbacks
    // are often what moves the story on: the old woman's AI starts her talk at the door and opens the yard gate when it
    // ends. The host's AI sees the guest as a player (ActingPlayer) and starts the same talk itself, callbacks and all;
    // the guest's version only runs if nothing started by then, and says so loudly: it means an AI here didn't see them.
    private const float DefaultWait = 3f;
    private static (Transform t, string key, bool b2, bool b4, bool b5, string[] extra, float due)? _pendingDefault;

    internal static void DeferDefault(Transform t, NetDataReader r)
    {
        string key = r.GetString();
        bool b2 = r.GetBool(), b4 = r.GetBool(), b5 = r.GetBool();
        var extra = new string[r.GetInt()];
        for (int i = 0; i < extra.Length; i++) extra[i] = r.GetString();
        _pendingDefault = (t, key, b2, b4, b5, extra, Time.unscaledTime + DefaultWait);
        _requestedAt = Time.unscaledTime + DefaultWait;  // the talk the host's AI starts is still the guest's to lead
    }

    private static void RunPendingDefault(DialogueUILogic ui)
    {
        if (_pendingDefault is not { } p) return;
        if (ui.IsTalking && !ui.currentDialogueIsPassive)
        {
            _pendingDefault = null;
            Plugin.Logger.LogInfo($"Dialogue: {p.key} started here by itself; the guest's copy of it isn't needed");
            return;
        }
        if (Time.unscaledTime < p.due) return;
        _pendingDefault = null;
        if (p.t == null) return;
        Plugin.Logger.LogWarning($"Dialogue: nothing here started {p.key}; starting the guest's copy (without its callbacks). {ActingPlayer.Describe()}");
        _requestedAt = Time.unscaledTime;  // still theirs to lead
        p.t.GetComponent<DialogueFunctions>()?.StartDefaultDialogue(p.key, null, p.b2, new Il2CppSystem.Nullable<DialogueBranch>(), p.b4, p.b5,
            p.extra.Length > 0 ? new Il2CppStringArray(p.extra) : null, null);
    }

    internal static void RequestDefaultTalk(DialogueFunctions c, string key, bool b2, bool b4, bool b5, Il2CppStringArray extra) =>
        Intents.Send(Intents.Kind.DefaultTalk, c, args: w =>
        {
            w.Put(key ?? "");
            w.Put(b2); w.Put(b4); w.Put(b5);
            w.Put(extra?.Length ?? 0);
            if (extra != null) foreach (var x in extra) w.Put(x ?? "");
        });

    private static DialogueOptionsPanel Panel() =>
        DialogueOptionsPanel.instance != null ? DialogueOptionsPanel.instance : Object.FindFirstObjectByType<DialogueOptionsPanel>(FindObjectsInactive.Include);

    // Guest. _leadingTalk: this guest started the conversation on screen; _leading: and is choosing an option now.
    private static bool _leading, _leadingTalk, _watching, _following;
    private static int _optionsLeader;

    private static void OnMessage(CoopSession session, NetDataReader r)
    {
        switch ((Kind)r.GetByte())
        {
            case Kind.Passive:
            {
                string name = r.GetString(), title = r.GetString(), text = r.GetString();
                var ui = DialogueUILogic.Instance;
                if (InTalkHere || ui != null && ui.IsTalking && !ui.currentDialogueIsPassive) break;  // a real talk is on screen
                Show(name, title, text);
                Plugin.Logger.LogInfo($"Dialogue: passive {name}: {text}");
                break;
            }
            case Kind.Start:
            {
                int leader = r.GetInt(), talkId = r.GetInt(), from = r.GetInt();
                bool passive = r.GetBool();
                string headPath = r.GetString(), speaker = r.GetString();
                var lines = new (string, string, string, string)[r.GetInt()];
                for (int i = 0; i < lines.Length; i++) lines[i] = (r.GetString(), r.GetString(), r.GetString(), r.GetString());
                var ui = DialogueUILogic.Instance;
                if (passive)
                {
                    if (InTalkHere || ui != null && ui.IsTalking && !ui.currentDialogueIsPassive) break;  // a real talk is on screen
                    StartPassive(lines, from);
                    break;
                }
                _leadingTalk = leader == session.LocalPlayerId;
                ClosePanel();
                _pending = null;
                _following = true;
                _hostLine = (talkId, from);
                var head = headPath.Length > 0 ? ScenePath.Resolve(headPath) : null;
                if (head != null) { _pulledIn = true; _speakerHead = head; _speaker = speaker; }
                var o = new One { TalkId = talkId, Lines = lines, HostIndex = from, Head = head, Speaker = speaker };
                Plugin.Logger.LogInfo($"Dialogue: one session {talkId}: {lines.Length} lines from {from}, {(_leadingTalk ? "leading" : "watching")}, "
                    + (head != null ? "camera on " + speaker : headPath.Length > 0 ? $"no speaker at {headPath} here" : "no camera"));
                if (_one == null) { _one = o; StartOne(from); break; }
                _next = o;
                _nextBy = Time.unscaledTime + 0.4f;
                if (ui != null) ChainNext(ui);
                break;
            }
            case Kind.Index:
            {
                int talkId = r.GetInt(), index = r.GetInt();
                var o = _next != null && _next.TalkId == talkId ? _next : _one;
                if (o == null || o.TalkId != talkId) break;
                if (index > o.HostIndex) o.HostIndex = index;
                break;
            }
            case Kind.Line:
            {
                if (_one != null) { Plugin.Logger.LogInfo($"Dialogue: one session {_one.TalkId} goes on line by line"); _one = _next = null; }
                _leadingTalk = r.GetInt() == session.LocalPlayerId;
                _hostLine = (r.GetInt(), r.GetInt());
                string name = r.GetString(), title = r.GetString(), text = r.GetString();
                ClosePanel();
                Follow(name, title, text);
                Plugin.Logger.LogInfo($"Dialogue: {name}: {text}");
                break;
            }
            case Kind.Options:
            {
                int leader = r.GetInt();
                bool allowExit = r.GetBool();
                var labels = new List<(string label, bool heard)>();
                for (int i = r.GetInt(); i > 0; i--) labels.Add((r.GetString(), r.GetBool()));
                _optionsLeader = leader;
                _leadingTalk = leader == session.LocalPlayerId;
                _lastOptions = (labels, allowExit);
                _hoverWanted = _hoverSent = -1;
                ShowOptions(session, labels, allowExit, leader == session.LocalPlayerId);
                break;
            }
            case Kind.Chosen:
            {
                int leader = r.GetInt();
                string label = r.GetString();
                ClosePanel();
                Plugin.Logger.LogInfo($"Dialogue: {NameTags.NameOf(leader)} chose {label}");
                break;
            }
            case Kind.End:
                _lastOptions = null;
                ClosePanel();
                StopFollowing();
                _strayUntil = Time.unscaledTime + 3f;
                Plugin.Logger.LogInfo("Dialogue: conversation over");
                break;
            case Kind.Camera:
                PullIn(r.GetString(), r.GetString());
                break;
            case Kind.State:
                Reconcile(session, (Phase)r.GetByte(), r.GetInt(), (r.GetInt(), r.GetInt()));
                break;
            case Kind.Gather:
                for (int i = r.GetInt(); i > 0; i--)
                {
                    int id = r.GetInt();
                    var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
                    float yaw = r.GetFloat();
                    if (id == session.LocalPlayerId && PlayerSync.InWorld) MoveHere(pos, yaw);
                }
                break;
            case Kind.Cam:
                TalkCamera.OnMessage(session, null, r);
                break;
            case Kind.Skip:
                _skipFor = (r.GetInt(), r.GetInt());
                break;
            case Kind.Hover:
                _hoverWanted = r.GetInt();
                break;
        }
    }

    private static (int gen, int index) _hostLine = (-1, -1);
    private static float _idleSince = -1;

    // Guest: whatever this guest shows of the host's conversation must match the host's state; anything left over from
    // a message that was lost or overtaken is closed.
    private static void Reconcile(CoopSession session, Phase phase, int leader, (int, int) line)
    {
        bool showingOurs = _following || _leading || _watching || _pulledIn;
        if (phase == Phase.Idle)
        {
            // One beat of grace: End and the last line can cross the state in flight.
            if (!showingOurs) { _idleSince = -1; return; }
            if (_idleSince < 0) { _idleSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _idleSince < 1.5f) return;
            _idleSince = -1;
            Plugin.Logger.LogInfo("Dialogue: the host isn't talking any more; closing what was left here");
            _lastOptions = null;
            ClosePanel();
            StopFollowing();
            return;
        }
        _idleSince = -1;
        _leadingTalk = leader == session.LocalPlayerId;
        if (_one is { } o && _next == null && phase == Phase.Line && line.Item1 == o.TalkId)
        {
            if (line.Item2 > o.HostIndex) o.HostIndex = line.Item2;  // a lost Index
            // The host is on a line of this dialogue and the box here isn't (it closed, or never started: this player
            // was still loading in): it starts from the host's line.
            var ui = DialogueUILogic.Instance;
            if (ui != null && PlayerSync.InWorld && (!ui.IsTalking || ui.DialogueGeneration != o.LocalGen) && Time.unscaledTime - o.StartedAt > 0.5f && o.HostIndex < o.Lines.Length)
            {
                Plugin.Logger.LogWarning($"Dialogue: one session {o.TalkId}: not on the box here (start {o.Starts}); starting at line {o.HostIndex}");
                StartOne(o.HostIndex);
            }
        }
        // Our panel closed (or never opened) while the host still waits for the choice.
        bool mine = leader == session.LocalPlayerId;
        if (phase == Phase.Options && !(mine ? _leading : _watching) && !DialogueOptionsPanel.IsShowing && _lastOptions != null && PlayerSync.InWorld
            && Time.unscaledTime - _pickedAt > 2f)
        {
            Plugin.Logger.LogInfo($"Dialogue: the host waits for {(mine ? "our" : "the starter's")} choice; showing the options again");
            _optionsLeader = leader;
            ShowOptions(session, _lastOptions.Value.labels, _lastOptions.Value.allowExit, mine);
        }
        // We led, and the host chooses now: our panel goes read-only.
        if (phase == Phase.Options && leader != session.LocalPlayerId && _leading)
        {
            _optionsLeader = leader;
            _leading = false;
            _watching = true;
            CoopDriver.Notice($"{NameTags.NameOf(leader)} is choosing what to say.");
        }
    }

    private static (List<(string label, bool heard)> labels, bool allowExit)? _lastOptions;
    private static float _pickedAt = -10;

    // Pulled-in guests who aren't choosing see the same panel, read-only, as the host does when a guest chooses (the
    // open panel also keeps their dialogue camera on the speaker).
    private static void ShowOptions(CoopSession session, List<(string label, bool heard)> labels, bool allowExit, bool choosing)
    {
        var panel = Panel();
        if (panel == null) return;
        _pending = null;
        // One session: the box stays on its last line under the panel, as the host's does (the next dialogue goes on
        // from it). Line by line: the panel sits where the line was.
        if (_one == null) CloseLine();
        var ls = new Il2CppSystem.Collections.Generic.List<string>();
        var callbacks = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Action>();
        var heard = new Il2CppSystem.Collections.Generic.List<bool>();
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            ls.Add(labels[i].label);
            callbacks.Add((System.Action)(() => SendPick(session, index)));
            heard.Add(labels[i].heard);
        }
        _leading = choosing;
        _watching = !choosing;
        _leadingSince = Time.unscaledTime;
        panel.Show(ls, callbacks, heard, choosing && allowExit);
        if (!choosing) CoopDriver.Notice($"{NameTags.NameOf(_optionsLeader)} is choosing what to say.");
        Plugin.Logger.LogInfo($"Dialogue: {(choosing ? "choosing" : "watching")} from {string.Join(" | ", labels.ConvertAll(l => l.label))}");
    }

    private static float _leadingSince;

    // Guest starter: the choice goes to the host (-1: left without one).
    private static void SendPick(CoopSession session, int index)
    {
        if (!_leading) return;
        _leading = false;
        _pickedAt = Time.unscaledTime;
        var w = session.BeginMessage(MsgType.Dialogue);
        w.Put((byte)Kind.Pick);
        w.Put(index);
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Guest: the options panel closed without a pick (left with the back button): the host ends the talk too.
    private static void WatchLeaving(CoopSession session)
    {
        if (!_leading || DialogueOptionsPanel.IsShowing || Time.unscaledTime - _leadingSince < 0.5f) return;
        SendPick(session, -1);
        Plugin.Logger.LogInfo("Dialogue: left the options");
    }

    private static void ClosePanel()
    {
        if (!_leading && !_watching) return;
        _leading = _watching = false;
        if (DialogueOptionsPanel.IsShowing) Panel()?.Close();
    }

    // Guest: the dialogue camera on the local copy of the speaker. The starter's own; for everyone else it's what shows
    // if the starter's camera stops arriving for a moment.
    private static DialogueCamera _camera;
    private static bool _pulledIn;
    private static Transform _speakerHead;
    private static string _speaker;

    private static void PullIn(string headPath, string speaker)
    {
        var head = ScenePath.Resolve(headPath);
        var me = Find.One<PlayerMovement_2021>();
        if (head == null || me == null) { Plugin.Logger.LogInfo($"Dialogue: no speaker at {headPath} here"); return; }
        float d = Vector3.Distance(head.position, me.transform.position);
        _camera ??= Object.FindFirstObjectByType<DialogueCamera>(FindObjectsInactive.Include);
        if (_camera == null) return;
        _pulledIn = true;
        _speakerHead = head;
        _speaker = speaker;
        Plugin.Logger.LogInfo($"Dialogue: camera on {speaker} ({d:0} m)");
    }

    private static void LetGo()
    {
        if (!_pulledIn) return;
        _pulledIn = false;
        if (_camera != null) _camera.Deactivate();
    }

    // Guest: while following the host's conversation, passive lines don't time out on their own.
    private static float? _passiveDelay;

    // The newest line waits here until the box has taken the one before it: a request made while the last one is still
    // starting is dropped (the host's pick and the answer's first line often arrive in the same frame).
    private static (string name, string title, string text, bool aside)? _pending;
    private static float _shownAt = -10;
    private static int _shownGen = -1;

    // aside: a player's choosing or pick, shown as a subtitle so a pulled-in camera stays on the speaker.
    private static void Follow(string name, string title, string text, bool aside = false)
    {
        _pending = (name, title, text, aside);
        _following = true;
    }

    private static void ShowPending()
    {
        if (_pending is not { } line) return;
        var ui = DialogueUILogic.Instance;
        bool settled = ui == null || ui.DialogueGeneration != _shownGen || Time.unscaledTime - _shownAt > 0.3f;
        if (!settled) return;
        _pending = null;
        _shownAt = Time.unscaledTime;
        _shownGen = ui?.DialogueGeneration ?? -1;
        FollowNow(line.name, line.title, line.text, line.aside);
    }

    // Pulled in (or leading the talk), a line is a real one on the guest's dialogue box (camera, input held, continue
    // prompt): moving past it asks the host for the next. Otherwise it's a subtitle.
    private static int _lineToken;

    private static void FollowNow(string name, string title, string text, bool aside)
    {
        var ui = DialogueUILogic.Instance;
        if (ui == null) { Show(name, title, text); return; }
        _passiveDelay ??= ui.delayForPassiveDialogue;
        ui.delayForPassiveDialogue = 3600f;
        CloseLine();
        if (aside) { Show(name, title, text); return; }
        if (_pulledIn && _camera != null && _speakerHead != null) _camera.Activate(_speakerHead, _speaker);  // the camera takes a speaker only for a line starting in the same frame
        int token = _lineToken;
        var line = _hostLine;
        if (!_leadingTalk) { Show(name, title, text, false); return; }  // held until the host's next line
        Show(name, title, text, false, () =>
        {
            if (token != _lineToken || !_following) return;
            var session = CoopDriver.Session;
            if (session == null || !session.IsConnected) return;
            var w = session.BeginMessage(MsgType.Dialogue);
            w.Put((byte)Kind.Next);
            w.Put(line.gen);
            w.Put(line.index);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        });
    }

    private static void CloseLine()
    {
        _lineToken++;
        var ui = DialogueUILogic.Instance;
        if (ui == null) return;
        ui.queue?.Clear();
        if (ui.IsTalking) ui.ForceCloseDialogue();
    }

    private static void StopFollowing()
    {
        _pending = null;
        _one = _next = null;
        _following = false;
        _leadingTalk = false;
        LetGo();
        var ui = DialogueUILogic.Instance;
        if (_passiveDelay is not { } delay || ui == null) { _passiveDelay = null; CloseLine(); return; }
        _passiveDelay = null;
        ui.delayForPassiveDialogue = delay;
        CloseLine();
    }

    // A notice about the talk that's ending (the hut refusing a guest): the end of the talk would close it, so it shows
    // once the talk is over.
    private static string _afterTalk;

    internal static void ShowAfterTalk(string text) => _afterTalk = text;

    private static void ShowAfterTalkNow()
    {
        if (_afterTalk == null || InTalkHere || Time.unscaledTime < _strayUntil - 2.5f) return;
        var text = _afterTalk;
        _afterTalk = null;
        ShowLocal(text);
    }

    public static void ShowLocal(string text)
    {
        var keep = _lastShown;  // a notice of our own isn't a stray line
        Show("", "", text);
        _lastShown = keep;
    }

    // A line of the host's talk still on screen after it ended (seen once after the hut talk that hands over the
    // controls: the box stayed up) is closed for a few seconds after the end, and when the host starts driving.
    private static string _lastShown;
    private static float _strayUntil;

    internal static void CloseStrayLine()
    {
        if (InTalkHere || _lastShown == null) return;
        var ui = DialogueUILogic.Instance;
        if (ui == null || !ui.IsTalking || ui.currentDialogue == null) return;
        int i = ui.indexInDialogue;
        if (i < 0 || i >= ui.currentDialogue.Length || ui.currentDialogue[i] != _lastShown) return;
        Plugin.Logger.LogInfo($"Dialogue: closed a line left from the talk: {_lastShown}");
        _lastShown = null;
        CloseLine();
    }

    private static void Show(string name, string title, string text, bool passive = true, System.Action done = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        _lastShown = text;
        var ui = DialogueUILogic.Instance;
        if (ui == null) { CoopDriver.Notice(string.IsNullOrEmpty(name) ? text : $"{name}: {text}"); return; }  // e.g. at the main menu
        Il2CppStringArray One(string s) => new(new[] { s ?? "" });
        ui.RequestStartDialogue(One(text), One(name), One(title), done, new Il2CppSystem.Nullable<DialogueBranch>(), passive, false, One(""), null);
    }

    public static string Describe() =>
        $"leader={_leader} talking={_talking} requestedBy={_requestedBy} following={_following} leading={_leading} pulledIn={_pulledIn} leadingTalk={_leadingTalk} one={(_one == null ? "no" : $"{_one.TalkId}@{_one.HostIndex}/{_one.Lines.Length} from {_one.From} starts {_one.Starts}")}{(_next == null ? "" : $" next={_next.TalkId}")} oneGen={_oneGen} camera: {TalkCamera.Describe()} offered={(_offered == null ? "" : string.Join(" | ", _offered))}";

    public static void LateTick() => TalkCamera.LateTick();

    // Dev (guest): a line left over from the host's conversation, as a lost End would leave it.
    public static string TestStuck()
    {
        _leadingTalk = true;
        Follow("Tester", "", "A line the host has already moved past.");
        return "stuck line shown";
    }

    // Dev: start a short conversation on the host's dialogue box, with options.
    public static string Test()
    {
        var ui = DialogueUILogic.Instance;
        if (ui == null) return "no dialogue UI";
        Il2CppStringArray A(params string[] s) => new(s);
        ui.RequestStartDialogue(A("Co-op test: first line.", "Second line, still talking.", "Last line."), A("Tester", "Tester", "Tester"),
            A("", "", ""), null, new Il2CppSystem.Nullable<DialogueBranch>(), false, false, A("", "", ""), null);
        return "dialogue requested";
    }

    // Dev: open the nearest villager's conversation options, as walking up and talking would.
    public static string TalkToNearest(string pathPart = null)
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>();
        NPC_DialogueManager best = null;
        float bestD = float.MaxValue;
        foreach (var npc in Object.FindObjectsByType<NPC_DialogueManager>(FindObjectsSortMode.None))
        {
            if (pathPart != null && !GearObjects.PathOf(npc.transform).Contains(pathPart)) continue;
            float d = (npc.transform.position - me.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = npc; }
        }
        if (best == null) return "no villager";
        best.PresentsDialogueOptions();
        return $"talking to {best.name} at {Mathf.Sqrt(bestD):0} m";
    }

    public static string TestOptions()
    {
        var panel = Object.FindFirstObjectByType<DialogueOptionsPanel>(FindObjectsInactive.Include);
        if (panel == null) return "no options panel";
        var labels = new Il2CppSystem.Collections.Generic.List<string>();
        var callbacks = new Il2CppSystem.Collections.Generic.List<Il2CppSystem.Action>();
        var heard = new Il2CppSystem.Collections.Generic.List<bool>();
        foreach (var l in new[] { "Ask about the forest", "Ask about the hut", "Leave" })
        {
            labels.Add(l);
            string picked = l;
            callbacks.Add((System.Action)(() => Plugin.Logger.LogInfo($"Dialogue test: picked {picked}")));
            heard.Add(false);
        }
        panel.Show(labels, callbacks, heard, true);
        return "options shown";
    }
}

[HarmonyPatch]
internal static class DialogueHooks
{
    [HarmonyPostfix, HarmonyPatch(typeof(DialogueOptionsPanel), nameof(DialogueOptionsPanel.Show))]
    private static void Show(Il2CppSystem.Collections.Generic.List<string> __0, Il2CppSystem.Collections.Generic.List<bool> __2, bool __3) => DialogueSync.OnOptionsShown(__0, __2, __3);

    [HarmonyPrefix, HarmonyPatch(typeof(DialogueOptionsPanel), nameof(DialogueOptionsPanel.OnOptionPicked))]
    private static bool Picked(int __0) => DialogueSync.OnOptionPicked(__0);

    [HarmonyPrefix, HarmonyPatch(typeof(DialogueUILogic), nameof(DialogueUILogic.WaitForPlayerInputThenDoSth))]
    private static void Waiting(DialogueUILogic __instance, ref Il2CppSystem.Action __0) => DialogueSync.OnWaitingForContinue(__instance, ref __0);

    [HarmonyPostfix, HarmonyPatch(typeof(Febucci.TextAnimatorForUnity.TypewriterComponent), nameof(Febucci.TextAnimatorForUnity.TypewriterComponent.SkipTypewriter))]
    private static void Skipped(Febucci.TextAnimatorForUnity.TypewriterComponent __instance) => DialogueSync.OnSkipped(__instance);

    [HarmonyPostfix, HarmonyPatch(typeof(DialogueCamera), nameof(DialogueCamera.Activate), typeof(Transform), typeof(string), typeof(DialogueShotFamilyOverride), typeof(Transform))]
    private static void CameraOn(Transform __0, string __1) => DialogueSync.OnCameraActivated(__0, __1);

    // A guest's own copy of a villager, story dialogue or hand-in doesn't run: the host's copy does it for everyone.
    [HarmonyPrefix, HarmonyPatch(typeof(NPC_DialogueManager), nameof(NPC_DialogueManager.SaySomething))]
    private static bool Say(NPC_DialogueManager __instance) => DialogueSync.Personal(__instance) || Local(Intents.Entry.Say, __instance);

    [HarmonyPrefix, HarmonyPatch(typeof(NPC_DialogueManager), nameof(NPC_DialogueManager.PresentsDialogueOptions), new System.Type[0])]
    private static bool Options(NPC_DialogueManager __instance) => DialogueSync.Personal(__instance) || Local(Intents.Entry.Options, __instance);

    [HarmonyPrefix, HarmonyPatch(typeof(StandaloneDialogue), nameof(StandaloneDialogue.StartDialogue))]
    private static bool Standalone(StandaloneDialogue __instance)
    {
        // A new map's arrival talk runs on the host when it arrives. A guest loads that map later and its copy raises
        // the talk again: asking the host replayed it there, and its chained line started on the guest alone (hung).
        if (WorldSync.IsGuest && __instance.name.StartsWith("MapIntro"))
        {
            Plugin.Logger.LogInfo($"Dialogue: {__instance.name} is the host's arrival talk; not raised here");
            return false;
        }
        return __instance.isPassiveDialogue || Local(Intents.Entry.Standalone, __instance);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(TutorialNPCLogic), nameof(TutorialNPCLogic.TriggerInteraction))]
    private static bool Tutorial(TutorialNPCLogic __instance) => Local(Intents.Entry.Tutorial, __instance);

    [HarmonyPrefix, HarmonyPatch(typeof(POI_Dialogue), nameof(POI_Dialogue.TriggerInteraction))]
    private static bool PoiDialogue(POI_Dialogue __instance) => Local(Intents.Entry.PoiDialogue, __instance);

    [HarmonyPrefix, HarmonyPatch(typeof(POI_Dialogue), nameof(POI_Dialogue.TriggerInteractionThanDisableDialogue))]
    private static bool PoiDialogueOnce(POI_Dialogue __instance) => Local(Intents.Entry.PoiDialogueOnce, __instance);

    // The old woman's talks start on their own triggers, not the dialogue components above, so the mod never saw a
    // guest start one (the host wasn't brought over).
    [HarmonyPrefix, HarmonyPatch(typeof(BabaYagaStartTalking), nameof(BabaYagaStartTalking.StartTalking))]
    private static bool BabaTalk(BabaYagaStartTalking __instance) => Local(Intents.Entry.BabaTalk, __instance);

    // The lowest common start: an NPC's own scripts (the old woman's AI coming to the door once the talisman is taken)
    // call it directly, so none of the hooks above see it. The ones above end here too, but on a guest they've already
    // been sent to the host and don't run.
    [HarmonyPrefix, HarmonyPatch(typeof(DialogueFunctions), nameof(DialogueFunctions.StartDefaultDialogue), typeof(string), typeof(Il2CppSystem.Action), typeof(bool), typeof(Il2CppSystem.Nullable<DialogueBranch>), typeof(bool), typeof(bool), typeof(Il2CppStringArray), typeof(Il2CppSystem.Action))]
    private static bool DefaultDialogue(DialogueFunctions __instance, string __0, bool __2, bool __4, bool __5, Il2CppStringArray __6)
    {
        if (!WorldSync.IsGuest)
        {
            // An NPC's AI started it for the player it sees (ActingPlayer): theirs to lead.
            if (ActingPlayer.InAi && Intents.Actor < 0 && CoopDriver.Session is { Role: Role.Host, IsConnected: true })
                DialogueSync.NoteAiStart(ActingPlayer.Current);
            return true;
        }
        if (DialogueSync.Personal(__instance)) return true;
        if (!WorldObjects.ApplyingRemote) DialogueSync.RequestDefaultTalk(__instance, __0, __2, __4, __5, __6);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GenericPOIFunctions), nameof(GenericPOIFunctions.TakeItemFromPlayer))]
    private static bool TakeItem(GenericPOIFunctions __instance)
    {
        // Not enough in the shared pack: the game's own "you don't have" line, on this screen, not the host's.
        if (WorldSync.IsGuest && __instance.takeItem != null && Find.One<InventoryManager>() is { } m
            && m.GetItemAmount(__instance.takeItem) < System.Math.Max(1, __instance.amountofItems)) return true;
        return Local(Intents.Entry.TakeItem, __instance);
    }

    private static bool Local(Intents.Entry entry, Component c)
    {
        if (!WorldSync.IsGuest) return true;
        if (!WorldObjects.ApplyingRemote) Intents.Send(Intents.Kind.Call, c, entry);  // applying the host's state replays what already happened there
        return false;
    }
}
