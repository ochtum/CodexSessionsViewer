<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English%20Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/Japanese%20Mode-red.svg" alt="Japanese"></a>
</p>

# Codex Sessions Viewer

Codex Sessions Viewer is a local viewer for listing, inspecting, and searching the history of Codex CLI, including sessions created with the Codex VS Code extension. You can also attach labels to content you want to keep and search for it later.

- This tool supports Japanese / English / Simplified Chinese / Traditional Chinese.
- Feedback and feature requests are welcome in the issue tracker.

## Screen Layout

### Main Screen

![image](/image/00001.jpg)

### Label Management Screen

![image](/image/00002.jpg)

### Keyboard Shortcut List Screen

![image](/image/00003.jpg)

If this project is useful to you, please consider giving it a star.

If you want to follow updates, watching the repository also helps.

## How to Launch

Download the `app-framework-dependent` folder from Releases, extract it, and run `run.cmd`. The executable and related assemblies are bundled under the `payload` folder.

Note: Running this tool requires the .NET 10 SDK or .NET 10 Runtime. If you are not sure whether either is installed, or if you prefer not to install them, download the `app-self-contained` folder instead.

Prefer `run.cmd` over launching `payload\CodexSessionsViewer.exe` directly. `run.cmd` keeps the working directory consistent and also keeps the console window title aligned with `CodexSessionsViewer`.

---

If you want to build from `src`, run the PowerShell script as shown below.

- Framework-dependent build (when .NET 10 SDK or .NET 10 Runtime is already installed)

```powershell
.\publish.ps1 -CleanOutput
```

- Self-contained build (when the .NET 10 SDK or .NET 10 Runtime may not be installed, or when you do not want to install them)

```powershell
.\publish.ps1 -SelfContained -CleanOutput
```

## Screen Features

- Header
  - Displays the product icon and the `Codex Sessions Viewer` title in the top-left corner.
  - Provides a language switcher in the top-right corner: `日本語` / `English` / `简体中文` / `繁體中文`, and a currency selector: `USD` / `JPY` / `CNY` / `TWD` / `HKD`.
  - Includes `Label Management`, `Cost Display`, `Show Meta`, `Shortcuts`, and a list toggle for mobile layouts.
  - Shows a "Today's usage" summary under the header so you can quickly see token, cost, and score metrics.
  - `Show Meta` is hidden by default. It lets you check `session root`, `path`, `cwd`, `time`, `source`, `events`, and `raw lines` for the active session.
- Left pane
  - Two tabs: `Session list` and `Label list`.
  - The session list shows a session preview, `source` label (`CLI` / `VS Code`), session labels, and `cwd`.
  - Shows the `sessions: filtered/total` count above the list.
  - Sort tabs: `Newest` / `Oldest` / `Last Updated`.
  - `Clear` resets the search conditions and filters in the left pane.
  - `Show Filters` / `Hide Filters` collapses or expands the search and filter area.
  - In portrait layout, `Hide List` / `Show List` in the header toggles the entire left pane.
- Search and filters in the left pane
  - Filters by `cwd` / keyword / `Start Date` / `End Date` / `Event Start Datetime` / `Event End Datetime` / `source` / session label / event label.
  - The time input for event datetime becomes enabled after you enter the corresponding date.
  - Search targets include not only `message`, but also `function_call.arguments`, `function_output.output`, and `agent_update.message`.
  - In the keyword field, text enclosed in double quotes is treated as a single phrase. Example: `"Working Space"`.
  - `cwd` / datetime / `source` / label conditions are always combined with AND.
  - The `AND/OR` switch applies only to the keyword field (`AND` = all keywords, `OR` = any keyword).
  - Filter settings persist across restarts.
- Left pane: label list
  - Shows labeled sessions and labeled events grouped by label.
  - Distinguishes kinds: `message` / `function_call` / `function_output` / `agent_update` / `token_usage`.
  - Clicking an item opens the target session or event.
