# SshTerm -Multi Tabbed SSH Client for Windows - WinForms / C#

A Tera Term inspired SSH-only terminal client using C# WinForms and SSH.NET.

# LICENSE
Product released as "Public Domain" software.

## Features

- Multi-tab SSH sessions
- Menu bar similar to terminal clients: File, Edit, Setup, Control, Macro, Window, Help
- Commands are typed directly into the terminal screen, not a separate command box
- Interactive SSH shell using pseudo-terminal allocation
- Password authentication and private-key authentication
- Per-tab terminal buffer
- Copy, paste, select all, clear screen
- Save terminal buffer
- Start/stop session logging with optional timestamping
- Font and foreground/background color settings
- Local echo option
- CR, LF, CRLF send-newline modes
- Keepalive interval setting
- Terminal type and backspace mode; rows/columns are calculated automatically from the terminal window
- SSH local port forwarding dialog
- Simple macro runner: sends each line from a text file to the terminal
- Broadcast command to all connected tabs
- Notify icon and completion/error balloon notifications
- Session profiles saved in `sessions.json`
- Top-level **Recent Sessions** menu on the menu bar
- Stores up to 256 recent SSH sessions, newest first
- Reopening a recent session promotes it to the top
- Recent sessions are de-duplicated by username, host, and port
- Clear Recent Sessions command

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Run

```powershell
dotnet run
```

## Notes

This is SSH-only. Tera Term also supports serial, telnet, named pipes, file transfer protocols, TEK windows, and TTL macros; those are intentionally out of scope here. This package implements the SSH-client features most relevant to an SSH terminal application.


## Recent sessions

Recent SSH sessions are stored in the same `sessions.json` file as regular saved profiles. The `Recent Sessions` menu is a single top-level menu on the menu bar and is rebuilt whenever it opens. It displays up to 256 entries in newest-first order.

For convenience, recent entries currently store the same connection fields as the profile used to connect, including authentication fields. For higher-security deployments, replace password persistence with Windows Credential Manager or DPAPI-protected storage before distributing binaries.

## Security update: passwords are never saved

This version deliberately does not persist passwords or private-key passphrases.

Implementation details:

- `SessionProfile.Password` is marked with `[JsonIgnore]`.
- `SessionProfile.PrivateKeyPassphrase` is marked with `[JsonIgnore]`.
- `CloneForStorage()` clears secrets before saved profiles or recent sessions are stored.
- `AppSettings.Save()` calls `ClearAllSecrets()` before writing `sessions.json`.
- Loading an existing `sessions.json` also clears any legacy secret values in memory.
- The connect dialog may use the password/passphrase only for the current runtime connection.

Saved/recent sessions may contain only non-secret metadata such as name, host, port, username, auth mode, private key path, terminal type, keepalive, and display settings. The user-facing connection/profile dialogs no longer expose a fixed PTY size; the app derives rows and columns from the visible terminal window.

## Session management windows

This build adds two separate management windows:

- **File > Session Manager...** opens a dedicated saved-session manager for creating, editing, deleting, refreshing, and opening saved SSH profiles.
- **File > Session Status...** opens a live status window that displays open active tabs, disconnected/inactive tabs, inactive saved sessions, and inactive recent sessions. From this window you can activate an existing tab, connect to an inactive profile, disconnect an active tab, or close a tab.

## Terminal/curses improvements

The terminal display no longer uses a plain `RichTextBox` as the terminal screen. It now uses a lightweight terminal surface with ANSI/VT handling for cursor movement, screen/line clearing, insert/delete line and character operations, alternate-screen mode, OSC title escape skipping, and common xterm key sequences.

These changes are intended to make full-screen ncurses/curses applications such as `top`, `vim`, `nano`, and similar tools render and update correctly inside the SSH tab. The default terminal type is now `xterm-256color`, and the client resizes the SSH pseudo-terminal when the window or tab area changes.

## Full-screen SSH session window

Use **Window > Toggle Full Screen** or **F11** to make the SSH session area scale to the full screen. When the main window is resized or maximized, the terminal recalculates rows/columns and sends the new PTY size to the remote shell when supported by SSH.NET.


## Automatic terminal sizing

The new SSH connection and saved-session edit dialogs no longer ask for PTY rows or columns. The client calculates the terminal grid from the actual visible terminal surface before opening the SSH shell, then sends resize updates whenever the tab, main window, font, or full-screen state changes. This lets `vim`, `top`, and other full-screen terminal programs scale with the window instead of using a stale saved size.

## Keyboard / escape-sequence mapping

Use **Setup > Keyboard / key mappings...** to configure exactly what the client sends for Backspace, Delete, Page Up, Page Down, Home, End, and Insert.

Accepted values include readable forms such as `DEL`, `BS`, `ESC`, control-key notation such as `^H` and `^?`, and escaped byte forms such as `\x7f`, `\b`, `\e[3~`, `\x1b[5~`, `\r`, `\n`, `\t`, octal escapes, and Unicode escapes. The dialog shows a decoded byte/character preview before saving.

Defaults are:

