using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;

public partial class Main : Control
{
    [Export] public string AgUiRecentUrl { get; set; } = "http://127.0.0.1:5080/ag-ui/recent";
    [Export] public string AgUiActionsUrl { get; set; } = "http://127.0.0.1:5080/ag-ui/actions";
    [Export] public float PollIntervalSeconds { get; set; } = 0.75f;
    [Export] public int RecentEventCount { get; set; } = 100;

    private readonly PackedScene _textComponentScene = GD.Load<PackedScene>("res://scenes/components/A2TextComponent.tscn");
    private readonly PackedScene _buttonComponentScene = GD.Load<PackedScene>("res://scenes/components/A2ButtonComponent.tscn");

    private HttpRequest? _recentRequest;
    private HttpRequest? _actionRequest;
    private Timer? _pollTimer;
    private bool _recentInFlight;
    private bool _actionInFlight;

    private Label? _titleLabel;
    private Label? _statusLabel;
    private LineEdit? _taskTitleInput;
    private LineEdit? _taskDescriptionInput;
    private VBoxContainer? _componentContainer;
    private RichTextLabel? _logOutput;

    private long _lastSequence;
    private string? _activeTaskId;

    public override void _Ready()
    {
        DisplayServer.WindowSetMinSize(new Vector2I(960, 640));
        BuildLayout();
        SetupNetworking();
        TriggerPoll();
    }

