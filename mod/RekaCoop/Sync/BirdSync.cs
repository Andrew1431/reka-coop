using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// The raven spell, seen by everyone. Each player streams where their own birds are flying (ten times a second) and the
// others fly look-alike copies of the bird prefab along that path: the copy's own logic is off (it would collect
// items and fly home to the wrong player), only its model, animator and trail effects run. Collecting itself is
// already shared through WorldObjects and the pack.
public static class BirdSync
{
    private const float SendEvery = 0.1f, StaleAfter = 1f, Follow = 12f;

    private class Copy { public GameObject Go; public Material[] Mats; public Vector3 Pos; public Quaternion Rot; public float Vis, LastHeard; }

    private static readonly int Visibility = Shader.PropertyToID("Visibility");

    private static readonly Dictionary<(int player, byte slot), Copy> Copies = new();
    private static readonly Dictionary<System.IntPtr, byte> SlotOf = new();
    private static UseItem_Birds _birds;
    private static float _next;
    private static bool _wasSending;
    internal static int Sent;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Birds) Receive(session, from, r); };
        session.PlayerLeft += id => DropPlayer(id);
    }

    public static void ResetLocal()
    {
        foreach (var c in Copies.Values) if (c.Go != null) Object.Destroy(c.Go);
        Copies.Clear();
        SlotOf.Clear();
        _birds = null;
    }

    private static UseItem_Birds Birds()
    {
        if (_birds == null && Find.Scanned<UseItem_Birds>(30f, inactive: true) is { Length: > 0 } all) _birds = all[0];
        return _birds;
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected) return;
        Send(session);
        Move();
    }

    private static void Send(CoopSession session)
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + SendEvery;
        var b = Birds();
        var flying = new List<(byte slot, CollectProjectile bird)>();
        if (b != null && b.spawnedBirds != null)
            foreach (var bird in b.spawnedBirds)
            {
                if (bird == null || bird.isDespawning || bird.birdVisibility < 0.05f || !bird.gameObject.activeInHierarchy) continue;
                if (!SlotOf.TryGetValue(bird.Pointer, out var slot))
                {
                    slot = (byte)(SlotOf.Count % 250);
                    SlotOf[bird.Pointer] = slot;
                }
                flying.Add((slot, bird));
            }
        if (flying.Count == 0 && !_wasSending) return;
        _wasSending = flying.Count > 0;  // one empty message lets the others put their copies away at once
        var w = session.BeginMessage(MsgType.Birds);
        w.Put(session.LocalPlayerId);
        w.Put((byte)flying.Count);
        foreach (var (slot, bird) in flying)
        {
            w.Put(slot);
            var t = bird.transform;
            var p = t.position;
            var e = t.rotation.eulerAngles;
            w.Put(p.x); w.Put(p.y); w.Put(p.z);
            w.Put(e.x); w.Put(e.y);
            w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(bird.birdVisibility) * 255));
        }
        session.Send(w, DeliveryMethod.Sequenced);
        Sent++;
    }

    private static void Receive(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        int start = r.Position;
        int player = r.GetInt();
        int n = r.GetByte();
        var seen = new HashSet<byte>();
        for (int i = 0; i < n; i++)
        {
            byte slot = r.GetByte();
            var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            var rot = Quaternion.Euler(r.GetFloat(), r.GetFloat(), 0f);
            float vis = r.GetByte() / 255f;
            seen.Add(slot);
            if (!Copies.TryGetValue((player, slot), out var c))
            {
                var go = Spawn(pos, rot);
                if (go == null) continue;
                var mats = new List<Material>();
                foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in rend.materials)
                        if (m != null && m.HasProperty(Visibility)) mats.Add(m);
                c = new Copy { Go = go, Mats = mats.ToArray() };
                Copies[(player, slot)] = c;
            }
            c.Pos = pos;
            c.Rot = rot;
            c.Vis = vis;
            c.LastHeard = Time.unscaledTime;
        }
        var gone = new List<(int, byte)>();
        foreach (var key in Copies.Keys) if (key.player == player && !seen.Contains(key.slot)) gone.Add(key);
        foreach (var key in gone) Remove(key);

        if (session.Role != Role.Host) return;
        var w = session.BeginMessage(MsgType.Birds);
        w.Put(r.RawData, start, r.Position - start);
        session.Send(w, DeliveryMethod.Sequenced, exceptPlayerId: from.PlayerId);
    }

    private static GameObject Spawn(Vector3 pos, Quaternion rot)
    {
        var b = Birds();
        if (b == null || b.collectProjectilePrefab == null) return null;
        var go = Object.Instantiate(b.collectProjectilePrefab, pos, rot);
        go.name = "CoopBird";
        foreach (var p in go.GetComponentsInChildren<CollectProjectile>(true))
        {
            p.enabled = false;
            Object.Destroy(p);
        }
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
        foreach (var col in go.GetComponentsInChildren<Collider>(true)) col.enabled = false;
        foreach (var vfx in go.GetComponentsInChildren<UnityEngine.VFX.VisualEffect>(true)) vfx.Play();
        return go;
    }

    private static void Move()
    {
        if (Copies.Count == 0) return;
        float k = 1f - Mathf.Exp(-Follow * Time.deltaTime);
        List<(int, byte)> stale = null;
        foreach (var (key, c) in Copies)
        {
            if (c.Go == null || Time.unscaledTime - c.LastHeard > StaleAfter) { (stale ??= new()).Add(key); continue; }
            var t = c.Go.transform;
            t.position = Vector3.Lerp(t.position, c.Pos, k);
            t.rotation = Quaternion.Slerp(t.rotation, c.Rot, k);
            foreach (var m in c.Mats) m.SetFloat(Visibility, c.Vis);
        }
        if (stale != null) foreach (var key in stale) Remove(key);
    }

    private static void Remove((int, byte) key)
    {
        if (Copies.TryGetValue(key, out var c) && c.Go != null) Object.Destroy(c.Go);
        Copies.Remove(key);
    }

    private static void DropPlayer(int id)
    {
        var gone = new List<(int, byte)>();
        foreach (var key in Copies.Keys) if (key.player == id) gone.Add(key);
        foreach (var key in gone) Remove(key);
    }

    // Dev: a copy hovering in front of the camera for a few seconds.
    public static string Test()
    {
        var cam = Camera.main;
        if (cam == null) return "no camera";
        var pos = cam.transform.position + cam.transform.forward * 3f;
        var go = Spawn(pos, Quaternion.LookRotation(cam.transform.right));
        if (go == null) return "no prefab";
        var mats = new List<Material>();
        foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            foreach (var m in rend.materials)
                if (m != null && m.HasProperty(Visibility)) mats.Add(m);
        Copies[(-1, 0)] = new Copy { Go = go, Mats = mats.ToArray(), Pos = pos, Rot = go.transform.rotation, Vis = 1f, LastHeard = Time.unscaledTime + 5f };
        return $"test bird at {pos}, {mats.Count} materials";
    }

    public static string Describe()
    {
        var b = Birds();
        var s = $"birds: mine={(b?.spawnedBirds?.Count ?? -1)} sent={Sent} copies={Copies.Count}";
        if (b?.spawnedBirds != null)
            foreach (var bird in b.spawnedBirds)
            {
                if (bird == null) continue;
                s += $" | mine vis={bird.birdVisibility:0.00} emis={bird.currentEmission:0.00} trail={bird.currentTrailLength:0.00}";
                if (bird.materials != null)
                    foreach (var m in bird.materials)
                    {
                        if (m == null) continue;
                        s += $" [{m.name}";
                        for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                            if (m.shader.GetPropertyType(i) is UnityEngine.Rendering.ShaderPropertyType.Float or UnityEngine.Rendering.ShaderPropertyType.Range)
                                s += $" {m.shader.GetPropertyName(i)}={m.GetFloat(m.shader.GetPropertyName(i)):0.##}";
                        s += "]";
                    }
                break;
            }
        foreach (var (key, c) in Copies)
        {
            if (c.Go == null) continue;
            s += $" | p{key.player}/{key.slot} at {c.Go.transform.position}";
            foreach (var rend in c.Go.GetComponentsInChildren<Renderer>(true))
            {
                s += $" [{rend.GetType().Name} {rend.name} on={rend.enabled}";
                foreach (var m in rend.sharedMaterials)
                    if (m != null) s += $" {m.name}/{m.shader.name}";
                s += "]";
            }
        }
        return s;
    }
}
