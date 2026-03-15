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

### Shortcut List Screen

![image](/image/00003.jpg)

## Directory Structure

```text
.
├─ viewer.py
├─ icons
│  ├─ codex-sessions-viewer.svg
│  ├─ claude-sessions-viewer.svg
│  └─ github-copilot-sessions-viewer.svg
├─ image
│  ├─ 00001.jpg
│  ├─ 00002.jpg
│  └─ 00003.jpg
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

- Header
  - Shows the product icon and the `Codex Sessions Viewer` title on the left
  - Provides a language switcher in the upper-right: `日本語` / `English` / `简体中文` / `繁體中文`
  - Includes `Label Management`, `Show meta`, `Shortcuts`, and the mobile list toggle
  - `Show meta` is hidden by default and reveals `session root`, `path`, `cwd`, `time`, `source`, `events`, and `raw lines`
- Left pane: session list, sorted newest first
  - Shows a session preview, `source` labels (`CLI` / `VS Code`), session labels, `cwd`, and `id`
  - Shows loading feedback during the initial load and manual `Reload`
  - `Clear` resets all left-pane search and filter conditions
  - `Show filters` / `Hide filters` collapses or expands the search and filter area
  - In vertical layout, the header button `Hide List` / `Show List` can hide or show the entire left pane
  - The filter area uses internal scrolling so it remains usable on smaller displays
- Left-pane search and filters
  - Filter by `cwd` / date / keyword / `source` / session label / event label
  - Keyword search uses a SQLite full-text index
  - Search covers not only `message`, but also `function_call.arguments`, `function_output.output`, and `agent_update.message`
  - `cwd`, date, `source`, and label conditions are always evaluated with AND
  - The `AND/OR` switch applies only to the keyword field
    - `AND`: must include all space-separated keywords
    - `OR`: must include at least one space-separated keyword
- Right pane: chronological event view for the selected session
  - Shows a loading state during the first detail load, and an updating overlay during manual `Refresh`
  - Uses a flat toolbar made up of `Display`, `Actions`, `Search`, and `Range`
  - `Show detail actions` / `Hide detail actions` toggles the `Actions`, `Search`, and `Range` sections together
  - Display, search, and range controls are disabled until a session is selected
- Right-pane display and actions
  - Display conditions: `Only user instructions`, `Only AI responses`, `Only each input and final reply`, `Reverse order`, and the compact `label` filter
  - `Refresh` reloads only the selected session
  - `Clear` resets the entire right-pane state
    - Display filters
    - Detail keyword input plus `Filter` / `Search` state
    - Selection mode and selected messages
    - Anchor mode, selected anchor, and before/after anchor filtering
    - The currently open label picker
  - `Copy Resume Command` copies `codex resume <session_id>`
  - `Copy Displayed Messages` copies all visible `message` events
  - Shows session labels and supports `Add Session Label`
  - Supports per-event label display / add / remove
  - Each `message` event has its own `Copy` button
- Right-pane search and selection
  - Detail keyword search separates `Filter` from `Search`
    - `Filter`: shows only events that contain the keyword
    - `Search`: highlights matches and lets you move through them with `Prev` / `Next`
    - `Clear search`: clears the input, filter state, and search state together
  - Matching is a literal substring match, not AND / OR parsing
  - Search targets include `message`, `function_call`, `function_output`, and `agent_update`
  - Pressing `Enter` in the detail keyword field runs the search and releases focus so you can keep navigating with `N` / `P`
  - `Selection Mode` lets you check individual `message` events and copy them together with `Copy Selected`
    - Already selected `message` events remain selected even when filters are applied
  - `Anchor Selection Mode` lets you choose one `message` event and filter the view to messages before or after that anchor
- Event rendering
  - `message` (`user` / `assistant` / `developer`)
  - `user` messages use a light blue background, while execution context such as `AGENTS.md` and `environment_context` uses a gray background
  - `function_call` / `function_output`
  - `agent_update`
- Label Management
  - Opens in a separate window from the upper-right `Label Management` button
  - Shares the same language setting as the main window
  - Manages session labels and event labels in one shared UI
  - Label colors can be entered directly as `#hex`, `rgb(...)`, or `oklch(...)`, or selected from color presets

## Keyboard Shortcuts

Shortcuts do not run while an input is focused. Press `Esc` to close the shortcut dialog or label picker, or to leave a search field.

| Key | Action |
| --- | --- |
| `F5` | Refresh the current list or session detail |
| `Shift + F` | Toggle the left-pane filters |
| `Shift + L` | Run `Clear` on the left pane |
| `/` | Focus the search input |
| `N` | Move to the next detail-search match |
| `P` | Move to the previous detail-search match |
| `M` | Toggle the `path / cwd / time` meta block |
| `[` | Open the previous session |
| `]` | Open the next session |
| `1` | Toggle `Only user instructions` |
| `2` | Toggle `Only AI responses` |
| `3` | Toggle `Only each input and final reply` |
| `4` | Toggle `Reverse order` |
| `Shift + D` | Clear right-pane filters and active modes |
| `Shift + T` | Toggle detail actions |
| `Shift + R` | Copy the session resume command |
| `Shift + C` | Copy displayed messages |
| `Shift + S` | Toggle selection mode |
| `Shift + X` | Copy selected messages |
| `Shift + G` | Toggle anchor mode |
| `Shift + H` | Clear the anchor |
| `,` | Show only events before the anchor |
| `.` | Show only events after the anchor |
| `Esc` | Close the shortcut dialog or label picker, and leave search fields |

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
