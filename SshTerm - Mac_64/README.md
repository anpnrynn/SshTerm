# MacSshClient C++/Objective-C++ Xcode Project

Native macOS SSH-only client built for Xcode 26 / macOS Tahoe 26.

Bundle identifier:

```text
com.github.anpnrynn.MacSshClient
```

The app is implemented in Objective-C++/C++ (`.mm`) using Cocoa/AppKit. It does not use Swift or SwiftUI.

## Features

- Native macOS `.app` target
- Explicit AppKit app lifecycle; no storyboard, no SwiftUI scene setup
- Explicit main menu creation
- Main GUI window created at launch
- Multi-tab SSH terminal
- Terminal-screen input: type directly in the terminal area
- PTY-based `/usr/bin/ssh`
- Uses `posix_spawn`, not `fork()`
- Recent Sessions menu stores up to 256 sessions
- Passwords and passphrases are never saved
- Copy, paste, font-size changes, disconnect, close tab

## Build

Open `MacSshClient.xcodeproj` in Xcode 26 or later, select the `MacSshClient` scheme, then Run.

Or build from Terminal on macOS:

```bash
xcodebuild -project MacSshClient.xcodeproj -scheme MacSshClient -configuration Debug build
```

## Important

Run the app from Xcode or launch the generated `.app` bundle. Do not run the raw executable directly.

If code signing prompts appear, set your local Team under:

`Target > Signing & Capabilities > Team`

## Security

Recent sessions store only:

- host
- port
- username
- last-used timestamp

Credentials are entered only through the SSH terminal and are not persisted by this app.
