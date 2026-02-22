# SwarmAssistant Godot UI (Phase 6)

This project is a Godot Mono (C#) client that renders A2UI payloads received from the AG-UI SSE stream exposed by `SwarmAssistant.Runtime`.

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

## Headless Smoke Check

```bash
/Users/apprenticegc/Work/lunar-horse/tools/Godot_mono.app/Contents/MacOS/Godot --headless --path /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/godot-ui --quit-after 120
```

By default, networking is disabled in headless mode to keep CI/smoke checks stable. Windowed mode keeps AG-UI networking enabled.
