<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/日本語 モード-red.svg" alt="日本語"></a>
</p>

# Codex Sessions Viewer

A local viewer for browsing and inspecting `.jsonl` files under `~/.codex/sessions` in a WSL environment.

## Directory Structure

```text
.
├─ viewer.py
└─ scripts
   ├─ windows
   │  ├─ launch_viewer.bat
   │  └─ stop_viewer.bat
   ├─ wsl
   │  └─ launch_viewer_wsl.sh
   └─ registry
      ├─ add_wsl_context_menu.reg
      └─ remove_wsl_context_menu.reg
```

## How to Start

### Start Directly from WSL

```bash
python3 viewer.py
```

After startup, open the following URL in your browser:

```text
http://127.0.0.1:8765
```

### One-Click Start from Windows (Batch)

Running `scripts\windows\launch_viewer.bat` starts `python3 viewer.py` on the WSL side and automatically opens `http://127.0.0.1:8765` in your default browser.

To stop it, run `scripts\windows\stop_viewer.bat`.

`launch_viewer.bat` waits for startup and then opens the browser. If startup fails, it shows diagnostic information.

## Registry Scripts

To register the WSL context menu:

- `scripts\registry\add_wsl_context_menu.reg`

To unregister it:

- `scripts\registry\remove_wsl_context_menu.reg`

## Options

To use a non-default sessions directory, set `SESSIONS_DIR`.

```bash
SESSIONS_DIR=/path/to/sessions python3 viewer.py
```

To change the bind address, set `HOST`.

```bash
HOST=0.0.0.0 python3 viewer.py
```

## UI Features

- Left pane: session list (newest first)
- Top-left filters: narrow down by path and first user input
- Search is partial match and also targets the first message set in each session
- `cwd` / date-time / keyword are always combined with AND
- `AND/OR` switch applies only within the keyword field
  - `AND`: must include all space-separated keywords
  - `OR`: must include at least one space-separated keyword
- Right pane: timeline of events for the selected session
  - `message` (`user` / `assistant` / `developer`)
  - `user` messages are shown with a light-blue background; execution context such as `AGENTS.md` and `environment_context` is shown in gray
  - `function_call` / `function_output`
  - `agent_update`

## Notes

- For large logs, the list is capped at `300` sessions and events are capped at `2000`.
- The viewer listens only on localhost (`127.0.0.1`).

---

## AutoHotkey Shortcut Startup (Windows)

If you want to launch `scripts\windows\launch_viewer.bat` / `scripts\windows\stop_viewer.bat` using keyboard shortcuts, use AutoHotkey.

### 1. Install AutoHotkey

1. Open the official website:  
   [https://www.autohotkey.com/](https://www.autohotkey.com/)
2. Download and install **AutoHotkey v2**.  
   Note: v1 and v2 use different syntax, and this guide uses v2.

After installation, `.ahk` files become executable.

### 2. Create a Hotkey Script

Create a file named `CodexViewerHotkeys.ahk` in any location (for example, the repository root or your Documents folder).

Use the following content (adjust paths for your environment):

```ahk
#SingleInstance Force

; Start with Win + P
#p::Run "C:\path\to\CodexSessionsViewer\scripts\windows\launch_viewer.bat"

; Stop with Win + O
#o::Run "C:\path\to\CodexSessionsViewer\scripts\windows\stop_viewer.bat"
```

### Meaning of Key Symbols

| Symbol | Meaning |
| --- | --- |
| `#` | Win key |
| `^` | Ctrl |
| `!` | Alt |
| `+` | Shift |

Example: `^!v` means `Ctrl + Alt + V`.

### 3. Verify Behavior

Double-click the `.ahk` file you created.

If the AutoHotkey icon appears in the task tray, it is active.

Confirm that the configured key (for example, `Win + P`) starts the viewer.

### 4. Enable Automatically at Windows Startup

1. Press `Win + R`.
2. Enter the following and press Enter:

```text
shell:startup
```

3. Place `CodexViewerHotkeys.ahk` in the opened folder.

Now the hotkeys are enabled automatically when Windows starts.

### 5. If Administrator Privileges Are Required

If the batch scripts must be run as administrator, change them as follows:

```ahk
#p::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\launch_viewer.bat"'
#o::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\stop_viewer.bat"'
```

### 6. Additional Notes

- If you edit the `.ahk` file, right-click the AutoHotkey icon in the task tray and click "Reload Script" to reload it.
- If both v1 and v2 are installed, set v2 as the default file association.

## ❗This project is licensed under the MIT License, see the LICENSE file for details