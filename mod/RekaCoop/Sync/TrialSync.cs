using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Trials (the quest chapters) run on the host. Its tracker's progress (the current trial, which trials and tasks are
// done, finished quest steps, the tutorial counters, claimed rewards) is flattened into named numbers and mirrored to
// guests the way story flags are: diffs once a second, everything on a guest's Ready. Guests don't move to the next
// trial on their own, and a guest's tasks don't check themselves: they are done when the host's are. A story event the
// tracker hears on a guest's copy (the chicken placed, a site cleansed, a quest step finished) is an intent: the host's
// tracker handles it. A guest's pickups and help counted in its stats (tasks like "collect 10 mushrooms" read the
// player's own stats, not the shared pack) are sent to the host, which adds them to its own and evaluates.
public static class TrialSync
{
    private enum Kind : byte { Ready = 1, State = 2, Stats = 4, HostStats = 5 }

    private static Dictionary<string, int> _sent;   // host: as last sent
    private static float _nextPoll;
    private static bool _readySent;
    internal static bool Applying;
    private static Dictionary<string, int> _base;   // guest: progress as the host last set it, or as we last reported
    private static Dictionary<string, int> _statsBase;
    private static Dictionary<string, int> _statsSent;  // host: as last sent
    private static float _nextGain;

    private static readonly PropertyInfo[] Tutorial = typeof(TasksTracker).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name.StartsWith("tutorial_") && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int)) && p.CanWrite).ToArray();

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Trial) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _sent = null;
        _readySent = false;
        _base = null;
        _statsBase = null;
        _statsSent = null;
    }

    private static TasksTracker Tracker => RekaCoop.Sync.Find.One<TasksTracker>();

    private static Dictionary<string, int> Capture(TasksTracker t)
    {
        var all = new Dictionary<string, int> { ["idx"] = t._currentTrialIndex };
        if (t.trials != null)
            for (int i = 0; i < t.trials.Count; i++)
            {
                var trial = t.trials[i];
                if (trial == null) continue;
                all[$"T{i}.done"] = trial.hasBeenFinished ? 1 : 0;
                all[$"T{i}.complete"] = trial._isComplete ? 1 : 0;
                all[$"T{i}.suspended"] = trial.suspended ? 1 : 0;
                if (trial.tasks != null)
                    for (int j = 0; j < trial.tasks.Count; j++)
                        if (trial.tasks[j] != null) all[$"T{i}.{j}"] = trial.tasks[j]._isComplete ? 1 : 0;
            }
        if (TasksTracker.completedSteps != null)
            foreach (var s in TasksTracker.completedSteps) all[$"step.{(int)s}"] = 1;
        foreach (var p in Tutorial)
        {
            var v = p.GetValue(t);
            all[$"tut.{p.Name}"] = v is bool b ? (b ? 1 : 0) : (int)v;
        }
        if (t.claimedRewards != null)
            for (int i = 0; i < t.claimedRewards.Length; i++) all[$"claim.{i}"] = t.claimedRewards[i] ? 1 : 0;
        return all;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        var t = Tracker;
        if (t == null || !t.isInitialized) return;
        if (session.Role == Role.Client)
        {
            if (!_readySent)
            {
                _readySent = true;
                var w = session.BeginMessage(MsgType.Trial);
                w.Put((byte)Kind.Ready);
                session.Send(w, DeliveryMethod.ReliableOrdered);
                return;
            }
            if (_base == null || Time.unscaledTime < _nextGain) return;
            _nextGain = Time.unscaledTime + 1f;
            SendStats(session);
            return;
        }
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + 1f;
        SendHostStats(session, -1);
        var now = Capture(t);
        if (_sent == null) { _sent = now; return; }
        var changed = now.Where(kv => !_sent.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        _sent = now;
        if (changed.Count == 0) return;
        Send(session, changed, -1);
        Plugin.Logger.LogInfo($"Trials: sent {changed.Count} changes: {string.Join(", ", changed.Take(6).Select(kv => $"{kv.Key}={kv.Value}"))}");
    }

    private static void Send(CoopSession session, List<KeyValuePair<string, int>> state, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Trial);
        w.Put((byte)Kind.State);
        w.Put(state.Count);
        foreach (var kv in state) { w.Put(kv.Key); w.Put(kv.Value); }
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var t = Tracker;
            if (t == null) return;
            var all = Capture(t).ToList();
            Send(session, all, from.PlayerId);
            Plugin.Logger.LogInfo($"Trials: sent all {all.Count} trial values to player {from.PlayerId} (trial {t._currentTrialIndex})");
            SendHostStats(session, from.PlayerId);
        }
        else if (kind == Kind.State && session.Role == Role.Client)
        {
            int n = r.GetInt();
            var state = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) state[r.GetString()] = r.GetInt();
            var t = Tracker;
            if (t == null) { Plugin.Logger.LogWarning("Trials: host's progress arrived before the tracker"); return; }
            Applying = true;
            try { Plugin.Logger.LogInfo($"Trials: host's progress: {n} values, {Apply(t, state)} changed here"); }
            finally { Applying = false; }
            _base = Capture(t);
        }
        else if (kind == Kind.HostStats && session.Role == Role.Client)
        {
            int n = r.GetInt();
            var stats = new List<(string, int)>();
            for (int i = 0; i < n; i++) stats.Add((r.GetString(), r.GetInt()));
            var s = RekaCoop.Sync.Find.One<StatsTracker>();
            if (s == null) return;
            int raised = ChangeStats(s, stats, System.Math.Max);
            if (_statsBase != null) foreach (var (k, v) in stats) _statsBase[k] = System.Math.Max(_statsBase.GetValueOrDefault(k), v);
            if (raised > 0) Plugin.Logger.LogInfo($"Trials: host's stats: {raised} of {n} raised here");
        }
        else if (kind == Kind.Stats && session.Role == Role.Host)
        {
            int n = r.GetInt();
            var deltas = new List<(string, int)>();
            for (int i = 0; i < n; i++) deltas.Add((r.GetString(), r.GetInt()));
            var s = RekaCoop.Sync.Find.One<StatsTracker>();
            if (s == null) return;
            int added = ChangeStats(s, deltas, (a, b) => a + b);
            // A place a guest came to is a place visited on this map: the history of places per map (which the next
            // maps are made from) is the host's, and the guest's copy follows it.
            var history = RekaCoop.Sync.Find.One<PoiMapHistoryTracker>();
            if (history != null)
                foreach (var (key, d) in deltas)
                    if (d > 0 && key.StartsWith("v:"))
                        try { history.RecordVisit(key[2..]); } catch (System.Exception e) { Plugin.Logger.LogWarning($"Trials: recording the visit to {key[2..]} failed: {e.Message}"); }
            try { Tracker?.Evaluate(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"Trials: evaluating after stats failed: {e.Message}"); }
            Plugin.Logger.LogInfo($"Trials: player {from.PlayerId}'s stats: {added} of {n} added");
        }
    }

    // The tracker's handlers for story events (it subscribes them to GameEvents.Tutorial). On a guest they don't run;
    // the host's runs for that guest. FinishedStep is OnFinishedQuestStep, which takes the step.
    internal static readonly string[] Handlers =
    {
        "OnPineBranchRevealed", "OnGotChicken", "OnPlacedChicken", "OnCleansedSingleSite", "OnCleansedAllSites", "OnBroughtEgg",
        "OnPlacedEgg", "OnPlacedSingleOffering", "OnPlacedAllOfferings", "OnReceivedTalisman", "OnDissolvedSingleBone",
        "OnDissolvedAllBones", "OnBroughtEmbers", "OnGaveEmbersToBaba", "OnRitualFinished", "OnBuildingTutorialFinished",
    };
    internal const byte FinishedStep = 255;

    // Host: a guest's copy heard this event.
    internal static void EventFor(int player, byte handler, int step)
    {
        var t = Tracker;
        if (t == null || (handler != FinishedStep && handler >= Handlers.Length)) return;
        string name = handler == FinishedStep ? $"OnFinishedQuestStep({(QuestStepID)step})" : Handlers[handler];
        DialogueSync.NoteRequest(player, 10f);
        try
        {
            if (handler == FinishedStep) t.OnFinishedQuestStep((QuestStepID)step);
            else typeof(TasksTracker).GetMethod(Handlers[handler], BindingFlags.Public | BindingFlags.Instance).Invoke(t, null);
            Plugin.Logger.LogInfo($"Trials: {name} for player {player}: {Describe()}");
        }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Trials: {name} for player {player} failed: {(e.InnerException ?? e).Message}"); }
    }

    // The stats that tasks count: pickups and crafts per item, places visited and solved, people helped.
    private static readonly string[] StatInts =
    {
        "peopleHelped", "ghostsHelped", "creaturesHelpedWithHerbs", "misteriesSolved", "timesTeleported", "plantsFullyGrown",
        "plantTypesPlanted", "tradesMade", "collectiblesCollectedTotal", "craftablesCraftedTotal", "poisSolvedTotal", "poisVisitedTotal",
    };
    private static readonly PropertyInfo[] StatProps = StatInts
        .Select(n => typeof(StatsTracker).GetProperty(n, BindingFlags.Public | BindingFlags.Instance))
        .Where(p => p != null && p.PropertyType == typeof(int) && p.CanWrite).ToArray();
    private static Dictionary<string, Item> _items;

    private static Dictionary<string, int> CaptureStats(StatsTracker s)
    {
        var all = new Dictionary<string, int>();
        if (s.collectiblesCollected != null) foreach (var kv in s.collectiblesCollected) if (kv.Key != null) all["c:" + kv.Key.name] = kv.Value;
        if (s.craftablesCrafted != null) foreach (var kv in s.craftablesCrafted) if (kv.Key != null) all["k:" + kv.Key.name] = kv.Value;
        if (s.poisVisited != null) foreach (var kv in s.poisVisited) all["v:" + kv.Key] = kv.Value;
        if (s.poisSolved != null) foreach (var kv in s.poisSolved) all["s:" + kv.Key] = kv.Value;
        foreach (var p in StatProps) all["i:" + p.Name] = (int)p.GetValue(s);
        return all;
    }

    private static void SendStats(CoopSession session)
    {
        var s = RekaCoop.Sync.Find.One<StatsTracker>();
        if (s == null) return;
        var now = CaptureStats(s);
        if (_statsBase == null) { _statsBase = now; return; }
        var deltas = now.Select(kv => (kv.Key, d: kv.Value - _statsBase.GetValueOrDefault(kv.Key))).Where(x => x.d > 0).ToList();
        _statsBase = now;
        if (deltas.Count == 0) return;
        var w = session.BeginMessage(MsgType.Trial);
        w.Put((byte)Kind.Stats);
        w.Put(deltas.Count);
        foreach (var (k, d) in deltas) { w.Put(k); w.Put(d); }
        session.Send(w, DeliveryMethod.ReliableOrdered);
        Plugin.Logger.LogInfo($"Trials: sent our stats to the host: {string.Join(", ", deltas.Take(6).Select(x => $"{x.Key}+{x.d}"))}");
    }

    // Host: everyone's counts (its own plus what guests sent), so a guest's task counter ("collect 10 mushrooms") shows
    // the same number as the host's instead of the guest's own pickups. Guests take the higher of theirs and the host's.
    private static void SendHostStats(CoopSession session, int toPlayer)
    {
        var s = RekaCoop.Sync.Find.One<StatsTracker>();
        if (s == null) return;
        var now = CaptureStats(s);
        var changed = toPlayer >= 0 || _statsSent == null ? now.ToList()
            : now.Where(kv => !_statsSent.TryGetValue(kv.Key, out var v) || v != kv.Value).ToList();
        if (toPlayer < 0) _statsSent = now;
        if (changed.Count == 0) return;
        var w = session.BeginMessage(MsgType.Trial);
        w.Put((byte)Kind.HostStats);
        w.Put(changed.Count);
        foreach (var kv in changed) { w.Put(kv.Key); w.Put(kv.Value); }
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // Host: a guest's pickups and help count as ours, so the host's tasks see everyone's (combine: add). Guest: the
    // host's counts (combine: the higher).
    private static int ChangeStats(StatsTracker s, List<(string key, int value)> values, System.Func<int, int, int> combine)
    {
        int added = 0;
        foreach (var (key, d) in values)
        {
            var name = key[2..];
            switch (key[0])
            {
                case 'c':
                case 'k':
                    var dict = key[0] == 'c' ? s.collectiblesCollected : s.craftablesCrafted;
                    if (_items == null)
                    {
                        _items = new Dictionary<string, Item>();
                        foreach (var it in Resources.FindObjectsOfTypeAll<Item>()) _items.TryAdd(it.name, it);
                    }
                    if (dict == null || !_items.TryGetValue(name, out var item)) continue;
                    int had = dict.ContainsKey(item) ? dict[item] : 0;
                    if (combine(had, d) == had) continue;
                    dict[item] = combine(had, d);
                    break;
                case 'v':
                case 's':
                    var pois = key[0] == 'v' ? s.poisVisited : s.poisSolved;
                    if (pois == null) continue;
                    int was = pois.ContainsKey(name) ? pois[name] : 0;
                    if (combine(was, d) == was) continue;
                    pois[name] = combine(was, d);
                    break;
                case 'i':
                    var p = StatProps.FirstOrDefault(x => x.Name == name);
                    if (p == null) continue;
                    int cur = (int)p.GetValue(s);
                    if (combine(cur, d) == cur) continue;
                    p.SetValue(s, combine(cur, d));
                    break;
                default: continue;
            }
            added++;
        }
        try { s.OnChanged?.Invoke(); } catch (System.Exception e) { Plugin.Logger.LogWarning($"Trials: stats listeners failed: {e.Message}"); }
        return added;
    }

    private static int Apply(TasksTracker t, Dictionary<string, int> state)
    {
        int changed = 0;
        if (state.TryGetValue("idx", out var idx) && idx != t._currentTrialIndex)
        {
            Plugin.Logger.LogInfo($"Trials: moving from trial {t._currentTrialIndex} to the host's {idx}");
            t.SetTrial(idx);
            changed++;
        }
        foreach (var (key, value) in state)
        {
            bool on = value != 0;
            if (key.StartsWith("T") && key.Contains('.'))
            {
                var parts = key.Substring(1).Split('.');
                if (!int.TryParse(parts[0], out int ti) || t.trials == null || ti >= t.trials.Count || t.trials[ti] == null) continue;
                var trial = t.trials[ti];
                switch (parts[1])
                {
                    case "done": if (trial.hasBeenFinished != on) { trial.hasBeenFinished = on; changed++; } break;
                    case "complete": if (trial._isComplete != on) { trial._isComplete = on; changed++; } break;
                    case "suspended": if (trial.suspended != on) { trial.suspended = on; changed++; } break;
                    default:
                        if (int.TryParse(parts[1], out int tj) && trial.tasks != null && tj < trial.tasks.Count && trial.tasks[tj] != null
                            && trial.tasks[tj]._isComplete != on)
                        {
                            if (on) trial.tasks[tj].OverrideAsCompleted();
                            else trial.tasks[tj]._isComplete = false;
                            changed++;
                        }
                        break;
                }
            }
            else if (key.StartsWith("step.") && on && int.TryParse(key.Substring(5), out int step))
            {
                var id = (QuestStepID)step;
                if (TasksTracker.completedSteps != null && !TasksTracker.completedSteps.Contains(id)) { TasksTracker.completedSteps.Add(id); changed++; }
            }
            else if (key.StartsWith("tut."))
            {
                var p = Tutorial.FirstOrDefault(x => x.Name == key.Substring(4));
                if (p == null) continue;
                object v = p.PropertyType == typeof(bool) ? on : value;
                if (!Equals(p.GetValue(t), v)) { p.SetValue(t, v); changed++; }
            }
            else if (key.StartsWith("claim.") && int.TryParse(key.Substring(6), out int ci) && t.claimedRewards != null && ci < t.claimedRewards.Length
                     && t.claimedRewards[ci] != on)
            {
                t.claimedRewards[ci] = on;
                changed++;
            }
        }
        if (changed > 0)
        {
            try { t.Evaluate(); }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"Trials: refreshing the quest display failed: {e.Message}"); }
        }
        return changed;
    }

    // The desync detector's repair: a guest whose values drifted (a task it ticked on its own) takes the host's.
    internal static int MatchHost(Dictionary<string, string> host)
    {
        var t = Tracker;
        if (t == null) return 0;
        var state = new Dictionary<string, int>();
        foreach (var (k, v) in host) if (int.TryParse(v, out var n)) state[k] = n;
        Applying = true;
        try { return Apply(t, state); }
        finally { Applying = false; }
    }

    internal static Dictionary<string, string> Snapshot()
    {
        var t = Tracker;
        if (t == null) return null;
        var all = new Dictionary<string, string>();
        foreach (var kv in Capture(t)) all[kv.Key] = kv.Value.ToString();
        return all;
    }

    public static string Describe()
    {
        var t = Tracker;
        if (t == null) return "no TasksTracker";
        var all = Capture(t);
        var trial = t.trials != null && t._currentTrialIndex < t.trials.Count ? t.trials[t._currentTrialIndex] : null;
        var tasks = trial?.tasks == null ? "" : string.Join("", Enumerable.Range(0, trial.tasks.Count).Select(j => trial.tasks[j]._isComplete ? "x" : "."));
        return $"trial {t._currentTrialIndex} {trial?.idForSaving} tasks [{tasks}] complete={trial?._isComplete}; {all.Count} values, steps "
               + string.Join(",", all.Keys.Where(k => k.StartsWith("step.")).Select(k => k.Substring(5)));
    }
}

