using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ZenProxyUI;

public static class ProxyCore
{
    public static string AppDir => AppContext.BaseDirectory;
    public static string EnvPath => Path.Combine(AppDir, ".env.local");
    public static string ProxyExePath => Path.Combine(AppDir, OperatingSystem.IsWindows() ? "ZenProxy.exe" : "zen-proxy");

    public static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string CcSwitchDbPath => Path.Combine(HomeDir, ".cc-switch", "cc-switch.db");
    public static string ClaudeSettingsPath => Path.Combine(HomeDir, ".claude", "settings.json");

    public static Dictionary<string, string> LoadEnv()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(EnvPath))
            foreach (var line in File.ReadAllLines(EnvPath))
            {
                var m = Regex.Match(line, "^([^#=][^=]*)=(.*)$");
                if (m.Success) d[m.Groups[1].Value.Trim()] = m.Groups[2].Value;
            }
        return d;
    }

    public static void SaveEnv(Dictionary<string, string> d)
    {
        var sb = new StringBuilder();
        foreach (var kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
        File.WriteAllText(EnvPath, sb.ToString(), new UTF8Encoding(false));
    }

    public static string GetEnv(string key)
    {
        var d = LoadEnv();
        return d.TryGetValue(key, out var v) ? v : "";
    }

    // ---------- CC Switch sync (Windows only) ----------
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool CcSwitchAvailable =>
        IsWindows && File.Exists(CcSwitchDbPath) && SqliteFound();

    static bool _sqliteChecked;
    static bool _sqliteFound;
    static bool SqliteFound()
    {
        if (_sqliteChecked) return _sqliteFound;
        _sqliteChecked = true;
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo("sqlite3", "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            p.Start();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            _sqliteFound = p.ExitCode == 0;
        }
        catch { _sqliteFound = false; }
        return _sqliteFound;
    }

    static string RunSqlite(string sql)
    {
        try
        {
            if (!CcSwitchAvailable) return "";
            var p = new Process
            {
                StartInfo = new ProcessStartInfo("sqlite3", "\"" + CcSwitchDbPath + "\" " + sql)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return output.Trim();
        }
        catch { return ""; }
    }

    public static string GetCcSwitchProviderName() =>
        RunSqlite("SELECT name FROM providers WHERE app_type='claude' AND is_current=1 LIMIT 1;");

    public static string GetCcSwitchKey() =>
        RunSqlite("SELECT json_extract(settings_config,'$.env.ANTHROPIC_AUTH_TOKEN') FROM providers WHERE app_type='claude' AND is_current=1 LIMIT 1;");

    public static void SetCcSwitchKey(string key) =>
        RunSqlite("UPDATE providers SET settings_config = json_set(settings_config,'$.env.ANTHROPIC_AUTH_TOKEN','" + key.Replace("'", "''") + "') WHERE app_type='claude' AND is_current=1;");

    public static void SetClaudeSettingsKey(string key)
    {
        try
        {
            if (!File.Exists(ClaudeSettingsPath)) return;
            string text = File.ReadAllText(ClaudeSettingsPath);
            string safeKey = key.Replace("\"", "\\\"");
            var m = Regex.Match(text, "\"ANTHROPIC_AUTH_TOKEN\"\\s*:\\s*\"[^\"]*\"");
            if (m.Success)
                text = text.Replace(m.Value, "\"ANTHROPIC_AUTH_TOKEN\": \"" + safeKey + "\"");
            else
            {
                var envMatch = Regex.Match(text, "\"env\"\\s*:\\s*\\{");
                if (envMatch.Success)
                    text = text.Insert(envMatch.Index + envMatch.Length, "\n    \"ANTHROPIC_AUTH_TOKEN\": \"" + safeKey + "\",");
            }
            File.WriteAllText(ClaudeSettingsPath, text);
        }
        catch { }
    }
}
