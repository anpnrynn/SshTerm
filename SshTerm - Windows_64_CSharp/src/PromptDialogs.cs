using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SshTerm;

public sealed class ConnectDialog : Form
{
    private readonly TextBox _name = new() { Width = 260 };
    private readonly TextBox _host = new() { Width = 260 };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 22, Width = 90 };
    private readonly TextBox _user = new() { Width = 260 };
    private readonly TextBox _password = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly ComboBox _auth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _keyPath = new() { Width = 220 };
    private readonly TextBox _keyPass = new() { Width = 260, UseSystemPasswordChar = true };
    private readonly TextBox _term = new() { Width = 140, Text = "xterm-256color" };
    private readonly CheckBox _save = new() { Text = "Save session profile (password/passphrase are never saved)", AutoSize = true };

    public SessionProfile Profile { get; private set; } = new();
    public bool SaveProfile => _save.Checked;

    public ConnectDialog(SessionProfile? existing = null)
    {
        Text = "New SSH Connection";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(440, 385);
        _auth.Items.AddRange(Enum.GetNames(typeof(AuthMode)));
        _auth.SelectedIndex = 0;
        var browse = new Button { Text = "...", Width = 32 };
        browse.Click += (_, _) => { using var ofd = new OpenFileDialog(); if (ofd.ShowDialog(this) == DialogResult.OK) _keyPath.Text = ofd.FileName; };

        var ok = new Button { Text = "Connect", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        ok.Click += (_, _) => BuildProfile();

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 11, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(string label, Control c) { panel.Controls.Add(new Label { Text = label, TextAlign = ContentAlignment.MiddleLeft, AutoSize = true }, 0, panel.Controls.Count / 2); panel.Controls.Add(c, 1, panel.Controls.Count / 2); }
        Row("Name", _name); Row("Host", _host); Row("Port", _port); Row("Username", _user); Row("Auth", _auth); Row("Password", _password);
        var keyPanel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight }; keyPanel.Controls.Add(_keyPath); keyPanel.Controls.Add(browse); Row("Private key", keyPanel);
        Row("Key passphrase", _keyPass); Row("Terminal", _term);
        panel.Controls.Add(_save, 1, 9);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) }; buttons.Controls.Add(cancel); buttons.Controls.Add(ok);
        Controls.Add(panel); Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
        if (existing != null) LoadProfile(existing);
    }

    private void LoadProfile(SessionProfile p)
    {
        _name.Text = p.Name; _host.Text = p.Host; _port.Value = p.Port; _user.Text = p.UserName; _password.Text = string.Empty;
        _auth.SelectedItem = p.AuthMode.ToString(); _keyPath.Text = p.PrivateKeyPath; _keyPass.Text = string.Empty; _term.Text = p.TerminalType;
    }

    private void BuildProfile()
    {
        Profile = new SessionProfile { Name = string.IsNullOrWhiteSpace(_name.Text) ? _host.Text : _name.Text, Host = _host.Text.Trim(), Port = (int)_port.Value, UserName = _user.Text.Trim(), Password = _password.Text, AuthMode = Enum.Parse<AuthMode>(_auth.SelectedItem!.ToString()!), PrivateKeyPath = _keyPath.Text, PrivateKeyPassphrase = _keyPass.Text, TerminalType = _term.Text };
    }
}

public sealed class SimpleTextDialog : Form
{
    private readonly TextBox _box = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
    public string Value => _box.Text;
    public SimpleTextDialog(string title, string initial = "")
    {
        Text = title; Width = 520; Height = 360; StartPosition = FormStartPosition.CenterParent; _box.Text = initial;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) }; p.Controls.Add(cancel); p.Controls.Add(ok);
        Controls.Add(_box); Controls.Add(p); AcceptButton = ok; CancelButton = cancel;
    }
}

