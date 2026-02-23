using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;

public partial class Main : Control
{
    private string _recentEventsUrl = "";
    private string _actionsUrl = "";

    public override void _Ready()
    {
        var agUiUrl = System.Environment.GetEnvironmentVariable("AGUI_HTTP_URL");
        if (string.IsNullOrEmpty(agUiUrl))
        {
            agUiUrl = "http://127.0.0.1:5080";
        }
        GD.Print($"Using AGUI HTTP URL: {agUiUrl}");

        _recentEventsUrl = $"{agUiUrl}/ag-ui/recent";
        _actionsUrl = $"{agUiUrl}/ag-ui/actions";

        GetTree().AutoAcceptQuit = false;
        DisplayServer.WindowSetMinSize(new Vector2I(1280, 800));
        BuildLayout();
        SetupNetworking();
        TriggerPoll();
    }

    [Export] public float PollIntervalSeconds { get; set; } = 0.75f;
    [Export] public int RecentEventCount { get; set; } = 100;

    private HttpRequest? _recentRequest;
    private HttpRequest? _actionRequest;
    private Timer? _pollTimer;
    private bool _recentInFlight;
    private bool _actionInFlight;

    // Header
    private Label? _titleLabel;
    private Label? _statusLabel;
    private Label? _connectionStatusLabel;

    // Task submission
    private LineEdit? _taskTitleInput;
    private LineEdit? _taskDescriptionInput;

    // Task hierarchy/tree
    private ItemList? _taskList;
    private Tree? _taskTree;

    // Task details panel
    private Label? _selectedTaskLabel;
    private Label? _taskStatusLabel;
    private Label? _taskRoleLabel;
    private ProgressBar? _taskProgressBar;

    // HITL Controls
    private Button? _approveButton;
    private Button? _rejectButton;
    private Button? _reworkButton;
    private Button? _pauseButton;
    private Button? _resumeButton;
    private SpinBox? _depthSpinBox;
    private Button? _setDepthButton;

    // Component container for dynamic UI
    private VBoxContainer? _componentContainer;
    private RichTextLabel? _logOutput;

    // Status widgets
    private Label? _qualityStatusLabel;
    private Label? _retryStatusLabel;
    private Label? _adapterStatusLabel;

    private const int TaskIdShortLength = 8;
    private const int TaskTitleMaxLength = 44;
    private const int TaskTitleTruncatedLength = 41;

    // State model for graph and task selection
    private readonly Dictionary<string, int> _taskListTaskIds = [];
    private readonly Dictionary<int, string> _taskListByIndex = [];
    private readonly Dictionary<string, TaskNodeState> _taskGraphState = [];
    private readonly List<string> _actionHistory = [];
    private long _lastSequence;
    private string? _activeTaskId;
    private string? _selectedTaskId;
    private string? _pendingSelectionRefreshTaskId;
    private bool _shuttingDown;
    private bool _isConnected;
    private DateTime _lastPollTime = DateTime.MinValue;

    // Task state model
    private class TaskNodeState
    {
        public string TaskId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown";
        public string? ParentId { get; set; }
        public List<string> Children { get; set; } = [];
        public string? Role { get; set; }
        public string? LastMessage { get; set; }
        public double Progress { get; set; }
        public int Depth { get; set; }
        public string? Quality { get; set; }
        public int RetryCount { get; set; }
        public string? Adapter { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
        {
            Shutdown();
            // Use deferred quit to allow cleanup
            CallDeferred(nameof(DeferredQuit));
        }
    }

    private void DeferredQuit()
    {
        GetTree().Quit();
    }

    public override void _ExitTree()
    {
        Shutdown();
    }

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        GD.Print("[Shutdown] Cleaning up resources...");

        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer.Timeout -= TriggerPoll;
            _pollTimer.QueueFree();
            _pollTimer = null;
        }

        if (_recentRequest is not null)
        {
            _recentRequest.RequestCompleted -= OnRecentRequestCompleted;
            _recentRequest.CancelRequest();
            _recentRequest = null;
        }

