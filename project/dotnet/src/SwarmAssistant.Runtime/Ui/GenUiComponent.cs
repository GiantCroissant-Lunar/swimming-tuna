namespace SwarmAssistant.Runtime.Ui;

/// <summary>
/// Typed representation of a nested A2UI component tree.
/// Serialized to JSON by A2UiPayloadFactory for the Godot frontend GenUiNodeFactory to render.
/// </summary>
public sealed record GenUiComponent(
    string Id,
    string Type,
    Dictionary<string, object?>? Props = null,
    GenUiComponent[]? Children = null,
    string? ThemeTypeVariation = null)
{
    // --- Container factories ---

    public static GenUiComponent VBox(string id, params GenUiComponent[] children)
        => new(id, "vbox", Children: children);

    public static GenUiComponent VBox(string id, int separation, params GenUiComponent[] children)
        => new(id, "vbox", new Dictionary<string, object?> { ["separation"] = separation }, Children: children);

    public static GenUiComponent HBox(string id, params GenUiComponent[] children)
        => new(id, "hbox", Children: children);

    public static GenUiComponent HBox(string id, int separation, params GenUiComponent[] children)
        => new(id, "hbox", new Dictionary<string, object?> { ["separation"] = separation }, Children: children);

    public static GenUiComponent Panel(string id, params GenUiComponent[] children)
        => new(id, "panel", Children: children);

    public static GenUiComponent Panel(string id, string themeVariation, params GenUiComponent[] children)
        => new(id, "panel", Children: children, ThemeTypeVariation: themeVariation);

    public static GenUiComponent Margin(
        string id,
        int left = 0, int top = 0, int right = 0, int bottom = 0,
        params GenUiComponent[] children)
        => new(id, "margin", new Dictionary<string, object?>
        {
            ["margin_left"] = left,
            ["margin_top"] = top,
            ["margin_right"] = right,
            ["margin_bottom"] = bottom
        }, Children: children);

    public static GenUiComponent Margin(string id, int all, params GenUiComponent[] children)
        => Margin(id, all, all, all, all, children);

    public static GenUiComponent Scroll(string id, params GenUiComponent[] children)
        => new(id, "scroll", Children: children);

    public static GenUiComponent Grid(string id, int columns, params GenUiComponent[] children)
        => new(id, "grid", new Dictionary<string, object?> { ["columns"] = columns }, Children: children);

    // --- Content factories ---

    public static GenUiComponent Text(string id, string text, string? themeVariation = null, string? fontColor = null, int? fontSize = null)
    {
        var props = new Dictionary<string, object?> { ["text"] = text };
        if (fontColor != null)
            props["font_color"] = fontColor;
        if (fontSize != null)
            props["font_size"] = fontSize;
        return new(id, "text", props, ThemeTypeVariation: themeVariation);
    }

    public static GenUiComponent RichText(string id, string text, bool bbcode = false, string? fontColor = null, int? fontSize = null)
    {
        var props = new Dictionary<string, object?> { ["text"] = text, ["bbcode"] = bbcode };
        if (fontColor != null)
            props["font_color"] = fontColor;
        if (fontSize != null)
            props["font_size"] = fontSize;
        return new(id, "rich_text", props);
    }

    public static GenUiComponent Separator(string id = "sep")
        => new(id, "separator");

    public static GenUiComponent VSeparator(string id = "vsep")
        => new(id, "vseparator");

    // --- Interactive factories ---

    public static GenUiComponent Button(string id, string label, string actionId)
        => new(id, "button", new Dictionary<string, object?> { ["label"] = label, ["actionId"] = actionId });

    public static GenUiComponent LineEdit(string id, string placeholder = "", string text = "")
        => new(id, "line_edit", new Dictionary<string, object?>
        {
            ["text"] = text,
            ["placeholder"] = placeholder
        });

    public static GenUiComponent ProgressBar(string id, double value, double min = 0, double max = 100)
        => new(id, "progress_bar", new Dictionary<string, object?>
        {
            ["value"] = value,
            ["min"] = min,
            ["max"] = max
        });
}
