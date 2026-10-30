using System;
using System.Collections.Generic;
using System.IO;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using RekaCoop.Sync;
using UnityEngine;

namespace RekaCoop.Appearance;

// Everyone's look. A guest wears their profile look instead of the one in the host's save, and keeps the
// profile updated when they change clothes. Looks are sent when they change; the host keeps the latest
// of each player and replays them to anyone who joins later.
public static class AppearanceSync
{
    private const float CheckEvery = 2f, ResendEvery = 20f;
    private static float _nextResend;
    private static bool _warnedNoLook;

    private static readonly Dictionary<int, AppearanceData> Looks = new();
    private static readonly Dictionary<int, string> LookXml = new();
    private static bool _appliedProfile;
    private static string _lastSentXml;
    private static AppearanceData _woreProfile;
    private static int _wearTries;
    private static float _nextCheck;

    private static readonly bool RandomLook = Array.Exists(Environment.GetCommandLineArgs(),
        a => string.Equals(a, "-cooprandomlook", StringComparison.OrdinalIgnoreCase));

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Appearance) OnLook(session, from, r); };
        session.PlayerLeft += id => { if (session.Role == Role.Client && id == 0) _lastSentXml = null; };  // resend after reconnecting
        session.PlayerJoined += id =>
        {
            foreach (var (playerId, look) in Looks) SendLook(session, playerId, look, toPlayer: id);
        };
        session.PlayerLeft += id => { Looks.Remove(id); LookXml.Remove(id); };
    }

    public static AppearanceData LookOf(int playerId) => Looks.TryGetValue(playerId, out var l) ? l : null;

    // Called every frame once the local body is fully assembled.
    public static void Tick(CoopSession session)
    {
        if (!_appliedProfile)
        {
            _appliedProfile = true;
            if (session.Role == Role.Client) WearProfile();
            _nextCheck = Time.unscaledTime + 5f;  // wearables load asynchronously; let the re-dress finish
            return;
        }
        if (Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + CheckEvery;

        var look = AppearanceBridge.CaptureLocal();
        if (look == null)
        {
            if (!_warnedNoLook) Plugin.Logger.LogWarning("Look: can't read the local look yet");
            _warnedNoLook = true;
            return;
        }
        string xml = look.ToXml();
        // Once in a while the same look goes out again, so a lost or overtaken one can't leave someone undressed.
        if (xml == _lastSentXml)
        {
            if (session.IsConnected && Time.unscaledTime >= _nextResend)
            {
                _nextResend = Time.unscaledTime + ResendEvery;
                SendLook(session, session.LocalPlayerId, look);
            }
            return;
        }
        _nextResend = Time.unscaledTime + ResendEvery;
        _lastSentXml = xml;

        if (session.Role == Role.Client)
        {
            // The first capture after putting the profile on must wear the same meshes; if not, the re-dress failed
            // and the profile must not be overwritten with the save's look. Try once more, then send what is worn.
            if (_woreProfile != null && !SameMeshes(look, _woreProfile))
            {
                Plugin.Logger.LogWarning($"Profile look did not stick (wearing {string.Join(",", look.Meshes)}; profile {string.Join(",", _woreProfile.Meshes)})");
                Dump(_woreProfile.ToXml(), xml);
                if (_wearTries < 2) { _appliedProfile = false; _lastSentXml = null; return; }
            }
            else
            {
                try { AppearanceProfile.Save(look); }
                catch (Exception e) { Plugin.Logger.LogWarning($"Couldn't save the look profile: {e.Message}"); }
            }
            _woreProfile = null;
        }
        Store(session.LocalPlayerId, look, xml);
        if (session.IsConnected) SendLook(session, session.LocalPlayerId, look);
        Plugin.Logger.LogInfo($"Local look changed ({look.Meshes.Count} meshes), sent");
    }

    // Leaving the world: the next world load re-applies the profile and re-sends.
    public static void ResetLocal()
    {
        _appliedProfile = false;
        _lastSentXml = null;
        _wearTries = 0;
    }

    // Material and float values come back slightly different from the game; the meshes are what make the look.
    private static bool SameMeshes(AppearanceData a, AppearanceData b) =>
        new HashSet<string>(a.Meshes).SetEquals(b.Meshes);

    private static void Dump(string profileXml, string wornXml)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppearanceProfile.FilePath);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mismatch-profile.xml"), profileXml);
            File.WriteAllText(Path.Combine(dir, "mismatch-worn.xml"), wornXml);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Couldn't write the look mismatch files: {e.Message}"); }
    }

    private static void WearProfile()
    {
        var profile = AppearanceProfile.Load() ?? AppearanceProfile.ImportFromSaves(OwnSaveRoot());
        // A guest is wearing the look from the host's save; without a look of their own they'd be the host's twin.
        bool twin = profile == null && Instance.Role == Role.Client;
        if (RandomLook || twin)
        {
            var assembler = RekaCoop.Sync.Find.One<PlayerMovement_2021>()?.GetComponent<CharacterAssembler>();
            assembler?.RandomizeLook();
            Plugin.Logger.LogInfo(twin ? "No look of my own (no profile, no save): randomized one" : "Randomized local look (-cooprandomlook)");
            if (twin) CoopDriver.Notice("No character created yet, so you're wearing a random look. Start a game of your own once to make your character; co-op uses it from then on.");
            return;  // the next capture saves it as the profile
        }
        if (profile == null) return;  // keep the save's look; the first capture becomes the profile
        if (AppearanceBridge.ApplyLocal(profile))
        {
            _woreProfile = profile;
            _wearTries++;
            Plugin.Logger.LogInfo($"Wearing profile look (try {_wearTries})");
        }
    }

    // Where this player's own saves are. A guest's save root is a sandbox holding the host's world, so a real guest
    // looks in the game's usual folder (read only); a dev guest copy has none of its own.
    private static string OwnSaveRoot()
    {
        if (Instance.Role != Role.Client) return FileIO.SaveFilesRootPath;
        if (Instance.Name != null) return null;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RekaGame", "Saves");
    }

    private static void OnLook(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        int playerId = r.GetInt();
        if (session.Role == Role.Host) playerId = from.PlayerId;  // clients only speak for themselves
        var look = AppearanceData.Read(r);
        if (playerId == session.LocalPlayerId) return;
        string xml = look.ToXml();
        if (LookXml.TryGetValue(playerId, out var known) && known == xml) return;

        Store(playerId, look, xml);
        if (session.Role == Role.Host) SendLook(session, playerId, look, exceptPlayerId: playerId);
        PlayerSync.ApplyLook(playerId, look);
        Plugin.Logger.LogInfo($"Received look for player {playerId}");
    }

    private static void Store(int playerId, AppearanceData look, string xml)
    {
        Looks[playerId] = look;
        LookXml[playerId] = xml;
    }

    private static void SendLook(CoopSession session, int playerId, AppearanceData look, int toPlayer = -1, int exceptPlayerId = -1)
    {
        NetDataWriter w = session.BeginMessage(MsgType.Appearance);
        w.Put(playerId);
        look.Write(w);
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId);
    }
}
