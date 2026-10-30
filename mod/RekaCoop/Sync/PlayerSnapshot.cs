using LiteNetLib.Utils;
using UnityEngine;

namespace RekaCoop.Sync;

// One sample of a player's body: where it is and what its animator is doing. Animator floats and bools are
// sent by parameter index (both sides run the same controller); per-layer state hashes cover triggers.
public class PlayerSnapshot
{
    public const int Layers = 3;

    public Vector3 Position;   // world, or relative to the hut floor when OnHut
    public float Yaw;
    public bool OnHut;         // riding the hut: Position/Yaw are in the hut floor's frame, so riders don't lag behind it
    public float[] Floats = System.Array.Empty<float>();
    public ulong Bools;  // bit i = i-th bool parameter
    public readonly int[] StateHash = new int[Layers];
    public readonly float[] StateTime = new float[Layers];
    public readonly float[] LayerWeight = new float[Layers];
    // Carrying (and anything else posed by the arm IK rigs): the arms are posed by rigs the puppet doesn't have, so their bones' local rotations come along.
    public Quaternion[] Arms;

    public void Write(NetDataWriter w)
    {
        w.Put(Position.x); w.Put(Position.y); w.Put(Position.z);
        w.Put(Yaw);
        w.Put(OnHut);
        w.Put((byte)Floats.Length);
        foreach (var f in Floats) w.Put(f);
        w.Put(Bools);
        for (int i = 0; i < Layers; i++) { w.Put(StateHash[i]); w.Put(StateTime[i]); w.Put((byte)Mathf.RoundToInt(Mathf.Clamp01(LayerWeight[i]) * 255)); }
        w.Put((byte)(Arms?.Length ?? 0));
        if (Arms != null)
            foreach (var q in Arms) { PutHalf(w, q.x); PutHalf(w, q.y); PutHalf(w, q.z); PutHalf(w, q.w); }
    }

    private static void PutHalf(NetDataWriter w, float f) => w.Put(System.BitConverter.HalfToInt16Bits((System.Half)f));
    private static float GetHalf(NetDataReader r) => (float)System.BitConverter.Int16BitsToHalf(r.GetShort());

    public static PlayerSnapshot Read(NetDataReader r)
    {
        var s = new PlayerSnapshot { Position = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat()), Yaw = r.GetFloat(), OnHut = r.GetBool() };
        s.Floats = new float[r.GetByte()];
        for (int i = 0; i < s.Floats.Length; i++) s.Floats[i] = r.GetFloat();
        s.Bools = r.GetULong();
        for (int i = 0; i < Layers; i++) { s.StateHash[i] = r.GetInt(); s.StateTime[i] = r.GetFloat(); s.LayerWeight[i] = r.GetByte() / 255f; }
        int arms = r.GetByte();
        if (arms > 0)
        {
            s.Arms = new Quaternion[arms];
            for (int i = 0; i < arms; i++) s.Arms[i] = new Quaternion(GetHalf(r), GetHalf(r), GetHalf(r), GetHalf(r));
        }
        return s;
    }
}
