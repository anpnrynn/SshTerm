using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SshTerm;

public sealed class TerminalSurface : Control
{
    private const int MaxScrollbackLines = 10000;
    private const int MaxCaptureChars = 4_000_000;
    private char[,] _cells = new char[24, 80];
    private char[,]? _alternate;
    private int _rows = 24;
    private int _cols = 80;
    private int _row;
    private int _col;
    private int _savedRow;
    private int _savedCol;
    private bool _wrap = true;
    private readonly StringBuilder _capture = new();
    private readonly System.Windows.Forms.Timer _caretTimer = new() { Interval = 500 };
    private bool _caretVisible = true;
    private bool _cursorEnabled = true;
    private bool _pendingWrap;
    private int _scrollTop;
    private int _scrollBottom = 23;
    private string _pendingEscape = string.Empty;
    private bool _selecting;
    private Point? _selectionStart;
    private Point? _selectionEnd;
    private readonly List<char[]> _scrollback = new();
    private int _viewportOffset;
    private readonly VScrollBar _scrollBar = new() { Dock = DockStyle.Right, SmallChange = 1, LargeChange = 5 };
    private bool _updatingScrollBar;

    public event Action<int, int>? TerminalSizeChanged;

    public int Rows => _rows;
    public int Columns => _cols;
    public bool HasSelection => _selectionStart.HasValue && _selectionEnd.HasValue && _selectionStart.Value != _selectionEnd.Value;

