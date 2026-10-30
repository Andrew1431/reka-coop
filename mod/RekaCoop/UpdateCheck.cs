using System;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx.Configuration;

namespace RekaCoop;

// Asks GitHub once per start whether a newer release is out, and says so at the main menu. It only tells: the files
// are swapped by "Update Co-op.bat" (BepInEx\plugins\RekaCoop\update.ps1) while the game is closed, since a loaded
// plugin can't replace itself.
internal static class UpdateCheck
{
    internal const string Releases = "https://api.github.com/repos/Andrew1431/reka-coop/releases?per_page=10";

    internal static string Newer { get; private set; }
    private static bool _told;

    internal static void Init(ConfigFile config)
    {
        var on = config.Bind("Updates", "CheckForUpdates", true,
            "At start, ask GitHub whether a newer Reka Co-op is out and say so at the main menu. Nothing is downloaded.");
        if (on.Value) Task.Run(Check);
    }

    private static void Check()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RekaCoop/" + Plugin.PluginVersion);
            var json = http.GetStringAsync(Releases).GetAwaiter().GetResult();
            // The list is newest first; drafts aren't visible without a token.
            var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9][^\"]*)\"");
            if (!m.Success) return;
            var latest = m.Groups[1].Value;
            if (IsNewer(latest, Plugin.PluginVersion))
            {
                Newer = latest;
                Plugin.Logger.LogInfo($"Update: Reka Co-op {latest} is out (this is {Plugin.PluginVersion})");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogInfo($"Update: couldn't check ({e.GetType().Name})");
        }
    }

    // CoopDriver.Update: once, at the main menu.
    internal static void Tick()
    {
        if (_told || Newer == null || !Sync.MenuWatch.Up) return;
        _told = true;
        CoopDriver.Notice($"Reka Co-op {Newer} is out. Close Reka and run \"Update Co-op\" in the game folder.");
    }

    internal static bool IsNewer(string a, string b) =>
        Version.TryParse(Core(a), out var va) && Version.TryParse(Core(b), out var vb) && va > vb;

    private static string Core(string v) => Regex.Match(v, @"^\d+(\.\d+){1,3}").Value;
}