- Right pane: chronological event view for the selected session
  - Shows a loading indicator during initial detail load, and an updating overlay during manual `Refresh`.
  - Detail toolbar contains `Display`, `Actions`, `Search`, and `Range Selection` sections.
  - Individual sections (`Actions`, `Search`, `Range Selection`) can be opened or closed independently.
  - Controls are disabled when no session is selected.
- Right pane: display & actions
  - Display conditions: `Only User Instructions` / `Only AI Responses` / `Only Each Input and Final Reply` / `Reverse Order` / `Only token usage` / `Label filter`.
  - `Cost Sort` lets you sort groups (a user message up to the next user message) by `total tokens` / `cost` / `score`. `Reverse Order` reverses ordering within groups.
  - `Refresh` reloads only the currently selected session.
  - `Clear` resets the right-pane state (display filters, detail keyword/filter/search, selection mode and selected events, anchor mode and anchor selection, any open label picker).
  - `Copy Resume Command` copies `codex resume <session_id>`.
  - `Copy Displayed Messages` copies all currently displayed `message` content.
  - Session label display and `Add Session Label` are supported.
  - Event-level label view/add/remove is supported.
  - Each `message` event has an individual `Copy` button.
- Right pane: search & selection
  - The detail keyword feature separates `Filter` and `Search`.
    - `Filter`: displays only events that contain the keyword.
    - `Search`: highlights matches and lets you move through them with `Prev` / `Next`.
    - Shows the hit count as `current / total`.
    - `Clear Search` clears the input field, filter state, and search state together.
  - Detail keywords use literal substring matching (no AND/OR parsing).
  - Search targets: `message` / `function_call` / `function_output` / `agent_update`.
  - Pressing `Enter` in the detail search field runs the search and blurs the field so `N` / `P` navigation is available.
  - `Event Start Datetime` / `Event End Datetime` narrow the event timeline in the right pane.
  - Event datetime filters use split `date + time` inputs; the time field becomes enabled after a date is entered.
  - `Selection Mode` allows checking events and copying them together; selections persist while filters change.
  - `Selected Events Only` toggles showing only selected events.
  - `Anchor Selection Mode` lets you pick a single event as an anchor, then show only before/after that anchor.
- Event display
  - `message` (`user` / `assistant` / `developer`)
  - `user` messages use a light blue background; execution context entries (e.g., `AGENTS.md`, `environment_context`) use a gray background.
  - `function_call` / `function_output` / `agent_update` / `token_usage` are also displayed.
- Label Management
  - Opens in a separate window via the `Label Management` button in the header.
  - Shares the main UI language setting.
  - Shared management for session labels and event labels.
  - Label colors accept `#hex` / `rgb(...)` / `oklch(...)` or selection from presets.
  - Candidate labels are shown with colors in the add-label UI.
- Cost Display
  - Opens a separate window from the `Cost Display` button in the header.
  - Shows aggregated usage and cost; the currency selector affects displayed cost values.

## Keyboard Shortcuts

Shortcuts do not run while an input field has focus. Press `Esc` to close the shortcut list or label picker, or to move focus away from the search input.

Tooltips for major buttons and toggles also show the corresponding keyboard shortcut.

| Key | Action |
| --- | --- |
| `F5` | Refresh the currently visible list or session detail |
| `Shift + F` | Toggle the left-pane filter visibility |
| `Shift + L` | Run `Clear` in the left pane |
| `/` | Focus the search input |
| `N` | Move to the next hit in detail search |
| `P` | Move to the previous hit in detail search |
| `M` | Toggle meta display for `path / cwd / time` |
| `[` | Open the previous session |
| `]` | Open the next session |
| `1` | Toggle `Only User Instructions` |
| `2` | Toggle `Only AI Responses` |
| `3` | Toggle `Only Each Input and Final Reply` |
| `4` | Toggle `Reverse Order` |
| `5` | Toggle `Only token usage` |
| `Shift + D` | Clear display conditions and action state in the right pane |
| `Shift + T` | Toggle detail actions visibility |
| `Shift + R` | Copy the session resume command |
| `Shift + C` | Copy displayed messages |
| `Shift + S` | Toggle selection mode on and off |
| `Shift + X` | Copy selected messages |
| `Shift + G` | Toggle anchor selection mode on and off |
| `Shift + H` | Clear the anchor |
| `,` | Show only content before the anchor |
| `.` | Show only content after the anchor |
| `Esc` | Close the shortcut list or add-label popup, and remove focus from the search input |