    public TerminalSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.FromArgb(16, 16, 16);
        ForeColor = Color.FromArgb(230, 230, 230);
        Font = new Font("Consolas", 10.0f, FontStyle.Regular, GraphicsUnit.Point);
        Cursor = Cursors.IBeam;
        _scrollBar.ValueChanged += (_, _) =>
        {
            if (_updatingScrollBar) return;
            var max = MaxViewportOffset();
            SetViewportOffset(max - Math.Min(max, _scrollBar.Value));
        };
        Controls.Add(_scrollBar);
        Clear();
        _caretTimer.Tick += CaretTimer_Tick;
        _caretTimer.Start();
        Resize += (_, _) => { if (!IsDisposed && !Disposing) ResizeToClient(); };
    }

    private void CaretTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            try { _caretTimer.Stop(); } catch { }
            return;
        }
        _caretVisible = !_caretVisible;
        var rect = CursorRectangle();
        if (!rect.IsEmpty) Invalidate(rect);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _caretTimer.Stop(); } catch { }
            try { _caretTimer.Tick -= CaretTimer_Tick; } catch { }
            try { _caretTimer.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ResizeToClient();
    }

    public void RefreshTerminalSize() => ResizeToClient();

    public override string Text { get => GetScreenText(); set { Clear(); if (!string.IsNullOrEmpty(value)) Process(value); } }

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown or Keys.Delete or Keys.Insert or Keys.Tab || base.IsInputKey(keyData);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ResizeToClient();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        var rect = CursorRectangle();
        if (!rect.IsEmpty) Invalidate(rect);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        var rect = CursorRectangle();
        if (!rect.IsEmpty) Invalidate(rect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (e.Button == MouseButtons.Left)
        {
            var cell = PointToCell(e.Location);
            _selectionStart = cell;
            _selectionEnd = cell;
            _selecting = true;
            Capture = true;
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            _selectionEnd = PointToCell(e.Location);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _selecting = false;
            Capture = false;
            _selectionEnd = PointToCell(e.Location);
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        var p = PointToClient(MousePosition);
        var cell = PointToCell(p);
        var r = cell.Y;
        var c = cell.X;
        if (r < 0 || r >= _rows) return;
        while (c > 0 && !char.IsWhiteSpace(GetVisibleChar(r, c - 1))) c--;
        var end = cell.X;
        while (end < _cols - 1 && !char.IsWhiteSpace(GetVisibleChar(r, end + 1))) end++;
        _selectionStart = new Point(c, r);
        _selectionEnd = new Point(Math.Min(_cols - 1, end + 1), r);
        Invalidate();
        base.OnDoubleClick(e);
    }

    private SizeF CellSize()
    {
        if (IsDisposed || Disposing) return new SizeF(8, Math.Max(1, Font?.Height ?? 16));
        if (!IsHandleCreated) return new SizeF(8, Math.Max(1, Font?.Height ?? 16));
        try
        {
            using var g = CreateGraphics();
            var s = TextRenderer.MeasureText(g, "M", Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return new SizeF(Math.Max(1, s.Width), Math.Max(1, Font.Height));
        }
        catch (ObjectDisposedException)
        {
            return new SizeF(8, Math.Max(1, Font?.Height ?? 16));
        }
        catch (InvalidOperationException)
        {
            return new SizeF(8, Math.Max(1, Font?.Height ?? 16));
        }
    }

    private Point PointToCell(Point p)
    {
        var cell = CellSize();
        var col = Math.Clamp((int)Math.Floor(p.X / cell.Width), 0, _cols - 1);
        var row = Math.Clamp((int)Math.Floor(p.Y / cell.Height), 0, _rows - 1);
        return new Point(col, row);
    }

    private void ResizeToClient()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        var cell = CellSize();
        var newCols = Math.Max(20, (int)Math.Floor(UsableClientWidth() / cell.Width));
        var newRows = Math.Max(5, (int)Math.Floor(ClientSize.Height / cell.Height));
        if (newCols == _cols && newRows == _rows) return;
        ResizeBuffer(newRows, newCols, preserve: true);
        TerminalSizeChanged?.Invoke(_cols, _rows);
    }

    private void ResizeBuffer(int rows, int cols, bool preserve)
    {
        var next = NewBuffer(rows, cols);
        if (preserve)
        {
            var copyRows = Math.Min(rows, _rows);
            var copyCols = Math.Min(cols, _cols);
            for (var r = 0; r < copyRows; r++)
                for (var c = 0; c < copyCols; c++)
                    next[r, c] = _cells[r, c];
        }
        _rows = rows;
        _cols = cols;
        _cells = next;
        _row = Math.Clamp(_row, 0, _rows - 1);
        _col = Math.Clamp(_col, 0, _cols - 1);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _pendingWrap = false;
        ClearSelection();
        UpdateScrollBar();
        Invalidate();
    }

    private int UsableClientWidth() => Math.Max(1, ClientSize.Width - (_scrollBar.Visible ? _scrollBar.Width : 0));

    private static char[,] NewBuffer(int rows, int cols)
    {
        var b = new char[rows, cols];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                b[r, c] = ' ';
        return b;
    }

    public void Clear()
    {
        _cells = NewBuffer(_rows, _cols);
        _row = 0;
        _col = 0;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _pendingWrap = false;
        _pendingEscape = string.Empty;
        _scrollback.Clear();
        _viewportOffset = 0;
        ClearSelection();
        UpdateScrollBar();
        Invalidate();
    }

    public void Process(string text)
    {
        _capture.Append(text);
        if (_capture.Length > MaxCaptureChars + 65536)
            _capture.Remove(0, _capture.Length - MaxCaptureChars);
        if (!string.IsNullOrEmpty(_pendingEscape))
        {
            text = _pendingEscape + text;
            _pendingEscape = string.Empty;
        }

        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            var ch = text[i++];
            if (ch == '\x1b')
            {
                var consumed = ConsumeEscape(text, i);
                if (consumed < 0)
                {
                    _pendingEscape = text[start..];
                    break;
                }
                i = consumed;
                continue;
            }
            if (ch == '\r') { _col = 0; _pendingWrap = false; continue; }
            if (ch == '\n') { LineFeed(); continue; }
            if (ch == '\b') { _pendingWrap = false; if (_col > 0) _col--; continue; }
            if (ch == '\a') continue;
            if (ch == '\t') { var nextTab = Math.Min(_cols - 1, ((_col / 8) + 1) * 8); while (_col < nextTab) PutChar(' '); continue; }
            if (!char.IsControl(ch)) PutChar(ch);
        }
        if (_viewportOffset == 0) UpdateScrollBar();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxViewportOffset() <= 0) return;
        var lines = Math.Max(1, SystemInformation.MouseWheelScrollLines);
        var delta = e.Delta > 0 ? lines : -lines;
        SetViewportOffset(_viewportOffset + delta);
    }

    private int ConsumeEscape(string text, int i)
    {
        if (i >= text.Length) return -1;
        var ch = text[i++];
        if (ch == '[') return ConsumeCsi(text, i);
        if (ch == ']') return ConsumeOsc(text, i);
        if (ch == 'P' || ch == '^' || ch == '_') return ConsumeStringTerminatedBySt(text, i);
        if (ch is '(' or ')' or '*' or '+') return i < text.Length ? i + 1 : -1;
        switch (ch)
        {
            case '7': _savedRow = _row; _savedCol = _col; break;
            case '8': _row = Math.Clamp(_savedRow, 0, _rows - 1); _col = Math.Clamp(_savedCol, 0, _cols - 1); _pendingWrap = false; break;
            case 'c': Clear(); break;
            case 'D': LineFeed(); break;
            case 'M': ReverseLineFeed(); break;
            case 'E': _col = 0; LineFeed(); break;
        }
        return i;
    }

    private int ConsumeOsc(string text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] == '\a') return i + 1;
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') return i + 2;
            i++;
        }
        return -1;
    }

    private int ConsumeStringTerminatedBySt(string text, int i)
    {
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') return i + 2;
            i++;
        }
        return -1;
    }

    private int ConsumeCsi(string text, int i)
    {
        var start = i;
        while (i < text.Length && (text[i] < '@' || text[i] > '~')) i++;
        if (i >= text.Length) return -1;
        var body = text[start..i];
        var final = text[i++];
        var privateMode = body.StartsWith("?");
        if (privateMode) body = body[1..];
        var parts = body.Split(new[] { ';', ':' }, StringSplitOptions.None);
        int P(int index, int def = 1)
        {
            if (index >= parts.Length || string.IsNullOrEmpty(parts[index])) return def;
            return int.TryParse(parts[index], out var v) ? v : def;
        }

        switch (final)
        {
            case 'A': _row = Math.Max(0, _row - P(0)); _pendingWrap = false; break;
            case 'B': _row = Math.Min(_rows - 1, _row + P(0)); _pendingWrap = false; break;
            case 'C': _col = Math.Min(_cols - 1, _col + P(0)); _pendingWrap = false; break;
            case 'D': _col = Math.Max(0, _col - P(0)); _pendingWrap = false; break;
            case 'E': _row = Math.Min(_rows - 1, _row + P(0)); _col = 0; _pendingWrap = false; break;
            case 'F': _row = Math.Max(0, _row - P(0)); _col = 0; _pendingWrap = false; break;
            case 'G': _col = Math.Clamp(P(0) - 1, 0, _cols - 1); _pendingWrap = false; break;
            case 'H':
            case 'f': _row = Math.Clamp(P(0) - 1, 0, _rows - 1); _col = Math.Clamp(P(1) - 1, 0, _cols - 1); _pendingWrap = false; break;
            case 'd': _row = Math.Clamp(P(0) - 1, 0, _rows - 1); _pendingWrap = false; break;
            case 'e': _row = Math.Min(_rows - 1, _row + P(0)); _pendingWrap = false; break;
            case 'J': ClearDisplay(P(0, 0)); break;
            case 'K': ClearLine(P(0, 0)); break;
            case 'X': EraseChars(P(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case '@': InsertChars(P(0)); break;
            case 'S': for (var n = 0; n < P(0); n++) ScrollUp(); break;
            case 'T': for (var n = 0; n < P(0); n++) ScrollDown(); break;
            case 'r': SetScrollRegion(P(0, 1), P(1, _rows)); break;
            case 's': _savedRow = _row; _savedCol = _col; break;
            case 'u': _row = _savedRow; _col = _savedCol; break;
            case 'h': ApplyMode(parts, privateMode, true); break;
            case 'l': ApplyMode(parts, privateMode, false); break;
            case 'm': break;
        }
        return i;
    }

    private void ApplyMode(string[] parts, bool privateMode, bool enabled)
    {
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var mode)) continue;
            if (privateMode && mode == 25) _cursorEnabled = enabled;
            if (privateMode && (mode == 1049 || mode == 1047 || mode == 47))
            {
                if (enabled)
                {
                    _alternate = _cells;
                    _cells = NewBuffer(_rows, _cols);
                    _viewportOffset = 0;
                    _row = _col = 0;
                    UpdateScrollBar();
                }
                else if (_alternate != null)
                {
                    _cells = _alternate;
                    _alternate = null;
                    _viewportOffset = 0;
                    _row = _col = 0;
                    UpdateScrollBar();
                }
            }
            if (mode == 7) _wrap = enabled;
        }
    }

    private void PutChar(char ch)
    {
        if (_pendingWrap)
        {
            _pendingWrap = false;
            _col = 0;
            LineFeed();
        }
        if (_row < 0 || _row >= _rows || _col < 0 || _col >= _cols) return;
        _cells[_row, _col] = ch;
        if (_col == _cols - 1)
        {
            if (_wrap) _pendingWrap = true;
        }
        else _col++;
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (_row == _scrollBottom) ScrollUp();
        else if (_row < _rows - 1) _row++;
    }

    private void ReverseLineFeed()
    {
        _pendingWrap = false;
        if (_row == _scrollTop) ScrollDown();
        else if (_row > 0) _row--;
    }

    private void ScrollUp()
    {
        if (_scrollTop == 0 && _scrollBottom == _rows - 1 && _alternate == null) AddScrollbackRow(0);
        for (var r = _scrollTop + 1; r <= _scrollBottom; r++)
            for (var c = 0; c < _cols; c++) _cells[r - 1, c] = _cells[r, c];
        BlankRow(_scrollBottom);
    }

    private void ScrollDown()
    {
        for (var r = _scrollBottom - 1; r >= _scrollTop; r--)
            for (var c = 0; c < _cols; c++) _cells[r + 1, c] = _cells[r, c];
        BlankRow(_scrollTop);
    }

    private void BlankRow(int r) { for (var c = 0; c < _cols; c++) _cells[r, c] = ' '; }

    private void AddScrollbackRow(int row)
    {
        var line = GetRowChars(row, _cols);
        if (_scrollback.Count >= MaxScrollbackLines)
        {
            _scrollback.RemoveAt(0);
            if (_viewportOffset > 0) _viewportOffset--;
        }
        _scrollback.Add(line);
        if (_viewportOffset > 0) _viewportOffset = Math.Min(MaxViewportOffset(), _viewportOffset + 1);
        UpdateScrollBar();
    }

    private void ClearDisplay(int mode)
    {
        if (mode == 3) { _scrollback.Clear(); _viewportOffset = 0; UpdateScrollBar(); return; }
        if (mode == 2) { for (var r = 0; r < _rows; r++) BlankRow(r); _row = 0; _col = 0; _pendingWrap = false; Invalidate(); return; }
        if (mode == 0)
        {
            for (var c = _col; c < _cols; c++) _cells[_row, c] = ' ';
            for (var r = _row + 1; r < _rows; r++) BlankRow(r);
        }
        else if (mode == 1)
        {
            for (var r = 0; r < _row; r++) BlankRow(r);
            for (var c = 0; c <= _col; c++) _cells[_row, c] = ' ';
        }
    }

    private void ClearLine(int mode)
    {
        if (mode == 2) { BlankRow(_row); return; }
        if (mode == 0) for (var c = _col; c < _cols; c++) _cells[_row, c] = ' ';
        if (mode == 1) for (var c = 0; c <= _col; c++) _cells[_row, c] = ' ';
    }

    private void InsertLines(int count)
    {
        count = Math.Min(count, _rows - _row);
        for (var r = _rows - 1; r >= _row + count; r--)
            for (var c = 0; c < _cols; c++) _cells[r, c] = _cells[r - count, c];
        for (var r = _row; r < _row + count; r++) BlankRow(r);
    }

    private void DeleteLines(int count)
    {
        count = Math.Min(count, _rows - _row);
        for (var r = _row; r < _rows - count; r++)
            for (var c = 0; c < _cols; c++) _cells[r, c] = _cells[r + count, c];
        for (var r = _rows - count; r < _rows; r++) BlankRow(r);
    }

    private void InsertChars(int count)
    {
        count = Math.Min(count, _cols - _col);
        for (var c = _cols - 1; c >= _col + count; c--) _cells[_row, c] = _cells[_row, c - count];
        for (var c = _col; c < _col + count; c++) _cells[_row, c] = ' ';
    }

    private void DeleteChars(int count)
    {
        count = Math.Min(count, _cols - _col);
        for (var c = _col; c < _cols - count; c++) _cells[_row, c] = _cells[_row, c + count];
        for (var c = _cols - count; c < _cols; c++) _cells[_row, c] = ' ';
        _pendingWrap = false;
    }

    private void EraseChars(int count)
    {
        count = Math.Min(count, _cols - _col);
        for (var c = _col; c < _col + count; c++) _cells[_row, c] = ' ';
        _pendingWrap = false;
    }

    private void SetScrollRegion(int topOneBased, int bottomOneBased)
    {
        var top = Math.Clamp(topOneBased - 1, 0, _rows - 1);
        var bottom = Math.Clamp(bottomOneBased - 1, 0, _rows - 1);
        if (bottom <= top) { _scrollTop = 0; _scrollBottom = _rows - 1; }
        else { _scrollTop = top; _scrollBottom = bottom; }
        _row = 0;
        _col = 0;
        _pendingWrap = false;
    }

    public void CopySelectionOrScreenToClipboard()
    {
        var text = HasSelection ? GetSelectedText() : GetScreenText();
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    public void CopyAllToClipboard()
    {
        Clipboard.SetText(GetScreenText());
    }

    public void SelectAllText()
    {
        _selectionStart = new Point(0, 0);
        _selectionEnd = new Point(_cols - 1, _rows - 1);
        Invalidate();
    }

    public void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        _selecting = false;
    }

    public string GetSelectedText()
    {
        if (!HasSelection) return string.Empty;
        var (start, end) = NormalizedSelection();
        var sb = new StringBuilder();
        for (var r = start.Y; r <= end.Y; r++)
        {
            var c1 = r == start.Y ? start.X : 0;
            var c2 = r == end.Y ? end.X : _cols - 1;
            c1 = Math.Clamp(c1, 0, _cols - 1);
            c2 = Math.Clamp(c2, 0, _cols - 1);
            var last = c2;
            while (last >= c1 && GetVisibleChar(r, last) == ' ') last--;
            if (last >= c1)
            {
                for (var c = c1; c <= last; c++) sb.Append(GetVisibleChar(r, c));
            }
            if (r < end.Y) sb.AppendLine();
        }
        return sb.ToString();
    }

    public string GetScreenText()
    {
        var sb = new StringBuilder();
        for (var r = 0; r < _rows; r++)
        {
            var last = _cols - 1;
            while (last >= 0 && GetVisibleChar(r, last) == ' ') last--;
            if (last >= 0) sb.Append(new string(GetVisibleRowChars(r, last + 1)));
            if (r < _rows - 1) sb.AppendLine();
        }
        return sb.ToString();
    }

    public string GetCaptureText() => _capture.ToString();

    private char[] GetRowChars(int row, int count)
    {
        var a = new char[count];
        for (var c = 0; c < count; c++) a[c] = _cells[row, c];
        return a;
    }

    private char[] GetVisibleRowChars(int visibleRow, int count)
    {
        var a = new char[count];
        for (var c = 0; c < count; c++) a[c] = GetVisibleChar(visibleRow, c);
        return a;
    }

    private char GetVisibleChar(int visibleRow, int col)
    {
        var logical = _scrollback.Count + visibleRow - _viewportOffset;
        if (logical < 0 || col < 0) return ' ';
        if (logical < _scrollback.Count)
        {
            var line = _scrollback[logical];
            return col < line.Length ? line[col] : ' ';
        }
        var screenRow = logical - _scrollback.Count;
        return screenRow >= 0 && screenRow < _rows && col < _cols ? _cells[screenRow, col] : ' ';
    }

    private int MaxViewportOffset() => _alternate == null ? _scrollback.Count : 0;

    private void SetViewportOffset(int offset)
    {
        _viewportOffset = Math.Clamp(offset, 0, MaxViewportOffset());
        ClearSelection();
        UpdateScrollBar();
        Invalidate();
    }

    private void UpdateScrollBar()
    {
        var max = MaxViewportOffset();
        _updatingScrollBar = true;
        try
        {
            _scrollBar.Visible = max > 0;
            _scrollBar.LargeChange = Math.Max(1, _rows);
            _scrollBar.SmallChange = 1;
            _scrollBar.Maximum = Math.Max(0, max + _scrollBar.LargeChange - 1);
            _scrollBar.Value = Math.Clamp(max - _viewportOffset, _scrollBar.Minimum, Math.Max(_scrollBar.Minimum, _scrollBar.Maximum - _scrollBar.LargeChange + 1));
        }
        finally { _updatingScrollBar = false; }
    }

    private (Point Start, Point End) NormalizedSelection()
    {
        var a = _selectionStart!.Value;
        var b = _selectionEnd!.Value;
        if (a.Y > b.Y || (a.Y == b.Y && a.X > b.X)) (a, b) = (b, a);
        return (a, b);
    }

    private bool IsSelected(int row, int col)
    {
        if (!HasSelection) return false;
        var (start, end) = NormalizedSelection();
        if (row < start.Y || row > end.Y) return false;
        if (row == start.Y && col < start.X) return false;
        if (row == end.Y && col > end.X) return false;
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (IsDisposed || Disposing) return;
        e.Graphics.Clear(BackColor);
        var cell = CellSize();
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        if (!HasSelection)
        {
            for (var r = 0; r < _rows; r++)
            {
                var line = new string(GetVisibleRowChars(r, _cols));
                var rect = new Rectangle(0, (int)Math.Round(r * cell.Height), (int)Math.Ceiling(_cols * cell.Width), (int)Math.Ceiling(cell.Height));
                TextRenderer.DrawText(e.Graphics, line, Font, rect, ForeColor, BackColor, flags);
            }
        }
        else
        {
            for (var r = 0; r < _rows; r++)
            {
                for (var c = 0; c < _cols; c++)
                {
                    var selected = IsSelected(r, c);
                    var fg = selected ? SystemColors.HighlightText : ForeColor;
                    var bg = selected ? SystemColors.Highlight : BackColor;
                    var rect = new Rectangle((int)Math.Round(c * cell.Width), (int)Math.Round(r * cell.Height), (int)Math.Ceiling(cell.Width), (int)Math.Ceiling(cell.Height));
                    TextRenderer.DrawText(e.Graphics, GetVisibleChar(r, c).ToString(), Font, rect, fg, bg, flags);
                }
            }
        }

        if (_viewportOffset == 0 && Focused && _caretVisible && _cursorEnabled && !HasSelection)
        {
            using var b = new SolidBrush(ForeColor);
            var rect = CursorRectangle();
            e.Graphics.FillRectangle(b, rect);
        }
    }

    private Rectangle CursorRectangle()
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return Rectangle.Empty;
        var cell = CellSize();
        var x = (int)Math.Round(_col * cell.Width);
        var y = (int)Math.Round((_row + 1) * cell.Height) - 2;
        return new Rectangle(x, Math.Max(0, y), Math.Max(2, (int)Math.Round(cell.Width)), 2);
    }
}
