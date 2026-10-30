using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Animals and villagers near a guest move and act where the host's do. Running their own AI, a guest's copies pick
// their own goals and activities (and deer flee from the guest's own player) and AiSync keeps dragging them back,
// which looks like snapping and sliding. So near a guest, the host streams each one ten times a second (position,
// facing, animator parameters and states) and the guest's copy is a pure mirror: its AI and nav agent are off and it
// replays the stream a little behind, like player puppets. One that stands still is sent once a second. A copy nobody
// streams any more gets its own AI back; far away, AiSync's coarse sync covers villagers.
public static class MirrorSync
{
    private const float SendEvery = 0.1f, StillEvery = 1f, InterpDelay = 0.15f, StaleAfter = 3f;
    private const float AnimalRange = 120f, VillagerRange = 80f;
    // Animating every villager in a town at full rate costs the host; beyond this a guest sees little detail anyway.
    private const float UnthrottleVillagers = 40f;
    private const int PacketBudget = 900;  // unreliable packets must fit 1023 bytes

    private class Sample { public float Time; public Vector3 Pos; public float Yaw; public float[] Params; public (int hash, float time)[] States; }

    private class Mirror
    {
        public GeneralAIController Ai;
        public Animator Anim;
        public readonly List<Sample> Buffer = new();
        public bool AgentWas, RootMotionWas;
        public float LastHeard;
        public readonly int[] HostState = new int[4];        // per layer: the host's state as last followed
        public readonly float[] OffSince = new float[4];     // per layer: when this copy left that state, or 0
    }