## Notes

- Label definitions and label assignments are stored in `.cache/label-store.json`.
- Display limits can be changed in `.cache/viewer-settings.json`.
- The default values are `session_list_max: 1000` and `session_events_max: 10000`.
- If the file does not exist, it is created automatically on first startup.
- The viewer is local-only and listens on `http://127.0.0.1:8765` by default. If the default port is already in use, it falls back to the next available port within the configured range.

---

## File Structure

```text
.
├── .gitignore                         # Root ignore settings
├── LICENSE                            # License
├── README.md                          # Japanese README
├── README_en.md                       # English README
├── publish.ps1                        # Publish script for distribution
├── .vscode/
│   ├── launch.json                    # VS Code debug launch settings
│   └── tasks.json                     # VS Code build task settings
├── image/
│   ├── 00001.jpg                      # Main screen sample image for README
│   ├── 00002.jpg                      # Label management screen sample image for README
│   └── 00003.jpg                      # Shortcut screen sample image for README
├── image-token-estimator/
│   ├── index.html                     # Main HTML for the image-input token estimation tool
│   ├── app.js                         # Estimation logic
│   └── styles.css                     # Styles for the estimation tool
└── src/
    ├── .cache/
    │   ├── label-store.json           # Storage for label definitions and assignments
    │   └── viewer-settings.json       # Display-count settings for session list and detail events
    ├── CodexSessionsViewer.sln        # Solution
    ├── CodexSessionsViewer.csproj     # ASP.NET Core / Blazor project definition
    ├── Program.cs                     # App startup, URL configuration, and API endpoints
    ├── appsettings.json               # Production settings
    ├── appsettings.Development.json   # Development settings
    ├── Components/
    │   ├── App.razor                  # HTML root and shared script loading
    │   ├── Routes.razor               # Routing definitions
    │   ├── _Imports.razor             # Shared Razor usings
    │   ├── Layout/
    │   │   ├── MainLayout.razor       # Shared layout
    │   │   ├── MainLayout.razor.css   # Styles for the shared layout
    │   │   ├── ReconnectModal.razor   # Reconnect modal UI
    │   │   ├── ReconnectModal.razor.css # Styles for the reconnect modal
    │   │   └── ReconnectModal.razor.js  # Script for the reconnect modal
    │   └── Pages/
    │       ├── Error.razor            # Error page
    │       ├── Home.razor             # Main screen
    │       ├── Labels.razor           # Label management screen
    │       └── NotFound.razor         # 404 page
    ├── Models/
    │   └── ViewerDtos.cs              # DTOs for API responses and requests
    ├── Properties/
    │   ├── AssemblyInfo.cs            # Version information
    │   └── launchSettings.json        # Local development launch settings
    ├── Services/
    │   ├── LabelStore.cs              # Label storage and validation logic
    │   └── ViewerService.cs           # Session discovery, loading, and search logic
    └── wwwroot/
        ├── app.css                    # Shared global styles
        ├── css/
        │   ├── labels.css             # Styles for the label management screen
        │   └── viewer.css             # Styles for the main screen
        ├── icons/
        │   └── codex-sessions-viewer.svg # App icon
        └── js/
            ├── labels.js              # Scripts for the label management screen
            └── viewer.js              # Scripts for the main screen
```

## This project is provided under the MIT License. See the LICENSE file for details.
