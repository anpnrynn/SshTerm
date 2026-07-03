using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SshTerm;

public enum AuthMode { Password, PrivateKey }
public enum NewLineMode { CR, LF, CRLF }
public enum BackspaceMode { Delete, Backspace }

public sealed class SessionProfile
{
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public string Name { get; set; } = "New session";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string UserName { get; set; } = "";
    [JsonIgnore]
    public string Password { get; set; } = "";
    public AuthMode AuthMode { get; set; } = AuthMode.Password;
    public string PrivateKeyPath { get; set; } = "";
    [JsonIgnore]
    public string PrivateKeyPassphrase { get; set; } = "";
    public string TerminalType { get; set; } = "xterm-256color";
    public int Columns { get; set; } = 120;
    public int Rows { get; set; } = 40;
    public bool LocalEcho { get; set; }
    public NewLineMode SendNewLine { get; set; } = NewLineMode.CR;
    public BackspaceMode BackspaceMode { get; set; } = BackspaceMode.Delete;
    public int KeepAliveSeconds { get; set; } = 30;
    public bool LogWithTimestamps { get; set; }

    public SessionProfile Clone() => new()
    {
        Name = Name,
        Host = Host,
        Port = Port,
        UserName = UserName,
        Password = Password,
        AuthMode = AuthMode,
        PrivateKeyPath = PrivateKeyPath,
        PrivateKeyPassphrase = PrivateKeyPassphrase,
        TerminalType = TerminalType,
        Columns = Columns,
        Rows = Rows,
        LocalEcho = LocalEcho,
        SendNewLine = SendNewLine,
        BackspaceMode = BackspaceMode,
        KeepAliveSeconds = KeepAliveSeconds,
        LogWithTimestamps = LogWithTimestamps,
        LastUsedUtc = LastUsedUtc
    };

    public SessionProfile CloneForStorage()
    {
        var clone = Clone();
        clone.ClearSecrets();
        return clone;
    }

    public void ClearSecrets()
    {
        Password = string.Empty;
        PrivateKeyPassphrase = string.Empty;
    }

    [JsonIgnore]
    public string RecentDisplayText => $"{Name}  ({UserName}@{Host}:{Port})";

    public bool SameEndpoint(SessionProfile other) =>
        Host.Equals(other.Host, StringComparison.OrdinalIgnoreCase) &&
        Port == other.Port &&
        UserName.Equals(other.UserName, StringComparison.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    public string FontFamily { get; set; } = "Consolas";
    public float FontSize { get; set; } = 10.0f;
    public string ForeColor { get; set; } = "#E6E6E6";
    public string BackColor { get; set; } = "#101010";
    public Dictionary<string, string> KeyMappings { get; set; } = DefaultKeyMappings();
    public const int MaxRecentSessions = 256;
    public List<SessionProfile> Sessions { get; set; } = new();
    public List<SessionProfile> RecentSessions { get; set; } = new();
    public string SessionLogDirectory { get; set; } = @"C:\SshSessionsData";
    public bool DeleteSessionLogsOnExit { get; set; } = true;

    public static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "sessions.json");

