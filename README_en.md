<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English%20Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/Japanese%20Mode-red.svg" alt="Japanese"></a>
</p>

# Codex Sessions Viewer

Codex Sessions Viewer is a local viewer for browsing, inspecting, and searching Codex CLI history, including sessions created from the Codex VS Code extension. You can also attach labels to important content and find it again later.

- This tool supports Japanese, English, Simplified Chinese, and Traditional Chinese.
- Feedback and feature requests are welcome via issues.

## Screen Layout

### Main Screen

![image](/image/00001.jpg)

### Label Management Screen

![image](/image/00002.jpg)

### Shortcut List Screen

![image](/image/00003.jpg)

If this project is useful, consider giving it a star.

If you want to follow updates, watching the repository also helps.

## How to Start

Download the `app-framework-dependent` folder from Releases, extract it, and run `CodexSessionsViewer.exe`.

Note: Running this build requires the .NET 10 SDK or the .NET 10 Runtime. If you are not sure whether it is installed, or if you do not want to install it, download the `app-self-contained` folder instead.

---

If you want to build from `src`, run the PowerShell script as shown below.

- Framework-dependent build
  - Use this if the .NET 10 SDK or the .NET 10 Runtime is already installed.

```powershell
.\publish.ps1 -CleanOutput
```

- Self-contained build
  - Use this if the .NET 10 SDK or the .NET 10 Runtime may not be installed, or if you do not want to install it.

```powershell
.\publish.ps1 -SelfContained -CleanOutput
```

## UI Features

- Header
  - Shows the product icon and the `Codex Sessions Viewer` title on the upper left.
  - Provides a language switcher in the upper right: `日本語` / `English` / `简体中文` / `繁體中文`.
  - Includes `Label Management`, `Show Meta`, `Shortcuts`, and the mobile session-list toggle.
  - `Show Meta` is hidden by default and reveals `session root`, `path`, `cwd`, `time`, `source`, `events`, and `raw lines`.
- Left pane: session list
  - Shows a session preview, `source` labels (`CLI` / `VS Code`), session labels, and `cwd`.
  - Shows the `sessions: filtered/total` count above the list.
  - Lets you switch the sort order with `Newest`, `Oldest`, and `Last Updated`.
  - `Clear` resets the left-pane search conditions and filters.
  - `Show Filters` / `Hide Filters` collapses or expands the search and filter area.
  - In vertical layout, `Hide List` / `Show List` in the header can hide or show the entire left pane.
- Left-pane search and filters
  - Filter by `cwd`, `Start Date`, `End Date`, `Event Start Datetime`, `Event End Datetime`, keyword, `source`, session label, and event label.
  - The time input for event datetime becomes enabled after the corresponding date is entered.
  - Search covers not only `message`, but also `function_call.arguments`, `function_output.output`, and `agent_update.message`.
  - In the keyword field, text enclosed in double quotes is treated as a single phrase.
  - Example: `"Working Space"` is searched as one term.
  - `cwd`, datetime, `source`, and label conditions are always evaluated with AND.
  - The `AND/OR` switch applies only to the keyword field.
  - `AND`: matches sessions that contain all space-separated keywords.
  - `OR`: matches sessions that contain any of the space-separated keywords.
- Right pane: chronological event view for the selected session
  - Shows a loading state during the initial detail load, and an updating overlay during manual `Refresh`.
  - Uses a flat toolbar with `Display`, `Actions`, `Search`, and `Range`.
  - `Show Detail Actions` / `Hide Detail Actions` toggles the `Actions`, `Search`, and `Range` sections together.
  - Display, search, and range controls are disabled until a session is selected.
- Right-pane display and actions
  - Display conditions: `Only User Instructions`, `Only AI Responses`, `Only Each Input and Final Reply`, `Reverse Order`, and `label`.
  - `Refresh` reloads only the selected session.
  - `Clear` resets the entire right-pane state.
  - This reset includes display filters.
  - This reset includes the detail keyword input and the `Filter` / `Search` state.
  - This reset includes selection mode and selected messages.
  - This reset includes anchor selection mode, the selected anchor, and before/after anchor filtering.
  - This reset includes any open label picker.
  - `Copy Resume Command` copies `codex resume <session_id>`.
  - `Copy Displayed Messages` copies the currently displayed `message` content.
  - Supports session-label display and `Add Session Label`.
  - Supports per-event label display, add, and remove.
  - Each `message` event has its own `Copy` button.
