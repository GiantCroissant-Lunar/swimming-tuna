using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;

public partial class Main : Control
{
    [Export] public string AgUiEventsUrl { get; set; } = "http://127.0.0.1:5080/ag-ui/events";
    [Export] public string AgUiActionsUrl { get; set; } = "http://127.0.0.1:5080/ag-ui/actions";
    [Export] public bool DisableNetworkingInHeadless { get; set; } = true;

    private readonly System.Net.Http.HttpClient _httpClient = new();
    private CancellationTokenSource? _lifecycleCts;
    private Task? _listenerTask;

    private Label? _titleLabel;
    private Label? _statusLabel;
    private VBoxContainer? _componentContainer;
    private RichTextLabel? _logOutput;
    private string? _activeTaskId;

    public override void _Ready()
    {
        BuildLayout();
        var allowHeadlessNetworking = string.Equals(
            System.Environment.GetEnvironmentVariable("SWARM_UI_HEADLESS_NETWORK"),
            "1",
            StringComparison.Ordinal);
        if (DisableNetworkingInHeadless && OS.HasFeature("headless") && !allowHeadlessNetworking)
        {
            _statusLabel!.Text = "Headless mode detected. AG-UI listener disabled.";
            AppendLog("[info] Headless mode: AG-UI listener disabled by default.");
            return;
        }

        _lifecycleCts = new CancellationTokenSource();
        _listenerTask = ListenToEventsAsync(_lifecycleCts.Token);
    }

    public override void _ExitTree()
    {
        if (_lifecycleCts is not null)
        {
            _lifecycleCts.Cancel();
            if (_listenerTask is not null && !_listenerTask.IsCompleted)
            {
                try
                {
                    _listenerTask.Wait(TimeSpan.FromMilliseconds(500));
                }
                catch
                {
                }
            }
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }

        _httpClient.Dispose();
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

        _titleLabel = new Label { Text = "SwarmAssistant UI" };
        layout.AddChild(_titleLabel);

        _statusLabel = new Label { Text = "Connecting to AG-UI stream..." };
        layout.AddChild(_statusLabel);

        _componentContainer = new VBoxContainer();
        layout.AddChild(_componentContainer);

        _logOutput = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 260),
            ScrollActive = true
        };
        layout.AddChild(_logOutput);
    }

    private async Task ListenToEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AgUiEventsUrl);
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);

                var eventName = "message";
                while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                    {
                        continue;
                    }

                    if (line.StartsWith("event:"))
                    {
                        eventName = line[6..].Trim();
                        continue;
                    }

                    if (line.StartsWith("data:"))
                    {
                        var json = line[5..].Trim();
                        CallDeferred(nameof(ApplyEvent), eventName, json);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                CallDeferred(nameof(AppendLog), $"[error] AG-UI stream failure: {exception.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private void ApplyEvent(string eventName, string dataJson)
    {
        AppendLog($"[{eventName}] {dataJson}");

        try
        {
            using var document = JsonDocument.Parse(dataJson);
            var root = document.RootElement;

            if (root.TryGetProperty("taskId", out var taskIdElement) && taskIdElement.ValueKind == JsonValueKind.String)
            {
                _activeTaskId = taskIdElement.GetString();
            }

            if (!root.TryGetProperty("payload", out var payload))
            {
                return;
            }

            if (!payload.TryGetProperty("a2ui", out var a2ui))
            {
                return;
            }

            if (!a2ui.TryGetProperty("operation", out var operationElement))
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
        catch (Exception exception)
        {
            AppendLog($"[error] Failed to parse event payload: {exception.Message}");
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
            var type = component.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (!component.TryGetProperty("props", out var props))
            {
                continue;
            }

            if (type == "text")
            {
                var text = props.TryGetProperty("text", out var textElement)
                    ? textElement.GetString() ?? string.Empty
                    : string.Empty;
                var label = new Label
                {
                    Text = text,
                    AutowrapMode = TextServer.AutowrapMode.WordSmart
                };
                _componentContainer.AddChild(label);
                continue;
            }

            if (type == "button")
            {
                var labelText = props.TryGetProperty("label", out var labelElement)
                    ? labelElement.GetString() ?? "Action"
                    : "Action";
                var actionId = props.TryGetProperty("actionId", out var actionElement)
                    ? actionElement.GetString() ?? "unknown_action"
                    : "unknown_action";

                var button = new Button { Text = labelText };
                button.Pressed += () => _ = SendActionAsync(actionId);
                _componentContainer.AddChild(button);
            }
        }
    }

    private void ApplyDataModelPatch(JsonElement a2ui)
    {
        if (!a2ui.TryGetProperty("dataModelPatch", out var patch))
        {
            return;
        }

        if (_statusLabel is not null && patch.TryGetProperty("status", out var statusElement))
        {
            var status = statusElement.GetString() ?? "unknown";
            _statusLabel.Text = $"Status: {status}";
        }

        if (patch.TryGetProperty("lastRole", out var roleElement) &&
            patch.TryGetProperty("lastMessage", out var messageElement))
        {
            var role = roleElement.GetString() ?? "role";
            var message = messageElement.GetString() ?? string.Empty;
            AppendLog($"[{role}] {message}");
        }
    }

    private async Task SendActionAsync(string actionId)
    {
        try
        {
            var request = new
            {
                taskId = _activeTaskId,
                actionId,
                payload = new
                {
                    source = "godot-ui",
                    at = DateTimeOffset.UtcNow
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(AgUiActionsUrl, content);
            response.EnsureSuccessStatusCode();
            CallDeferred(nameof(AppendLog), $"[action] Sent {actionId}");
        }
        catch (Exception exception)
        {
            CallDeferred(nameof(AppendLog), $"[error] Action failed ({actionId}): {exception.Message}");
        }
    }

    private void AppendLog(string line)
    {
        _logOutput?.AppendText($"{line}\n");
    }
}