public sealed class ForwardDialog : Form
{
    private readonly TextBox _bindHost = new() { Text = "127.0.0.1", Width = 140 };
    private readonly NumericUpDown _bindPort = new() { Minimum = 1, Maximum = 65535, Value = 10022 };
    private readonly TextBox _remoteHost = new() { Text = "127.0.0.1", Width = 140 };
    private readonly NumericUpDown _remotePort = new() { Minimum = 1, Maximum = 65535, Value = 22 };
    public ForwardRule Rule => new() { BoundHost = _bindHost.Text, BoundPort = (uint)_bindPort.Value, RemoteHost = _remoteHost.Text, RemotePort = (uint)_remotePort.Value };
    public ForwardDialog()
    {
        Text = "SSH Local Port Forwarding"; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(360, 190);
        var t = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(12), ColumnCount = 2, RowCount = 4, Height = 130 };
        void Add(string l, Control c, int r) { t.Controls.Add(new Label { Text = l, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft }, 0, r); t.Controls.Add(c, 1, r); }
        Add("Local bind host", _bindHost, 0); Add("Local bind port", _bindPort, 1); Add("Remote host", _remoteHost, 2); Add("Remote port", _remotePort, 3);
        var ok = new Button { Text = "Start", DialogResult = DialogResult.OK }; var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var p = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) }; p.Controls.Add(cancel); p.Controls.Add(ok);
        Controls.Add(t); Controls.Add(p); AcceptButton = ok; CancelButton = cancel;
    }
}

public sealed class KeyMappingDialog : Form
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, TextBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] KeyOrder = { "Backspace", "Delete", "PageUp", "PageDown", "Home", "End", "Insert" };

    public KeyMappingDialog(AppSettings settings)
    {
        _settings = settings;
        _settings.EnsureKeyMappings();
        Text = "Keyboard / Escape Sequence Mapping";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(620, 390);

        var table = new TableLayoutPanel { Dock = DockStyle.Top, Padding = new Padding(12), ColumnCount = 3, RowCount = KeyOrder.Length + 1, Height = 245 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(new Label { Text = "Key", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        table.Controls.Add(new Label { Text = "Sequence/value", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
        table.Controls.Add(new Label { Text = "Decoded preview", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 2, 0);

        for (var i = 0; i < KeyOrder.Length; i++)
        {
            var key = KeyOrder[i];
            var row = i + 1;
            var box = new TextBox { Width = 190, Text = _settings.KeyMappings[key] };
            var preview = new Label { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 5, 0, 0) };
            box.TextChanged += (_, _) => preview.Text = Preview(AppSettings.DecodeSequence(box.Text));
            preview.Text = Preview(AppSettings.DecodeSequence(box.Text));
            _boxes[key] = box;
            table.Controls.Add(new Label { Text = key, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 5, 0, 0) }, 0, row);
            table.Controls.Add(box, 1, row);
            table.Controls.Add(preview, 2, row);
        }

        var help = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 0, 12, 0),
            Text = "Use common terminal forms such as \\x7f, \\b, \\e[3~, \\x1b[5~, ^H, ^?, DEL, BS, or ESC. " +
                   "Defaults: Backspace=\\x7f, Delete=\\x1b[3~, PageUp=\\x1b[5~, PageDown=\\x1b[6~, Home=\\x1b[H, End=\\x1b[F.",
            AutoSize = false
        };

        var defaults = new Button { Text = "Restore defaults", Width = 120 };
        defaults.Click += (_, _) =>
        {
            foreach (var kv in AppSettings.DefaultKeyMappings())
                if (_boxes.TryGetValue(kv.Key, out var box)) box.Text = kv.Value;
        };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 90 };
        ok.Click += (_, _) => SaveMappings();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(defaults);

        Controls.Add(help);
        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void SaveMappings()
    {
        foreach (var kv in _boxes)
            _settings.KeyMappings[kv.Key] = kv.Value.Text;
        _settings.Save();
    }

    private static string Preview(string value)
    {
        if (value.Length == 0) return "<empty>";
        var parts = new List<string>();
        foreach (var ch in value)
        {
            parts.Add(ch switch
            {
                '\x1b' => "ESC(0x1B)",
                '\x7f' => "DEL(0x7F)",
                '\b' => "BS(0x08)",
                '\r' => "CR(0x0D)",
                '\n' => "LF(0x0A)",
                '\t' => "TAB(0x09)",
                _ when char.IsControl(ch) => $"0x{(int)ch:X2}",
                _ => $"'{ch}'(0x{(int)ch:X2})"
            });
        }
        return string.Join(" ", parts);
    }
}
