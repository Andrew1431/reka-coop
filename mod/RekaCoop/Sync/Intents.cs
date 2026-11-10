using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// What a guest does to the story goes to the host as an intent: what kind, which object, arguments. The host runs the
// game's own entry point for it with that guest as the acting player (Actor); what it changes comes back as state.
// A guest never sends story state (PLAN.md, rule 2). Picking an option and clicking a line on are intents too; they
// travel with the conversation (DialogueSync), since only its leader may send them.
public static class Intents
{
    public enum Kind : byte
    {
        Interact = 1,     // used a story PlayerDetection (the interact key); see StoryInteract
        Enter = 2,        // walked into a player detector; the host runs it if its own copy is off; see DetectorSync
        Call = 3,         // an entry point the guest's copy reached by itself: byte Entry
        DefaultTalk = 4,  // DialogueFunctions.StartDefaultDialogue: string key, 3 x bool, int n, n x string
        AreaEnter = 5,    // walked into a PlayerTrigger; see AreaSync
        AreaExit = 6,     // walked out of one
        TutorialSolve = 7, // did what the current tutorial task asks for: int block, int task, string id; see TutorialSync
        TrackerEvent = 8, // the trial tracker heard a story event on the guest's copy: byte handler, int step; see TrialSync
        UseItem = 9,      // finished a spot with a tool: int index in the area at the path, -1 for the spot itself; see UseItemSync
    }

    // Kind.Call: what the guest's copy was about to run. These go once the host's copy sees guests as players and a
    // guest's copy no longer runs its own story (PLAN.md phases 2 and 3).
    public enum Entry : byte { None = 0, Say = 1, Options = 2, Standalone = 3, Tutorial = 4, PoiDialogue = 5, PoiDialogueOnce = 6, TakeItem = 7, BabaTalk = 8 }

    // Host: the player whose intent is running right now; -1 outside one.
    public static int Actor { get; private set; } = -1;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Intent && session.Role == Role.Host && from != null) OnIntent(from, r); };
    }

    private static string Name(Kind kind, Entry entry) => kind == Kind.Call ? entry.ToString() : kind.ToString();

    // Guest.
    internal static void Send(Kind kind, Component c, Entry entry = Entry.None, System.Action<NetDataWriter> args = null)
    {
        var session = CoopDriver.Session;
        if (session == null || !session.IsConnected || c == null) return;
        string path = ScenePath.Of(c.transform);
        var w = session.BeginMessage(MsgType.Intent);
        w.Put((byte)kind);
        w.Put(path);
        if (kind == Kind.Call) w.Put((byte)entry);
        args?.Invoke(w);
        session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Intent: asked the host to {Name(kind, entry)} at {path}");
    }

    // Host.
    private static void OnIntent(RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        string path = r.GetString();
        var entry = kind == Kind.Call ? (Entry)r.GetByte() : Entry.None;
        var t = kind is Kind.AreaEnter or Kind.AreaExit or Kind.TutorialSolve or Kind.TrackerEvent or Kind.UseItem ? ScenePath.Resolve(path) : DialogueSync.Admit(from.PlayerId, kind, Name(kind, entry), path);
        if (t == null) return;
        Actor = from.PlayerId;
        bool acting = ActingPlayer.Enter(Actor);
        try { Run(kind, entry, t, r); }
        finally { Actor = -1; if (acting) ActingPlayer.Exit(); }
    }

    private static void Run(Kind kind, Entry entry, Transform t, NetDataReader r)
    {
        switch (kind)
        {
            case Kind.Interact: StoryInteract.Run(t.GetComponent<PlayerDetection>()); break;
            case Kind.Enter: DetectorSync.EnterFor(t.GetComponent<ObjectDetector>(), Actor); break;
            case Kind.DefaultTalk: DialogueSync.DeferDefault(t, r); break;
            case Kind.AreaEnter: AreaSync.Run(t, Actor, true); break;
            case Kind.AreaExit: AreaSync.Run(t, Actor, false); break;
            case Kind.UseItem: UseItemSync.Run(t, r.GetInt(), Actor); break;
            case Kind.TrackerEvent: TrialSync.EventFor(Actor, r.GetByte(), r.GetInt()); break;
            case Kind.TutorialSolve: TutorialSync.SolveFor(Actor, r.GetInt(), r.GetInt(), r.GetString()); break;
            case Kind.Call:
                switch (entry)
                {
                    case Entry.Say: t.GetComponent<NPC_DialogueManager>()?.SaySomething(); break;
                    case Entry.Options: t.GetComponent<NPC_DialogueManager>()?.PresentsDialogueOptions(); break;
                    case Entry.Standalone: t.GetComponent<StandaloneDialogue>()?.StartDialogue(); break;
                    case Entry.Tutorial: t.GetComponent<TutorialNPCLogic>()?.TriggerInteraction(); break;
                    case Entry.PoiDialogue: t.GetComponent<POI_Dialogue>()?.TriggerInteraction(); break;
                    case Entry.PoiDialogueOnce: t.GetComponent<POI_Dialogue>()?.TriggerInteractionThanDisableDialogue(); break;
                    case Entry.TakeItem: t.GetComponent<GenericPOIFunctions>()?.TakeItemFromPlayer(); break;
                    case Entry.BabaTalk: t.GetComponent<BabaYagaStartTalking>()?.StartTalking(); break;
                }
                break;
        }
    }
}
