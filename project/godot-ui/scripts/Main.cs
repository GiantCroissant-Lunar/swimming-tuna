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
    private VBoxContainer? _componentContainer;
    private RichTextLabel? _logOutput;

    private long _lastSequence;
    private string? _activeTaskId;

    public override void _Ready()
    {
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
        root.AddChild(layout);

        _titleLabel = new Label
        {
            Text = "SwarmAssistant UI",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        layout.AddChild(_titleLabel);

        _statusLabel = new Label { Text = "Connecting..." };
        layout.AddChild(_statusLabel);

        _componentContainer = new VBoxContainer();
        layout.AddChild(_componentContainer);

        _logOutput = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 280),
            ScrollActive = true
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
        if (_actionInFlight || _actionRequest is null)
        {
            return;
        }

        var body = JsonSerializer.Serialize(new
        {
            taskId = _activeTaskId,
            actionId,
            payload = new Dictionary<string, object?>
            {
                ["source"] = "godot-ui",
                ["at"] = DateTimeOffset.UtcNow
            }
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
        if (result == (long)HttpRequest.Result.Success && responseCode >= 200 && responseCode <= 299)
        {
            AppendLog("[action] Sent.");
            return;
        }

        AppendLog($"[warn] Action response result={result} http={responseCode}");
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
