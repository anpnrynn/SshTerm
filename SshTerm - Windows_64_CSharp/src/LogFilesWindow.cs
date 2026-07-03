using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MultiTabbedSshClient;

public sealed class LogFilesForm : Form
{
    private readonly AppSettings _settings;
    private readonly ListView _files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
    private readonly Label _pathLabel = new() { Dock = DockStyle.Top, AutoSize = false, Height = 24 };

    public LogFilesForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Session Log Files";
        Width = 840;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        _files.Columns.Add("File", 360);
        _files.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _files.Columns.Add("Modified", 180);
        _files.Columns.Add("Path", 320);
        _files.DoubleClick += (_, _) => OpenSelected();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.LeftToRight, Height = 44, Padding = new Padding(8) };
        buttons.Controls.Add(new Button { Text = "Refresh", AutoSize = true }.Also(b => { b.Click += (_, _) => Reload(); }));
        buttons.Controls.Add(new Button { Text = "Open", AutoSize = true }.Also(b => { b.Click += (_, _) => OpenSelected(); }));
        buttons.Controls.Add(new Button { Text = "Open Folder", AutoSize = true }.Also(b => { b.Click += (_, _) => OpenFolder(); }));
        buttons.Controls.Add(new Button { Text = "Delete Selected", AutoSize = true }.Also(b => { b.Click += (_, _) => DeleteSelected(); }));
        buttons.Controls.Add(new Button { Text = "Delete All", AutoSize = true }.Also(b => { b.Click += (_, _) => DeleteAll(); }));
        buttons.Controls.Add(new Button { Text = "Close", AutoSize = true }.Also(b => { b.Click += (_, _) => Close(); }));

        Controls.Add(_files);
        Controls.Add(_pathLabel);
        Controls.Add(buttons);
        Reload();
    }

    public void Reload()
    {
        var dir = LogDirectory;
        _pathLabel.Text = "  " + dir;
        Directory.CreateDirectory(dir);
        _files.Items.Clear();
        foreach (var file in Directory.EnumerateFiles(dir, "*.log").OrderByDescending(File.GetLastWriteTime))
        {
            var info = new FileInfo(file);
            var item = new ListViewItem(info.Name) { Tag = file };
            item.SubItems.Add(FormatBytes(info.Length));
            item.SubItems.Add(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(info.FullName);
            _files.Items.Add(item);
        }
    }

    private string LogDirectory => string.IsNullOrWhiteSpace(_settings.SessionLogDirectory) ? @"C:\SshSessionsData" : _settings.SessionLogDirectory;

    private void OpenSelected()
    {
        foreach (ListViewItem item in _files.SelectedItems)
        {
            var path = item.Tag as string;
            if (path == null || !File.Exists(path)) continue;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    private void DeleteSelected()
    {
        if (_files.SelectedItems.Count == 0) return;
        if (MessageBox.Show(this, "Delete selected session log files?", "Session Logs", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        foreach (ListViewItem item in _files.SelectedItems)
        {
            var path = item.Tag as string;
            TryDelete(path);
        }
        Reload();
    }

    private void DeleteAll()
    {
        if (MessageBox.Show(this, "Delete all session log files?", "Session Logs", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log")) TryDelete(file);
        Reload();
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}

internal static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