- Backspace: `\x7f`
- Delete: `\x1b[3~`
- Page Up: `\x1b[5~`
- Page Down: `\x1b[6~`
- Home: `\x1b[H`
- End: `\x1b[F`
- Insert: `\x1b[2~`

## Latest terminal fixes

This revision moves the visible caret to the bottom of the active terminal cell instead of drawing it at the top edge of the row, which could make the cursor look like it belonged to the previous line. It also creates and resizes the remote PTY from the actual visible terminal grid, so full-screen programs such as `vim` and `top` receive the current rows and columns when the tab or full-screen window changes.

## Session logs and selectable terminal text

- Terminal screen text is now selectable with the mouse. Drag to select, double-click to select a word, then use **Edit > Copy** or **Ctrl+Insert**. **Edit > Select All** marks the visible screen before copying.
- Each SSH tab automatically creates an escaped session log under `C:\SshSessionsData\` using a sanitized session/host/timestamp file name.
- Log content escapes terminal control characters such as ESC, BEL, backspace, carriage return, and NUL so ANSI/curses output does not corrupt the log file.
- Use **File > Manage Session Log Files...** to view, open, open the folder, refresh, or delete saved logs from inside the client.
- **File > Delete session logs on exit** is checked by default. Leave it checked to remove `*.log` files from `C:\SshSessionsData\` when the application exits, or uncheck it to keep logs between runs.

## Terminal parser fixes in this build

- Escape/ANSI/OSC sequences are now parsed across SSH read boundaries, so split sequences such as bracketed paste mode (`ESC[?2004h` / `ESC[?2004l`) and window-title updates (`ESC]0;...BEL`) are consumed instead of being drawn as visible noise.
- Autowrap now follows VT-style delayed wrapping, which prevents an extra blank line after full-width output followed by CR/LF.
- Basic scroll-region handling (`ESC[top;bottom r`) was added for curses/ncurses applications such as `top` and `vim`, so full-screen updates are less likely to appear truncated.
- Additional cursor/screen operations used by curses apps are handled, including vertical positioning and erase-character commands.

## Session log cleanup/readability fix

This build writes readable session logs by stripping terminal control sequences before saving log text. Sequences such as bracketed-paste toggles, SGR color codes, and OSC window-title updates are no longer written as visible `\\x1b...` noise in the log file. Backspace is handled by removing the prior logged character where possible, and CR/LF pairs are normalized to a single line break.

## Compile fix note

This package includes a small WinForms compile fix for environments where `Timer` is ambiguous under implicit usings and `DropDownItems.Add(...)` is inferred as `ToolStripItem`. Timers are now fully qualified as `System.Windows.Forms.Timer`, and shortcut menu items are created as `ToolStripMenuItem` before assigning `ShortcutKeys`.

## Compile fix: SSH.NET PTY resize and TextFormatFlags

This package removes the direct compile-time dependency on `ShellStream.ResizeTerminal`, because some SSH.NET versions expose PTY window-size changes through a different method name such as `SendWindowChangeRequest`. The resize logic now uses reflection and tries the known SSH.NET method names at runtime while keeping the project buildable.

This package also removes `TextFormatFlags.PreserveClipping`, which is not available in all WinForms target/reference combinations.

## Disconnect input safety fix

This build adds null-safe disconnect teardown and terminal input guards. After a session is disconnected, key presses in that terminal are consumed locally and are no longer written to a disposed `ShellStream` or disposed `SshClient`.

Changes:
- `IsConnected` now treats disposed/tearing-down SSH objects as disconnected.
- `Disconnect()` cancels reads, stops forwarding/logging, disposes SSH objects, and then nulls the shell/client references.
- `Send()` catches disposed/closed stream cases and marks the tab disconnected instead of throwing.
- `KeyPress`, `KeyDown`, and paste handling ignore terminal input after disconnect.
- UI callbacks from the SSH reader are now disposed-control safe.

## Scrollback and terminal I/O stability update

This build adds an internal terminal scrollback buffer capped at 10,000 lines. Normal shell output is retained in scrollback and can be reviewed with the terminal scrollbar or mouse wheel. Alternate-screen applications such as `top` and `vim` continue to use the active terminal screen and do not pollute the shell scrollback.

Terminal input/output handling was also adjusted to reduce degradation during long sessions:

- SSH reads use a larger buffer.
- Output is coalesced before being appended to the terminal UI, reducing BeginInvoke/repaint pressure.
- The terminal paints full rows when no selection is active instead of drawing every character individually.
- The in-memory capture buffer is bounded; persistent session logs remain on disk under the configured session log folder.

## Menu layout update

- **File > Reconnect** is now placed immediately after **File > New SSH connection...**.
- **File > Disconnect** is now immediately after **Reconnect**.
- **Close Tab** and **Clone Current Tab** have been moved to the **Window** menu.


### Reconnect disposed-terminal timer fix

This build stops and disposes the terminal caret timer when a terminal surface is disposed, and all cursor/resize calculations now guard against disposed controls. Reconnect can replace the current tab without a stale timer calling `CreateGraphics()` on the old `TerminalSurface`.

