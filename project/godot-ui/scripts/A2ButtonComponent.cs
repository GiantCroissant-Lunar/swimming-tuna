using Godot;

public partial class A2ButtonComponent : HBoxContainer
{
    [Signal]
    public delegate void ActionRequestedEventHandler(string actionId);

    private Button? _button;
    private string _actionId = "unknown_action";

    public override void _Ready()
    {
        _button = GetNode<Button>("Button");
        _button.Pressed += HandlePressed;
    }

    public override void _ExitTree()
    {
        if (_button is not null)
        {
            _button.Pressed -= HandlePressed;
        }
    }

    public void Configure(string componentId, string label, string actionId)
    {
        Name = string.IsNullOrWhiteSpace(componentId) ? "a2-button" : componentId;
        _actionId = string.IsNullOrWhiteSpace(actionId) ? "unknown_action" : actionId;
        _button ??= GetNode<Button>("Button");
        _button.Text = label;
    }

    private void HandlePressed()
    {
        EmitSignal(SignalName.ActionRequested, _actionId);
    }
}
