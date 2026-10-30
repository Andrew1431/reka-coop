using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using RekaCoop.Net;
using UnityEngine;

namespace RekaCoop.Sync;

// Host owns the clock and weather. It sends the game's own weather snapshot (which includes the time of day
// and the forecast timers) every few seconds; guests apply it and don't roll weather of their own.
public static class WorldSync
{
    private const float SendEvery = 5f;

    private static float _nextSend, _nextDiag;
    private static byte[] _pending;  // latest host snapshot, applied once a WeatherManager exists
    private static WeatherManager _weather;

    internal static bool IsGuest => CoopDriver.Session?.Role == Role.Client;

    public static void Attach(CoopSession session)
    {
        session.Message += (from, type, r) => { if (type == MsgType.Weather) OnWeather(r); };
        session.PlayerJoined += id =>
        {
            var w = Weather;
            if (w != null) Send(session, w, toPlayer: id);
        };
    }

    private static WeatherManager Weather =>
        _weather != null ? _weather : _weather = RekaCoop.Sync.Find.One<WeatherManager>();

    public static void Tick(CoopSession session)
    {
        var w = Weather;
        if (w == null) return;

        if (session.Role == Role.Host)
        {
            if (session.IsConnected && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + SendEvery;
                Send(session, w);
            }
        }
        else if (_pending != null)
        {
            Apply(w, _pending);
            _pending = null;
        }

        if (Time.unscaledTime >= _nextDiag)
        {
            _nextDiag = Time.unscaledTime + 10f;
            Plugin.Logger.LogInfo($"[world] day {w.dayIndex} time {w.dayTime:0.000} clouds {w.cloudCoverage:0.00} rain {w.rainIntensity:0.00} fog {w.fogDensity:0.00} override {w.manualOverride} cloudOffset {w.CurrentCloudOffset}");
        }
    }

    private static void Send(CoopSession session, WeatherManager w, int toPlayer = -1)
    {
        var snap = w.CaptureWeatherSnapshot();
        var writer = session.BeginMessage(MsgType.Weather);
        WriteFields(writer, snap, SnapFields);
        bool hasForecast = snap.forecast != null;
        writer.Put(hasForecast);
        if (hasForecast) WriteFields(writer, snap.forecast, ForecastFields);
        // Where the clouds have drifted to: each game moves them with the wind from its own start, so the same
        // weather shows a different sky (and different light). Last, so a guest without it just ignores it.
        var o = w.CurrentCloudOffset;
        writer.Put(o.x); writer.Put(o.y); writer.Put(o.z);
        if (toPlayer >= 0) session.SendTo(toPlayer, writer, DeliveryMethod.ReliableOrdered);
        else session.Send(writer, DeliveryMethod.ReliableOrdered);
    }

    private static void OnWeather(NetDataReader r)
    {
        // Keep the raw body; the WeatherManager may not exist yet (main menu, loading).
        _pending = r.GetRemainingBytes();
    }

    private static void Apply(WeatherManager w, byte[] body)
    {
        var r = new NetDataReader(body);
        var snap = new SerializableWeather();
        ReadFields(r, snap, SnapFields);
        if (r.GetBool())
        {
            snap.forecast = new SerializableWeatherForecast();
            ReadFields(r, snap.forecast, ForecastFields);
        }
        float before = w.dayTime;
        w.ApplyWeatherSnapshot(snap);
        if (snap.forecast != null) w.RestoreForecast(snap.forecast);
        if (r.AvailableBytes >= 12)
        {
            var offset = new Vector3(r.GetFloat(), r.GetFloat(), r.GetFloat());
            float off = (offset - w.CurrentCloudOffset).magnitude;
            WeatherManager.SetCloudAnimationOffset(offset);
            if (off > 1f) Plugin.Logger.LogInfo($"[world] clouds moved by {off:0.0} to the host's {offset}");
        }
        float drift = Mathf.Abs(Mathf.DeltaAngle(before * 15f, snap.dayTime * 15f)) / 15f;
        if (drift > 0.05f) Plugin.Logger.LogInfo($"[world] clock corrected by {drift:0.000} h to {snap.dayTime:0.000}");
    }

    // Plain value fields of the game's snapshot types, in a fixed order (both sides run the same build).
    private static readonly PropertyInfo[] SnapFields = ValueProps(typeof(SerializableWeather));
    private static readonly PropertyInfo[] ForecastFields = ValueProps(typeof(SerializableWeatherForecast));

    private static PropertyInfo[] ValueProps(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.DeclaringType == t &&
                        (p.PropertyType == typeof(float) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

    private static void WriteFields(NetDataWriter w, object obj, PropertyInfo[] props)
    {
        foreach (var p in props)
        {
            var v = p.GetValue(obj);
            if (v is float f) w.Put(f);
            else if (v is int i) w.Put(i);
            else w.Put((bool)v);
        }
    }

    private static void ReadFields(NetDataReader r, object obj, PropertyInfo[] props)
    {
        foreach (var p in props)
        {
            if (p.PropertyType == typeof(float)) p.SetValue(obj, r.GetFloat());
            else if (p.PropertyType == typeof(int)) p.SetValue(obj, r.GetInt());
            else p.SetValue(obj, r.GetBool());
        }
    }
}

// Guests take the host's weather; their own random rolls would only cause visible jumps at the next snapshot.
[HarmonyPatch(typeof(WeatherManager))]
internal static class GuestWeatherRolls
{
    [HarmonyPrefix, HarmonyPatch(nameof(WeatherManager.RandomizeWeather))]
    private static bool RandomizeWeather() => !WorldSync.IsGuest;

    [HarmonyPrefix, HarmonyPatch(nameof(WeatherManager.RollMoonPhase))]
    private static bool RollMoonPhase() => !WorldSync.IsGuest;
}
