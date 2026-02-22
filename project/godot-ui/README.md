# SwarmAssistant Godot UI (Phase 10)

This project is a Godot Mono (C#) client that renders A2UI payloads received from AG-UI events exposed by `SwarmAssistant.Runtime`.

Rendering strategy:

- A2UI `text` maps to `res://scenes/components/A2TextComponent.tscn`
- A2UI `button` maps to `res://scenes/components/A2ButtonComponent.tscn`
- `Main.cs` polls `/ag-ui/recent` and applies `createSurface` / `updateDataModel` operations.
- Bi-directional AG-UI action loop:
- inline submit controls send `submit_task`
- snapshot button sends `request_snapshot`
- refresh button sends `refresh_surface`
- load memory button sends `load_memory`
- startup restore events (`agui.memory.bootstrap`) and runtime responses (`agui.action.task.submitted`, `agui.task.snapshot`, `agui.memory.tasks`) are reflected in the status/log panel.

## Prerequisites

- Godot Mono app at `/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app`
- Runtime running on `http://127.0.0.1:5080`

## Run Runtime

```bash
DOTNET_ENVIRONMENT=Local dotnet run --project /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/dotnet/src/SwarmAssistant.Runtime --no-launch-profile
```

## Run Godot (Windowed)

```bash
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --windowed --resolution 1280x720
```

## Export macOS App

Install export template once:

```bash
mkdir -p "$HOME/Library/Application Support/Godot/export_templates/4.6.1.stable.mono"
curl -L "https://github.com/godotengine/godot-builds/releases/download/4.6.1-stable/Godot_v4.6.1-stable_mono_export_templates.tpz" -o /tmp/Godot_v4.6.1-stable_mono_export_templates.tpz
unzip -o /tmp/Godot_v4.6.1-stable_mono_export_templates.tpz templates/macos.zip -d /tmp/godot_templates_extract
cp -f /tmp/godot_templates_extract/templates/macos.zip "$HOME/Library/Application Support/Godot/export_templates/4.6.1.stable.mono/macos.zip"
```

Export:

```bash
mkdir -p /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/build/godot-ui
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --headless --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --export-debug "macOS" /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/build/godot-ui/SwarmAssistantUI.app
```

Run exported app in windowed mode:

```bash
"/Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/build/godot-ui/SwarmAssistantUI.app/Contents/MacOS/SwarmAssistant UI" --windowed --resolution 1280x720
```

## Headless Smoke Check

```bash
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --headless --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --quit-after 120
```
