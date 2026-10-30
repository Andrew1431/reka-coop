using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Blueprint sites: laid-out ghosts of a blueprint that anyone can fill in. Pieces built into a site travel as ordinary
// house blocks (BuildSync); this shares the sites themselves, the way the game saves them (SavedBBs.blueprintSites:
// card, frame, blocks, which ones are done). Whoever changes the list sends all of it; everyone else loads it in place
// of theirs. Checked once a second; nothing is sent while nobody has a site.
public static class BlueprintSync
{
    private enum Kind : byte { State = 1, Ready = 2 }

    private static string _known;  // the sites as agreed (serialized); null until the house is ready
    private static float _next;
    private static bool _readySent;

    private static BB_Building Building => RekaCoop.Sync.Find.One<BB_Building>();
    private static PossessionTypeReference Ids => RekaCoop.Sync.Find.One<SaveLoadBBs>()?.BBIDConverter;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Blueprint) OnMessage(session, from, r); };
    }

    public static void ResetLocal()
    {
        _known = null;
        _readySent = false;
    }

    private static string Capture()
    {
        var b = Building;
        var ids = Ids;
        if (b == null || ids == null) return null;
        var saved = new SaveLoadBBs.SavedBBs { blueprintSites = b.GetBlueprintSitesForSaving(ids) };
        return saved.blueprintSites == null || saved.blueprintSites.Count == 0
            ? ""
            : ClassicElementStateBridge.Serialize(Il2CppType.Of<SaveLoadBBs.SavedBBs>(), saved);
    }

    private static void Apply(string xml)
    {
        var b = Building;
        var ids = Ids;
        if (b == null || ids == null) return;
        var sites = xml.Length == 0
            ? new Il2CppSystem.Collections.Generic.List<BlueprintSiteData>()
            : ClassicElementStateBridge.Deserialize(Il2CppType.Of<SaveLoadBBs.SavedBBs>(), xml).Cast<SaveLoadBBs.SavedBBs>().blueprintSites;
        var placed = new Il2CppSystem.Collections.Generic.List<HouseBuildingObject>();
        foreach (var kv in BB_Building.placedBBs)
        {
            var hbo = kv.Key != null ? kv.Key.GetComponent<HouseBuildingObject>() : null;
            if (hbo != null) placed.Add(hbo);
        }
        b.ClearBlueprintSites();
        b.RestoreBlueprintSites(sites, placed, ids);
        _known = Capture();
        Plugin.Logger.LogInfo($"Blueprints: took {sites?.Count ?? 0} sites from another player");
    }

    public static void Tick(CoopSession session)
    {
        if (!session.IsConnected || !HouseBlocks.Ready || Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 1f;
        if (_known == null)
        {
            if (session.Role == Role.Host) { _known = Capture(); return; }
            if (_readySent) return;
            _readySent = true;
            var rw = session.BeginMessage(MsgType.Blueprint);
            rw.Put((byte)Kind.Ready);
            session.Send(rw, DeliveryMethod.ReliableOrdered);
            return;
        }
        var now = Capture();
        if (now == null || now == _known) return;
        _known = now;
        Send(session, now, -1);
        Plugin.Logger.LogInfo($"Blueprints: sent my sites ({now.Length} chars)");
    }

    private static void Send(CoopSession session, string xml, int toPlayer)
    {
        var w = session.BeginMessage(MsgType.Blueprint);
        w.Put((byte)Kind.State);
        w.Put(xml);
        if (toPlayer >= 0) session.SendTo(toPlayer, w, DeliveryMethod.ReliableOrdered);
        else session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        var kind = (Kind)r.GetByte();
        if (kind == Kind.Ready && session.Role == Role.Host)
        {
            var xml = _known ?? Capture();
            if (!string.IsNullOrEmpty(xml)) Send(session, xml, from.PlayerId);
            else
            {
                var w = session.BeginMessage(MsgType.Blueprint);  // an empty list still says "ready"
                w.Put((byte)Kind.State);
                w.Put("");
                session.SendTo(from.PlayerId, w, DeliveryMethod.ReliableOrdered);
            }
            return;
        }
        if (kind != Kind.State) return;
        string state = r.GetString(int.MaxValue);
        if (session.Role == Role.Host)
        {
            var w = session.BeginMessage(MsgType.Blueprint);
            w.Put((byte)Kind.State);
            w.Put(state);
            session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
        }
        if (!HouseBlocks.Ready) return;
        if (state == (_known ?? Capture())) { _known = state; return; }
        try { Apply(state); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Blueprints: can't load the sites: {e.InnerException?.Message ?? e.Message}"); }
    }

    // Dev: unlock blueprints and lay out a one-block site next to the newest block (test saves have none).
    public static string DevMakeSite()
    {
        var b = Building;
        var ids = Ids;
        if (b == null || ids == null) return "no building";
        b.DevForceBlueprintUnlockStage((BlueprintUnlockStage)99);
        HouseBlueprintCard card = null;
        foreach (var c in Resources.FindObjectsOfTypeAll<HouseBlueprintCard>()) { card = c; break; }
        if (card == null) return "no blueprint card loaded";
        HouseBlock top = null;
        foreach (var hb in HouseBlocks.Capture().Values) if (top == null || hb.Index > top.Index) top = hb;
        if (top == null) return "no blocks";
        var block = new BlueprintBlockData
        {
            typeID = top.TypeId, partPosition = new SaveGame.SerializableVector3(top.RefPos), partRotation = new SaveGame.SerializableQuaternion(top.RefRot),
            blockLocalPosition = new SaveGame.SerializableVector3(top.LocalPos + Vector3.up), blockLocalRotation = new SaveGame.SerializableQuaternion(top.LocalRot),
            diagonalForward = new SaveGame.SerializableVector3(top.DiagonalForward), boardDirection = new SaveGame.SerializableVector3(top.BoardDirection),
        };
        var site = new BlueprintSiteData
        {
            cardID = ids.TypeToID(card), framePosition = new SaveGame.SerializableVector3(Vector3.zero), frameRotation = new SaveGame.SerializableQuaternion(Quaternion.identity),
            blocks = new Il2CppSystem.Collections.Generic.List<BlueprintBlockData>(), states = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(1),
            layoutPlacementIndex = -1,
        };
        site.blocks.Add(block);
        var sites = b.GetBlueprintSitesForSaving(ids);
        sites.Add(site);
        var placed = new Il2CppSystem.Collections.Generic.List<HouseBuildingObject>();
        foreach (var kv in BB_Building.placedBBs)
        {
            var hbo = kv.Key != null ? kv.Key.GetComponent<HouseBuildingObject>() : null;
            if (hbo != null) placed.Add(hbo);
        }
        b.ClearBlueprintSites();
        b.RestoreBlueprintSites(sites, placed, ids);
        return $"card {site.cardID}; {Describe()}";
    }

    // Dev.
    public static string Describe()
    {
        var b = Building;
        if (b == null) return "no building";
        return $"sites {b.GetBlueprintSitesForSaving(Ids)?.Count ?? -1}, unlocked={b.BlueprintCardsUnlocked}, story allows={BB_Building.StoryAllowsBlueprints}, agreed {(_known == null ? "not yet" : _known.Length + " chars")}";
    }
}
