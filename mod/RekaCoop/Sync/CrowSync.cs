using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// A place's own birds (the crows around Bartek): not the players' raven spell (BirdSync) and not animals with an AI
// (MirrorSync). Each is a CollectProjectile that flies by its own logic: circles, lands on a sitting spot when the
// story opens them, flies off when shooed. Run on a guest's copy they did their own thing: they never landed where
// the host's sat, while the spots to shoo them from were where the host's were (first Steam session). Host-first: the
// host's birds are the birds. The host streams each one near a guest (pose, visibility, animator) and the guest's copy
// follows with its own logic off. Shooing them (BirdsStartFlyingTrigger) is story and runs on the host: used by a
// guest it goes whole through StoryInteract; set off any other way on a guest's copy, the host is asked to do it.
public static class CrowSync
{
    private const float SendEvery = 0.1f, Range = 120f, StaleAfter = 3f, Follow = 12f;
    private const int PerPacket = 3;  // unreliable packets must fit 1023 bytes; the keys are scene paths

    private sealed class Copy
    {
        public CollectProjectile Bird;
        public Animator Anim;
        public Rigidbody Rb;
        public bool WasKinematic, WasEnabled;
        public Vector3 Pos; public Quaternion Rot; public float Vis; public bool On;
        public float[] Params; public (int hash, float time)[] States;
        public float LastHeard;
    }

