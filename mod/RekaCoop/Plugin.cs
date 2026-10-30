using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace RekaCoop;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BasePlugin
{
    public const string PluginGuid = "com.andrew1431.rekacoop";
    public const string PluginName = "Reka Co-op";
    public const string PluginVersion = "0.4.0";

    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        Instance.Init();
        Diagnostics.Reporter.Init(Config);
        UpdateCheck.Init(Config);
        Logger.LogInfo($"{PluginName} {PluginVersion} loaded on Reka {UnityEngine.Application.version} (instance '{Instance.Name ?? "default"}')");

        var harmony = new Harmony(PluginGuid);
        Il2CppFixes.Apply(harmony);
        SaveIsolation.Apply();
        Diagnostics.Reporter.PatchAll(harmony);

        ClassInjector.RegisterTypeInIl2Cpp<CoopDriver>();
        ClassInjector.RegisterTypeInIl2Cpp<Sync.PuppetAnimEvents>();
        var go = new GameObject("RekaCoop");
        Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<CoopDriver>();
        Diagnostics.Reporter.HookUnityLog();
    }
}
