using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiTabbedSshClient;

public sealed class TerminalTab : UserControl
{
    private readonly TerminalSurface _terminal = new() { Dock = DockStyle.Fill };

    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _state = new("Disconnected");
    private readonly ToolStripStatusLabel _host = new("");
    private readonly ToolStripStatusLabel _log = new("");
    private SshClient? _client;
    private ShellStream? _shell;
    private CancellationTokenSource? _cts;
    private StreamWriter? _logWriter;
    private ForwardedPortLocal? _forward;
    private readonly object _writeLock = new();
    private readonly AppSettings _appSettings;
    private System.Windows.Forms.Timer? _resizeTimer;
    private string? _currentLogPath;
    private string _pendingLogEscape = string.Empty;
    private bool _lastLogWasCarriageReturn;
    private volatile bool _disconnecting;
    private readonly object _appendLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private bool _appendScheduled;
    private readonly Action<int, int> _terminalSizeChangedHandler;

    public SessionProfile Profile { get; }
    public bool IsConnected
    {
        get
        {
            if (_disconnecting || _shell == null || _client == null) return false;
            try { return _client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public string Title => string.IsNullOrWhiteSpace(Profile.Name) ? Profile.Host : Profile.Name;
    public string? CurrentLogPath => _currentLogPath;
    public event Action<string>? StatusMessage;
    public event Action<TerminalTab>? ConnectionClosed;

    public TerminalTab(SessionProfile profile, AppSettings appSettings)
    {
        Profile = profile;
        _appSettings = appSettings;
        _appSettings.EnsureKeyMappings();
        Dock = DockStyle.Fill;
        _terminal.Font = appSettings.TerminalFont();
        _terminal.ForeColor = appSettings.TerminalForeColor();
        _terminal.BackColor = appSettings.TerminalBackColor();
        _terminalSizeChangedHandler = (_, _) => QueueRemotePtyResize();
        _terminal.KeyPress += Terminal_KeyPress;
        _terminal.KeyDown += Terminal_KeyDown;
        _terminal.TerminalSizeChanged += _terminalSizeChangedHandler;
        _status.Items.AddRange(new ToolStripItem[] { _state, _host, _log });
        Controls.Add(_terminal);
        Controls.Add(_status);
        _status.Dock = DockStyle.Bottom;
        AppendLine($"Connecting to {Profile.UserName}@{Profile.Host}:{Profile.Port} ...");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Disconnect(); } catch { }
            try { _terminal.KeyPress -= Terminal_KeyPress; } catch { }
            try { _terminal.KeyDown -= Terminal_KeyDown; } catch { }
            try { _terminal.TerminalSizeChanged -= _terminalSizeChangedHandler; } catch { }
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }
        base.Dispose(disposing);
    }

    public async Task ConnectAsync()
    {
        _terminal.RefreshTerminalSize();
        var initialColumns = Math.Max(20, _terminal.Columns);
        var initialRows = Math.Max(5, _terminal.Rows);
        var initialPixelWidth = Math.Max(1, _terminal.ClientSize.Width);
        var initialPixelHeight = Math.Max(1, _terminal.ClientSize.Height);
        Profile.Columns = initialColumns;
        Profile.Rows = initialRows;
        _disconnecting = false;
        _cts = new CancellationTokenSource();
        await Task.Run(() =>
        {
            try
            {
                var auth = BuildAuth();
                var ci = new ConnectionInfo(Profile.Host, Profile.Port, Profile.UserName, auth)
                {
                    Timeout = TimeSpan.FromSeconds(20),
                    Encoding = Encoding.UTF8
                };
                _client = new SshClient(ci);
                if (Profile.KeepAliveSeconds > 0) _client.KeepAliveInterval = TimeSpan.FromSeconds(Profile.KeepAliveSeconds);
                _client.Connect();
                _shell = _client.CreateShellStream(Profile.TerminalType, (uint)initialColumns, (uint)initialRows, (uint)initialPixelWidth, (uint)initialPixelHeight, 4096);
                UI(() => { _state.Text = "Connected"; _host.Text = $"{Profile.UserName}@{Profile.Host}"; _terminal.Focus(); ResizeRemotePty(); StartDefaultSessionLog(); AppendLine("Connected."); });
                ReaderLoop(_cts.Token);
            }
            catch (Exception ex)
            {
                UI(() => { _state.Text = "Error"; AppendLine("Connection error: " + ex.Message); StatusMessage?.Invoke(ex.Message); ConnectionClosed?.Invoke(this); });
            }
        });
    }

    private AuthenticationMethod BuildAuth()
    {
        if (Profile.AuthMode == AuthMode.PrivateKey)
        {
            PrivateKeyFile key = string.IsNullOrEmpty(Profile.PrivateKeyPassphrase) ? new PrivateKeyFile(Profile.PrivateKeyPath) : new PrivateKeyFile(Profile.PrivateKeyPath, Profile.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(Profile.UserName, key);
        }
        return new PasswordAuthenticationMethod(Profile.UserName, Profile.Password);
    }

    private void ReaderLoop(CancellationToken token)
    {
        var buffer = new byte[32768];
        while (!token.IsCancellationRequested && IsConnected)
        {
            try
            {
                var shell = _shell;
                if (shell == null) break;
                if (shell.DataAvailable)
                {
                    int read = shell.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        var text = Encoding.UTF8.GetString(buffer, 0, read);
                        QueueAppend(text);
                    }
                }
                else Thread.Sleep(5);
            }
            catch (Exception ex)
            {
                UI(() => AppendLine("Terminal read stopped: " + ex.Message));
                break;
            }
        }
        UI(() => { _disconnecting = true; _state.Text = "Disconnected"; ConnectionClosed?.Invoke(this); });
    }


    private void QueueAppend(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var shouldSchedule = false;
        lock (_appendLock)
        {
            _pendingOutput.Append(text);
            if (!_appendScheduled)
            {
                _appendScheduled = true;
                shouldSchedule = true;
            }
        }
        if (shouldSchedule) UI(FlushPendingOutput);
    }

    private void FlushPendingOutput()
    {
        string text;
        lock (_appendLock)
        {
            text = _pendingOutput.ToString();
            _pendingOutput.Clear();
            _appendScheduled = false;
        }
        if (!string.IsNullOrEmpty(text)) Append(text);
    }

    private void Terminal_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (!IsConnected) { e.Handled = true; return; }
        if (char.IsControl(e.KeyChar) && e.KeyChar != '\r' && e.KeyChar != '\b' && e.KeyChar != '\x1b')
        {
            Send(e.KeyChar.ToString());
            e.Handled = true;
            return;
        }
        string data = e.KeyChar switch
        {
            '\r' => Profile.SendNewLine switch { NewLineMode.CR => "\r", NewLineMode.LF => "\n", _ => "\r\n" },
            '\b' => _appSettings.GetKeySequence("Backspace"),
            '\x1b' => "\x1b",
            _ => e.KeyChar.ToString()
        };
        Send(data);
        if (Profile.LocalEcho) Append(data);
        e.Handled = true;
    }

