<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English%20Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/Japanese%20Mode-red.svg" alt="Japanese"></a>
</p>

# Codex Sessions Viewer

A local viewer for listing and inspecting `.jsonl` files under `~/.codex/sessions`.  
When running in WSL, if the WSL-side `~/.codex/sessions` is not found, it also auto-discovers the Windows-side location `C:\Users\<user>\.codex\sessions` (`/mnt/c/Users/<user>/.codex/sessions`).
If sessions exist on both the WSL side and the Windows side, it loads and lists both.

## Screen Layout

### Main Screen

![image](/image/00001.jpg)

### Label Management Screen

![image](/image/00002.jpg)

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

To use a sessions directory other than the default, set `SESSIONS_DIR`.  
In WSL, even if you pass a Windows-style path to `SESSIONS_DIR` (for example, `C:\Users\workuser\.codex\sessions`), it will be converted automatically.

```bash
SESSIONS_DIR=/path/to/sessions python3 viewer.py
```

To change the bind address, set `HOST`.

```bash
HOST=0.0.0.0 python3 viewer.py
```

## UI Features

- Left pane: session list, sorted newest first
  - Shows session `source` labels (`CLI` / `VS Code`) and session labels in the list
  - Shows a loading state during the initial load
  - `Reload` reloads the session list
    - During a manual `Reload`, the list shows an updating overlay and button state feedback
  - `Clear` resets the left-pane search conditions
  - `Hide` / `Show` collapses or expands the search filter area
  - In vertical layout, the header button `Hide List` / `Show List` can hide or show the entire left pane
- Top-left filters
  - Filter by `cwd` / date / keyword / `source` / session label / event label
  - Keyword search uses a SQLite full-text index
  - Search covers not only `message`, but also `function_call.arguments`, `function_output.output`, and `agent_update.message`
  - `cwd`, date, `source`, and label conditions are always evaluated with AND
  - The `AND/OR` switch applies only to the keyword field
    - `AND`: must include all space-separated keywords
    - `OR`: must include at least one space-separated keyword
- Right pane: chronological event view for the selected session
  - Shows a loading state during the first detail load, and an updating overlay during manual `Refresh`
  - The detail header shows the `source` label (`CLI` / `VS Code`)
  - The detail header uses a 3-row layout
    - Row 1: display filters, `Refresh`, and `Hide` / `Show` to collapse rows 2 and 3 together
    - Row 2: copy actions, label actions, and selection-copy actions
    - Row 3: keyword input, `Filter`, `Search`, `Previous`, `Next`, and `Keyword Clear`
  - Display options
    - `Show only user instructions`
    - `Show only AI responses`
    - `Reverse display order`
    - `event label: all` filter
  - Keyword search
    - `Filter`: shows only events that contain the keyword
    - `Search`: highlights matches and lets you move through them with `Previous` / `Next`
    - `Keyword Clear`: clears the input, filter state, and search state together
    - Matching is a literal substring match, not AND / OR parsing
    - Search targets include `message`, `function_call`, `function_output`, and `agent_update`
  - `Refresh` reloads only the currently selected session
  - `Copy Resume Command` copies `codex resume <session_id>`
  - `Copy Displayed Messages` copies all messages currently visible under the active display filters
  - Session label display and `Add Session Label`
  - Per-event label display / add / remove
  - Each `message` event has its own `Copy` button
  - `Selection Mode` lets you check individual `message` events and copy them together with `Copy Selected`
    - Even when filters are applied, already selected `message` events remain selected
  - `message` (`user` / `assistant` / `developer`)
  - `user` messages are shown with a light blue background, while execution context such as `AGENTS.md` and `environment_context` is shown with a gray background
  - `function_call` / `function_output`
  - `agent_update`
- Label Management
  - Opens in a separate window from the `Label Management` button in the upper-right
  - Manages session labels and event labels in one shared UI
  - Label colors can be entered directly as `#hex`, `rgb(...)`, or `oklch(...)`, or selected from color presets

## Notes

- The search index is stored in `.cache/search_index.sqlite3`, and only changed sessions are updated incrementally.
- To keep large logs manageable, the list is limited to `300` sessions and the event view is limited to `2000` events.
- The viewer listens on localhost only (`127.0.0.1`).

---

## AutoHotkey Shortcut Startup (Windows)

If you want to launch `scripts\windows\launch_viewer.bat` / `scripts\windows\stop_viewer.bat` with keyboard shortcuts, use AutoHotkey.

### 1. Install AutoHotkey

1. Open the official website:  
   [https://www.autohotkey.com/](https://www.autohotkey.com/)

2. Download and install **AutoHotkey v2**.  
   Note: v1 and v2 use different syntax, and this guide uses v2.

After installation, `.ahk` files become executable.

### 2. Create a Hotkey Script

Create a file named `CodexViewerHotkeys.ahk` in any location you like, such as the repository root or your Documents folder.

Use the following content, adjusting the paths for your environment:

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

Confirm that the configured key, for example `Win + P`, starts the viewer.

### 4. Enable It Automatically at Windows Startup

1. Press `Win + R`.
2. Enter the following and press Enter:

```text
shell:startup
```

3. Place `CodexViewerHotkeys.ahk` in the opened folder.

Now the hotkeys are enabled automatically when Windows starts.

### 5. If Administrator Privileges Are Required

If the batch files are expected to run with administrator privileges, change them like this:

```ahk
#p::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\launch_viewer.bat"'
#o::Run '*RunAs "C:\path\to\CodexSessionsViewer\scripts\windows\stop_viewer.bat"'
```

### 6. Additional Notes

- If you edit the `.ahk` file, right-click the AutoHotkey icon in the task tray and choose `Reload Script`.
- If both v1 and v2 are installed, make sure v2 is the default file association.

## This project is provided under the MIT License. See the LICENSE file for details.