- Right-pane search and selection
  - Detail keyword handling separates `Filter` from `Search`.
  - `Filter`: shows only events that contain the keyword.
  - `Search`: highlights matches and lets you move through them with `Prev` / `Next`.
  - Displays the hit count as `current / total`.
  - `Clear Search`: clears the input, filter state, and search state together.
  - Matching is a literal substring match, not AND / OR parsing.
  - Search targets include `message`, `function_call`, `function_output`, and `agent_update`.
  - Pressing `Enter` in the detail keyword field runs the search and then releases focus so you can move with `N` / `P`.
  - `Event Start Datetime` / `Event End Datetime` can narrow the event timeline shown in the right pane.
  - Right-pane event datetime filters also use split `date + time` inputs, and the time field becomes enabled after a date is entered.
  - `Selection Mode` lets you check individual `message` items and copy them together with `Copy Selected`.
  - Already selected `message` items remain selected even when filters are applied.
  - `Anchor Selection Mode` lets you choose a single `message` and filter the view to messages before or after that anchor.
- Event rendering
  - `message` (`user` / `assistant` / `developer`)
  - `user` messages use a light blue background, while execution context such as `AGENTS.md` and `environment_context` uses a gray background.
  - `function_call` / `function_output`
  - `agent_update`
- Label Management
  - Opens in a separate window from the `Label Management` button in the upper right.
  - Shares the same language setting as the main window.
  - Manages session labels and event labels in one shared UI.
  - Label colors can be entered directly as `#hex`, `rgb(...)`, or `oklch(...)`, or selected from color presets.
  - Add-label UI elements keep the candidates colorized for easier recognition.

## Keyboard Shortcuts

Shortcuts do not run while an input field is focused. Press `Esc` to close the shortcut list or a label picker, or to move focus out of a search field.

Major buttons and toggles also show their shortcut keys in tooltips.

| Key | Action |
| --- | --- |
| `F5` | Refresh the currently visible list or the selected session detail |
| `Shift + F` | Toggle the left-pane filter visibility |
| `Shift + L` | Run `Clear` on the left pane |
| `/` | Focus the search input |
| `N` | Move to the next detail-search hit |
| `P` | Move to the previous detail-search hit |
| `M` | Toggle the `path / cwd / time` meta display |
| `[` | Open the previous session |
| `]` | Open the next session |
| `1` | Toggle `Only User Instructions` |
| `2` | Toggle `Only AI Responses` |
| `3` | Toggle `Only Each Input and Final Reply` |
| `4` | Toggle `Reverse Order` |
| `Shift + D` | Clear right-pane display conditions and action state |
| `Shift + T` | Toggle detail actions visibility |
| `Shift + R` | Copy the session resume command |
| `Shift + C` | Copy displayed messages |
| `Shift + S` | Toggle selection mode |
| `Shift + X` | Copy selected messages |
| `Shift + G` | Toggle anchor selection mode |
| `Shift + H` | Clear the anchor |
| `,` | Show only messages before the anchor |
| `.` | Show only messages after the anchor |
| `Esc` | Close the shortcut list or add-label popup, and move focus out of the search field |

## Notes

- Label definitions and label assignments are stored in `.cache/label-store.json`.
- To keep large logs manageable, the list is limited to `300` sessions and the event view is limited to `2000` events.
- The viewer is local-only. By default it listens on `http://127.0.0.1:8765`. If that port is already in use, it falls back to the next available port within the configured range.

---

## Directory Structure

```text
.
── .vscode
   │  ├─ launch.json
   │  └─ tasks.json
── src
   ├─ .cache
   │  └─ label-store.json
   ├─ Components
   │  ├─ App.razor
   │  ├─ Routes.razor
   │  ├─ _Imports.razor
   │  ├─ Layout
   │  │  ├─ MainLayout.razor
   │  │  ├─ MainLayout.razor.css
   │  │  ├─ ReconnectModal.razor
   │  │  ├─ ReconnectModal.razor.css
   │  │  └─ ReconnectModal.razor.js
   │  └─ Pages
   │     ├─ Error.razor
   │     ├─ Home.razor
   │     ├─ Labels.razor
   │     └─ NotFound.razor
   ├─ Models
   │  └─ ViewerDtos.cs
   ├─ Properties
   │  └─ launchSettings.json
   ├─ Services
   │  ├─ LabelStore.cs
   │  └─ ViewerService.cs
   ├─ wwwroot
   │  ├─ app.css
   │  ├─ css
   │  │  ├─ labels.css
   │  │  └─ viewer.css
   │  ├─ icons
   │  │  └─ codex-sessions-viewer.svg
   │  └─ js
   │     ├─ labels.js
   │     └─ viewer.js
   ├─ appsettings.Development.json
   ├─ appsettings.json
   ├─ CodexSessionsViewer.csproj
   ├─ CodexSessionsViewer.sln
   └─ Program.cs
```

## This project is provided under the MIT License. See the LICENSE file for details.
