using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SshTerm;

public sealed class SessionManagerForm : Form
{
    private readonly AppSettings _settings;
    private readonly Func<SessionProfile, Task> _openSession;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, FullRowSelect = true, View = View.Details, HideSelection = false };

    public SessionManagerForm(AppSettings settings, Func<SessionProfile, Task> openSession)
    {
        _settings = settings;
        _openSession = openSession;
        Text = "Manage Saved Sessions";
        Width = 820;
        Height = 480;
        StartPosition = FormStartPosition.CenterParent;
        _list.Columns.Add("Name", 180);
        _list.Columns.Add("Host", 190);
        _list.Columns.Add("Port", 70);
        _list.Columns.Add("User", 140);
        _list.Columns.Add("Auth", 100);
        _list.Columns.Add("Terminal", 100);
        _list.DoubleClick += async (_, _) => await OpenSelected();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        buttons.Controls.Add(Button("New", async () => await NewSession()));
        buttons.Controls.Add(Button("Edit", async () => await EditSelected()));
        buttons.Controls.Add(Button("Delete", DeleteSelected));
        buttons.Controls.Add(Button("Open", async () => await OpenSelected()));
        buttons.Controls.Add(Button("Refresh", RefreshList));
        Controls.Add(_list);
        Controls.Add(buttons);
        RefreshList();
    }

    private static Button Button(string text, Action action)
    {
        var b = new Button { Text = text, Width = 90, Height = 28 };
        b.Click += (_, _) => action();
        return b;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Text = text, Width = 90, Height = 28 };
        b.Click += async (_, _) => await action();
        return b;
    }

    private SessionProfile? SelectedProfile() => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as SessionProfile;

    public void Reload() => RefreshList();

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var s in _settings.Sessions.OrderBy(x => x.Name))
        {
            var item = new ListViewItem(s.Name);
            item.SubItems.Add(s.Host);
            item.SubItems.Add(s.Port.ToString());
            item.SubItems.Add(s.UserName);
            item.SubItems.Add(s.AuthMode.ToString());
            item.SubItems.Add(s.TerminalType);
            item.Tag = s;
            _list.Items.Add(item);
        }
    }

    private async Task NewSession()
    {
        using var d = new ConnectDialog();
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _settings.Sessions.RemoveAll(x => x.Name.Equals(d.Profile.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Sessions.Add(d.Profile.CloneForStorage());
        _settings.Save();
        RefreshList();
        await Task.CompletedTask;
    }

    private async Task EditSelected()
    {
        var selected = SelectedProfile();
        if (selected == null) return;
        using var d = new ConnectDialog(selected);
        d.Text = "Edit Saved Session";
        if (d.ShowDialog(this) != DialogResult.OK) return;
        _settings.Sessions.Remove(selected);
        _settings.Sessions.RemoveAll(x => x.Name.Equals(d.Profile.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Sessions.Add(d.Profile.CloneForStorage());
        _settings.Save();
        RefreshList();
        await Task.CompletedTask;
    }

    private void DeleteSelected()
    {
        var selected = SelectedProfile();
        if (selected == null) return;
        if (MessageBox.Show(this, $"Delete saved session '{selected.Name}'?", "Delete Session", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _settings.Sessions.Remove(selected);
        _settings.Save();
        RefreshList();
    }

    private async Task OpenSelected()
    {
        var selected = SelectedProfile();
        if (selected != null) await _openSession(selected.Clone());
    }
}

public sealed class SessionStatusForm : Form
{
    private readonly Func<TerminalTab[]> _activeTabs;
    private readonly AppSettings _settings;
    private readonly Func<SessionProfile, Task> _openSession;
    private readonly Action<TerminalTab> _activate;
    private readonly Action<TerminalTab> _disconnect;
    private readonly Action<TerminalTab> _close;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, FullRowSelect = true, View = View.Details, HideSelection = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    public SessionStatusForm(AppSettings settings, Func<TerminalTab[]> activeTabs, Func<SessionProfile, Task> openSession, Action<TerminalTab> activate, Action<TerminalTab> disconnect, Action<TerminalTab> close)
    {
        _settings = settings;
        _activeTabs = activeTabs;
        _openSession = openSession;
        _activate = activate;
        _disconnect = disconnect;
        _close = close;
        Text = "Active and Inactive Sessions";
        Width = 920;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        _list.Columns.Add("State", 90);
        _list.Columns.Add("Name", 180);
        _list.Columns.Add("Endpoint", 260);
        _list.Columns.Add("User", 120);
        _list.Columns.Add("Last Used", 160);
        _list.Columns.Add("Source", 90);
        _list.DoubleClick += async (_, _) => await DefaultAction();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        buttons.Controls.Add(Button("Activate", ActivateSelected));
        buttons.Controls.Add(Button("Connect", async () => await ConnectSelected()));
        buttons.Controls.Add(Button("Disconnect", DisconnectSelected));
        buttons.Controls.Add(Button("Close Tab", CloseSelected));
        buttons.Controls.Add(Button("Refresh", RefreshList));
        Controls.Add(_list);
        Controls.Add(buttons);
        _timer.Tick += (_, _) => RefreshList();
        _timer.Start();
        FormClosed += (_, _) => _timer.Stop();
        RefreshList();
    }

    private static Button Button(string text, Action action)
    {
        var b = new Button { Text = text, Width = 92, Height = 28 };
        b.Click += (_, _) => action();
        return b;
    }

    private static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Text = text, Width = 92, Height = 28 };
        b.Click += async (_, _) => await action();
        return b;
    }

    private object? SelectedTag() => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag;

    public void Reload() => RefreshList();

    private void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        var active = _activeTabs();
        foreach (var tab in active)
            AddRow(tab.IsConnected ? "Active" : "Inactive", tab.Profile, "Open tab", tab);

        foreach (var profile in _settings.Sessions.OrderBy(x => x.Name))
        {
            var isOpen = active.Any(t => t.Profile.SameEndpoint(profile));
            if (!isOpen) AddRow("Inactive", profile, "Saved", profile.Clone());
        }

        foreach (var profile in _settings.RecentSessions.OrderByDescending(x => x.LastUsedUtc))
        {
            var isKnown = _settings.Sessions.Any(s => s.SameEndpoint(profile)) || active.Any(t => t.Profile.SameEndpoint(profile));
            if (!isKnown) AddRow("Inactive", profile, "Recent", profile.Clone());
        }
        _list.EndUpdate();
    }

    private void AddRow(string state, SessionProfile profile, string source, object tag)
    {
        var item = new ListViewItem(state);
        item.SubItems.Add(profile.Name);
        item.SubItems.Add($"{profile.Host}:{profile.Port}");
        item.SubItems.Add(profile.UserName);
        item.SubItems.Add(profile.LastUsedUtc == default ? "" : profile.LastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        item.SubItems.Add(source);
        item.Tag = tag;
        _list.Items.Add(item);
    }

    private async Task DefaultAction()
    {
        if (SelectedTag() is TerminalTab tab) _activate(tab);
        else await ConnectSelected();
    }

    private void ActivateSelected()
    {
        if (SelectedTag() is TerminalTab tab) _activate(tab);
    }

    private async Task ConnectSelected()
    {
        switch (SelectedTag())
        {
            case SessionProfile p:
                await _openSession(p.Clone());
                RefreshList();
                break;
        }
    }

    private void DisconnectSelected()
    {
        if (SelectedTag() is TerminalTab tab) _disconnect(tab);
        RefreshList();
    }

    private void CloseSelected()
    {
        if (SelectedTag() is TerminalTab tab) _close(tab);
        RefreshList();
    }
}
