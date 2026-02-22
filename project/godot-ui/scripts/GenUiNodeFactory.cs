using System;
using System.Text.Json;
using Godot;

/// <summary>
/// Recursively builds Godot node trees from A2UI JSON component definitions.
/// Supports nested children, theme type variations, and common layout props.
/// </summary>
public static class GenUiNodeFactory
{
    private const int MaxDepth = 10;

    /// <summary>
    /// Builds a Godot node (and its children) from a JSON component element.
    /// </summary>
    /// <param name="component">JSON with {id?, type, props?, children?, theme_type_variation?}</param>
    /// <param name="onAction">Callback when any nested button is pressed, receiving the actionId</param>
    /// <param name="depth">Current recursion depth (internal)</param>
    public static Node Build(JsonElement component, Action<string>? onAction = null, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            GD.PushWarning($"GenUI: Max nesting depth ({MaxDepth}) exceeded, skipping subtree.");
            var placeholder = new Label { Text = "[max depth]" };
            return placeholder;
        }

        var type = GetString(component, "type") ?? "label";
        var id = GetString(component, "id") ?? "";
        var props = component.TryGetProperty("props", out var propsEl)
            && propsEl.ValueKind == JsonValueKind.Object
                ? propsEl
                : default;
        var themeVariation = GetString(component, "theme_type_variation");

        var node = CreateNode(type, id, props, onAction);

        if (node is Control control)
        {
            if (!string.IsNullOrWhiteSpace(themeVariation))
                control.ThemeTypeVariation = themeVariation;

            ApplyLayoutProps(control, props);
        }

