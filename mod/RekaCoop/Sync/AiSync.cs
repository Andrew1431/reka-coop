using System.Collections.Generic;
using LiteNetLib;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Villagers and animals are where the host has them. Twice a second the host sends, for each one, where it stands,
// which way it faces, which of its behaviours it is running and where it is heading. Guests keep
// their own AI running (so walking and animation look natural) but switch to the host's behaviour when theirs
// differs, and head for the same spot. Small drift is walked off a little every frame towards where the host's
// copy should be by now (its last position plus its speed since); only a big gap is closed by moving it at once.
public static class AiSync
{
    private const float WarpDistance = 5f, Interval = 0.5f;
    private const float Slack = 0.4f, CatchUp = 1.5f;  // m left alone; extra m/s used to close a gap
    private const int PerPacket = 8;  // unreliable packets must fit 1023 bytes: about 105 each with a place id + element id key

    private static float _next;
    private static Dictionary<string, GeneralAIController> _byKey;
    private static GeneralAIController[] _indexedFrom;
    internal static int Warps, Switches, Goals;
    private const float GoalSlack = 1f;

    private class Target { public Vector3 Pos, Vel; public float At; }
    private static readonly Dictionary<string, Target> Targets = new();
    private static readonly Dictionary<string, (int n, float d)> WarpsBy = new();  // dev: who drifts

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Ai && session.Role == Role.Client) Receive(r); };
    }

    public static void ResetLocal()
    {
        ElementKey.Clear();
        PathKeys.Clear();
        _byKey = null;
        _indexedFrom = null;
        Targets.Clear();
    }

    // Characters outside a place (the tutorial's talking crows) have no element id; their scene path is the same on
    // every copy.
    private static string KeyOf(GeneralAIController ai) =>
        ElementKey.Of(ai, ai._uniquePoiElementID) ?? (PathKeys.TryGetValue(ai.Pointer, out var k) ? k : PathKeys[ai.Pointer] = "@" + ScenePath.Of(ai.transform));
    private static readonly Dictionary<System.IntPtr, string> PathKeys = new();

    internal static Dictionary<string, GeneralAIController> Index()
    {
        var found = RekaCoop.Sync.Find.Scanned<GeneralAIController>(5f);
        if (_byKey != null && found == _indexedFrom) return _byKey;
        _indexedFrom = found;
        _byKey = new Dictionary<string, GeneralAIController>();
        foreach (var ai in found ?? new GeneralAIController[0])
        {
            if (ai == null) continue;   // gone with the old map since the last scan
            var key = KeyOf(ai);
            if (key != null) _byKey[key] = ai;
        }
        return _byKey;
    }

    private static int BehaviourIndex(GeneralAIController ai)
    {
        if (ai.aiBehaviours == null || ai.currentBehaviour == null) return -1;
        for (int i = 0; i < ai.aiBehaviours.Count; i++)
            if (ai.aiBehaviours[i] != null && ai.aiBehaviours[i].Pointer == ai.currentBehaviour.Pointer) return i;
        return -1;
    }

    public static void Tick(CoopSession session)
    {
        if (session.Role == Role.Client) { Steer(); return; }
        if (session.Role != Role.Host || !session.IsConnected || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + Interval;
        var all = new List<(string key, GeneralAIController ai)>();
        foreach (var (key, ai) in Index())
            if (ai != null && ai.isActiveAndEnabled && !CarrySync.IsCarried(ai) && !MirrorSync.IsWild(ai)) all.Add((key, ai));  // animals: MirrorSync
        for (int start = 0; start < all.Count; start += PerPacket)
        {
            int n = System.Math.Min(PerPacket, all.Count - start);
            var w = session.BeginMessage(MsgType.Ai);
            w.Put(n);
            for (int i = start; i < start + n; i++)
            {
                var (key, ai) = all[i];
                var t = ai.transform;
                w.Put(key);
                w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
                w.Put(t.eulerAngles.y);
                w.Put((short)BehaviourIndex(ai));
                var goal = ai.targetPos;
                w.Put(goal.x); w.Put(goal.y); w.Put(goal.z);
            }
            session.Send(w, DeliveryMethod.Unreliable);
        }
    }

    // Guest.
    private static void Receive(LiteNetLib.Utils.NetDataReader r)
    {
        int n = r.GetInt();
        var index = Index();
        for (int i = 0; i < n; i++)
        {
            string key = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float yaw = r.GetFloat();
            int behaviour = r.GetShort();
            var goal = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            if (!index.TryGetValue(key, out var ai) || ai == null || !ai.isActiveAndEnabled || CarrySync.IsCarried(ai) || MirrorSync.IsMirrored(ai)) continue;
            try
            {
                if (behaviour >= 0 && behaviour != BehaviourIndex(ai) && ai.aiBehaviours != null && behaviour < ai.aiBehaviours.Count)
                {
                    ai.SwitchToBehaviourIndex(behaviour);
                    Switches++;
                }
                // Same destination as the host's copy, so both walk the same way (wanderers pick their own otherwise).
                if (goal != Vector3.zero && (ai.targetPos - goal).sqrMagnitude > GoalSlack * GoalSlack)
                {
                    ai.SetMoveGoal(goal, false);
                    Goals++;
                }
                float now = Time.unscaledTime;
                if (Targets.TryGetValue(key, out var t))
                {
                    float dt = now - t.At;
                    t.Vel = dt > 0.05f && dt < 2f ? (pos - t.Pos) / dt : Vector3.zero;
                }
                else Targets[key] = t = new Target();
                t.Pos = pos;
                t.At = now;
                if ((ai.transform.position - pos).sqrMagnitude > WarpDistance * WarpDistance)
                {
                    float gap = (ai.transform.position - pos).magnitude;
                    ai.SetAIPos(pos, true, true);
                    ai.transform.rotation = Quaternion.Euler(0, yaw, 0);
                    Warps++;
                    WarpsBy[key] = (WarpsBy.GetValueOrDefault(key).n + 1, gap);
                }
            }
            catch (System.Exception e) { Plugin.Logger.LogWarning($"AI: can't follow {ai.name}: {e.Message}"); }
        }
    }

    // Guest, every frame.
    private static void Steer()
    {
        if (Targets.Count == 0 || _byKey == null) return;
        float now = Time.unscaledTime, dt = Time.deltaTime;
        foreach (var (key, t) in Targets)
        {
            if (!_byKey.TryGetValue(key, out var ai) || ai == null || !ai.isActiveAndEnabled || CarrySync.IsCarried(ai) || MirrorSync.IsMirrored(ai)) continue;
            float age = Mathf.Min(now - t.At, 1f);
            var gap = t.Pos + t.Vel * age - ai.transform.position;
            gap.y = 0;  // the ground decides height
            float d = gap.magnitude;
            if (d < Slack || d > WarpDistance) continue;
            var step = gap * (Mathf.Min(d - Slack, CatchUp * dt) / d);
            var agent = ai.agent;
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Move(step);
            else ai.transform.position += step;
        }
    }

    // Dev: the nearest villagers as this machine has them, one per line, to compare against the other machine.
    public static string DescribeNear(int count, Vector3? around = null)
    {
        var me = around ?? RekaCoop.Sync.Find.One<PlayerMovement_2021>().transform.position;
        var list = new List<(float d, string s)>();
        foreach (var (key, ai) in Index())
        {
            if (ai == null || MirrorSync.IsWild(ai)) continue;
            var anim = ai.GetComponentInChildren<Animator>();
            string st = anim == null || !anim.isActiveAndEnabled ? "-" : $"{anim.GetCurrentAnimatorStateInfo(0).shortNameHash}";
            var b = BehaviourIndex(ai);
            string bn = b >= 0 ? ai.aiBehaviours[b].GetIl2CppType().Name : "none";
            int shown = 0;
            foreach (var r in ai.GetComponentsInChildren<Renderer>()) if (r.enabled && r.gameObject.activeInHierarchy) shown++;
            list.Add(((ai.transform.position - me).magnitude,
                $"{key} {ai.name} on={ai.isActiveAndEnabled} at {ai.transform.position} yaw {ai.transform.eulerAngles.y:0} b{b} {bn} goal {ai.targetPos} anim {st} renderers {shown}"));
        }
        list.Sort((x, y) => x.d.CompareTo(y.d));
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < list.Count && i < count; i++) sb.Append($"\n{list[i].d:0.0}m {list[i].s}");
        return sb.ToString();
    }

    public static string Describe()
    {
        var me = RekaCoop.Sync.Find.One<PlayerMovement_2021>().transform.position;
        var list = new List<(float d, string s)>();
        foreach (var (key, ai) in Index())
            if (ai != null) list.Add(((ai.transform.position - me).magnitude, $"{key.Substring(0, 8)} {ai.name} {ai.transform.position} b{BehaviourIndex(ai)}"));
        list.Sort((x, y) => x.d.CompareTo(y.d));
        var near = new List<string>();
        for (int i = 0; i < list.Count && i < 4; i++) near.Add(list[i].s);
        var top = new List<(string k, int n, float d)>();
        foreach (var (k, v) in WarpsBy) top.Add((k, v.n, v.d));
        top.Sort((x, y) => y.n.CompareTo(x.n));
        var worst = new List<string>();
        for (int i = 0; i < top.Count && i < 5; i++)
            worst.Add($"{top[i].k.Substring(0, 8)} {(Index().TryGetValue(top[i].k, out var a) && a != null ? a.name : "?")} x{top[i].n} last {top[i].d:0.0}m");
        return $"{Index().Count} AIs with ids; {Switches} behaviour switches, {Goals} goals, {Warps} warps so far; nearest: {string.Join(" | ", near)}; most warped: {string.Join(" | ", worst)}";
    }
}
