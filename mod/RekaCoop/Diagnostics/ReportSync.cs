using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;

namespace RekaCoop.Diagnostics;

// "Report a problem" across the session: whoever presses the key tells everyone, and each copy uploads its own log
// under the same report id. Host relays a guest's report to the other guests.
public static class ReportSync
{
    private enum Kind : byte { Problem = 1 }

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Report) OnMessage(session, from, r); };
    }

    internal static void Send(CoopSession session, string id, int playerId, string note, int exceptPlayerId = -1)
    {
        var w = session.BeginMessage(MsgType.Report);
        w.Put((byte)Kind.Problem);
        w.Put(id);
        w.Put(playerId);
        w.Put(note ?? "");
        session.Send(w, DeliveryMethod.ReliableOrdered, exceptPlayerId);
    }

    private static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        if ((Kind)r.GetByte() != Kind.Problem) return;
        string id = r.GetString(16);
        int playerId = r.GetInt();
        string note = r.GetString(400);
        if (session.Role == Role.Host) playerId = from.PlayerId;  // a guest can only report as itself
        if (Reporter.OnRemoteReport(id, playerId, note) && session.Role == Role.Host)
            Send(session, id.ToLowerInvariant(), playerId, note, from.PlayerId);
    }
}
