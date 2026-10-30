#if DEVBUILD
using System;
using UnityEngine;

namespace RekaCoop.Dev;

// Test bots driven through the game's own debug move input, so sync can be checked with nobody at the keyboard.
//   -coopbotwalk    walk in a slow circle
//   -coopbotfollow  walk toward the nearest puppet and stop near it (keeps it in this camera's view)
internal static class BotWalk
{
    private static readonly bool Circle = HasArg("-coopbotwalk");
    private static readonly bool Follow = HasArg("-coopbotfollow");
    private static float _startedAt = -1;

    private static bool HasArg(string name) =>
        Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public static void Tick()
    {
        if (!Circle && !Follow) return;
        var local = UnityEngine.Object.FindFirstObjectByType<PlayerMovement_2021>();
        if (local == null) { _startedAt = -1; return; }
        if (_startedAt < 0) _startedAt = Time.unscaledTime + 5f;  // let the world settle first
        if (Time.unscaledTime < _startedAt) return;

        Vector2 input = Vector2.zero;
        if (Circle)
        {
            float a = (Time.unscaledTime - _startedAt) * 0.4f;
            input = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
        }
        else if (Sync.PlayerSync.NearestPuppet(local.transform.position) is Vector3 target)
        {
            var toTarget = target - local.transform.position;
            toTarget.y = 0;
            var cam = Camera.main;
            if (toTarget.magnitude > 3f && cam != null)
            {
                // Move input is camera-relative.
                var local2 = cam.transform.InverseTransformDirection(toTarget.normalized);
                input = new Vector2(local2.x, local2.z).normalized;
            }
        }
        PlayerMovement_2021.DebugMoveInputOverride = new Il2CppSystem.Nullable<Vector2>(input);
    }
}
#endif
