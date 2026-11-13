using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RekaCoop.Sync;

// Player detectors (ObjectDetector with the "Player" tag) move the story when someone walks up: the old woman comes to
// talk at her door, a monologue starts, a candle lights, the weather turns. Each copy's detectors only see that copy's
// own player, so a guest walking up to the old woman started her talk on the guest's copy alone and the host was never
// brought over. They now run on the host for everyone: the host's polling detectors are ticked with a guest's position
// when that guest is inside and the host isn't, and a guest's copy doesn't run their enter or exit (the results come
// from the host like any story change). A talk started that way is led by the guest who walked in.
// A guest's copy also reports each enter; the host runs it only if its own copy of that detector is off (a carried
// ritual egg switches on the oven's detector on the carrier's copy alone). Animals' flee triggers are FleeSync's.
[HarmonyPatch]
internal static class DetectorSync
{
    private static bool _acting;                          // host: the game's player references point at that guest
    private static int _tickFor = -1;                     // host: the guest a detector is being ticked for
    private static readonly HashSet<int> Logged = new();  // guest: detectors already logged as skipped
    private static readonly Dictionary<int, float> EnabledAt = new();  // host: when each detector last came on
    private const float JustSwitchedOn = 2f;

    private static bool Tracks(ObjectDetector d) =>
        d != null && d.tagString == "Player" && d.TracksThePlayer() && !PetEligibility.IsPlayerFleeTrigger(d);

    // An animal noticing someone (a hare's ObservationDetection) doesn't start talks; it once made the guest lead the
    // host's arrival talk after travelling.
    private static bool Watches(ObjectDetector d) => d.name.Contains("Observation") || d.transform.parent != null && d.transform.parent.name.Contains("ObservingAndFleeing");

    private static bool Connected(out Net.Role role)
    {
        var s = CoopDriver.Session;
        role = s?.Role ?? Net.Role.None;
        return s != null && s.IsConnected;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.ProximityTick))]
    private static void Tick(ObjectDetector __instance, ref Vector3 playerPos)
    {
        _tickFor = -1;
        if (!Connected(out var role) || role != Net.Role.Host || !Tracks(__instance)) return;
        if (__instance.ContainsPoint(playerPos)) return;  // the host is inside
        var head = playerPos - (PlayerSync.LocalFeet ?? playerPos);  // the manager passes the head; puppets are placed by their feet
        foreach (var (id, feet) in PlayerSync.PuppetPositions())
        {
            var at = feet + head;
            if (!__instance.ContainsPoint(at)) continue;
            playerPos = at;
            _tickFor = id;
            _acting = ActingPlayer.Enter(id);
            return;
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.OnEnable))]
    private static void Enabled(ObjectDetector __instance) => EnabledAt[__instance.GetInstanceID()] = Time.unscaledTime;

    [HarmonyPostfix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.ProximityTick))]
    private static void Ticked()
    {
        _tickFor = -1;
        if (_acting) ActingPlayer.Exit();
        _acting = false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.OnEntered))]
    private static bool Entered(ObjectDetector __instance) => Run(__instance, true);

    [HarmonyPrefix, HarmonyPatch(typeof(ObjectDetector), nameof(ObjectDetector.OnExited))]
    private static bool Exited(ObjectDetector __instance) => Run(__instance, false);

    private static bool Run(ObjectDetector d, bool enter)
    {
        if (!Connected(out var role) || !Tracks(d)) return true;
        if (role == Net.Role.Host)
        {
            // Switched on with the guest already standing in it: the talk comes from the step someone just did (the
            // host put the chicken in the nest), not from the guest walking up; whoever did that step leads.
            if (enter && _tickFor >= 0 && EnabledAt.TryGetValue(d.GetInstanceID(), out var on) && Time.unscaledTime - on < JustSwitchedOn)
                Plugin.Logger.LogInfo($"Detectors: {NameTags.NameOf(_tickFor)} was already inside {ScenePath.Of(d.transform)} when it came on");
            else if (enter && _tickFor >= 0 && !Watches(d))
            {
                DialogueSync.NoteRequest(_tickFor, 10f, walkedIn: true);
                Plugin.Logger.LogInfo($"Detectors: {NameTags.NameOf(_tickFor)} walked into {ScenePath.Of(d.transform)}");
            }
            // The host's own step counts too, or a guest's walk into some other detector (the Lutki wander in and out of
            // theirs) would still lead the talk the host just set off.
            else if (enter && _tickFor < 0 && !Watches(d)) DialogueSync.NoteRequest(DialogueSync.Me, 10f, walkedIn: true);
            return true;
        }
        // Wildlife the map spawns around each player (ODPOI_WildHare) is each copy's own: the host has no such animal
        // to run this for, so a guest's hare notices the guest by itself.
        if (Watches(d) && d.transform.root.name.StartsWith("ODPOI_")) return true;
        if (Logged.Add(d.GetInstanceID()))
            Plugin.Logger.LogInfo($"Detectors: {(enter ? "entered" : "left")} {ScenePath.Of(d.transform)}; the host runs it");
        // The host's copy of it may be off: something only this copy did switched it on.
        if (enter && !WorldObjects.ApplyingRemote) Intents.Send(Intents.Kind.Enter, d);
        return false;
    }

    // dev
    internal static string PuppetInside(ObjectDetector d)
    {
        var head = (GeneralAIController.PlayerHeadTransform?.position ?? Vector3.zero) - (PlayerSync.LocalFeet ?? Vector3.zero);
        return string.Join(",", System.Linq.Enumerable.Select(PlayerSync.PuppetPositions(), p => { try { return $"{p.Item1}:{d.ContainsPoint(p.Item2 + head)}/{d.ContainsPoint(p.Item2)}"; } catch { return "?"; } }));
    }

    // Host: this detector is on here, but whether the speaker can see the one who walked in was judged on that guest's
    // copy, with its real player; here the test runs against a puppet and doesn't pass.
    internal static bool NeedsWord(ObjectDetector d) =>
        d != null && d.doLOSCheck && !d._losEnteredThisStay && !(d.triggerOnlyOnce && d.onEnterHasBeenTriggered);

    // Host: a guest walked into a detector that was off here (now switched on for them), or one that wants a line of sight.
    internal static void EnterFor(ObjectDetector d, int player)
    {
        if (d == null || d.triggerOnlyOnce && d.onEnterHasBeenTriggered) return;
        _tickFor = player;
        try { d.OnEntered(); if (d.doLOSCheck) d._losEnteredThisStay = true; }
        catch (System.Exception e) { Plugin.Logger.LogWarning($"Detectors: entering {d.name} for {NameTags.NameOf(player)} failed: {e.Message}"); }
        finally { _tickFor = -1; }
    }
}
