using System;
using UnityEngine;

namespace RekaCoop.Sync;

// Receives the walk cycle's animation events on a puppet and plays footsteps at the puppet's own feet, through the
// local player's WalkingSounds. The game's own receiver (PlayerAnimationEventForward) is stripped from puppets
// because it would play them at the local player's feet.
public class PuppetAnimEvents : MonoBehaviour
{
    public PuppetAnimEvents(IntPtr ptr) : base(ptr) { }

    public static int Steps;  // diagnostics

    private Transform _leftFoot, _rightFoot;
    private float _lastStep;

    public void OnFootFall(int index)
    {
        try
        {
            if (Time.time - _lastStep < 0.15f) return;  // blended clips fire the same step twice
            var sounds = Sound;
            if (sounds == null || sounds.terrainInfo == null) return;
            _leftFoot ??= FindDeep(transform, "foot.l");
            _rightFoot ??= FindDeep(transform, "foot.r");
            var foot = WalkingSounds.DetermineFootType(index) ? _rightFoot : _leftFoot;
            var pos = foot != null ? foot.position : transform.position;
            int ground = (int)sounds.terrainInfo.GetGroundTypeAtPosition(pos);
            sounds.PlaySound(sounds.footSteps, ground, WalkingSounds.DetermineWalkType(index), pos);
            _lastStep = Time.time;
            Steps++;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Puppet footstep failed: {e.Message}");
        }
    }

    // Other clip events the player's animator sends; puppets ignore them.
    public void PlayAnimationVFX(string identifier) { }

    private static WalkingSounds _sound;
    private static WalkingSounds Sound => _sound != null ? _sound : _sound = FindFirstObjectByType<WalkingSounds>();

    private static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            var found = FindDeep(t.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }
}