    private static readonly Dictionary<string, Copy> Copies = new();
    private static readonly Dictionary<System.IntPtr, string> KeyOf = new();
    private static List<(string key, CollectProjectile bird)> _flock;
    private static float _next, _flockAt = -100;
    internal static int Sent, Received;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Crows && session.Role == Role.Client) Receive(r); };
        session.PlayerLeft += id => { if (session.Role == Role.Client && id == 0) ReleaseAll(); };
    }

    public static void ResetLocal()
    {
        Copies.Clear();
        KeyOf.Clear();
        _flock = null;
        _flockAt = -100;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || !PlayerSync.InWorld) return;
        if (session.Role == Role.Host) Send(session);
        else if (session.Role == Role.Client) Play();
    }

    // Host: the birds that belong to places. Looked up every few seconds; they are there from the start of a world.
    private static List<(string key, CollectProjectile bird)> Flock()
    {
        if (_flock != null && Time.unscaledTime - _flockAt < 5f) return _flock;
        _flockAt = Time.unscaledTime;
        _flock = new List<(string, CollectProjectile)>();
        var all = Find.Scanned<CollectProjectile>(5f, inactive: true);
        if (all == null) return _flock;
        foreach (var bird in all)
        {
            if (bird == null || bird.isPlayerBird) continue;  // (a place's birds know the raven spell too: `origin` is set on all)
            if (bird.GetComponentInParent<POI_SaveLoad>(true) == null) continue;
            if (!KeyOf.TryGetValue(bird.Pointer, out var key)) KeyOf[bird.Pointer] = key = ScenePath.Of(bird.transform);
            if (key != null) _flock.Add((key, bird));
        }
        return _flock;
    }

    private static void Send(CoopSession session)
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + SendEvery;
        var guests = new List<Vector3>();
        foreach (var p in session.Players.Values)
            if (p.Ready && PlayerSync.PuppetRoot(p.PlayerId) is Transform t) guests.Add(t.position);
        if (guests.Count == 0) return;

        var batch = new List<(string key, CollectProjectile bird)>();
        foreach (var (key, bird) in Flock())
        {
            if (bird == null || bird.isPlayerBird) continue;
            var pos = bird.transform.position;
            foreach (var g in guests)
                if ((g - pos).sqrMagnitude < Range * Range) { batch.Add((key, bird)); break; }
        }
        for (int i = 0; i < batch.Count; i += PerPacket)
        {
            var w = session.BeginMessage(MsgType.Crows);
            int n = Mathf.Min(PerPacket, batch.Count - i);
            w.Put((byte)n);
            for (int k = i; k < i + n; k++)
            {
                var (key, bird) = batch[k];
                var t = bird.transform;
                var p = t.position;
                var q = t.rotation;
                bool on = bird.gameObject.activeInHierarchy && !bird.isDespawning;
                w.Put(key);
                w.Put(p.x); w.Put(p.y); w.Put(p.z);
                w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w);
                w.Put(on);
                w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(bird.birdVisibility) * 255));
                MirrorSync.WriteAnimator(w, on ? bird.GetComponent<Animator>() : null);
                Sent++;
            }
            session.Send(w, DeliveryMethod.Sequenced);
        }
    }

    private static void Receive(NetDataReader r)
    {
        int n = r.GetByte();
        float now = Time.unscaledTime;
        for (int i = 0; i < n; i++)
        {
            string key = r.GetString();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
            bool on = r.GetBool();
            float vis = r.GetByte() / 255f;
            var (values, states) = MirrorSync.ReadAnimator(r);
            Received++;
            if (!Copies.TryGetValue(key, out var c) || c.Bird == null)
            {
                var bird = ScenePath.Resolve(key)?.GetComponent<CollectProjectile>();
                if (bird == null || bird.isPlayerBird) continue;
                c = new Copy { Bird = bird, Anim = bird.GetComponent<Animator>(), Rb = bird.GetComponent<Rigidbody>(), WasEnabled = bird.enabled };
                if (c.Rb != null) c.WasKinematic = c.Rb.isKinematic;
                Copies[key] = c;
                bird.transform.SetPositionAndRotation(pos, rot);
                Plugin.Logger.LogInfo($"Crows: following the host's {bird.name} ({Copies.Count} followed)");
            }
            c.Pos = pos; c.Rot = rot; c.On = on; c.Vis = vis;
            if (values.Length > 0) { c.Params = values; c.States = states; }
            c.LastHeard = now;
        }
    }

    // Guest: every frame, each followed bird goes where the host's is; its own flying stays off.
    private static void Play()
    {
        if (Copies.Count == 0) return;
        float k = 1f - Mathf.Exp(-Follow * Time.deltaTime);
        List<string> gone = null;
        foreach (var (key, c) in Copies)
        {
            if (c.Bird == null) { (gone ??= new()).Add(key); continue; }
            if (Time.unscaledTime - c.LastHeard > StaleAfter) { Release(c); (gone ??= new()).Add(key); continue; }
            var go = c.Bird.gameObject;
            if (go.activeSelf != c.On && (c.On || go.activeInHierarchy)) go.SetActive(c.On);
            if (!c.On) continue;
            if (c.Bird.enabled) c.Bird.enabled = false;
            if (c.Rb != null && !c.Rb.isKinematic) c.Rb.isKinematic = true;
            var t = c.Bird.transform;
            // A long way off (it just came into range, or was put somewhere by the place's saved state): no gliding there.
            if ((t.position - c.Pos).sqrMagnitude > 400f) t.SetPositionAndRotation(c.Pos, c.Rot);
            else t.SetPositionAndRotation(Vector3.Lerp(t.position, c.Pos, k), Quaternion.Slerp(t.rotation, c.Rot, k));
            MirrorSync.ApplyAnimator(c.Anim, c.Params, c.States);
        }
        if (gone != null) foreach (var key in gone) Copies.Remove(key);
    }

    // The host stopped sending it (we walked away, or left): it flies by itself again.
    private static void Release(Copy c)
    {
        if (c.Bird == null) return;
        c.Bird.enabled = c.WasEnabled;
        if (c.Rb != null) c.Rb.isKinematic = c.WasKinematic;
    }

    private static void ReleaseAll()
    {
        foreach (var c in Copies.Values) Release(c);
        Copies.Clear();
    }

    // Host, for a guest whose copy set a flock flying some way other than using it (walked into its trigger).
    internal static void FlyFor(BirdsStartFlyingTrigger trigger, int player)
    {
        if (trigger == null || trigger.birdsStartedFlying) return;
        Plugin.Logger.LogInfo($"Crows: {NameTags.NameOf(player)} set {trigger.name} flying; flying here");
        trigger.StartFlying(false);
    }

    public static string Describe()
    {
        var s = $"crows: sent={Sent} received={Received} followed={Copies.Count}";
        foreach (var bird in Object.FindObjectsByType<CollectProjectile>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            s += $"\n  bird {ScenePath.Of(bird.transform)} at {bird.transform.position} on={bird.gameObject.activeInHierarchy} sitting={bird.isSitting} vis={bird.birdVisibility:0.00} player={bird.isPlayerBird} origin={bird.origin != null} place={bird.GetComponentInParent<POI_SaveLoad>(true) != null} logic={bird.enabled} behaviour={bird.CurrentBehaviour}";
        foreach (var (key, c) in Copies)
            if (c.Bird != null) s += $"\n  following {key} at {c.Bird.transform.position} (host {c.Pos}) on={c.On} sitting={c.Bird.isSitting} logic={c.Bird.enabled}";
        foreach (var t in Object.FindObjectsByType<BirdsStartFlyingTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (t.GetComponentInParent<POI_SaveLoad>(true) != null)
                s += $"\n  trigger {ScenePath.Of(t.transform)} active={t.gameObject.activeInHierarchy} flown={t.birdsStartedFlying} birds={t.birds?.Count}";
        return s;
    }

    [HarmonyPatch(typeof(BirdsStartFlyingTrigger))]
    private static class Hooks
    {
        private static bool _applying;

        [HarmonyPrefix, HarmonyPatch(nameof(BirdsStartFlyingTrigger.ApplySavedState))]
        private static void Applying() => _applying = true;

        [HarmonyFinalizer, HarmonyPatch(nameof(BirdsStartFlyingTrigger.ApplySavedState))]
        private static void Applied() => _applying = false;

        // Guest: a flock set flying by this copy alone flew on this copy alone. The host does it; what follows (the
        // birds gone, the place's state) comes back. Loading a save or the host's state for the place may.
        [HarmonyPrefix, HarmonyPatch(nameof(BirdsStartFlyingTrigger.StartFlying))]
        private static bool Start(BirdsStartFlyingTrigger __instance)
        {
            if (!WorldSync.IsGuest || _applying || WorldObjects.ApplyingRemote || SaveLoadGame.isLoading) return true;
            if (CoopDriver.Session is not { IsConnected: true }) return true;
            Intents.Send(Intents.Kind.Fly, __instance);
            return false;
        }
    }
}
