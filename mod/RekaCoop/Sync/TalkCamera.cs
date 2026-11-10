using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// During a conversation everyone sees the starter's camera (and while the host drives the hut, the host's). The starter's game runs the dialogue camera as usual and
// sends its pose 30 times a second (a guest starter's goes through the host); everyone else's camera brain is switched
// off and their main camera put on that pose. If the stream stops for a moment, their own dialogue camera shows again.
internal static class TalkCamera
{
    private const float SendEvery = 1f / 30f, Stale = 0.6f;

    // Dev: off, a talk is framed by each copy's own dialogue camera (nothing sent, nothing shown); the hut drive is unaffected.
    internal static bool StreamTalks = true;

    private static float _nextSend;
    private static int _seqOut, _seqIn = -1;
    private static Vector3 _pos;
    private static Quaternion _rot;
    private static float _fov;
    private static float _receivedAt = -10;
    private static bool _overriding, _snap;
    private static Cinemachine.CinemachineBrain _brain;
    private static Camera _cam;

    public static void Tick(CoopSession session)
    {
        if (!(StreamTalks && DialogueSync.LeadingTalk || HutSync.HostDriving) || !session.IsConnected || Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + SendEvery;
        var cam = Camera.main;
        if (cam == null) return;
        var w = session.BeginMessage(MsgType.Dialogue);
        DialogueSync.PutCamKind(w);
        w.Put(session.LocalPlayerId);
        w.Put(++_seqOut);
        var t = cam.transform;
        w.Put(t.position.x); w.Put(t.position.y); w.Put(t.position.z);
        w.Put(t.rotation.x); w.Put(t.rotation.y); w.Put(t.rotation.z); w.Put(t.rotation.w);
        w.Put(cam.fieldOfView);
        session.Send(w, DeliveryMethod.Sequenced);
    }

    // Host: relays a guest starter's pose to everyone else. Both: shows it when someone else steers.
    public static void OnMessage(CoopSession session, RemotePlayer from, NetDataReader r)
    {
        int sender = r.GetInt(), seq = r.GetInt();
        var pos = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
        var rot = new Quaternion(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
        float fov = r.GetFloat();
        if (session.Role == Role.Host)
        {
            if (from == null || from.PlayerId != DialogueSync.Leader) return;
            var w = session.BeginMessage(MsgType.Dialogue);
            DialogueSync.PutCamKind(w);
            w.Put(sender); w.Put(seq);
            w.Put(pos.x); w.Put(pos.y); w.Put(pos.z);
            w.Put(rot.x); w.Put(rot.y); w.Put(rot.z); w.Put(rot.w);
            w.Put(fov);
            session.Send(w, DeliveryMethod.Sequenced, from.PlayerId);
        }
        if (sender == session.LocalPlayerId || !Watching) return;
        if (seq <= _seqIn && Time.unscaledTime - _receivedAt < Stale) return;  // late; a new talk restarts the count
        _snap = seq != _seqIn + 1 || Vector3.Distance(pos, _pos) > 2f || Quaternion.Angle(rot, _rot) > 25f;  // a cut
        _seqIn = seq;
        _pos = pos;
        _rot = rot;
        _fov = fov;
        _receivedAt = Time.unscaledTime;
    }

    public static void LateTick()
    {
        bool want = Watching && Time.unscaledTime - _receivedAt < Stale;
        if (!want) { Release(); return; }
        _cam = Camera.main ?? _cam;
        if (_cam == null) return;
        if (!_overriding)
        {
            _brain = _cam.GetComponent<Cinemachine.CinemachineBrain>();
            if (_brain != null) _brain.enabled = false;
            _overriding = true;
            _snap = true;
            _shownPos = _cam.transform.position;
            _shownRot = _cam.transform.rotation;
            _shownFov = _cam.fieldOfView;
            Plugin.Logger.LogInfo("Dialogue: showing the starter's camera");
        }
        float k = _snap ? 1f : 1f - Mathf.Exp(-25f * Time.unscaledDeltaTime);
        _snap = false;
        _shownPos = Vector3.Lerp(_shownPos, _pos, k);
        _shownRot = Quaternion.Slerp(_shownRot, _rot, k);
        _shownFov = Mathf.Lerp(_shownFov, _fov, k);
        Apply();
    }

    private static bool Watching => StreamTalks && DialogueSync.WatchingTalk || HutSync.WatchingDrive;

    private static Vector3 _shownPos;
    private static Quaternion _shownRot = Quaternion.identity;
    private static float _shownFov = 35f;

    // (Application.onBeforeRender is stripped from this build, so the pose can't go on again right before the draw.)
    private static void Apply()
    {
        if (!_overriding || _cam == null) return;
        _cam.transform.SetPositionAndRotation(_shownPos, _shownRot);
        _cam.fieldOfView = _shownFov;
    }

    public static void Release()
    {
        if (!_overriding) return;
        _overriding = false;
        _seqIn = -1;
        if (_brain != null) _brain.enabled = true;
        _brain = null;
        Plugin.Logger.LogInfo("Dialogue: own camera again");
    }

    public static string Describe() => $"overriding={_overriding} lastFrame={Time.unscaledTime - _receivedAt:0.00}s ago seqIn={_seqIn} seqOut={_seqOut} target={_pos} shown={_shownPos}";
}