        if (component.TryGetProperty("children", out var children)
            && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                node.AddChild(Build(child, onAction, depth + 1));
            }
        }

        return node;
    }

    private static Node CreateNode(string type, string id, JsonElement props, Action<string>? onAction)
    {
        Node node = type switch
        {
            "vbox" => MakeVBox(props),
            "hbox" => MakeHBox(props),
            "panel" => new PanelContainer(),
            "margin" => MakeMargin(props),
            "scroll" => new ScrollContainer(),
            "grid" => MakeGrid(props),
            "label" or "text" => MakeLabel(props),
            "button" => MakeButton(props, onAction),
            "rich_text" => MakeRichText(props),
            "line_edit" => MakeLineEdit(props),
            "progress_bar" => MakeProgressBar(props),
            "separator" or "hseparator" => new HSeparator(),
            "vseparator" => new VSeparator(),
            _ => MakeFallbackLabel(type, props)
        };

        if (!string.IsNullOrWhiteSpace(id))
            node.Name = id;

        return node;
    }

    // --- Container builders ---

    private static VBoxContainer MakeVBox(JsonElement props)
    {
        var vbox = new VBoxContainer();
        if (TryGetInt(props, "separation", out var sep))
            vbox.AddThemeConstantOverride("separation", sep);
        return vbox;
    }

    private static HBoxContainer MakeHBox(JsonElement props)
    {
        var hbox = new HBoxContainer();
        if (TryGetInt(props, "separation", out var sep))
            hbox.AddThemeConstantOverride("separation", sep);
        return hbox;
    }

    private static MarginContainer MakeMargin(JsonElement props)
    {
        var margin = new MarginContainer();
        if (TryGetInt(props, "margin_left", out var l))
            margin.AddThemeConstantOverride("margin_left", l);
        if (TryGetInt(props, "margin_top", out var t))
            margin.AddThemeConstantOverride("margin_top", t);
        if (TryGetInt(props, "margin_right", out var r))
            margin.AddThemeConstantOverride("margin_right", r);
        if (TryGetInt(props, "margin_bottom", out var b))
            margin.AddThemeConstantOverride("margin_bottom", b);
        return margin;
    }

    private static GridContainer MakeGrid(JsonElement props)
    {
        var grid = new GridContainer();
        if (TryGetInt(props, "columns", out var cols))
            grid.Columns = Math.Max(1, cols);
        return grid;
    }

    // --- Content builders ---

    private static Label MakeLabel(JsonElement props)
    {
        var label = new Label();
        label.Text = GetString(props, "text") ?? GetString(props, "label") ?? "";
        if (TryGetBool(props, "autowrap", out var autowrap) && autowrap)
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return label;
    }

    private static RichTextLabel MakeRichText(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var text = GetString(props, "text") ?? "";
        if (TryGetBool(props, "bbcode", out var bbcode) && bbcode)
        {
            rtl.BbcodeEnabled = true;
            rtl.Text = text;
        }
        else
        {
            rtl.Text = text;
        }

        if (TryGetBool(props, "scroll", out var scroll))
            rtl.ScrollActive = scroll;

        rtl.FitContent = true;
        return rtl;
    }

    // --- Interactive builders ---

    private static Button MakeButton(JsonElement props, Action<string>? onAction)
    {
        var button = new Button();
        button.Text = GetString(props, "label") ?? GetString(props, "text") ?? "Action";

        if (TryGetBool(props, "disabled", out var disabled))
            button.Disabled = disabled;

        var actionId = GetString(props, "actionId") ?? GetString(props, "action_id") ?? "";
        if (!string.IsNullOrWhiteSpace(actionId) && onAction is not null)
        {
            var capturedId = actionId;
            button.Pressed += () => onAction(capturedId);
        }

        return button;
    }

    private static LineEdit MakeLineEdit(JsonElement props)
    {
        var edit = new LineEdit();
        edit.Text = GetString(props, "text") ?? "";
        edit.PlaceholderText = GetString(props, "placeholder") ?? "";
        if (TryGetBool(props, "editable", out var editable))
            edit.Editable = editable;
        return edit;
    }

    private static ProgressBar MakeProgressBar(JsonElement props)
    {
        var bar = new ProgressBar();
        if (TryGetDouble(props, "min", out var min))
            bar.MinValue = min;
        if (TryGetDouble(props, "max", out var max))
            bar.MaxValue = max;
        if (TryGetDouble(props, "value", out var val))
            bar.Value = val;
        return bar;
    }

    // --- Fallback ---

    private static Label MakeFallbackLabel(string type, JsonElement props)
    {
        GD.PushWarning($"GenUI: Unsupported node type '{type}', falling back to Label.");
        return MakeLabel(props);
    }

    // --- Common layout props ---

    private static void ApplyLayoutProps(Control control, JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object)
            return;

        if (TryGetInt(props, "size_flags_h", out var flagsH))
            control.SizeFlagsHorizontal = (Control.SizeFlags)flagsH;
        if (TryGetInt(props, "size_flags_v", out var flagsV))
            control.SizeFlagsVertical = (Control.SizeFlags)flagsV;

        var minX = 0f;
        var minY = 0f;
        if (TryGetDouble(props, "min_size_x", out var msX)) minX = (float)msX;
        if (TryGetDouble(props, "min_size_y", out var msY)) minY = (float)msY;
        if (minX > 0 || minY > 0)
            control.CustomMinimumSize = new Vector2(minX, minY);

        var tooltip = GetString(props, "tooltip");
        if (!string.IsNullOrWhiteSpace(tooltip))
            control.TooltipText = tooltip;

        if (TryGetBool(props, "visible", out var visible))
            control.Visible = visible;
    }

    // --- JSON helpers ---

    private static string? GetString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static bool TryGetInt(JsonElement el, string key, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) { value = v.GetInt32(); return true; }
        return false;
    }

    private static bool TryGetDouble(JsonElement el, string key, out double value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) { value = v.GetDouble(); return true; }
        return false;
    }

    private static bool TryGetBool(JsonElement el, string key, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.True) { value = true; return true; }
        if (v.ValueKind == JsonValueKind.False) { value = false; return true; }
        return false;
    }
}