        if (_actionRequest is not null)
        {
            _actionRequest.RequestCompleted -= OnActionRequestCompleted;
            _actionRequest.CancelRequest();
            _actionRequest = null;
        }

        // Clear collections to help GC
        _taskListTaskIds.Clear();
        _taskListByIndex.Clear();
        _taskGraphState.Clear();

        GD.Print("[Shutdown] Cleanup complete");
    }

    private void BuildLayout()
    {
        // Main margin container
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_bottom", 16);
        AddChild(margin);

        // Main vertical split: top content vs log
        var mainVSplit = new VSplitContainer();
        mainVSplit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        mainVSplit.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVSplit.SplitOffset = 550;
        margin.AddChild(mainVSplit);

        // Top section with header and content
        var topVBox = new VBoxContainer();
        topVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        topVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        mainVSplit.AddChild(topVBox);

        // Header
        BuildHeader(topVBox);

        // Content area (horizontal split: task list | details)
        var contentHSplit = new HSplitContainer();
        contentHSplit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        contentHSplit.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        contentHSplit.SplitOffset = 350;
        topVBox.AddChild(contentHSplit);

        // Left: Task tree and list
        BuildLeftPanel(contentHSplit);

        // Right: Details and controls
        BuildRightPanel(contentHSplit);

        // Bottom: Log
        BuildLogPanel(mainVSplit);
    }

    private void BuildHeader(VBoxContainer parent)
    {
        var header = new HBoxContainer();
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(header);

        _titleLabel = new Label();
        _titleLabel.Text = "SwarmAssistant Operator Control Surface";
        _titleLabel.AddThemeFontSizeOverride("font_size", 16);
        header.AddChild(_titleLabel);

        var spacer = new Control();
        spacer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        header.AddChild(spacer);

        var statusVBox = new VBoxContainer();
        header.AddChild(statusVBox);

        _connectionStatusLabel = new Label();
        _connectionStatusLabel.Text = "● Disconnected";
        _connectionStatusLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _connectionStatusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.2f, 0.2f));
        statusVBox.AddChild(_connectionStatusLabel);

        _statusLabel = new Label();
        _statusLabel.Text = "Initializing...";
        _statusLabel.HorizontalAlignment = HorizontalAlignment.Right;
        statusVBox.AddChild(_statusLabel);

        // Separator
        parent.AddChild(new HSeparator());

        // Task submission row
        var submitRow = new HBoxContainer();
        submitRow.AddThemeConstantOverride("separation", 8);
        submitRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(submitRow);

        _taskTitleInput = new LineEdit();
        _taskTitleInput.PlaceholderText = "Task title";
        _taskTitleInput.CustomMinimumSize = new Vector2(200, 0);
        _taskTitleInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        submitRow.AddChild(_taskTitleInput);

        _taskDescriptionInput = new LineEdit();
        _taskDescriptionInput.PlaceholderText = "Task description";
        _taskDescriptionInput.CustomMinimumSize = new Vector2(280, 0);
        _taskDescriptionInput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        submitRow.AddChild(_taskDescriptionInput);

        var submitBtn = new Button();
        submitBtn.Text = "Submit Task";
        submitBtn.Pressed += OnSubmitTaskPressed;
        submitRow.AddChild(submitBtn);

        var snapshotBtn = new Button();
        snapshotBtn.Text = "Snapshot";
        snapshotBtn.Pressed += OnRequestSnapshotPressed;
        submitRow.AddChild(snapshotBtn);

        var refreshBtn = new Button();
        refreshBtn.Text = "Refresh";
        refreshBtn.Pressed += OnRefreshSurfacePressed;
        submitRow.AddChild(refreshBtn);

        var memoryBtn = new Button();
        memoryBtn.Text = "Memory";
        memoryBtn.Pressed += OnLoadMemoryPressed;
        submitRow.AddChild(memoryBtn);

        parent.AddChild(new HSeparator());
    }

    private void BuildLeftPanel(SplitContainer parent)
    {
        var leftScroll = new ScrollContainer();
        leftScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        leftScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        parent.AddChild(leftScroll);

        var leftVBox = new VBoxContainer();
        leftVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        leftVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        leftScroll.AddChild(leftVBox);

        // Tree label
        var treeLabel = new Label();
        treeLabel.Text = "Task Hierarchy";
        treeLabel.AddThemeFontSizeOverride("font_size", 14);
        treeLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        leftVBox.AddChild(treeLabel);

        // Tree
        _taskTree = new Tree();
        _taskTree.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _taskTree.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _taskTree.CustomMinimumSize = new Vector2(0, 200);
        _taskTree.HideRoot = true;
        _taskTree.ItemSelected += OnTaskTreeItemSelected;
        leftVBox.AddChild(_taskTree);

        leftVBox.AddChild(new HSeparator());

        // List label
        var listLabel = new Label();
        listLabel.Text = "Active Tasks";
        listLabel.AddThemeFontSizeOverride("font_size", 14);
        listLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        leftVBox.AddChild(listLabel);

        // Task list
        _taskList = new ItemList();
        _taskList.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _taskList.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _taskList.CustomMinimumSize = new Vector2(0, 150);
        _taskList.AllowReselect = true;
        _taskList.SelectMode = ItemList.SelectModeEnum.Single;
        _taskList.ItemSelected += OnTaskListItemSelected;
        leftVBox.AddChild(_taskList);
    }

    private void BuildRightPanel(SplitContainer parent)
    {
        var rightScroll = new ScrollContainer();
        rightScroll.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rightScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        parent.AddChild(rightScroll);

        var rightVBox = new VBoxContainer();
        rightVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rightVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        rightScroll.AddChild(rightVBox);

        // Task Details Section
        var detailsLabel = new Label();
        detailsLabel.Text = "Task Details";
        detailsLabel.AddThemeFontSizeOverride("font_size", 16);
        detailsLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        rightVBox.AddChild(detailsLabel);

        _selectedTaskLabel = new Label();
        _selectedTaskLabel.Text = "No task selected";
        _selectedTaskLabel.AddThemeFontSizeOverride("font_size", 14);
        rightVBox.AddChild(_selectedTaskLabel);

        _taskStatusLabel = new Label();
        _taskStatusLabel.Text = "Status: -";
        _taskStatusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        rightVBox.AddChild(_taskStatusLabel);

        _taskRoleLabel = new Label();
        _taskRoleLabel.Text = "Role: -";
        _taskRoleLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        rightVBox.AddChild(_taskRoleLabel);

        _taskProgressBar = new ProgressBar();
        _taskProgressBar.MinValue = 0;
        _taskProgressBar.MaxValue = 100;
        _taskProgressBar.Value = 0;
        _taskProgressBar.CustomMinimumSize = new Vector2(0, 20);
        rightVBox.AddChild(_taskProgressBar);

        rightVBox.AddChild(new HSeparator());

        // HITL Controls
        var hitlLabel = new Label();
        hitlLabel.Text = "HITL Controls";
        hitlLabel.AddThemeFontSizeOverride("font_size", 16);
        hitlLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        rightVBox.AddChild(hitlLabel);

        var hitlRow1 = new HBoxContainer();
        hitlRow1.AddThemeConstantOverride("separation", 8);
        rightVBox.AddChild(hitlRow1);

        _approveButton = new Button();
        _approveButton.Text = "Approve";
        _approveButton.Disabled = true;
        _approveButton.Pressed += () => SendHitlAction("approve");
        hitlRow1.AddChild(_approveButton);

        _rejectButton = new Button();
        _rejectButton.Text = "Reject";
        _rejectButton.Disabled = true;
        _rejectButton.Pressed += () => SendHitlAction("reject");
        hitlRow1.AddChild(_rejectButton);

        _reworkButton = new Button();
        _reworkButton.Text = "Rework";
        _reworkButton.Disabled = true;
        _reworkButton.Pressed += () => SendHitlAction("rework");
        hitlRow1.AddChild(_reworkButton);

        var hitlRow2 = new HBoxContainer();
        hitlRow2.AddThemeConstantOverride("separation", 8);
        rightVBox.AddChild(hitlRow2);

        _pauseButton = new Button();
        _pauseButton.Text = "Pause";
        _pauseButton.Disabled = true;
        _pauseButton.Pressed += () => SendHitlAction("pause");
        hitlRow2.AddChild(_pauseButton);

        _resumeButton = new Button();
        _resumeButton.Text = "Resume";
        _resumeButton.Disabled = true;
        _resumeButton.Pressed += () => SendHitlAction("resume");
        hitlRow2.AddChild(_resumeButton);

        // Depth control
        var depthRow = new HBoxContainer();
        depthRow.AddThemeConstantOverride("separation", 8);
        rightVBox.AddChild(depthRow);

        var depthLabel = new Label();
        depthLabel.Text = "Max Depth:";
        depthRow.AddChild(depthLabel);

        _depthSpinBox = new SpinBox();
        _depthSpinBox.MinValue = 1;
        _depthSpinBox.MaxValue = 10;
        _depthSpinBox.Value = 3;
        _depthSpinBox.CustomMinimumSize = new Vector2(80, 0);
        depthRow.AddChild(_depthSpinBox);

        _setDepthButton = new Button();
        _setDepthButton.Text = "Set Depth";
        _setDepthButton.Disabled = true;
        _setDepthButton.Pressed += OnSetDepthPressed;
        depthRow.AddChild(_setDepthButton);

        rightVBox.AddChild(new HSeparator());

        // Status Widgets
        var widgetsLabel = new Label();
        widgetsLabel.Text = "Status Widgets";
        widgetsLabel.AddThemeFontSizeOverride("font_size", 16);
        widgetsLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        rightVBox.AddChild(widgetsLabel);

        _qualityStatusLabel = new Label();
        _qualityStatusLabel.Text = "Quality: -";
        _qualityStatusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        rightVBox.AddChild(_qualityStatusLabel);

        _retryStatusLabel = new Label();
        _retryStatusLabel.Text = "Retries: 0";
        _retryStatusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        rightVBox.AddChild(_retryStatusLabel);

        _adapterStatusLabel = new Label();
        _adapterStatusLabel.Text = "Adapter: -";
        _adapterStatusLabel.AddThemeColorOverride("font_color", new Color(0.8f, 0.8f, 0.8f));
        rightVBox.AddChild(_adapterStatusLabel);

        rightVBox.AddChild(new HSeparator());

        // Dynamic Components
        var dynLabel = new Label();
        dynLabel.Text = "Dynamic Components";
        dynLabel.AddThemeFontSizeOverride("font_size", 16);
        dynLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        rightVBox.AddChild(dynLabel);

        _componentContainer = new VBoxContainer();
        _componentContainer.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rightVBox.AddChild(_componentContainer);
    }

    private void BuildLogPanel(SplitContainer parent)
    {
        var logPanel = new PanelContainer();
        logPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        parent.AddChild(logPanel);

        var logVBox = new VBoxContainer();
        logVBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        logVBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        logPanel.AddChild(logVBox);

        var logLabel = new Label();
        logLabel.Text = "Event Log";
        logLabel.AddThemeFontSizeOverride("font_size", 14);
        logLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.9f));
        logVBox.AddChild(logLabel);

        _logOutput = new RichTextLabel();
        _logOutput.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _logOutput.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _logOutput.ScrollActive = true;
        _logOutput.BbcodeEnabled = true;
        _logOutput.CustomMinimumSize = new Vector2(0, 150);
        logVBox.AddChild(_logOutput);
    }

    private void SetupNetworking()
    {
        _recentRequest = new HttpRequest();
        AddChild(_recentRequest);
        _recentRequest.RequestCompleted += OnRecentRequestCompleted;

        _actionRequest = new HttpRequest();
        AddChild(_actionRequest);
        _actionRequest.RequestCompleted += OnActionRequestCompleted;

        _pollTimer = new Timer();
        _pollTimer.WaitTime = Math.Max(0.2, PollIntervalSeconds);
        _pollTimer.Autostart = true;
        _pollTimer.OneShot = false;
        AddChild(_pollTimer);
        _pollTimer.Timeout += TriggerPoll;
    }

    private void TriggerPoll()
    {
        if (_shuttingDown || _recentInFlight || _recentRequest is null)
        {
            return;
        }

        _recentInFlight = true;
        _lastPollTime = DateTime.UtcNow;
        var url = $"{_recentEventsUrl}?count={Math.Clamp(RecentEventCount, 10, 500)}";
        var error = _recentRequest.Request(url);
        if (error != Error.Ok)
        {
            _recentInFlight = false;
            AppendLog($"[error] Poll request failed: {error}");
            UpdateConnectionStatus(false);
        }
    }

    private void OnRecentRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _recentInFlight = false;
        if (_shuttingDown) return;

        if (result != (long)HttpRequest.Result.Success || responseCode < 200 || responseCode > 299)
        {
            UpdateConnectionStatus(false);
            _statusLabel!.Text = $"Status: disconnected (HTTP {responseCode})";
            AppendLog($"[warn] Poll failed result={result} http={responseCode}");
            return;
        }

        UpdateConnectionStatus(true);
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

    private void UpdateConnectionStatus(bool connected)
    {
        _isConnected = connected;
        if (_connectionStatusLabel is not null)
        {
            _connectionStatusLabel.Text = connected ? "● Connected" : "● Disconnected";
            _connectionStatusLabel.AddThemeColorOverride("font_color",
                connected ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.2f, 0.2f));
        }

        UpdateHitlControlsState();
    }

    private void UpdateHitlControlsState()
    {
        var hasSelection = !string.IsNullOrWhiteSpace(_selectedTaskId) && _isConnected;

        if (_approveButton is not null) _approveButton.Disabled = !hasSelection;
        if (_rejectButton is not null) _rejectButton.Disabled = !hasSelection;
        if (_reworkButton is not null) _reworkButton.Disabled = !hasSelection;
        if (_pauseButton is not null) _pauseButton.Disabled = !hasSelection;
        if (_resumeButton is not null) _resumeButton.Disabled = !hasSelection;
        if (_setDepthButton is not null) _setDepthButton.Disabled = !hasSelection;
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

                    var title = payload.TryGetProperty("title", out var titleElement) &&
                                titleElement.ValueKind == JsonValueKind.String
                        ? titleElement.GetString() ?? string.Empty
                        : string.Empty;
                    UpsertTaskListItem(_activeTaskId!, title, "queued", updatedAt: null);
                    SelectTaskInList(_activeTaskId!);
                    UpdateTaskGraphState(_activeTaskId!, title, "queued", null);
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
                        var status = statusElement.GetString() ?? "unknown";
                        _statusLabel!.Text = $"Status: {status}";

                        var title = taskElement.TryGetProperty("title", out var titleElement) &&
                                    titleElement.ValueKind == JsonValueKind.String
                            ? titleElement.GetString() ?? string.Empty
                            : string.Empty;
                        var updatedAt = taskElement.TryGetProperty("updatedAt", out var updatedAtElement)
                            ? updatedAtElement.ToString()
                            : null;

                        var role = taskElement.TryGetProperty("role", out var roleElement) &&
                                   roleElement.ValueKind == JsonValueKind.String
                            ? roleElement.GetString()
                            : null;

                        var quality = taskElement.TryGetProperty("quality", out var qualityElement) &&
                                      qualityElement.ValueKind == JsonValueKind.String
                            ? qualityElement.GetString()
                            : null;

                        var adapter = taskElement.TryGetProperty("adapter", out var adapterElement) &&
                                      adapterElement.ValueKind == JsonValueKind.String
                            ? adapterElement.GetString()
                            : null;

                        var retryCount = taskElement.TryGetProperty("retryCount", out var retryElement) &&
                                         retryElement.ValueKind == JsonValueKind.Number
                            ? retryElement.GetInt32()
                            : 0;

                        var progress = taskElement.TryGetProperty("progress", out var progressElement) &&
                                       progressElement.ValueKind == JsonValueKind.Number
                            ? progressElement.GetDouble()
                            : 0.0;

                        if (!string.IsNullOrWhiteSpace(_activeTaskId))
                        {
                            UpsertTaskListItem(_activeTaskId!, title, status, updatedAt);
                            SelectTaskInList(_activeTaskId!);
                            UpdateTaskGraphState(_activeTaskId!, title, status, updatedAt, role, quality, adapter, retryCount, progress);

                            if (_activeTaskId == _selectedTaskId)
                            {
                                UpdateTaskDetailsPanel(_activeTaskId!, status, role, quality, adapter, retryCount, progress);
                            }
                        }
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
                ReplaceTaskListFromMemory(itemsElement);
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

    private void UpdateTaskGraphState(string taskId, string title, string status, string? updatedAt,
        string? role = null, string? quality = null, string? adapter = null,
        int retryCount = 0, double progress = 0.0)
    {
        if (!_taskGraphState.TryGetValue(taskId, out var state))
        {
            state = new TaskNodeState { TaskId = taskId };
            _taskGraphState[taskId] = state;
        }

        state.Title = title;
        state.Status = status;
        state.UpdatedAt = updatedAt is not null
            && DateTime.TryParse(updatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsedDate)
            ? parsedDate
            : DateTime.UtcNow;

        if (role is not null) state.Role = role;
        if (quality is not null) state.Quality = quality;
        if (adapter is not null) state.Adapter = adapter;
        state.RetryCount = retryCount;
        state.Progress = progress;

        RefreshTaskTree();
    }

    private void RefreshTaskTree()
    {
        if (_taskTree is null) return;

        _taskTree.Clear();
        var root = _taskTree.CreateItem();

        foreach (var kvp in _taskGraphState)
        {
            var state = kvp.Value;
            var item = _taskTree.CreateItem(root);
            var shortId = state.TaskId.Length > 8 ? state.TaskId[..8] : state.TaskId;
            item.SetText(0, $"[{state.Status}] {shortId} - {state.Title}");

            var color = state.Status switch
            {
                "completed" => new Color(0.2f, 0.8f, 0.2f),
                "failed" => new Color(0.8f, 0.2f, 0.2f),
                "paused" => new Color(0.8f, 0.8f, 0.2f),
                "running" => new Color(0.2f, 0.6f, 0.8f),
                "queued" => new Color(0.6f, 0.6f, 0.6f),
                _ => new Color(0.8f, 0.8f, 0.8f)
            };
            item.SetCustomColor(0, color);
            item.SetMetadata(0, state.TaskId);
        }
    }

    private void UpdateTaskDetailsPanel(string taskId, string status, string? role,
        string? quality, string? adapter, int retryCount, double progress)
    {
        if (_selectedTaskLabel is not null)
        {
            var shortId = taskId.Length > 16 ? taskId[..16] : taskId;
            _selectedTaskLabel.Text = $"Task: {shortId}...";
        }

        if (_taskStatusLabel is not null)
            _taskStatusLabel.Text = $"Status: {status}";

        if (_taskRoleLabel is not null)
            _taskRoleLabel.Text = $"Role: {role ?? "-"}";

        if (_taskProgressBar is not null)
            _taskProgressBar.Value = progress * 100;

        if (_qualityStatusLabel is not null)
            _qualityStatusLabel.Text = $"Quality: {quality ?? "-"}";

        if (_retryStatusLabel is not null)
            _retryStatusLabel.Text = $"Retries: {retryCount}";

        if (_adapterStatusLabel is not null)
            _adapterStatusLabel.Text = $"Adapter: {adapter ?? "-"}";
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
            var node = GenUiNodeFactory.Build(component, OnActionRequested);
            _componentContainer.AddChild(node);
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

    private void SendHitlAction(string actionType)
    {
        if (string.IsNullOrWhiteSpace(_selectedTaskId))
        {
            AppendLog("[warn] No task selected for HITL action");
            return;
        }

        var payload = new Dictionary<string, object?>
        {
            ["action"] = actionType,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };

        SendAction($"hitl_{actionType}", payload, _selectedTaskId);
        var shortId = _selectedTaskId.Length > 8 ? _selectedTaskId[..8] : _selectedTaskId;
        AppendLog($"[HITL] Sent {actionType} for task {shortId}");
    }

    private void OnSetDepthPressed()
    {
        if (string.IsNullOrWhiteSpace(_selectedTaskId) || _depthSpinBox is null)
        {
            AppendLog("[warn] No task selected or depth not set");
            return;
        }

        var depth = (int)_depthSpinBox.Value;
        var payload = new Dictionary<string, object?>
        {
            ["maxDepth"] = depth,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
        };

        SendAction("set_depth", payload, _selectedTaskId);
        var shortId = _selectedTaskId.Length > 8 ? _selectedTaskId[..8] : _selectedTaskId;
        AppendLog($"[HITL] Set depth to {depth} for task {shortId}");
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

    private void OnTaskListItemSelected(long index)
    {
        if (_taskList is null || index < 0)
        {
            return;
        }

        var taskId = _taskListByIndex.TryGetValue((int)index, out var id) ? id : null;
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        SelectTask(taskId);
    }

    private void OnTaskTreeItemSelected()
    {
        if (_taskTree is null) return;

        var selected = _taskTree.GetSelected();
        if (selected is null) return;

        var taskId = selected.GetMetadata(0).AsString();
        if (string.IsNullOrWhiteSpace(taskId)) return;

        SelectTask(taskId);
    }

    private void SelectTask(string taskId)
    {
        _activeTaskId = taskId;
        _selectedTaskId = taskId;
        _pendingSelectionRefreshTaskId = taskId;
        var shortId = taskId.Length > 8 ? taskId[..8] : taskId;
        _statusLabel!.Text = $"Status: selected ({shortId})";

        if (_taskGraphState.TryGetValue(taskId, out var state))
        {
            UpdateTaskDetailsPanel(taskId, state.Status, state.Role, state.Quality, state.Adapter, state.RetryCount, state.Progress);
        }
        else
        {
            UpdateTaskDetailsPanel(taskId, "unknown", null, null, null, 0, 0);
        }

        UpdateHitlControlsState();
        SendAction("request_snapshot", taskId: taskId);
    }

    private void SendAction(
        string actionId,
        Dictionary<string, object?>? actionPayload = null,
        string? taskId = null)
    {
        if (_shuttingDown || _actionInFlight || _actionRequest is null)
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
            _actionsUrl,
            ["Content-Type: application/json"],
            HttpClient.Method.Post,
            body);

        if (error != Error.Ok)
        {
            _actionInFlight = false;
            AppendLog($"[error] Action request failed: {error}");
        }

        _actionHistory.Add($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {actionId}");
        if (_actionHistory.Count > 50) _actionHistory.RemoveAt(0);
    }

    private void OnActionRequestCompleted(long result, long responseCode, string[] headers, byte[] body)
    {
        _actionInFlight = false;
        if (_shuttingDown) return;
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

                    if (document.RootElement.TryGetProperty("status", out var statusElement) &&
                        statusElement.ValueKind == JsonValueKind.String)
                    {
                        var status = statusElement.GetString();
                        if (status == "accepted")
                        {
                            AppendLog($"[action] ✓ Accepted (http={responseCode})");
                        }
                        else if (status == "rejected")
                        {
                            AppendLog($"[action] ✗ Rejected (http={responseCode})");
                        }
                        else
                        {
                            AppendLog($"[action] Sent (http={responseCode}, status={status})");
                        }
                    }
                    else
                    {
                        AppendLog($"[action] Sent (http={responseCode})");
                    }
                }
                catch (Exception ex)
                {
                    GD.Print($"[action] Could not parse response body as JSON: {ex.Message}");
                    AppendLog($"[action] Sent (http={responseCode})");
                }
            }
            else
            {
                AppendLog($"[action] Sent (http={responseCode})");
            }

            if (!string.IsNullOrWhiteSpace(_pendingSelectionRefreshTaskId))
            {
                var taskId = _pendingSelectionRefreshTaskId;
                _pendingSelectionRefreshTaskId = null;
                SendAction("refresh_surface", taskId: taskId);
            }

            return;
        }

        var errorMsg = responseCode switch
        {
            400 => "Bad request - invalid action payload",
            401 => "Unauthorized - authentication required",
            403 => "Forbidden - action not permitted",
            404 => "Not found - task or endpoint unavailable",
            409 => "Conflict - task in incompatible state",
            429 => "Rate limited - too many requests",
            500 => "Server error - runtime failure",
            503 => "Service unavailable - runtime disconnected",
            _ => $"Unknown error (HTTP {responseCode})"
        };

        AppendLog($"[error] Action failed: {errorMsg} result={result} body={responseBody[..Math.Min(200, responseBody.Length)]}");
    }

    private void AppendLog(string line)
    {
        if (_logOutput is null)
        {
            return;
        }

        var coloredLine = line switch
        {
            var s when s.StartsWith("[error]") => $"[color=red]{line}[/color]",
            var s when s.StartsWith("[warn]") => $"[color=yellow]{line}[/color]",
            var s when s.StartsWith("[action]") && s.Contains("✓") => $"[color=green]{line}[/color]",
            var s when s.StartsWith("[HITL]") => $"[color=cyan]{line}[/color]",
            _ => line
        };

        _logOutput.AppendText($"{coloredLine}\n");
    }

    private void ReplaceTaskListFromMemory(JsonElement items)
    {
        if (_taskList is null)
        {
            return;
        }

        _taskList.Clear();
        _taskListTaskIds.Clear();
        _taskListByIndex.Clear();

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var taskId = item.TryGetProperty("taskId", out var idElement) &&
                         idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(taskId))
            {
                continue;
            }

            var status = item.TryGetProperty("status", out var statusElement) &&
                         statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? "unknown"
                : "unknown";
            var title = item.TryGetProperty("title", out var titleElement) &&
                        titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString() ?? string.Empty
                : string.Empty;
            var updatedAt = item.TryGetProperty("updatedAt", out var updatedAtElement)
                ? updatedAtElement.ToString()
                : null;

            var idx = _taskList.ItemCount;
            _taskListTaskIds[taskId] = idx;
            _taskListByIndex[idx] = taskId;
            _taskList.AddItem(FormatTaskListItem(taskId, title, status, updatedAt));

            UpdateTaskGraphState(taskId, title, status, updatedAt);

            if (string.IsNullOrWhiteSpace(_activeTaskId))
            {
                _activeTaskId = taskId;
            }

            AppendLog($"[memory-task] {taskId} {status} {title}");
        }

        if (!string.IsNullOrWhiteSpace(_activeTaskId))
        {
            SelectTaskInList(_activeTaskId);
        }
    }

    private void UpsertTaskListItem(string taskId, string title, string status, string? updatedAt)
    {
        if (_taskList is null || string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        var displayText = FormatTaskListItem(taskId, title, status, updatedAt);
        if (_taskListTaskIds.TryGetValue(taskId, out var index))
        {
            _taskList.SetItemText(index, displayText);
            return;
        }

        var newIndex = _taskList.ItemCount;
        _taskListTaskIds[taskId] = newIndex;
        _taskListByIndex[newIndex] = taskId;
        _taskList.AddItem(displayText);
    }

    private void SelectTaskInList(string taskId)
    {
        if (_taskList is null || string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        if (!_taskListTaskIds.TryGetValue(taskId, out var index))
        {
            return;
        }

        _taskList.Select(index);
    }

    private static string FormatTaskListItem(string taskId, string title, string status, string? updatedAt)
    {
        var shortId = taskId.Length <= TaskIdShortLength ? taskId : taskId[..TaskIdShortLength];
        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "unknown" : status;
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;
        if (normalizedTitle.Length > TaskTitleMaxLength)
        {
            normalizedTitle = normalizedTitle[..TaskTitleTruncatedLength] + "...";
        }

        var updatedSuffix = string.IsNullOrWhiteSpace(updatedAt)
            ? string.Empty
            : $" @ {updatedAt}";

        return $"[{normalizedStatus}] {shortId}  {normalizedTitle}{updatedSuffix}";
    }
}
