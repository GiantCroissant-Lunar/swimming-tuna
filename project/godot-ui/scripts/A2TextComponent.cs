using Godot;

public partial class A2TextComponent : HBoxContainer
{
    private Label? _label;

    public override void _Ready()
    {
        _label = GetNode<Label>("Label");
    }

    public void Configure(string componentId, string text)
    {
        Name = string.IsNullOrWhiteSpace(componentId) ? "a2-text" : componentId;
        _label ??= GetNode<Label>("Label");
        _label.Text = text;
    }
}
