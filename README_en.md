<p align="left">
  <a href="README_en.md"><img src="https://img.shields.io/badge/English%20Mode-blue.svg" alt="English"></a>
  <a href="README.md"><img src="https://img.shields.io/badge/Japanese%20Mode-red.svg" alt="Japanese"></a>
</p>

# Codex Sessions Viewer

Codex Sessions Viewer is a local viewer that lets you browse, inspect, and search the history of CodexCLI, including sessions created through the Codex VS Code extension. You can also attach labels to sessions or events you want to remember and find them again later.

- This tool supports Japanese / English / Simplified Chinese / Traditional Chinese.
- Feedback and feature requests are always welcome in the issue tracker.
- The first launch can feel heavy, but once the initial loading finishes, cached data makes the viewer run much faster.
  - I plan to improve startup speed further soon by introducing deferred loading.

## Screen Layout

### Main Screen

![image](/image/00001.jpg)

### Label Management Screen

![image](/image/00002.jpg)

### Keyboard Shortcut List Screen

![image](/image/00003.jpg)

If this project is useful to you, please consider starring it.

If you want to follow updates, watching the repository also helps.

## How to Launch

Download the `app-framework-dependent` folder from Releases, extract it, and run `run.cmd` inside it. The main executable, DLLs, and other runtime files are bundled in the `payload` folder.

Note: This tool requires the .NET 10 SDK or .NET 10 Runtime. If you are not sure whether either is installed, or if you do not want to install them, download the `app-self-contained` folder instead.

Using `run.cmd` is recommended over launching `payload\CodexSessionsViewer.exe` directly. `run.cmd` sets up the working directory correctly and also sets the console window title to `CodexSessionsViewer`.

---

If you want to build from `src`, run the following PowerShell script.

- Framework-dependent build (when .NET 10 SDK or .NET 10 Runtime is already installed)

```powershell
.\publish.ps1 -CleanOutput
```

- Self-contained build (when .NET 10 SDK or .NET 10 Runtime may not be installed, or when you do not want to install them)

```powershell
.\publish.ps1 -SelfContained -CleanOutput
```

## Screen Features

- Header
  - Provides a language switcher in the top-right corner (`日本語` / `English` / `简体中文` / `繁體中文`) and a currency switcher (`USD` / `JPY` / `CNY` / `TWD` / `HKD`)
  - Includes `Label Management`, `Cost Display`, `Meta Display`, `Shortcuts`, and a list toggle for mobile layouts
  - Shows a "Today's usage" summary below the header so you can quickly check token, cost, and score values
  - `Meta Display` is hidden by default. It lets you inspect `session root`, `path`, `cwd`, `time`, `source`, `events`, and `raw lines` for the selected session
- Left pane
  - Two tabs: `Session List` and `Label List`
  - The session list shows a session preview, the `source` label (`CLI` / `VS Code`), session labels, and `cwd`
  - Displays the `sessions: filtered/total` count above the list
  - Lets you switch sort order with `Newest`, `Oldest`, and `Last Updated`
  - `Clear` resets the search conditions and filters in the left pane
  - `Show Filters` / `Hide Filters` collapses or expands the search and filter area
  - In portrait layout, `Hide List` / `Show List` in the top-right header toggles the entire left pane
- Search and filters in the left pane
  - Filter by `cwd` / keyword / `Start Date` / `End Date` / `Event Start Datetime` / `Event End Datetime` / `source` / session label / event label
  - Keyword search targets not only `message`, but also `function_call.arguments`, `function_output.output`, and `agent_update.message`
  - In the keyword field, text wrapped in double quotes is treated as a single phrase
    - Example: `"Working Space"` is searched as one phrase
  - `cwd` / datetime / `source` / label conditions are always combined with AND
  - The `AND/OR` switch applies only to the keyword field
    - `AND`: matches entries containing all space-separated keywords
    - `OR`: matches entries containing any of the space-separated keywords
  - The time field for event datetime becomes enabled after you enter the corresponding date
  - Filter settings are preserved the next time you launch the app
- Left pane label list
  - Groups labeled sessions and labeled events by label
  - Distinguishes `message`, `function_call`, `function_output`, `agent_update`, and `token_usage`
  - Clicking an item jumps to the target session or event
- Right pane: chronological event view for the selected session
  - Shows a loading indicator during the first detail load, and an updating overlay during manual `Refresh`
  - The detail toolbar is organized into `Display`, `Actions`, `Search`, and `Range Selection`
  - `Detail Actions`, `Search`, and `Range Selection` can each be expanded or collapsed independently
  - Display, search, and range-selection controls are disabled when no session is selected
