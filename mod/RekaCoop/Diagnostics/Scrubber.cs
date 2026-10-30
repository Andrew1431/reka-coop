using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RekaCoop.Diagnostics;

// Takes private things out of text before it leaves the PC: player names, the Windows user and PC name, IPs, Steam ids.
internal sealed class Scrubber
{
    private static readonly Regex UserPath = new(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\r\n""'<>|:*?]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Unc = new(@"(?<![\w\\])\\\\[^\\\s]+\\", RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", RegexOptions.Compiled);
    // 4+ groups so clock times (12:43:26) are left alone; or any form with "::" between hex digits.
    private static readonly Regex Ipv6 = new(@"(?<![\w:])(?:(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}|(?:[0-9a-fA-F]{1,4}:)*[0-9a-fA-F]{1,4}::(?:[0-9a-fA-F]{1,4}:)*[0-9a-fA-F]{1,4}|::ffff:[0-9.]+)(?![\w:])", RegexOptions.Compiled);
    // SteamID64s, lobby ids and other long numbers that identify an account or a lobby.
    private static readonly Regex LongId = new(@"(?<!\d)\d{15,20}(?!\d)", RegexOptions.Compiled);

    // Words that are dev defaults or appear in normal log text; never treated as someone's name.
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
        { "host", "client", "guest", "player", "players", "reka", "steam", "world", "default", "play", "unknown", "none", "null" };

    private readonly string _salt;
    private readonly List<(string name, string label)> _names;
    private readonly string _user, _machine;

    public Scrubber(string salt, IEnumerable<(string name, string label)> names)
    {
        _salt = salt;
        _names = names.Where(n => IsName(n.name)).OrderByDescending(n => n.name.Length).ToList();
        _user = SafeEnv(() => Environment.UserName);
        _machine = SafeEnv(() => Environment.MachineName);
    }

    public static bool IsName(string s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length >= 3 && !NotNames.Contains(s.Trim());

    public string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = UserPath.Replace(text, "$1<user>");
        s = Unc.Replace(s, @"\\<pc>\");
        if (_user is { Length: >= 3 }) s = ReplaceWord(s, _user, "<user>");
        if (_machine is { Length: >= 3 }) s = ReplaceWord(s, _machine, "<pc>");
        foreach (var (name, label) in _names) s = ReplaceWord(s, name.Trim(), label);
        s = Ipv4.Replace(s, KeepLocalIp);
        s = Ipv6.Replace(s, "<ip6>");
        s = LongId.Replace(s, HashId);
        return s;
    }

    // Method groups, not lambdas: a lambda here needs NullableAttribute, which this build has no definition of.
    private static string KeepLocalIp(Match m) => m.Value is "127.0.0.1" or "0.0.0.0" ? m.Value : "<ip>";
    private string HashId(Match m) => "id:" + Hash(m.Value);

    // Short, stable per tester, and not reversible by someone with only the uploaded text (salted with the tester id).
    public string Hash(string value)
    {
        using var sha = SHA256.Create();
        var h = sha.ComputeHash(Encoding.UTF8.GetBytes(_salt + "|" + value));
        return Convert.ToHexString(h, 0, 5).ToLowerInvariant();
    }

    private static string ReplaceWord(string s, string word, string with) =>
        Regex.Replace(s, @"(?<![\p{L}\p{N}_])" + Regex.Escape(word) + @"(?![\p{L}\p{N}_])", with.Replace("$", "$$"), RegexOptions.IgnoreCase);

    private static string SafeEnv(Func<string> f)
    {
        try { return f(); } catch { return null; }
    }
}
