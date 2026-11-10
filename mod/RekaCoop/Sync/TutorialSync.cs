using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The tutorial is a list of blocks, each a list of tasks solved in order ("take the book", "go behind the house"); the
// prompt at the top of the screen shows the current one. Only the host's tutorial moves by itself (PLAN.md, rule 1).
// A task that would be solved on a guest's copy (they walked, raised the lantern, took the book) is not solved there:
// it goes to the host as an intent, and the host solves its own current task with that guest as the acting player.
// Progress is two numbers, the block and how many of its tasks are solved; the host sends them to everyone, and a
// guest behind steps its own tutorial forward the game's way (solve the current task, or start the next block once a
// block is done), one step at a time, so its callbacks run (the prompt, a door) and nothing else on it moves the story.
// The tutorial manager used to be copied whole by SaveableSync; restoring it set the numbers without running a step,
// so the prompt and doors stayed behind.
[HarmonyPatch]
public static class TutorialSync
{
    private enum Kind : byte { State = 1 }

    private const float StepEvery = 1f, SendEvery = 3f, AskEvery = 3f;

    private static (int block, int solved) _target = (-1, 0);   // where the host is
    private static (int block, int solved) _sent = (-1, 0);
    private static bool _targetRunning, _sentRunning;            // the host's block has been started (not only loaded)
    private static (int block, int solved) _gaveUp = (-1, 0);    // guest: the host's place it could not step to
    private static float _nextStep, _nextSend;
    private static int _stuck;
    // Guest: the one task Step is solving right now (its Solve goes through), and the task it last asked the host for.
    private static System.IntPtr _allow;
    private static bool _stepping;
    private static (int block, int solved) _asked = (-1, 0);
    private static float _askAgain;
    private static int _asks;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Tutorial) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _target = (-1, 0);
        _sent = (-1, 0);
        _asked = (-1, 0);
        _gaveUp = (-1, 0);
        _targetRunning = _sentRunning = false;
        _stuck = 0;
    }

    private static TutorialManager_v2 Manager => Find.One<TutorialManager_v2>();

    private static (int block, int solved) Local(TutorialManager_v2 t) => (t.CurrentTutBlockIndex, t.SolvedTaskCount());

    private static bool Ahead((int block, int solved) a, (int block, int solved) b) =>
        a.block > b.block || a.block == b.block && a.solved > b.solved;

    private static TutorialManager_v2.TutTask Current(TutorialManager_v2 t, (int block, int solved) here)
    {
        var blocks = t.allTutBlocks;
        if (blocks == null || here.block < 0 || here.block >= blocks.Count) return null;
        var tasks = blocks[here.block].Item1;
        return tasks != null && here.solved < tasks.Count ? tasks[here.solved] : null;
    }

    // A save made between two blocks loads with the block's number set and the block not started: its tasks are not
    // the manager's current list yet (a talk starts it), and solving the current task throws.
    private static bool Running(TutorialManager_v2 t)
    {
        var blocks = t.allTutBlocks;
        int i = t.CurrentTutBlockIndex;
        var now = t.currentTutBlock;
        if (now == null || blocks == null || i < 0 || i >= blocks.Count) return false;
        var tasks = blocks[i].Item1;
        return tasks != null && tasks.Pointer == now.Pointer;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        var t = Manager;
        if (t == null) return;
        var here = Local(t);
        if (here.block < 0) return;

        if (session.Role != Role.Host)
        {
            if (_target == _gaveUp) return;
            bool start = _targetRunning && _target.block == here.block && !Running(t);
            if ((start || Ahead(_target, here)) && Time.unscaledTime >= _nextStep) Step(t, here);
            // The host was not there yet when this copy's task was done; ask again once it is.
            else if (_asked == here && _target == here && Time.unscaledTime >= _askAgain && _asks < 5 && Current(t, here) is { } task)
                Ask(t, task, here);
            return;
        }

        // The host tells everyone where it is (and again now and then, for whoever just loaded).
        bool running = Running(t);
        if (here != _sent || running != _sentRunning || Time.unscaledTime >= _nextSend)
        {
            _sent = here;
            _sentRunning = running;
            _nextSend = Time.unscaledTime + SendEvery;
            var w = session.BeginMessage(MsgType.Tutorial);
            w.Put((byte)Kind.State);
            w.Put(here.block);
            w.Put(here.solved);
            w.Put(running);
            session.Send(w, DeliveryMethod.ReliableOrdered);
        }
    }

    // Solves the current task the game's way. Seen on a guest: the current task was never started there (its start
    // comes from a talk the host ran), and SolveCurrentTask throws.
    private static void SolveCurrent(TutorialManager_v2 t, TutorialManager_v2.TutTask task)
    {
        try { t.SolveCurrentTask(); }
        catch (System.Exception e)
        {
            if (task == null) throw;
            Plugin.Logger.LogInfo($"Tutorial: solving the current task failed ({e.Message}); starting {task.ID} and solving it directly");
            if (!task.Started) t.StartTutTask(task);
            if (!task.Solved) task.Solve();
        }
    }

    // Guest.
    private static void Step(TutorialManager_v2 t, (int block, int solved) here)
    {
        _nextStep = Time.unscaledTime + StepEvery;
        var task = Current(t, here);
        _stepping = true;
        _allow = task != null ? task.Pointer : System.IntPtr.Zero;
        try
        {
            if (!Running(t)) { Plugin.Logger.LogInfo($"Tutorial: block {here.block} was loaded but not started here; starting it"); t.StartTutBlock(here.block); }
            else if (t.HasCompletedCurrentBlock) t.StartNextTutBlock();
            else SolveCurrent(t, task);
            t.RefreshTutDisplay();
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Tutorial: step failed: {e.Message}"); }
        finally { _stepping = false; _allow = System.IntPtr.Zero; }
        var now = Local(t);
        if (now == here && Running(t) && !Ahead(_target, here)) { _stuck = 0; return; }   // only the start was owed
        Plugin.Logger.LogInfo($"Tutorial: stepped from block {here.block} task {here.solved} to {now.block} task {now.solved} (host at {_target.block} task {_target.solved})");
        // Some steps finish a moment later; a step that never moves gives up after a few tries.
        if (now == here && ++_stuck >= 5) { Plugin.Logger.LogWarning("Tutorial: can't step forward here; left as it is"); _gaveUp = _target; _stuck = 0; }
        else if (now != here) _stuck = 0;
    }

    // Guest: this copy's current task was done here.
    private static void Ask(TutorialManager_v2 t, TutorialManager_v2.TutTask task, (int block, int solved) here)
    {
        if (_asked != here) _asks = 0;
        _asked = here;
        _asks++;
        _askAgain = Time.unscaledTime + AskEvery;
        string id = task.ID ?? "";
        Intents.Send(Intents.Kind.TutorialSolve, t, Intents.Entry.None, w => { w.Put(here.block); w.Put(here.solved); w.Put(id); });
    }

    // Host: a guest did what the current task asks for.
    internal static void SolveFor(int player, int block, int solved, string id)
    {
        var t = Manager;
        if (t == null) return;
        var here = Local(t);
        string who = NameTags.NameOf(player);
        var task = Current(t, here);
        if (here != (block, solved) || task == null || task.Solved)
        {
            Plugin.Logger.LogInfo($"Tutorial: {who} did block {block} task {solved} ({id}); the tutorial here is at block {here.block} task {here.solved}, nothing to do");
            return;
        }
        Plugin.Logger.LogInfo($"Tutorial: {who} did block {block} task {solved} ({id}); solving it");
        DialogueSync.NoteRequest(player, 10f);   // a talk this sets off is theirs to lead
        try { SolveCurrent(t, task); t.RefreshTutDisplay(); }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Tutorial: solving it failed: {e.Message}"); }
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        if ((Kind)r.GetByte() != Kind.State || session.Role == Role.Host) return;
        _target = (r.GetInt(), r.GetInt());
        _targetRunning = r.GetBool();
    }

    // Guest: a task solves itself here (the player did it, or its condition holds): the host decides.
    [HarmonyPrefix, HarmonyPatch(typeof(TutorialManager_v2.TutTask), nameof(TutorialManager_v2.TutTask.Solve))]
    private static bool SolvePrefix(TutorialManager_v2.TutTask __instance)
    {
        if (!WorldSync.IsGuest || __instance == null) return true;
        if (_allow != System.IntPtr.Zero && __instance.Pointer == _allow) return true;
        var t = Manager;
        if (t == null) return true;
        var here = Local(t);
        var current = Current(t, here);
        if (current == null || current.Pointer != __instance.Pointer)
        {
            Plugin.Logger.LogInfo($"Tutorial: {__instance.ID} would be solved here but is not the current task (block {here.block} task {here.solved}); left alone");
            return false;
        }
        if (_asked != here || Time.unscaledTime >= _askAgain) Ask(t, __instance, here);
        return false;
    }

    // Guest: the old woman's tutorial talks are pieces of the story; an event runs one at once ("one more thing",
    // when the journal is closed). The same event runs the host's piece, with the callbacks that move the story on,
    // a few seconds later than a guest's copy would ask for it; so a guest's piece never runs or asks.
    [HarmonyPrefix, HarmonyPatch(typeof(TutorialNPCLogic.TutorialDialoguePiece), nameof(TutorialNPCLogic.TutorialDialoguePiece.RunDialogue))]
    private static bool PiecePrefix(TutorialNPCLogic.TutorialDialoguePiece __instance)
    {
        if (!WorldSync.IsGuest) return true;
        Plugin.Logger.LogInfo($"Tutorial: the old woman's talk {__instance.LinesTerm} would start here; the host's story runs it");
        return false;
    }

    // Guest: the next block starts when the host's does.
    [HarmonyPrefix, HarmonyPatch(typeof(TutorialManager_v2), nameof(TutorialManager_v2.StartNextTutBlock))]
    private static bool NextBlockPrefix(TutorialManager_v2 __instance)
    {
        if (!WorldSync.IsGuest || _stepping || _target.block < 0) return true;
        int block = __instance.CurrentTutBlockIndex;
        if (block < _target.block) return true;
        Plugin.Logger.LogInfo($"Tutorial: the block after {block} would start here by itself (host at block {_target.block}); waiting for the host");
        return false;
    }

    public static string Describe()
    {
        var t = Manager;
        if (t == null) return "no tutorial";
        var here = Local(t);
        var sb = new System.Text.StringBuilder($"tutorial block {here.block} task {here.solved} done={t.HasCompletedCurrentBlock} running={Running(t)}, host at {_target.block} task {_target.solved}");
        try
        {
            var blocks = t.allTutBlocks;
            if (blocks != null && here.block >= 0 && here.block < blocks.Count)
            {
                var tasks = blocks[here.block].Item1;
                for (int i = 0; i < tasks.Count; i++)
                {
                    var k = tasks[i];
                    sb.Append($"\n  {i}: {k.ID} started={k.Started} solved={k.Solved} condition={k.solveCondition != null} continuous={k.startContinousSolveCheck != null} args=[{k.argument}|{k.argument2}|{k.argument3}]");
                }
            }
        }
        catch (System.Exception e) { sb.Append($"\n  ({e.Message})"); }
        return sb.ToString();
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
