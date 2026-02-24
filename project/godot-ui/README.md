# SwarmAssistant Godot UI - Operator Control Surface

This project is a Godot Mono (C#) client that serves as an **Operator Control Surface** for task graph visibility and Human-In-The-Loop (HITL) actions. It renders A2UI payloads received from AG-UI events exposed by `SwarmAssistant.Runtime`.

## Features

### Task Graph Visualization
- **Task Hierarchy Tree**: Visual tree view of task relationships and dependencies
- **Active Tasks List**: Flat list of all active tasks with status indicators
- **Task Details Panel**: Shows selected task's status, role, progress, and metadata
- **Real-time Updates**: Near real-time updates during sub-task spawn/complete/fail

### HITL (Human-In-The-Loop) Controls
- **Approve**: Approve task execution or completion
- **Reject**: Reject task with feedback
- **Rework**: Request task rework
- **Pause/Resume**: Pause and resume task execution
- **Depth Control**: Set maximum task depth for swarm operations

### Status Widgets
- **Quality Indicator**: Shows task quality assessment
- **Retry Counter**: Displays retry attempts
- **Adapter Status**: Shows which adapter is handling the task

### Rich Component Support
- **Code Blocks**: Syntax-highlighted code display
- **Diff Views**: Color-coded diff visualization
- **Markdown Rendering**: Basic Markdown to BBCode conversion
- **JSON Viewer**: Pretty-printed, syntax-highlighted JSON
- **Status Badges**: Visual status indicators
- **Progress Bars**: Visual progress indication

## Prerequisites

- Godot Mono 4.x (set `GODOT_MONO` env var to the binary path, or ensure `godot` is on `PATH`)
- .NET 8.0 SDK
- Runtime running on `http://127.0.0.1:5080` (configurable via `AGUI_HTTP_URL` env var)

## Quick Start

### 1. Run the Runtime

```bash
# From repo root:
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

Or with local environment:
```bash
DOTNET_ENVIRONMENT=Local dotnet run --project project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

### 2. Run Godot UI (Development Mode)

```bash
# Set GODOT_MONO to your Godot Mono binary, e.g.:
# export GODOT_MONO="/path/to/Godot_mono.app/Contents/MacOS/Godot"
${GODOT_MONO:-godot} \
  --path project/godot-ui \
  --windowed --resolution 1280x800
```

## Building and Exporting

### Install Export Templates (First Time Only)

```bash
mkdir -p "$HOME/Library/Application Support/Godot/export_templates/4.6.1.stable.mono"
curl -L "https://github.com/godotengine/godot-builds/releases/download/4.6.1-stable/Godot_v4.6.1-stable_mono_export_templates.tpz" \
  -o /tmp/Godot_v4.6.1-stable_mono_export_templates.tpz
unzip -o /tmp/Godot_v4.6.1-stable_mono_export_templates.tpz templates/macos.zip -d /tmp/godot_templates_extract
cp -f /tmp/godot_templates_extract/templates/macos.zip "$HOME/Library/Application Support/Godot/export_templates/4.6.1.stable.mono/macos.zip"
```

### Export macOS App (Debug)

```bash
# From repo root:
mkdir -p build/godot-ui
${GODOT_MONO:-godot} \
  --headless --path project/godot-ui \
  --export-debug "macOS" \
  build/godot-ui/SwarmAssistantUI.app
```

### Export macOS App (Release)

```bash
# From repo root:
mkdir -p build/godot-ui
${GODOT_MONO:-godot} \
  --headless --path project/godot-ui \
  --export-release "macOS" \
  build/godot-ui/SwarmAssistantUI.app
```

## Running the Exported App

### Windowed Mode

```bash
# From repo root:
"build/godot-ui/SwarmAssistantUI.app/Contents/MacOS/SwarmAssistant UI" \
  --windowed --resolution 1280x800
```

### With Custom Runtime URL

```bash
export AGUI_HTTP_URL="http://127.0.0.1:5080"
"build/godot-ui/SwarmAssistantUI.app/Contents/MacOS/SwarmAssistant UI" \
  --windowed --resolution 1280x800
```

## Verification Steps

### 1. Headless Smoke Check

```bash
# From repo root:
${GODOT_MONO:-godot} \
  --headless --path project/godot-ui \
  --quit-after 120
```

Expected: Clean exit with no errors after 120 seconds.

### 2. UI Layout Verification

Launch the app and verify:
- [ ] Title shows "SwarmAssistant Operator Control Surface"
- [ ] Connection status indicator shows "● Disconnected" (red) initially
- [ ] Task hierarchy panel is visible on the left
- [ ] Task details panel is visible on the right
- [ ] HITL controls are visible (Approve, Reject, Rework, Pause, Resume)
- [ ] Status widgets are visible (Quality, Retries, Adapter)
- [ ] Event log is visible at the bottom

### 3. Connection Verification

With runtime running:
- [ ] Connection status changes to "● Connected" (green)
- [ ] Status label shows "Status: connected"
- [ ] Event log shows polling messages

### 4. Task Submission Verification

- [ ] Enter a task title in the "New Task" section
- [ ] Click "Submit Task"
- [ ] Verify task appears in the task list
- [ ] Verify task appears in the task hierarchy tree

### 5. HITL Actions Verification

- [ ] Select a task from the list or tree
- [ ] Verify HITL controls become enabled
- [ ] Click each HITL button and verify:
  - Action appears in event log
  - Backend receives the action (check runtime logs)
  - UI shows confirmation message

### 6. Task Details Verification

Select a running task and verify:
- [ ] Task ID is displayed
- [ ] Status is shown correctly
- [ ] Role is displayed (if available)
- [ ] Progress bar updates
- [ ] Quality status is shown
- [ ] Retry count is displayed
- [ ] Adapter name is shown

### 7. Stress Test

- [ ] Submit multiple tasks rapidly
- [ ] Select different tasks while others are updating
- [ ] Use HITL controls repeatedly
- [ ] Verify no crashes or UI freezes

## Architecture

### Main.cs
- Polls `/ag-ui/recent` for events
- Maintains task graph state (`TaskNodeState`)
- Handles HITL action sending to `/ag-ui/actions`
- Manages UI state and updates

### GenUiNodeFactory.cs
- Builds Godot nodes from A2UI JSON component definitions
- Supports containers, content, interactive elements, and status widgets
- Provides rich text, code, diff, markdown, and JSON rendering

### Event Handling
- `a2a.task.submitted`: New task created
- `agui.action.task.submitted`: Task submitted via UI
- `agui.runtime.snapshot`: Runtime status update
- `agui.task.snapshot`: Individual task update
- `agui.memory.tasks`: Memory-loaded tasks
- `agui.memory.bootstrap`: Memory bootstrap complete
- `agui.memory.bootstrap.failed`: Memory bootstrap error

## Component Types

### Containers
- `vbox` - Vertical box container
- `hbox` - Horizontal box container
- `panel` - Panel container with optional styling
- `margin` - Margin container
- `scroll` - Scroll container
- `grid` - Grid container
- `tab` - Tab container
- `split` - Split container

### Content
- `label` / `text` - Simple text label
- `rich_text` - BBCode-enabled rich text
- `code` - Code block with syntax highlighting
- `diff` - Color-coded diff view
- `markdown` - Markdown rendering
- `json` - Pretty-printed JSON with highlighting

### Interactive
- `button` - Action button
- `line_edit` - Single-line text input
- `text_edit` - Multi-line text editor
- `spin_box` - Numeric input with spinner
- `slider` - Horizontal or vertical slider

### Status
- `progress_bar` - Progress indicator
- `status_indicator` - Status dot with label
- `badge` - Colored status badge

## Troubleshooting

### Connection Issues
- Verify runtime is running on the expected port
- Check `AGUI_HTTP_URL` environment variable
- Check firewall settings

### Export Issues
- Verify export templates are installed correctly
- Check Godot version matches template version
- Review export_presets.cfg configuration

### UI Issues
- Check Godot console for errors
- Verify .NET assemblies are built
- Check that all script files compile without errors

## Development

### Building C# Scripts

```bash
# From repo root:
cd project/godot-ui
dotnet build
```

### Running Tests

```bash
# From repo root:
task test
```

## License

Part of the SwarmAssistant project.
