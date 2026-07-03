using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SshTerm;

public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _notify = new();
    private readonly AppSettings _settings;
    private readonly ToolStripMenuItem _recentMenu = new("&Recent Sessions");
    private readonly ToolStripMenuItem _savedMenu = new("Open &Saved Session");
    private SessionManagerForm? _sessionManager;
    private SessionStatusForm? _sessionStatus;
    private LogFilesForm? _logFiles;
    private bool _fullScreen;
    private FormWindowState _oldWindowState;
    private FormBorderStyle _oldBorderStyle;

    public MainForm()
    {
        _settings = AppSettings.Load();
        Text = "SshTerm - Multi Tabbed SSH client";
        Width = 1100; Height = 760;
        Icon = SystemIcons.Application;
        MainMenuStrip = BuildMenu();
        Controls.Add(_tabs);
        Controls.Add(MainMenuStrip);
        _notify.Icon = SystemIcons.Application;
        _notify.Visible = true;
        _notify.Text = "SshTerm - Multi Tabbed SSH Client";
        FormClosing += (_, _) => { foreach (var t in AllTabs()) t.Disconnect(); if (_settings.DeleteSessionLogsOnExit) DeleteSessionLogFiles(); _notify.Dispose(); _settings.Save(); };
    }

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&New SSH connection...", null, async (_, _) => await NewConnection());
        file.DropDownItems.Add("&Reconnect", null, async (_, _) => await ReconnectCurrentTab());
        file.DropDownItems.Add("&Disconnect", null, (_, _) => Current?.Disconnect());
        file.DropDownItems.Add(new ToolStripSeparator());
        _savedMenu.DropDownOpening += (_, _) => RefreshSavedMenu();
        RefreshSavedMenu();
        file.DropDownItems.Add(_savedMenu);
        _recentMenu.DropDownOpening += (_, _) => RefreshRecentMenu();
        RefreshRecentMenu();
        file.DropDownItems.Add("Session &Manager...", null, (_, _) => ShowSessionManager());
        file.DropDownItems.Add("Session S&tatus...", null, (_, _) => ShowSessionStatus());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("Start &Log...", null, (_, _) => StartLog());
        file.DropDownItems.Add("Stop Lo&g", null, (_, _) => Current?.StopLogging());
        file.DropDownItems.Add("Manage Session Log &Files...", null, (_, _) => ShowLogFiles());
        var deleteLogsOnExit = new ToolStripMenuItem("Delete session logs on e&xit") { Checked = _settings.DeleteSessionLogsOnExit, CheckOnClick = true };
        deleteLogsOnExit.CheckedChanged += (_, _) => { _settings.DeleteSessionLogsOnExit = deleteLogsOnExit.Checked; _settings.Save(); };
        file.DropDownItems.Add(deleteLogsOnExit);
        file.DropDownItems.Add("Save &Buffer...", null, (_, _) => SaveBuffer());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var edit = new ToolStripMenuItem("&Edit");
        AddMenuItem(edit.DropDownItems, "&Copy", (_, _) => Current?.Copy(), Keys.Control | Keys.Insert);
        AddMenuItem(edit.DropDownItems, "&Paste", (_, _) => Current?.PasteClipboard(), Keys.Shift | Keys.Insert);
        AddMenuItem(edit.DropDownItems, "Select &All", (_, _) => Current?.SelectAllText(), Keys.Control | Keys.A);
        AddMenuItem(edit.DropDownItems, "Clear &Screen", (_, _) => Current?.Clear(), Keys.Control | Keys.L);

        var setup = new ToolStripMenuItem("&Setup");
        setup.DropDownItems.Add("&Terminal settings...", null, (_, _) => TerminalSettings());
        setup.DropDownItems.Add("&Keyboard / key mappings...", null, (_, _) => ConfigureKeyMappings());
        setup.DropDownItems.Add("&Font...", null, (_, _) => ChooseFont());
        setup.DropDownItems.Add("&Foreground color...", null, (_, _) => ChooseColor(true));
        setup.DropDownItems.Add("&Background color...", null, (_, _) => ChooseColor(false));
        setup.DropDownItems.Add("SSH &Forwarding...", null, (_, _) => SetupForwarding());
        setup.DropDownItems.Add("&Save setup", null, (_, _) => { _settings.Save(); Info("Settings saved."); });

        var control = new ToolStripMenuItem("&Control");
        control.DropDownItems.Add("Send &Break / Ctrl+C", null, (_, _) => Current?.Send("\x03"));
        control.DropDownItems.Add("Send &NUL", null, (_, _) => Current?.Send("\0"));
        control.DropDownItems.Add("Send &Broadcast Command...", null, async (_, _) => await BroadcastCommand());
        control.DropDownItems.Add("Reset Terminal Screen", null, (_, _) => Current?.Send("reset\r"));

        var macro = new ToolStripMenuItem("&Macro");
        macro.DropDownItems.Add("Run command &script file...", null, async (_, _) => await RunMacroFile());
        macro.DropDownItems.Add("Send command &list...", null, async (_, _) => await SendCommandList());

        var window = new ToolStripMenuItem("&Window");
        AddMenuItem(window.DropDownItems, "&Next Tab", (_, _) => { if (_tabs.TabCount > 0) _tabs.SelectedIndex = (_tabs.SelectedIndex + 1) % _tabs.TabCount; }, Keys.Control | Keys.Tab);
        AddMenuItem(window.DropDownItems, "&Previous Tab", (_, _) => { if (_tabs.TabCount > 0) _tabs.SelectedIndex = (_tabs.SelectedIndex + _tabs.TabCount - 1) % _tabs.TabCount; }, Keys.Control | Keys.Shift | Keys.Tab);
        window.DropDownItems.Add(new ToolStripSeparator());
        window.DropDownItems.Add("&Clone Current Tab", null, async (_, _) => { var t = Current; if (t != null) await OpenSession(t.Profile); });
        window.DropDownItems.Add("Close &Tab", null, (_, _) => CloseCurrentTab());
        window.DropDownItems.Add("Close Other Tabs", null, (_, _) => CloseOtherTabs());
        window.DropDownItems.Add(new ToolStripSeparator());
        AddMenuItem(window.DropDownItems, "Toggle &Full Screen", (_, _) => ToggleFullScreen(), Keys.F11);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => MessageBox.Show(this, "MultiTabbed SSH Client\nWinForms / C# / SSH.NET\nSSH-only Tera Term style client.", "About"));

        menu.Items.AddRange(new ToolStripItem[] { file, _recentMenu, edit, setup, control, macro, window, help });
        return menu;
    }

    private void RefreshSavedMenu()
    {
        _savedMenu.DropDownItems.Clear();
        if (_settings.Sessions.Count == 0)
        {
            _savedMenu.DropDownItems.Add(new ToolStripMenuItem("No saved sessions") { Enabled = false });
            return;
        }
        foreach (var s in _settings.Sessions.OrderBy(x => x.Name))
        {
            var copy = s.Clone();
            _savedMenu.DropDownItems.Add(copy.Name, null, async (_, _) => await OpenSession(copy));
        }
    }

    private void RefreshRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        if (_settings.RecentSessions.Count == 0)
        {
            var empty = new ToolStripMenuItem("No recent sessions") { Enabled = false };
            _recentMenu.DropDownItems.Add(empty);
            return;
        }

        var index = 1;
        foreach (var profile in _settings.RecentSessions.Take(AppSettings.MaxRecentSessions))
        {
            var copy = profile.Clone();
            var text = $"{index,3}. {copy.RecentDisplayText}";
            var item = new ToolStripMenuItem(text, null, async (_, _) => await OpenSession(copy));
            item.ToolTipText = $"Last used: {copy.LastUsedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            _recentMenu.DropDownItems.Add(item);
            index++;
        }
        _recentMenu.DropDownItems.Add(new ToolStripSeparator());
        _recentMenu.DropDownItems.Add("Clear Recent Sessions", null, (_, _) =>
        {
            if (MessageBox.Show(this, "Clear all recent SSH sessions?", "Recent Sessions", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _settings.ClearRecentSessions();
                RefreshRecentMenu();
            }
        });
    }


    private static ToolStripMenuItem AddMenuItem(ToolStripItemCollection items, string text, EventHandler onClick, Keys shortcutKeys = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, onClick);
        if (shortcutKeys != Keys.None) item.ShortcutKeys = shortcutKeys;
        items.Add(item);
        return item;
    }

    private TerminalTab? Current => _tabs.SelectedTab?.Controls.OfType<TerminalTab>().FirstOrDefault();
    private TerminalTab[] AllTabs() => _tabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.OfType<TerminalTab>()).ToArray();

    private async System.Threading.Tasks.Task NewConnection()
    {
        using var d = new ConnectDialog();
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            if (d.SaveProfile)
            {
                _settings.Sessions.RemoveAll(x => x.Name.Equals(d.Profile.Name, StringComparison.OrdinalIgnoreCase));
                _settings.Sessions.Add(d.Profile.CloneForStorage());
                _settings.Save();
                RefreshSavedMenu();
            }
            await OpenSession(d.Profile);
        }
    }

    private async System.Threading.Tasks.Task ReconnectCurrentTab()
    {
        var oldTab = Current;
        var page = _tabs.SelectedTab;
        if (oldTab == null || page == null) return;

        var runtimeProfile = oldTab.Profile.Clone();
        oldTab.Disconnect();
        page.Controls.Remove(oldTab);
        oldTab.Dispose();

        var term = new TerminalTab(runtimeProfile, _settings);
        if (_fullScreen) term.SetTerminalChromeVisible(false);
        term.StatusMessage += s => Notify("SSH Client", s);
        term.ConnectionClosed += t => { _sessionStatus?.Reload(); };
        page.Text = runtimeProfile.Name;
        page.ToolTipText = $"{runtimeProfile.UserName}@{runtimeProfile.Host}:{runtimeProfile.Port}";
        page.Controls.Add(term);
        _tabs.SelectedTab = page;
        _sessionStatus?.Reload();
        await term.ConnectAsync();
    }

    private async System.Threading.Tasks.Task OpenSession(SessionProfile profile)
    {
        var runtimeProfile = profile.Clone();
        if (NeedsRuntimeSecret(runtimeProfile))
        {
            using var d = new ConnectDialog(runtimeProfile);
            d.Text = "Enter SSH Credentials";
            if (d.ShowDialog(this) != DialogResult.OK)
                return;
            runtimeProfile = d.Profile;
        }

        _settings.AddRecent(runtimeProfile);
        _settings.Save();
        RefreshRecentMenu();
        RefreshSavedMenu();
        _sessionStatus?.Reload();
        var term = new TerminalTab(runtimeProfile, _settings);
        if (_fullScreen) term.SetTerminalChromeVisible(false);
        term.StatusMessage += s => Notify("SSH Client", s);
        term.ConnectionClosed += t => { _sessionStatus?.Reload(); };
        var page = new TabPage(runtimeProfile.Name) { ToolTipText = $"{runtimeProfile.UserName}@{runtimeProfile.Host}:{runtimeProfile.Port}" };
        page.Controls.Add(term);
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
        await term.ConnectAsync();
    }

    private static bool NeedsRuntimeSecret(SessionProfile profile)
    {
        if (profile.AuthMode == AuthMode.Password)
            return string.IsNullOrEmpty(profile.Password);
        return !string.IsNullOrWhiteSpace(profile.PrivateKeyPath) && string.IsNullOrEmpty(profile.PrivateKeyPassphrase);
    }

    private void ShowSessionManager()
    {
        if (_sessionManager == null || _sessionManager.IsDisposed)
        {
            _sessionManager = new SessionManagerForm(_settings, OpenSession);
            _sessionManager.FormClosed += (_, _) => { RefreshSavedMenu(); _sessionStatus?.Reload(); };
        }
        _sessionManager.Show(this);
        _sessionManager.Activate();
    }

    private void ShowLogFiles()
    {
        if (_logFiles == null || _logFiles.IsDisposed)
            _logFiles = new LogFilesForm(_settings);
        else
            _logFiles.Reload();
        _logFiles.Show(this);
        _logFiles.Activate();
    }

    private void DeleteSessionLogFiles()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_settings.SessionLogDirectory) ? @"C:\SshSessionsData" : _settings.SessionLogDirectory;
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, "*.log"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private void ShowSessionStatus()
    {
        if (_sessionStatus == null || _sessionStatus.IsDisposed)
        {
            _sessionStatus = new SessionStatusForm(_settings, AllTabs, OpenSession, ActivateTab, t => t.Disconnect(), CloseTab);
        }
        _sessionStatus.Show(this);
        _sessionStatus.Activate();
    }

    private void ActivateTab(TerminalTab tab)
    {
        foreach (TabPage page in _tabs.TabPages)
        {
            if (page.Controls.Contains(tab))
            {
                _tabs.SelectedTab = page;
                Activate();
                tab.Focus();
                break;
            }
        }
    }

    private void CloseTab(TerminalTab tab)
    {
        foreach (TabPage page in _tabs.TabPages.Cast<TabPage>().ToArray())
        {
            if (!page.Controls.Contains(tab)) continue;
            tab.Disconnect();
            _tabs.TabPages.Remove(page);
            page.Dispose();
            break;
        }
    }

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _oldWindowState = WindowState;
            _oldBorderStyle = FormBorderStyle;
            MainMenuStrip!.Visible = false;
            foreach (var t in AllTabs()) t.SetTerminalChromeVisible(false);
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            MainMenuStrip!.Visible = true;
            foreach (var t in AllTabs()) t.SetTerminalChromeVisible(true);
            FormBorderStyle = _oldBorderStyle;
            WindowState = _oldWindowState;
            _fullScreen = false;
        }
    }

    private void CloseCurrentTab()
    {
        if (_tabs.SelectedTab == null) return;
        Current?.Disconnect();
        _tabs.TabPages.Remove(_tabs.SelectedTab);
        _sessionStatus?.Reload();
    }

    private void CloseOtherTabs()
    {
        var keep = _tabs.SelectedTab;
        foreach (TabPage p in _tabs.TabPages.Cast<TabPage>().Where(p => p != keep).ToArray())
        {
            p.Controls.OfType<TerminalTab>().FirstOrDefault()?.Disconnect();
            _tabs.TabPages.Remove(p);
            p.Dispose();
        }
        _sessionStatus?.Reload();
    }

    private void StartLog()
    {
        var t = Current; if (t == null) return;
        Directory.CreateDirectory(_settings.SessionLogDirectory);
        using var sfd = new SaveFileDialog { Filter = "Text log|*.log;*.txt|All files|*.*", InitialDirectory = _settings.SessionLogDirectory, FileName = $"ssh-{DateTime.Now:yyyyMMdd-HHmmss}.log" };
        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            var timestamps = MessageBox.Show(this, "Add timestamp prefix to log lines?", "Logging", MessageBoxButtons.YesNo) == DialogResult.Yes;
            t.StartLogging(sfd.FileName, timestamps);
        }
    }

    private void SaveBuffer()
    {
        var t = Current; if (t == null) return;
        using var sfd = new SaveFileDialog { Filter = "Text|*.txt|All files|*.*", FileName = "terminal-buffer.txt" };
        if (sfd.ShowDialog(this) == DialogResult.OK) t.SaveBuffer(sfd.FileName);
    }

    private void ChooseFont()
    {
        using var fd = new FontDialog { Font = _settings.TerminalFont(), FixedPitchOnly = false };
        if (fd.ShowDialog(this) == DialogResult.OK)
        {
            _settings.FontFamily = fd.Font.FontFamily.Name;
            _settings.FontSize = fd.Font.Size;
            ApplyAppearance();
        }
    }

    private void ChooseColor(bool foreground)
    {
        using var cd = new ColorDialog();
        cd.Color = foreground ? _settings.TerminalForeColor() : _settings.TerminalBackColor();
        if (cd.ShowDialog(this) == DialogResult.OK)
        {
            if (foreground) _settings.ForeColor = ColorTranslator.ToHtml(cd.Color); else _settings.BackColor = ColorTranslator.ToHtml(cd.Color);
            ApplyAppearance();
        }
    }

    private void ApplyAppearance()
    {
        foreach (var t in AllTabs()) t.ApplyAppearance(_settings);
        _settings.Save();
    }


    private void ConfigureKeyMappings()
    {
        using var d = new KeyMappingDialog(_settings);
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            Info("Keyboard mappings saved. New and existing SSH tabs will use the updated mappings.");
        }
    }
    private void TerminalSettings()
    {
        var t = Current; if (t == null) return;
        var p = t.Profile;
        using var d = new Form { Text = "Terminal Settings", StartPosition = FormStartPosition.CenterParent, Width = 360, Height = 270, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        var echo = new CheckBox { Text = "Local echo", Checked = p.LocalEcho, AutoSize = true };
        var nl = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 }; nl.Items.AddRange(Enum.GetNames(typeof(NewLineMode))); nl.SelectedItem = p.SendNewLine.ToString();
        var bs = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 }; bs.Items.AddRange(Enum.GetNames(typeof(BackspaceMode))); bs.SelectedItem = p.BackspaceMode.ToString();
        var keep = new NumericUpDown { Minimum = 0, Maximum = 3600, Value = p.KeepAliveSeconds };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(12), RowCount = 4, ColumnCount = 2, Height = 160 };
        table.Controls.Add(new Label { Text = "Send newline", AutoSize = true }, 0, 0); table.Controls.Add(nl, 1, 0);
        table.Controls.Add(new Label { Text = "Backspace sends", AutoSize = true }, 0, 1); table.Controls.Add(bs, 1, 1);
        table.Controls.Add(new Label { Text = "Keepalive seconds", AutoSize = true }, 0, 2); table.Controls.Add(keep, 1, 2);
        table.Controls.Add(echo, 1, 3);
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) }; buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        d.Controls.Add(table); d.Controls.Add(buttons); d.AcceptButton = ok; d.CancelButton = cancel;
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            p.LocalEcho = echo.Checked; p.SendNewLine = Enum.Parse<NewLineMode>(nl.SelectedItem!.ToString()!); p.BackspaceMode = Enum.Parse<BackspaceMode>(bs.SelectedItem!.ToString()!); p.KeepAliveSeconds = (int)keep.Value;
        }
    }

    private void SetupForwarding()
    {
        var t = Current; if (t == null) return;
        using var d = new ForwardDialog();
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            try { t.StartLocalForward(d.Rule); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Forwarding error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    private async System.Threading.Tasks.Task RunMacroFile()
    {
        var t = Current; if (t == null) return;
        using var ofd = new OpenFileDialog { Filter = "Text commands|*.txt;*.ttl;*.cmd|All files|*.*" };
        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            var lines = File.ReadAllLines(ofd.FileName).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"));
            await t.SendLinesAsync(lines);
            Notify("Macro complete", Path.GetFileName(ofd.FileName));
        }
    }

    private async System.Threading.Tasks.Task SendCommandList()
    {
        var t = Current; if (t == null) return;
        using var d = new SimpleTextDialog("Send command list", "# Enter one command per line\n");
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            var lines = d.Value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"));
            await t.SendLinesAsync(lines);
        }
    }

    private async System.Threading.Tasks.Task BroadcastCommand()
    {
        using var d = new SimpleTextDialog("Broadcast command to all connected tabs", "");
        if (d.ShowDialog(this) == DialogResult.OK)
        {
            foreach (var t in AllTabs().Where(x => x.IsConnected)) await t.SendLinesAsync(d.Value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries), 50);
        }
    }

    private void Notify(string title, string msg)
    {
        _notify.BalloonTipTitle = title; _notify.BalloonTipText = msg; _notify.ShowBalloonTip(2500);
    }
    private void Info(string msg) => MessageBox.Show(this, msg, "SSH Client", MessageBoxButtons.OK, MessageBoxIcon.Information);
}