- Right pane display and actions
  - Display conditions: `Only User Instructions` / `Only AI Responses` / `Only Each Input and Final Reply` / `Reverse Order` / `Only token usage` / label filter
  - `Cost Sort` lets you sort user-message groups by `Total Tokens`, `Cost`, or `Score`
  - `Refresh` reloads only the currently selected session
  - `Clear` resets the entire right-pane state
    - Display filters
    - Detail keyword input, `Filter`, and `Search` states
    - Selection mode and selected events
    - Anchor selection mode, anchor position, and before/after-anchor display state
    - Any open label picker
  - `Copy Resume Command` copies `codex resume <session_id>`
  - `Copy Displayed Messages` copies all currently displayed `message` entries at once
  - Shows session labels and supports `Add Session Label`
  - Supports showing, adding, and removing labels for each event
  - Each `message` event has its own `Copy` button
- Right pane search and selection
  - The detail keyword feature separates `Filter` and `Search`
    - `Filter`: shows only events that contain the keyword
    - `Search`: highlights matches and lets you move with `Prev` / `Next`
    - Displays hit counts as `current / total`
    - `Clear Search`: clears the input field, filter state, and search state together
  - Detail keywords use literal substring matching, not AND / OR parsing
  - Search targets are `message`, `function_call`, `function_output`, and `agent_update`
  - Pressing `Enter` in the search field starts the search, then removes focus so you can move with `N` / `P`
  - `Event Start Datetime` / `Event End Datetime` can narrow the event timeline shown in the right pane
  - Event datetime filters in the right pane also use split `date + time` inputs, and the time field becomes enabled after a date is entered
  - In `Selection Mode`, you can check events and copy them together with `Copy Selected`, and already selected events are preserved even while filters are active
  - `Selected Events Only` narrows the view to only selected events
  - In `Anchor Selection Mode`, you can choose a single `message` and narrow the view with `Show After Anchor Only` / `Show Before Anchor Only`
- Event display
  - `message` (`user` / `assistant` / `developer`)
  - `user` uses a light blue background, while execution-context entries such as `AGENTS.md` and `environment_context` use a gray background
  - `function_call` / `function_output`
  - `agent_update`
  - `token_usage`
- Label Management
  - Opens in a separate window from the `Label Management` button in the top-right corner
  - Shares the same language setting as the main screen
  - Manages session labels and event labels in one place
  - Label colors can be entered directly as `#hex`, `rgb(...)`, or `oklch(...)`, or selected from color presets
  - Label candidates remain colorized in the add-label UI
- Cost Display
  - Opens in a separate window from the `Cost Display` button in the top-right corner
  - Lets you review usage totals while switching cost display based on the selected currency

## Keyboard Shortcuts

Shortcuts do not run while an input field has focus. Press `Esc` to close the shortcut list or label picker, or to move focus away from a search field.

Tooltips for major buttons and toggles also show their corresponding shortcut keys.

| Key | Action |
| --- | --- |
| `F5` | Refresh the currently displayed list or session details |
| `Shift + F` | Toggle the left-pane filter area |
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
| `Shift + C` | Copy the displayed messages |
| `Shift + S` | Toggle selection mode on and off |
| `Shift + X` | Copy selected messages |
| `Shift + G` | Toggle anchor selection mode on and off |
| `Shift + H` | Clear the anchor |
| `,` | Show only content before the anchor |
| `.` | Show only content after the anchor |
| `Esc` | Close the shortcut list or add-label popup, and remove focus from the search field |

## Notes

- Label definitions and label assignments are stored in `.cache/label-store.json`.
- Display limits can be changed in `.cache/viewer-settings.json`.
- The default values are `session_list_max: 1000`, `session_list_initial_load_count: 50`, and `session_events_max: 10000`.
- If the file does not exist, it is created automatically on first launch, and missing setting keys are filled in with their defaults.
- The viewer is local-only and listens on `http://127.0.0.1:8765` by default. If that port is already in use, it falls back to the next available port within the configured range.

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
│   ├── 00001.jpg                      # Main screen sample image used in the README
│   ├── 00002.jpg                      # Label management screen sample image used in the README
│   └── 00003.jpg                      # Shortcut screen sample image used in the README
├── image-token-estimator/
│   ├── index.html                     # Main HTML for the image-input token estimation tool
│   ├── app.js                         # Estimation logic
│   └── styles.css                     # Styles for the estimation tool
└── src/
    ├── .cache/
    │   ├── label-store.json           # Storage for label definitions and assignments
    │   └── viewer-settings.json       # Settings for list size, initial staged-load size, and detail event count
    ├── CodexSessionsViewer.sln        # Solution
    ├── CodexSessionsViewer.csproj     # ASP.NET Core / Blazor project definition
    ├── Program.cs                     # App startup, URL configuration, and API endpoint definitions
    ├── appsettings.json               # Production settings
    ├── appsettings.Development.json   # Development settings
    ├── Components/
    │   ├── App.razor                  # HTML root and shared script loading
    │   ├── Routes.razor               # Routing definitions
    │   ├── _Imports.razor             # Shared Razor using directives
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
