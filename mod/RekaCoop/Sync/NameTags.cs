using System.Collections.Generic;
using LiteNetLib;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Everyone's name over their head. Each player announces their Steam name and their instance name (-coopinstance, if
// any); the host relays it and tells newcomers everyone's. The Steam name shows, unless two players share it (two
// copies on one Steam account, in the dev sandbox): then the instance name.
public static class NameTags
{
    private static readonly Dictionary<int, (string steam, string instance)> Names = new();
    private static float _steamWaitFrom = -1;
    private static bool _announced;
    private static GUIStyle _style, _shadow;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Name) OnMessage(session, from, r); };
        session.PlayerJoined += id => { if (session.Role == Role.Host) SendAllTo(session, id); };
        session.PlayerLeft += id =>
        {
            Names.Remove(id);
            if (session.Role == Role.Client && id == 0) _announced = false;  // announce again after reconnecting
        };
    }

    private static string SteamName()
    {
        try { if (Steamworks.SteamClient.IsValid) return Steamworks.SteamClient.Name; } catch { }
        return null;
    }

    public static void Tick(CoopSession session)
    {
        if (_announced || !session.IsConnected) { _steamWaitFrom = -1; return; }
        if (_steamWaitFrom < 0) _steamWaitFrom = Time.unscaledTime;
        var steam = SteamName();
        if (steam == null && Time.unscaledTime - _steamWaitFrom < 10f) return;  // Steam may start after us
        var instance = Instance.Name ?? "";
        if (steam == null && instance == "") return;
        _announced = true;
        Names[session.LocalPlayerId] = (steam ?? "", instance);
        var w = session.BeginMessage(MsgType.Name);
        w.Put(session.LocalPlayerId);
        w.Put(steam ?? "");
        w.Put(instance);
        session.Send(w, DeliveryMethod.ReliableOrdered);
    }

    private static void SendAllTo(CoopSession session, int playerId)
    {
        foreach (var (id, name) in Names)
        {
            var w = session.BeginMessage(MsgType.Name);
            w.Put(id);
            w.Put(name.steam);
            w.Put(name.instance);
            session.SendTo(playerId, w, DeliveryMethod.ReliableOrdered);
        }
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, LiteNetLib.Utils.NetDataReader r)
    {
        int id = r.GetInt();
        string steam = r.GetString(), instance = r.GetString();
        Names[id] = (steam, instance);
        Plugin.Logger.LogInfo($"Names: player {id} is {NameOf(id)}");
        if (session.Role != Role.Host) return;
        var w = session.BeginMessage(MsgType.Name);
        w.Put(id);
        w.Put(steam);
        w.Put(instance);
        session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId: from.PlayerId);
    }

    public static string NameOf(int playerId)
    {
        if (!Names.TryGetValue(playerId, out var n)) return $"player {playerId}";
        if (n.steam == "") return n.instance;
        if (n.instance == "") return n.steam;
        foreach (var (id, o) in Names) if (id != playerId && o.steam == n.steam) return n.instance;
        return n.steam;
    }

    // Called from OnGUI.
    public static void Draw(IEnumerable<(int id, Vector3 head)> players)
    {
        var cam = Camera.main;
        if (cam == null) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
            _style.normal.textColor = Color.white;
            _shadow = new GUIStyle(_style);
            _shadow.normal.textColor = new Color(0, 0, 0, 0.7f);
        }
        foreach (var (id, head) in players)
        {
            var s = cam.WorldToScreenPoint(head);
            if (s.z <= 0 || s.z > 80) continue;
            var rect = new Rect(s.x - 150, Screen.height - s.y - 14, 300, 28);
            var name = NameOf(id);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), name, _shadow);
            GUI.Label(rect, name, _style);
        }
    }
}