    private void BuildLayout()
    {
        var root = new MarginContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.OffsetLeft = 16;
        root.OffsetTop = 16;
        root.OffsetRight = -16;
        root.OffsetBottom = -16;
        AddChild(root);

        var layout = new VBoxContainer();
        layout.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        layout.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(layout);

        _titleLabel = new Label
        {
            Text = "SwarmAssistant UI",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        layout.AddChild(_titleLabel);

        _statusLabel = new Label { Text = "Connecting..." };
        layout.AddChild(_statusLabel);

        var submitRow = new HFlowContainer();
        submitRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        layout.AddChild(submitRow);

        _taskTitleInput = new LineEdit
        {
            PlaceholderText = "Task title",
            CustomMinimumSize = new Vector2(240, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        submitRow.AddChild(_taskTitleInput);

        _taskDescriptionInput = new LineEdit
        {
            PlaceholderText = "Task description",
            CustomMinimumSize = new Vector2(320, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        submitRow.AddChild(_taskDescriptionInput);

        var submitButton = new Button { Text = "Submit Task" };
        submitButton.Pressed += OnSubmitTaskPressed;
        submitRow.AddChild(submitButton);

        var actionRow = new HFlowContainer();
        actionRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        layout.AddChild(actionRow);

        var snapshotButton = new Button { Text = "Request Snapshot" };
        snapshotButton.Pressed += OnRequestSnapshotPressed;
        actionRow.AddChild(snapshotButton);

        var refreshButton = new Button { Text = "Refresh Surface" };
        refreshButton.Pressed += OnRefreshSurfacePressed;
        actionRow.AddChild(refreshButton);

        var loadMemoryButton = new Button { Text = "Load Memory" };
        loadMemoryButton.Pressed += OnLoadMemoryPressed;
        actionRow.AddChild(loadMemoryButton);

        _componentContainer = new VBoxContainer();
        _componentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _componentContainer.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        layout.AddChild(_componentContainer);

        _logOutput = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 280),
            ScrollActive = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        layout.AddChild(_logOutput);
    }

    private void SetupNetworking()
    {
        _recentRequest = new HttpRequest();
        AddChild(_recentRequest);
        _recentRequest.RequestCompleted += OnRecentRequestCompleted;

        _actionRequest = new HttpRequest();
        AddChild(_actionRequest);
        _actionRequest.RequestCompleted += OnActionRequestCompleted;

        _pollTimer = new Timer
        {
            WaitTime = Math.Max(0.2, PollIntervalSeconds),
            Autostart = true,
            OneShot = false
        };
        AddChild(_pollTimer);
        _pollTimer.Timeout += TriggerPoll;
    }

    private void TriggerPoll()
    {
        if (_recentInFlight || _recentRequest is null)
        {
            return;
        }

        _recentInFlight = true;
        var url = $"{AgUiRecentUrl}?count={Math.Clamp(RecentEventCount, 10, 500)}";
        var error = _recentRequest.Request(url);
        if (error != Error.Ok)
        {
            _recentInFlight = false;
            AppendLog($"[error] Poll request failed: {error}");
        }
    }

    private void OnRecentRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _recentInFlight = false;
        if (result != (long)HttpRequest.Result.Success || responseCode < 200 || responseCode > 299)
        {
            _statusLabel!.Text = "Status: disconnected";
            AppendLog($"[warn] Poll failed result={result} http={responseCode}");
            return;
        }

        _statusLabel!.Text = "Status: connected";

        try
        {
            var payload = Encoding.UTF8.GetString(body);
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var envelope in document.RootElement.EnumerateArray())
            {
                if (!envelope.TryGetProperty("sequence", out var sequenceElement) ||
                    sequenceElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var sequence = sequenceElement.GetInt64();
                if (sequence <= _lastSequence)
                {
                    continue;
                }

                _lastSequence = sequence;
                ApplyEnvelope(envelope);
            }
        }
        catch (Exception exception)
        {
            AppendLog($"[error] Invalid poll payload: {exception.Message}");
        }
    }

    private void ApplyEnvelope(JsonElement envelope)
    {
        var eventType = envelope.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "unknown"
            : "unknown";
        AppendLog($"[{eventType}] seq={_lastSequence}");

        if (envelope.TryGetProperty("taskId", out var taskIdElement) &&
            taskIdElement.ValueKind == JsonValueKind.String)
        {
            _activeTaskId = taskIdElement.GetString();
        }

        if (!envelope.TryGetProperty("payload", out var payload))
        {
            return;
        }

        ApplyEventPayload(eventType, payload);

        if (!payload.TryGetProperty("a2ui", out var a2ui))
        {
            return;
        }

        if (!a2ui.TryGetProperty("operation", out var operationElement) ||
            operationElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var operation = operationElement.GetString() ?? string.Empty;
        switch (operation)
        {
            case "createSurface":
                RenderSurface(a2ui);
                break;
            case "updateDataModel":
                ApplyDataModelPatch(a2ui);
                break;
        }
    }

    private void ApplyEventPayload(string eventType, JsonElement payload)
    {
        switch (eventType)
        {
            case "a2a.task.submitted":
            case "agui.action.task.submitted":
                if (payload.TryGetProperty("taskId", out var submittedTaskId) &&
                    submittedTaskId.ValueKind == JsonValueKind.String)
                {
                    _activeTaskId = submittedTaskId.GetString();
                    _statusLabel!.Text = $"Status: task submitted ({_activeTaskId})";
                }
                break;

            case "agui.runtime.snapshot":
                var totalTasks = payload.TryGetProperty("totalTasks", out var totalElement) &&
                                 totalElement.ValueKind == JsonValueKind.Number
                    ? totalElement.GetInt32()
                    : 0;

                var statuses = "n/a";
                if (payload.TryGetProperty("statusCounts", out var countsElement) &&
                    countsElement.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    foreach (var property in countsElement.EnumerateObject())
                    {
                        parts.Add($"{property.Name}:{property.Value.GetInt32()}");
                    }

                    if (parts.Count > 0)
                    {
                        statuses = string.Join(", ", parts);
                    }
                }

                AppendLog($"[runtime] total={totalTasks} statuses={statuses}");
                break;

            case "agui.task.snapshot":
                if (payload.TryGetProperty("task", out var taskElement) &&
                    taskElement.ValueKind == JsonValueKind.Object)
                {
                    if (taskElement.TryGetProperty("taskId", out var taskIdElement) &&
                        taskIdElement.ValueKind == JsonValueKind.String)
                    {
                        _activeTaskId = taskIdElement.GetString();
                    }

                    if (taskElement.TryGetProperty("status", out var statusElement) &&
                        statusElement.ValueKind == JsonValueKind.String)
                    {
                        _statusLabel!.Text = $"Status: {statusElement.GetString() ?? "unknown"}";
                    }
                }
                break;

            case "agui.memory.tasks":
                var source = payload.TryGetProperty("source", out var sourceElement) &&
                             sourceElement.ValueKind == JsonValueKind.String
                    ? sourceElement.GetString() ?? "unknown"
                    : "unknown";
                var count = payload.TryGetProperty("count", out var countElement) &&
                            countElement.ValueKind == JsonValueKind.Number
                    ? countElement.GetInt32()
                    : 0;

                _statusLabel!.Text = $"Status: memory loaded ({count})";
                AppendLog($"[memory] source={source} count={count}");

                if (!payload.TryGetProperty("items", out var itemsElement) ||
                    itemsElement.ValueKind != JsonValueKind.Array)
                {
                    break;
                }

                var shown = 0;
                foreach (var item in itemsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var taskId = item.TryGetProperty("taskId", out var idElement) &&
                                 idElement.ValueKind == JsonValueKind.String
                        ? idElement.GetString() ?? string.Empty
                        : string.Empty;
                    var status = item.TryGetProperty("status", out var statusElement) &&
                                 statusElement.ValueKind == JsonValueKind.String
                        ? statusElement.GetString() ?? "unknown"
                        : "unknown";
                    var title = item.TryGetProperty("title", out var titleElement) &&
                                titleElement.ValueKind == JsonValueKind.String
                        ? titleElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(_activeTaskId) && !string.IsNullOrWhiteSpace(taskId))
                    {
                        _activeTaskId = taskId;
                    }

                    AppendLog($"[memory-task] {taskId} {status} {title}");
                    shown++;
                    if (shown >= 8)
                    {
                        break;
                    }
                }
                break;

            case "agui.memory.bootstrap":
                var importedCount = payload.TryGetProperty("importedCount", out var importedElement) &&
                                    importedElement.ValueKind == JsonValueKind.Number
                    ? importedElement.GetInt32()
                    : 0;
                var fetchedCount = payload.TryGetProperty("fetchedCount", out var fetchedElement) &&
                                   fetchedElement.ValueKind == JsonValueKind.Number
                    ? fetchedElement.GetInt32()
                    : 0;
                _statusLabel!.Text = $"Status: memory bootstrap ({importedCount}/{fetchedCount})";
                AppendLog($"[memory-bootstrap] imported={importedCount} fetched={fetchedCount}");
                break;

            case "agui.memory.bootstrap.failed":
                var error = payload.TryGetProperty("error", out var errorElement) &&
                            errorElement.ValueKind == JsonValueKind.String
                    ? errorElement.GetString() ?? "unknown"
                    : "unknown";
                AppendLog($"[memory-bootstrap] failed error={error}");
                break;
        }
    }

    private void RenderSurface(JsonElement a2ui)
    {
        if (!a2ui.TryGetProperty("surface", out var surface))
        {
            return;
        }

        if (_titleLabel is not null && surface.TryGetProperty("title", out var titleElement))
        {
            _titleLabel.Text = titleElement.GetString() ?? "Swarm Task Monitor";
        }

        if (_componentContainer is null)
        {
            return;
        }

        foreach (Node child in _componentContainer.GetChildren())
        {
            child.QueueFree();
        }

        if (!surface.TryGetProperty("components", out var components) || components.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var component in components.EnumerateArray())
        {
            if (!component.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var type = typeElement.GetString() ?? string.Empty;
            if (!component.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var componentId = idElement.GetString() ?? string.Empty;
            var props = component.TryGetProperty("props", out var propsElement)
                ? propsElement
                : default;

            switch (type)
            {
                case "text":
                    var textNode = _textComponentScene.Instantiate<A2TextComponent>();
                    var text = props.ValueKind == JsonValueKind.Object &&
                               props.TryGetProperty("text", out var textElement)
                        ? textElement.GetString() ?? string.Empty
                        : string.Empty;
                    textNode.Configure(componentId, text);
                    _componentContainer.AddChild(textNode);
                    break;
                case "button":
                    var buttonNode = _buttonComponentScene.Instantiate<A2ButtonComponent>();
                    var label = props.ValueKind == JsonValueKind.Object &&
                                props.TryGetProperty("label", out var labelElement)
                        ? labelElement.GetString() ?? "Action"
                        : "Action";
                    var actionId = props.ValueKind == JsonValueKind.Object &&
                                   props.TryGetProperty("actionId", out var actionElement)
                        ? actionElement.GetString() ?? "unknown_action"
                        : "unknown_action";
                    buttonNode.Configure(componentId, label, actionId);
                    buttonNode.ActionRequested += OnActionRequested;
                    _componentContainer.AddChild(buttonNode);
                    break;
            }
        }
    }

    private void ApplyDataModelPatch(JsonElement a2ui)
    {
        if (!a2ui.TryGetProperty("dataModelPatch", out var patch) || patch.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (patch.TryGetProperty("status", out var statusElement))
        {
            _statusLabel!.Text = $"Status: {statusElement.GetString() ?? "unknown"}";
        }

        if (patch.TryGetProperty("lastRole", out var roleElement) &&
            patch.TryGetProperty("lastMessage", out var messageElement))
        {
            AppendLog($"[{roleElement.GetString() ?? "role"}] {messageElement.GetString() ?? string.Empty}");
        }
    }

    private void OnActionRequested(string actionId)
    {
        SendAction(actionId);
    }

    private void OnSubmitTaskPressed()
    {
        var title = _taskTitleInput?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            AppendLog("[warn] Task title is required for submit.");
            return;
        }

        var description = _taskDescriptionInput?.Text?.Trim() ?? string.Empty;

        SendAction("submit_task", new Dictionary<string, object?>
        {
            ["title"] = title,
            ["description"] = description
        });
    }

    private void OnRequestSnapshotPressed()
    {
        SendAction("request_snapshot");
    }

    private void OnRefreshSurfacePressed()
    {
        if (string.IsNullOrWhiteSpace(_activeTaskId))
        {
            AppendLog("[warn] No active task for refresh_surface.");
            return;
        }

        SendAction("refresh_surface");
    }

    private void OnLoadMemoryPressed()
    {
        SendAction("load_memory", new Dictionary<string, object?>
        {
            ["limit"] = 50
        });
    }

    private void SendAction(
        string actionId,
        Dictionary<string, object?>? actionPayload = null,
        string? taskId = null)
    {
        if (_actionInFlight || _actionRequest is null)
        {
            return;
        }

        var payload = actionPayload is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(actionPayload);
        payload["source"] = "godot-ui";
        payload["at"] = DateTimeOffset.UtcNow.ToString("O");

        var body = JsonSerializer.Serialize(new
        {
            taskId = taskId ?? _activeTaskId,
            actionId,
            payload
        });

        _actionInFlight = true;
        var error = _actionRequest.Request(
            AgUiActionsUrl,
            ["Content-Type: application/json"],
            HttpClient.Method.Post,
            body);

        if (error != Error.Ok)
        {
            _actionInFlight = false;
            AppendLog($"[error] Action request failed: {error}");
        }
    }

    private void OnActionRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _actionInFlight = false;
        var responseBody = Encoding.UTF8.GetString(body);

        if (result == (long)HttpRequest.Result.Success && responseCode >= 200 && responseCode <= 299)
        {
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    using var document = JsonDocument.Parse(responseBody);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("taskId", out var taskIdElement) &&
                        taskIdElement.ValueKind == JsonValueKind.String)
                    {
                        _activeTaskId = taskIdElement.GetString();
                    }
                }
                catch (Exception ex)
                {
                    // Non-JSON response (e.g. plain text or empty body) — not an error.
                    GD.Print($"[action] Could not parse response body as JSON: {ex.Message}");
                }
            }

            AppendLog($"[action] Sent (http={responseCode}).");
            return;
        }

        AppendLog($"[warn] Action response result={result} http={responseCode} body={responseBody}");
    }

    private void AppendLog(string line)
    {
        if (_logOutput is null)
        {
            return;
        }

        _logOutput.AppendText($"{line}\n");
    }
}