    private void Terminal_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsConnected) { e.Handled = true; e.SuppressKeyPress = true; return; }
        string? seq = e.KeyCode switch
        {
            Keys.Up => "\x1b[A",
            Keys.Down => "\x1b[B",
            Keys.Right => "\x1b[C",
            Keys.Left => "\x1b[D",
            Keys.Home => _appSettings.GetKeySequence("Home"),
            Keys.End => _appSettings.GetKeySequence("End"),
            Keys.Delete => _appSettings.GetKeySequence("Delete"),
            Keys.PageUp => _appSettings.GetKeySequence("PageUp"),
            Keys.PageDown => _appSettings.GetKeySequence("PageDown"),
            Keys.Insert => _appSettings.GetKeySequence("Insert"),
            Keys.F1 => "\x1bOP",
            Keys.F2 => "\x1bOQ",
            Keys.F3 => "\x1bOR",
            Keys.F4 => "\x1bOS",
            Keys.F5 => "\x1b[15~",
            Keys.F6 => "\x1b[17~",
            Keys.F7 => "\x1b[18~",
            Keys.F8 => "\x1b[19~",
            Keys.F9 => "\x1b[20~",
            Keys.F10 => "\x1b[21~",
            Keys.F11 => "\x1b[23~",
            Keys.F12 => "\x1b[24~",
            _ => null
        };
        if (e.Shift && e.KeyCode == Keys.Tab) seq = "\x1b[Z";
        if (seq != null) { Send(seq); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.C) { Send("\x03"); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.V) { PasteClipboard(); e.Handled = true; e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
        {
            Send(((char)(e.KeyCode - Keys.A + 1)).ToString());
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    public void Send(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsConnected) return;

        lock (_writeLock)
        {
            if (_disconnecting || _shell == null) return;
            try
            {
                _shell.Write(text);
                _shell.Flush();
            }
            catch (ObjectDisposedException)
            {
                MarkDisconnected();
            }
            catch (InvalidOperationException)
            {
                MarkDisconnected();
            }
            catch (SshConnectionException)
            {
                MarkDisconnected();
            }
            catch (IOException)
            {
                MarkDisconnected();
            }
        }
    }

    public async Task SendLinesAsync(IEnumerable<string> lines, int delayMs = 100)
    {
        foreach (var line in lines)
        {
            Send(line + (Profile.SendNewLine == NewLineMode.CRLF ? "\r\n" : Profile.SendNewLine == NewLineMode.LF ? "\n" : "\r"));
            await Task.Delay(delayMs);
        }
    }

    public void Disconnect()
    {
        MarkDisconnected();
        try { _cts?.Cancel(); } catch { }
        try { _forward?.Stop(); } catch { }
        try { _forward = null; } catch { }
        lock (_writeLock)
        {
            try { _shell?.Dispose(); } catch { }
            _shell = null;
        }
        try
        {
            if (_client != null)
            {
                try { if (_client.IsConnected) _client.Disconnect(); } catch { }
                _client.Dispose();
            }
        }
        catch { }
        finally { _client = null; }
        StopLogging();
        try { _resizeTimer?.Stop(); _resizeTimer?.Dispose(); } catch { }
        _resizeTimer = null;
        if (!IsDisposed && !Disposing)
        {
            try { _state.Text = "Disconnected"; } catch { }
        }
    }

    private void MarkDisconnected()
    {
        _disconnecting = true;
        if (!IsDisposed && !Disposing)
        {
            try { _state.Text = "Disconnected"; } catch { }
        }
    }

    public void StartLogging(string path, bool timestamps)
    {
        StopLogging();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        _currentLogPath = path;
        _logWriter = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8) { AutoFlush = true };
        Profile.LogWithTimestamps = timestamps;
        _log.Text = "Logging: " + Path.GetFileName(path);
    }

    private void StartDefaultSessionLog()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_appSettings.SessionLogDirectory) ? @"C:\SshSessionsData" : _appSettings.SessionLogDirectory;
            Directory.CreateDirectory(dir);
            var safeName = SafeFilePart(string.IsNullOrWhiteSpace(Profile.Name) ? $"{Profile.UserName}@{Profile.Host}" : Profile.Name);
            var safeHost = SafeFilePart(Profile.Host);
            var file = $"{DateTime.Now:yyyyMMdd-HHmmss}_{safeName}_{Profile.UserName}@{safeHost}_{Profile.Port}.log";
            StartLogging(Path.Combine(dir, file), Profile.LogWithTimestamps);
        }
        catch (Exception ex)
        {
            _log.Text = "Log disabled";
            StatusMessage?.Invoke("Could not start session log: " + ex.Message);
        }
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in value)
        {
            if (Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch)) sb.Append('_');
            else if (ch == ' ' || ch == '\t') sb.Append('_');
            else sb.Append(ch);
        }
        var s = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(s) ? "session" : s;
    }

    public void StopLogging()
    {
        _logWriter?.Dispose(); _logWriter = null; _log.Text = "";
    }

    public void SetTerminalChromeVisible(bool visible) => _status.Visible = visible;
    public void SaveBuffer(string path) => File.WriteAllText(path, _terminal.GetCaptureText());
    public void Clear() => _terminal.Clear();
    public void Copy() => _terminal.CopySelectionOrScreenToClipboard();
    public void PasteClipboard() { if (IsConnected && Clipboard.ContainsText()) Send(NormalizePaste(Clipboard.GetText())); }
    public void SelectAllText() => _terminal.SelectAllText();
    public void ApplyAppearance(AppSettings s) { _terminal.Font = s.TerminalFont(); _terminal.ForeColor = s.TerminalForeColor(); _terminal.BackColor = s.TerminalBackColor(); }

    public void StartLocalForward(ForwardRule rule)
    {
        if (!IsConnected || _client == null) throw new InvalidOperationException("Not connected.");
        _forward?.Stop();
        _forward = new ForwardedPortLocal(rule.BoundHost, rule.BoundPort, rule.RemoteHost, rule.RemotePort);
        _client.AddForwardedPort(_forward);
        _forward.Start();
        AppendLine($"Local forwarding started: {rule.BoundHost}:{rule.BoundPort} -> {rule.RemoteHost}:{rule.RemotePort}");
    }

    private void Append(string text)
    {
        _terminal.Process(text);
        if (_logWriter != null)
        {
            var sanitized = SanitizeForLog(text);
            if (Profile.LogWithTimestamps)
            {
                foreach (var part in sanitized.Split('\n'))
                    if (part.Length > 0) _logWriter.WriteLine($"{DateTime.Now:O} {part}");
            }
            else _logWriter.Write(sanitized);
        }
    }

    private static string NormalizePaste(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r");

    private string SanitizeForLog(string text)
    {
        if (!string.IsNullOrEmpty(_pendingLogEscape))
        {
            text = _pendingLogEscape + text;
            _pendingLogEscape = string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i++];
            if (ch == '\x1b')
            {
                var consumed = ConsumeLogEscape(text, i);
                if (consumed < 0)
                {
                    _pendingLogEscape = text[(i - 1)..];
                    break;
                }
                i = consumed;
                continue;
            }

            if (ch == '\r')
            {
                if (!_lastLogWasCarriageReturn) sb.AppendLine();
                _lastLogWasCarriageReturn = true;
                continue;
            }
            if (ch == '\n')
            {
                if (!_lastLogWasCarriageReturn) sb.AppendLine();
                _lastLogWasCarriageReturn = false;
                continue;
            }
            _lastLogWasCarriageReturn = false;

            if (ch == '\b')
            {
                if (sb.Length > 0 && sb[^1] != '\n' && sb[^1] != '\r') sb.Length--;
                continue;
            }
            if (ch == '\t') { sb.Append('\t'); continue; }
            if (ch == '\a' || ch == '\0') continue;
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int ConsumeLogEscape(string text, int i)
    {
        if (i >= text.Length) return -1;
        var introducer = text[i++];
        if (introducer == '[')
        {
            while (i < text.Length && (text[i] < '@' || text[i] > '~')) i++;
            return i < text.Length ? i + 1 : -1;
        }
        if (introducer == ']')
        {
            while (i < text.Length)
            {
                if (text[i] == '\a') return i + 1;
                if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') return i + 2;
                i++;
            }
            return -1;
        }
        if (introducer == 'P' || introducer == '^' || introducer == '_')
        {
            while (i < text.Length)
            {
                if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') return i + 2;
                i++;
            }
            return -1;
        }
        if (introducer is '(' or ')' or '*' or '+') return i < text.Length ? i + 1 : -1;
        return i;
    }

    private void QueueRemotePtyResize()
    {
        _resizeTimer ??= new System.Windows.Forms.Timer { Interval = 120 };
        _resizeTimer.Stop();
        _resizeTimer.Tick -= ResizeTimer_Tick;
        _resizeTimer.Tick += ResizeTimer_Tick;
        _resizeTimer.Start();
    }

    private void ResizeTimer_Tick(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();
        ResizeRemotePty();
    }

    private void ResizeRemotePty()
    {
        if (!IsConnected || _shell == null) return;

        Profile.Columns = _terminal.Columns;
        Profile.Rows = _terminal.Rows;

        // Different SSH.NET versions expose PTY resize with different public APIs.
        // Keep this reflection-based so the project still compiles against versions
        // where ShellStream.ResizeTerminal is not available.
        TryInvokePtyResize(
            _terminal.Columns,
            _terminal.Rows,
            Math.Max(1, _terminal.Width),
            Math.Max(1, _terminal.Height));
    }

    private void TryInvokePtyResize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        if (!IsConnected || _shell == null) return;

        var methodNames = new[]
        {
            "ResizeTerminal",
            "SendWindowChangeRequest",
            "SetWindowSize",
            "ChangeWindowSize"
        };

        foreach (var name in methodNames)
        {
            foreach (var method in _shell.GetType().GetMethods())
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 4) continue;

                try
                {
                    var args = new object?[]
                    {
                        ConvertForParameter(columns, parameters[0].ParameterType),
                        ConvertForParameter(rows, parameters[1].ParameterType),
                        ConvertForParameter(pixelWidth, parameters[2].ParameterType),
                        ConvertForParameter(pixelHeight, parameters[3].ParameterType)
                    };
                    method.Invoke(_shell, args);
                    return;
                }
                catch
                {
                    // Try the next compatible method signature/name.
                }
            }
        }
    }

    private static object ConvertForParameter(int value, Type parameterType)
    {
        var targetType = Nullable.GetUnderlyingType(parameterType) ?? parameterType;
        if (targetType == typeof(uint)) return (uint)Math.Max(0, value);
        if (targetType == typeof(ushort)) return (ushort)Math.Max(0, Math.Min(ushort.MaxValue, value));
        if (targetType == typeof(short)) return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, value));
        if (targetType == typeof(long)) return (long)value;
        if (targetType == typeof(ulong)) return (ulong)Math.Max(0, value);
        return Convert.ChangeType(value, targetType);
    }

    private void AppendLine(string text) => Append(text + "\r\n");
    private void UI(Action a)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch (InvalidOperationException) { }
        catch (Exception) { }
    }
}
