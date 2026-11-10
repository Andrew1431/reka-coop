using System;
using UnityEngine;

namespace RekaCoop.Dev;

// `-coopnoinput`: the keyboard, mouse and gamepads are switched off for this instance, so a scripted run
// (mod/regress.py) isn't steered by whoever is using the PC. Dev commands don't go through them.
internal static class NoInput
{
    private static readonly bool Enabled = Array.Exists(Environment.GetCommandLineArgs(),
        a => string.Equals(a, "-coopnoinput", StringComparison.OrdinalIgnoreCase));
    private static float _next;
    private static bool _said;

    public static void Tick()
    {
        if (!Enabled || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 2f;
        try
        {
            if (!Rewired.ReInput.isReady) return;
            var c = Rewired.ReInput.controllers;
            if (c.Keyboard != null) c.Keyboard.enabled = false;
            if (c.Mouse != null) c.Mouse.enabled = false;
            var pads = c.Joysticks;
            if (pads != null) for (int i = 0; i < c.joystickCount; i++) pads[i].enabled = false;
            if (!_said) { _said = true; Plugin.Logger.LogInfo("No input: keyboard, mouse and gamepads are off for this run (-coopnoinput)"); }
        }
        catch (Exception e) { if (!_said) { _said = true; Plugin.Logger.LogWarning($"No input: {e.Message}"); } }
    }
}
