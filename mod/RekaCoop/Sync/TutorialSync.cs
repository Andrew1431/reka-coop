using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The tutorial is a list of blocks, each a list of tasks solved in order ("take the book", "go behind the house"); the
// prompt at the top of the screen shows the current one. Each copy runs its own, and a step's callbacks (a door opening,
// the next talk) run on the copy that solved it. Progress is two numbers, the block and how many of its tasks are
// solved. Whoever gets ahead pulls the others along: a guest's progress goes to the host, the host's to everyone, and a
// copy behind steps its own tutorial forward the game's way (solve the current task, or start the next block once a
// block is done), one step at a time, so every copy's callbacks run and its prompt follows.
// The tutorial manager used to be copied whole by SaveableSync; restoring it set the numbers without running a step,
// so the prompt and doors stayed behind.
public static class TutorialSync
{
    private enum Kind : byte { State = 1 }

    private const float StepEvery = 1f, SendEvery = 3f;

    private static (int block, int solved) _target = (-1, 0);   // the furthest anyone has told us about
    private static (int block, int solved) _sent = (-1, 0);
    // Guest: where its tutorial stood when the host's world loaded. A save resumes at the start of the next block, so a
    // guest that just loaded is often a block "ahead" of the host it copied; only progress made here counts.
    private static (int block, int solved) _loadedAt = (-1, 0);
    private static float _resetAt;
    private static float _nextStep, _nextSend;
    private static int _stuck;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Tutorial) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _target = (-1, 0);
        _sent = (-1, 0);
        _loadedAt = (-1, 0);
        _resetAt = Time.unscaledTime;
        _stuck = 0;
    }

    private static TutorialManager_v2 Manager => Find.One<TutorialManager_v2>();

    private static (int block, int solved) Local(TutorialManager_v2 t) => (t.CurrentTutBlockIndex, t.SolvedTaskCount());

    private static bool Ahead((int block, int solved) a, (int block, int solved) b) =>
        a.block > b.block || a.block == b.block && a.solved > b.solved;

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        var t = Manager;
        if (t == null) return;
        var here = Local(t);
        if (here.block < 0) return;
        if (_loadedAt.block < 0)
        {
            if (Time.unscaledTime - _resetAt < 5f) return;  // the loaded tutorial settles over the first frames
            _loadedAt = here;
        }

        if (Ahead(_target, here) && Time.unscaledTime >= _nextStep) Step(t, here);
        here = Local(t);

        // The host tells everyone where it is (and again now and then, for whoever just loaded); a guest only speaks up
        // when it got ahead by itself, once it has heard where the host is.
        bool host = session.Role == Role.Host;
        if (host ? here != _sent || Time.unscaledTime >= _nextSend : _target.block >= 0 && Ahead(here, _target) && here != _sent && here != _loadedAt)
        {
            _sent = here;
            _nextSend = Time.unscaledTime + SendEvery;
            var w = session.BeginMessage(MsgType.Tutorial);
            w.Put((byte)Kind.State);
            w.Put(here.block);
            w.Put(here.solved);
            session.Send(w, DeliveryMethod.ReliableOrdered);
            if (!host) Plugin.Logger.LogInfo($"Tutorial: got ahead here, block {here.block} task {here.solved}; told the host");
        }
    }

    private static void Step(TutorialManager_v2 t, (int block, int solved) here)
    {
        _nextStep = Time.unscaledTime + StepEvery;
        var blocks = t.allTutBlocks;
        if (blocks == null || here.block >= blocks.Count) return;
        try
        {
            if (t.HasCompletedCurrentBlock) t.StartNextTutBlock();
            else
            {
                try { t.SolveCurrentTask(); }
                catch (System.Exception e)
                {
                    // Seen on a guest: the current task was never started there (its start comes from a talk the host ran).
                    var task = blocks[here.block].Item1[here.solved];
                    Plugin.Logger.LogInfo($"Tutorial: solving the current task failed ({e.Message}); starting {task.ID} and solving it directly");
                    if (!task.Started) t.StartTutTask(task);
                    if (!task.Solved) task.Solve();
                }
            }
            t.RefreshTutDisplay();
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Tutorial: step failed: {e.Message}"); }
        var now = Local(t);
        Plugin.Logger.LogInfo($"Tutorial: stepped from block {here.block} task {here.solved} to {now.block} task {now.solved} (others at {_target.block} task {_target.solved})");
        // Some steps finish a moment later; a step that never moves gives up after a few tries.
        if (now == here && ++_stuck >= 5) { Plugin.Logger.LogWarning("Tutorial: can't step forward here; left as it is"); _target = now; _stuck = 0; }
        else if (now != here) _stuck = 0;
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        if ((Kind)r.GetByte() != Kind.State) return;
        var theirs = (r.GetInt(), r.GetInt());
        if (Ahead(theirs, _target)) _target = theirs;
        if (session.Role == Role.Host && Manager is { } t && Ahead(theirs, Local(t)))
            Plugin.Logger.LogInfo($"Tutorial: {NameTags.NameOf(from.PlayerId)} is at block {theirs.Item1} task {theirs.Item2}; catching up");
    }

    public static string Describe()
    {
        var t = Manager;
        if (t == null) return "no tutorial";
        var here = Local(t);
        return $"tutorial block {here.block} task {here.solved} done={t.HasCompletedCurrentBlock}, others at {_target.block} task {_target.solved}";
    }
}

// The building tutorial is always skipped in co-op (decided 2026-10-29): it takes away and gives back building blocks on
// each copy separately, and its tasks count pieces built on each copy. Every copy answers the game's question itself.
[HarmonyLib.HarmonyPatch(typeof(TutorialManager_v2), nameof(TutorialManager_v2.ShowBuildingTutorialSkipPrompt))]
internal static class SkipBuildingTutorial
{
    private static bool Prefix(TutorialManager_v2 __instance)
    {
        if (CoopDriver.Session is not { IsConnected: true }) return true;
        Plugin.Logger.LogInfo("Tutorial: skipping the building tutorial (always skipped in co-op)");
        __instance.SkipBuildingTutorial();
        return false;
    }
}