    public static Dictionary<string, string> DefaultKeyMappings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = "\\x7f",
        ["Delete"] = "\\x1b[3~",
        ["PageUp"] = "\\x1b[5~",
        ["PageDown"] = "\\x1b[6~",
        ["Home"] = "\\x1b[H",
        ["End"] = "\\x1b[F",
        ["Insert"] = "\\x1b[2~"
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
                loaded.ClearAllSecrets();
                loaded.NormalizeRecentSessions();
                loaded.EnsureKeyMappings();
                if (string.IsNullOrWhiteSpace(loaded.SessionLogDirectory)) loaded.SessionLogDirectory = @"C:\SshSessionsData";
                return loaded;
            }
        }
        catch { }
        var settings = new AppSettings();
        settings.EnsureKeyMappings();
        return settings;
    }

    public void Save()
    {
        ClearAllSecrets();
        NormalizeRecentSessions();
        EnsureKeyMappings();
        if (string.IsNullOrWhiteSpace(SessionLogDirectory)) SessionLogDirectory = @"C:\SshSessionsData";
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public void EnsureKeyMappings()
    {
        KeyMappings ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in DefaultKeyMappings())
            if (!KeyMappings.ContainsKey(kv.Key) || KeyMappings[kv.Key] == null)
                KeyMappings[kv.Key] = kv.Value;
    }

    public string GetKeySequence(string keyName)
    {
        EnsureKeyMappings();
        return DecodeSequence(KeyMappings.TryGetValue(keyName, out var value) ? value : DefaultKeyMappings()[keyName]);
    }

    public static string DecodeSequence(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var s = value.Trim();
        if (s.Equals("DEL", StringComparison.OrdinalIgnoreCase)) return "\x7f";
        if (s.Equals("BS", StringComparison.OrdinalIgnoreCase)) return "\b";
        if (s.Equals("ESC", StringComparison.OrdinalIgnoreCase)) return "\x1b";
        if (s.StartsWith("^", StringComparison.Ordinal) && s.Length == 2)
        {
            var ch = char.ToUpperInvariant(s[1]);
            if (ch >= '@' && ch <= '_') return ((char)(ch - '@')).ToString();
            if (ch == '?') return "\x7f";
        }

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            var n = s[++i];
            switch (n)
            {
                case 'e': case 'E': sb.Append('\x1b'); break;
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case '\\': sb.Append('\\'); break;
                case 'x':
                    if (i + 2 < s.Length && int.TryParse(s.Substring(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var hex))
                    {
                        sb.Append((char)hex);
                        i += 2;
                    }
                    else sb.Append('x');
                    break;
                case 'u':
                    if (i + 4 < s.Length && int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var uni))
                    {
                        sb.Append((char)uni);
                        i += 4;
                    }
                    else sb.Append('u');
                    break;
                default:
                    if (n >= '0' && n <= '7')
                    {
                        var oct = n.ToString();
                        var consumed = 0;
                        while (consumed < 2 && i + 1 < s.Length && s[i + 1] >= '0' && s[i + 1] <= '7')
                        {
                            oct += s[++i];
                            consumed++;
                        }
                        sb.Append((char)Convert.ToInt32(oct, 8));
                    }
                    else sb.Append(n);
                    break;
            }
        }
        return sb.ToString();
    }

    public void AddRecent(SessionProfile profile)
    {
        var entry = profile.CloneForStorage();
        entry.LastUsedUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = $"{entry.UserName}@{entry.Host}:{entry.Port}";
        RecentSessions.RemoveAll(x => x.SameEndpoint(entry));
        RecentSessions.Insert(0, entry);
        NormalizeRecentSessions();
    }

    public void ClearRecentSessions()
    {
        RecentSessions.Clear();
        Save();
    }

    public void ClearAllSecrets()
    {
        foreach (var session in Sessions)
            session.ClearSecrets();
        foreach (var session in RecentSessions)
            session.ClearSecrets();
    }

    private void NormalizeRecentSessions()
    {
        foreach (var session in RecentSessions)
            session.ClearSecrets();
        foreach (var session in Sessions)
            session.ClearSecrets();

        RecentSessions = RecentSessions
            .Where(x => !string.IsNullOrWhiteSpace(x.Host))
            .GroupBy(x => $"{x.UserName.ToLowerInvariant()}|{x.Host.ToLowerInvariant()}|{x.Port}")
            .Select(g => g.OrderByDescending(x => x.LastUsedUtc).First())
            .OrderByDescending(x => x.LastUsedUtc)
            .Take(MaxRecentSessions)
            .ToList();
    }

    public Font TerminalFont() => new(FontFamily, FontSize, FontStyle.Regular, GraphicsUnit.Point);
    public Color TerminalForeColor() => ColorTranslator.FromHtml(ForeColor);
    public Color TerminalBackColor() => ColorTranslator.FromHtml(BackColor);
}

public sealed class ForwardRule
{
    public string BoundHost { get; set; } = "127.0.0.1";
    public uint BoundPort { get; set; } = 0;
    public string RemoteHost { get; set; } = "127.0.0.1";
    public uint RemotePort { get; set; } = 0;
}