[HarmonyPatch]
internal static class TrialHooks
{
    // Guests move on with the host.
    [HarmonyPrefix, HarmonyPatch(typeof(TasksTracker), nameof(TasksTracker.AdvanceToNextTrial), typeof(bool))]
    private static bool Advance() => Allowed("advance to the next trial");

    [HarmonyPrefix, HarmonyPatch(typeof(TasksTracker), nameof(TasksTracker.SolveTrial))]
    private static bool Solve() => Allowed("solve a trial");

    [HarmonyPrefix, HarmonyPatch(typeof(TasksTracker), nameof(TasksTracker.SkipCurrentTrial))]
    private static bool Skip() => Allowed("skip a trial");

    // A guest's tasks are done when the host's are; they don't check themselves (or run what follows from being solved).
    [HarmonyPrefix, HarmonyPatch(typeof(ProgressionTask), nameof(ProgressionTask.Evaluate))]
    private static bool Check(ProgressionTask __instance, ref bool __result)
    {
        if (!WorldSync.IsGuest || WorldTransfer.LoadingHostWorld) return true;
        __result = __instance._isComplete;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(TasksTracker), nameof(TasksTracker.OnFinishedQuestStep))]
    private static bool Step(TasksTracker __instance, QuestStepID step) => Heard(__instance, TrialSync.FinishedStep, (int)step, $"OnFinishedQuestStep({step})");

    internal static bool Heard(TasksTracker t, byte handler, int step, string name)
    {
        if (!WorldSync.IsGuest || TrialSync.Applying || WorldTransfer.LoadingHostWorld) return true;
        // Bones dissolve on the host's say only (BoneSync); what this copy shows of it is not a second bone.
        if (name == "OnDissolvedSingleBone") return false;
        Plugin.Logger.LogInfo($"Trials: {name} here; the host's tracker handles it");
        Intents.Send(Intents.Kind.TrackerEvent, t, args: w => { w.Put(handler); w.Put(step); });
        return false;
    }

    private static bool Allowed(string what)
    {
        if (!WorldSync.IsGuest || TrialSync.Applying || WorldTransfer.LoadingHostWorld) return true;
        Plugin.Logger.LogInfo($"Trials: guest can't {what} on its own; the host's progress decides");
        return false;
    }
}

[HarmonyPatch]
internal static class TrackerEventHooks
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        TrialSync.Handlers.Select(n => (MethodBase)typeof(TasksTracker).GetMethod(n, BindingFlags.Public | BindingFlags.Instance)).Where(m => m != null);

    private static bool Prefix(TasksTracker __instance, MethodBase __originalMethod) =>
        TrialHooks.Heard(__instance, (byte)System.Array.IndexOf(TrialSync.Handlers, __originalMethod.Name), 0, __originalMethod.Name);
}