    private static readonly Dictionary<string, Mirror> Mirrors = new();
    private static readonly HashSet<System.IntPtr> Mirrored = new();
    private static readonly Dictionary<System.IntPtr, Animator> AnimOf = new();
    private static readonly Dictionary<System.IntPtr, (int hash, bool isBool)[]> ParamsOf = new();  // anim.parameters allocates
    private static float _next;
    internal static int Sent, Received, Packets;
    // Host: what each streamed one last looked like (without the time into its states) and when it was last sent.
    private static readonly Dictionary<string, (long sig, float at)> LastSent = new();
    // Host: animals the game would stop animating (far from the host's camera) but a guest is looking at.
    private static readonly Dictionary<System.IntPtr, AnimatorDistanceThrottle> Unthrottled = new();

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Mirror && session.Role == Role.Client) Receive(r); };
        session.PlayerLeft += id => { if (session.Role == Role.Client && id == 0) ReleaseAll(); };
    }

    public static void ResetLocal()
    {
        Mirrors.Clear();
        Mirrored.Clear();
        AnimOf.Clear();
        Unthrottled.Clear();
        ParamsOf.Clear();
        LastSent.Clear();
    }

    // The game animates animals far from its own camera rarely or not at all; the ones streamed to a guest must move.
    private static void Unthrottle(List<(string key, GeneralAIController ai)> streamed)
    {
        var now = new HashSet<System.IntPtr>();
        foreach (var (_, ai) in streamed)
        {
            now.Add(ai.Pointer);
            if (Unthrottled.ContainsKey(ai.Pointer)) continue;
            var th = ai.Throttle;
            if (th == null || th.skipThisThrottle) { Unthrottled[ai.Pointer] = null; continue; }
            th.skipThisThrottle = true;
            th.SetAnimatorLive(true);
            Unthrottled[ai.Pointer] = th;
        }
        foreach (var (ptr, th) in new List<KeyValuePair<System.IntPtr, AnimatorDistanceThrottle>>(Unthrottled))
        {
            if (now.Contains(ptr)) continue;
            if (th != null) th.skipThisThrottle = false;
            Unthrottled.Remove(ptr);
        }
    }

    // Float and bool parameters in declaration order (same prefab, same order everywhere); triggers are left out.
    private static (int hash, bool isBool)[] Defs(Animator anim)
    {
        if (ParamsOf.TryGetValue(anim.Pointer, out var d)) return d;
        var list = new List<(int, bool)>();
        foreach (var p in anim.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Float) list.Add((p.nameHash, false));
            else if (p.type == AnimatorControllerParameterType.Bool) list.Add((p.nameHash, true));
            if (list.Count == 32) break;
        }
        return ParamsOf[anim.Pointer] = list.ToArray();
    }

    // AiSync and CarrySync: this copy replays the host's stream.
    internal static bool IsMirrored(GeneralAIController ai) => ai != null && Mirrored.Contains(ai.Pointer);

    internal static bool IsWild(GeneralAIController ai)
    {
        var n = ai.name;
        return n.Contains("Deer") || n.Contains("Hare");
    }

    private static Animator AnimatorOf(GeneralAIController ai)
    {
        if (AnimOf.TryGetValue(ai.Pointer, out var a) && a != null) return a;
        return AnimOf[ai.Pointer] = ai.GetComponentInChildren<Animator>();
    }

    public static void Tick(CoopSession session)
    {
        if (session.Role == Role.Host) { if (session.IsConnected) Send(session); }
        else if (session.Role == Role.Client) Play();
    }

    private static void Send(CoopSession session)
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + SendEvery;
        var guests = new List<Vector3>();
        foreach (var p in session.Players.Values)
            if (p.Ready && PlayerSync.PuppetRoot(p.PlayerId) is Transform t) guests.Add(t.position);
        if (guests.Count == 0) return;

        var batch = new List<(string key, GeneralAIController ai)>();
        var live = new List<(string key, GeneralAIController ai)>();
        foreach (var (key, ai) in AiSync.Index())
        {
            if (ai == null || !ai.isActiveAndEnabled || CarrySync.IsCarried(ai)) continue;
            var pos = ai.transform.position;
            float range = IsWild(ai) ? AnimalRange : VillagerRange;
            float nearest = float.MaxValue;
            foreach (var g in guests) nearest = Mathf.Min(nearest, (g - pos).sqrMagnitude);
            if (nearest >= range * range) continue;
            batch.Add((key, ai));
            if (IsWild(ai) || nearest < UnthrottleVillagers * UnthrottleVillagers) live.Add((key, ai));
        }
        Unthrottle(live);
        // Entries are written on their own first, then packed into packets by size.
        var entries = new List<byte[]>();
        float now = Time.unscaledTime;
        foreach (var (key, ai) in batch)
        {
            var e = new NetDataWriter();
            var t = ai.transform;
            e.Put(key);
            e.Put(t.position.x); e.Put(t.position.y); e.Put(t.position.z);
            e.Put(t.eulerAngles.y);
            long sig = WriteParams(e, AnimatorOf(ai)) ^ Quantize(t.position, t.eulerAngles.y);
            if (LastSent.TryGetValue(key, out var last) && last.sig == sig && now - last.at < StillEvery) continue;
            LastSent[key] = (sig, now);
            entries.Add(e.CopyData());
        }
        for (int i = 0; i < entries.Count;)
        {
            int n = 0, size = 0;
            while (i + n < entries.Count && n < 255 && (n == 0 || size + entries[i + n].Length <= PacketBudget)) size += entries[i + n++].Length;
            var w = session.BeginMessage(MsgType.Mirror);
            w.Put((byte)n);
            for (int k = 0; k < n; k++) w.Put(entries[i + k]);
            session.Send(w, DeliveryMethod.Unreliable);
            Sent += n;
            Packets++;
            i += n;
        }
    }

    // CarrySync: an animal in someone's arms plays the carrier's animator, written and read the same way.
    internal static void WriteAnimator(NetDataWriter w, Animator anim) => WriteParams(w, anim);

    internal static (float[] values, (int hash, float time)[] states) ReadAnimator(NetDataReader r)
    {
        var values = new float[r.GetByte()];
        for (int k = 0; k < values.Length; k++) values[k] = (float)System.BitConverter.Int16BitsToHalf(r.GetShort());
        var states = new (int, float)[r.GetByte()];
        for (int k = 0; k < states.Length; k++) states[k] = (r.GetInt(), r.GetFloat());
        return (values, states);
    }

    internal static void ApplyAnimator(Animator anim, float[] values, (int hash, float time)[] states)
    {
        if (anim == null || values == null) return;
        ApplyParams(anim, values);
        for (int l = 0; l < states.Length && l < anim.layerCount; l++)
        {
            var (hash, time) = states[l];
            if (hash == 0 || anim.IsInTransition(l) || anim.GetCurrentAnimatorStateInfo(l).fullPathHash == hash || !anim.HasState(l, hash)) continue;
            anim.CrossFade(hash, BlendFraction, l, time - Mathf.Floor(time));
        }
    }

    internal static Animator AnimatorFor(GeneralAIController ai) => ai == null ? null : AnimatorOf(ai);

    // Centimetres and degrees, so a still one is recognised as still.
    private static long Quantize(Vector3 p, float yaw) =>
        ((long)Mathf.RoundToInt(p.x * 50) * 73856093) ^ ((long)Mathf.RoundToInt(p.y * 50) * 19349663) ^ ((long)Mathf.RoundToInt(p.z * 50) * 83492791) ^ ((long)Mathf.RoundToInt(yaw) << 40);

    // Returns a fingerprint of what was written, leaving out how far into its states it is (that always moves).
    private static long WriteParams(NetDataWriter w, Animator anim)
    {
        if (anim == null || anim.runtimeAnimatorController == null) { w.Put((byte)0); w.Put((byte)0); return 0; }
        long sig = 17;
        var defs = Defs(anim);
        w.Put((byte)defs.Length);
        foreach (var (hash, isBool) in defs)
        {
            var h = (System.Half)(isBool ? (anim.GetBool(hash) ? 1f : 0f) : anim.GetFloat(hash));
            short bits = System.BitConverter.HalfToInt16Bits(h);
            w.Put(bits);
            sig = sig * 31 + bits;
        }
        // The state each layer is in: activities (drinking, grazing, sitting, working) are played as states, not parameters.
        int layers = System.Math.Min(anim.layerCount, 4);
        w.Put((byte)layers);
        for (int l = 0; l < layers; l++)
        {
            var st = anim.GetCurrentAnimatorStateInfo(l);
            w.Put(st.fullPathHash);
            w.Put(st.normalizedTime);
            sig = sig * 31 + st.fullPathHash;
        }
        return sig;
    }

    private static void Receive(NetDataReader r)
    {
        int n = r.GetByte();
        var index = AiSync.Index();
        float now = Time.unscaledTime;
        for (int i = 0; i < n; i++)
        {
            string key = r.GetString();
            var s = new Sample { Time = now, Pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Yaw = r.GetFloat() };
            (s.Params, s.States) = ReadAnimator(r);
            Received++;
            if (!index.TryGetValue(key, out var ai) || ai == null || CarrySync.IsCarried(ai)) continue;
            if (!Mirrors.TryGetValue(key, out var m) || m.Ai == null) Mirrors[key] = m = Begin(ai, s);
            m.LastHeard = now;
            m.Buffer.Add(s);
            if (m.Buffer.Count > 20) m.Buffer.RemoveAt(0);
        }
    }

    private static Mirror Begin(GeneralAIController ai, Sample first)
    {
        var m = new Mirror { Ai = ai, Anim = AnimatorOf(ai) };
        var agent = ai.agent;
        m.AgentWas = agent != null && agent.enabled;
        if (agent != null) agent.enabled = false;
        if (m.Anim != null) { m.RootMotionWas = m.Anim.applyRootMotion; m.Anim.applyRootMotion = false; }
        ai.enabled = false;
        ai.transform.SetPositionAndRotation(first.Pos, Quaternion.Euler(0, first.Yaw, 0));
        Mirrored.Add(ai.Pointer);
        return m;
    }

    private static void End(Mirror m)
    {
        var ai = m.Ai;
        if (ai == null) return;
        Mirrored.Remove(ai.Pointer);
        if (m.Anim != null) m.Anim.applyRootMotion = m.RootMotionWas;
        ai.enabled = true;
        var agent = ai.agent;
        if (agent != null && m.AgentWas) { agent.enabled = true; if (agent.isOnNavMesh) agent.Warp(ai.transform.position); }
    }

    // CarrySync: someone picks it up; the game's own carrying takes over.
    internal static void Release(GeneralAIController ai)
    {
        if (ai == null) return;
        foreach (var (key, m) in Mirrors)
            if (m.Ai != null && m.Ai.Pointer == ai.Pointer) { End(m); Mirrors.Remove(key); return; }
    }

    private static void ReleaseAll()
    {
        foreach (var m in Mirrors.Values) End(m);
        Mirrors.Clear();
    }

    // Guest, every frame.
    private static void Play()
    {
        if (Mirrors.Count == 0) return;
        float now = Time.unscaledTime, renderTime = now - InterpDelay;
        List<string> stale = null;
        foreach (var (key, m) in Mirrors)
        {
            if (m.Ai == null || now - m.LastHeard > StaleAfter || CarrySync.IsCarried(m.Ai)) { (stale ??= new()).Add(key); continue; }
            var buf = m.Buffer;
            int i = buf.Count - 1;
            while (i > 0 && buf[i - 1].Time > renderTime) i--;
            var b = buf[i];
            var a = i > 0 ? buf[i - 1] : b;
            float t = b.Time > a.Time ? Mathf.Clamp01((renderTime - a.Time) / (b.Time - a.Time)) : 1f;
            m.Ai.transform.SetPositionAndRotation(Vector3.Lerp(a.Pos, b.Pos, t), Quaternion.Euler(0, Mathf.LerpAngle(a.Yaw, b.Yaw, t), 0));
            ApplyParams(m.Anim, b.Params);
            ApplyStates(m, b.States, now);
        }
        if (stale == null) return;
        foreach (var key in stale) { End(Mirrors[key]); Mirrors.Remove(key); }
    }

    private static void ApplyParams(Animator anim, float[] values)
    {
        if (anim == null || values.Length == 0) return;
        var defs = Defs(anim);
        for (int k = 0; k < defs.Length && k < values.Length; k++)
        {
            if (defs[k].isBool) anim.SetBool(defs[k].hash, values[k] > 0.5f);
            else anim.SetFloat(defs[k].hash, values[k]);
        }
    }

    private const float BlendFraction = 0.15f;  // of the target state's length
    private const float OffGrace = 0.5f;        // s a copy may be in another state (its own transition) before it is put back
    internal static int StateJumps;

    // A layer follows the host into a new state at once, at the host's point in it. A copy that drifts out of the
    // host's state by itself (its parameters can trigger a transition the host's took a moment later or not at all)
    // is only put back after a grace period, or the two fight every frame.
    private static void ApplyStates(Mirror m, (int hash, float time)[] states, float now)
    {
        var anim = m.Anim;
        if (anim == null || states == null) return;
        for (int l = 0; l < states.Length && l < anim.layerCount && l < m.HostState.Length; l++)
        {
            var (hash, time) = states[l];
            if (hash == 0 || anim.IsInTransition(l)) continue;
            bool hostMoved = m.HostState[l] != hash;
            if (anim.GetCurrentAnimatorStateInfo(l).fullPathHash == hash) { m.HostState[l] = hash; m.OffSince[l] = 0; continue; }
            if (!hostMoved)
            {
                if (m.OffSince[l] == 0) m.OffSince[l] = now;
                if (now - m.OffSince[l] < OffGrace) continue;
            }
            if (!anim.HasState(l, hash)) continue;
            anim.CrossFade(hash, BlendFraction, l, time - Mathf.Floor(time));
            m.HostState[l] = hash;
            m.OffSince[l] = 0;
            StateJumps++;
        }
    }

    public static string Describe()
    {
        var sb = new System.Text.StringBuilder($"mirrors: sent {Sent} in {Packets} packets, received {Received}, mirroring {Mirrors.Count}, state blends {StateJumps}");
        int shown = 0;
        foreach (var (key, m) in Mirrors)
        {
            if (shown++ == 3 || m.Buffer.Count == 0) break;
            var last = m.Buffer[m.Buffer.Count - 1];
            sb.Append($"\n  {m.Ai?.name} anim={(m.Anim != null ? m.Anim.name + (m.Anim.isActiveAndEnabled ? "" : " (off)") + $" layers {m.Anim.layerCount}" : "none")} params {last.Params.Length}");
            for (int l = 0; l < last.States.Length; l++)
            {
                int mine = m.Anim != null && l < m.Anim.layerCount ? m.Anim.GetCurrentAnimatorStateInfo(l).fullPathHash : 0;
                sb.Append($" | L{l} host {last.States[l].hash} t{last.States[l].time:0.00} mine {mine} has={(m.Anim != null && m.Anim.HasState(l, last.States[l].hash))}");
            }
        }
        return sb.ToString();
    }
}
