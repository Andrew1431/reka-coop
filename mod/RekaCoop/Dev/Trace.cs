#if DEVBUILD
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace RekaCoop.Dev;

// trace <seconds>: every frame, the wall clock, this player's position and each puppet's, to trace.csv in the game
// folder. Two copies on one PC share the clock, so mod/synccheck.py can line a guest's real path up with the host's
// copy of it.
internal static class Trace
{
    private static float _until = -1;
    private static StringBuilder _rows;

    public static string Start(float seconds)
    {
        _until = Time.unscaledTime + seconds;
        _rows = new StringBuilder("utc_ms,who,x,y,z\n");
        return $"tracing for {seconds:0} s";
    }

    public static void Tick()
    {
        if (_rows == null) return;
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var me = Sync.Find.One<PlayerMovement_2021>();
        if (me != null) Row(ms, "self", me.transform.position);
        foreach (var (id, head) in Sync.PlayerSync.PuppetHeads()) Row(ms, $"puppet{id}", head - Vector3.up * 2.1f);
        if (Time.unscaledTime < _until) return;
        File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "trace.csv"), _rows.ToString());
        Plugin.Logger.LogInfo("Trace written");
        _rows = null;
    }

    private static void Row(long ms, string who, Vector3 p) =>
        _rows.Append(ms).Append(',').Append(who).Append(',').Append(p.x.ToString("0.000")).Append(',')
            .Append(p.y.ToString("0.000")).Append(',').Append(p.z.ToString("0.000")).Append('\n');
}
#endif
