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

    // Compiled regex patterns for ConvertMarkdownToBbcode
    private static readonly System.Text.RegularExpressions.Regex RxCodeBlock =
        new(@"```(\w+)?\n([\s\S]*?)```", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxInlineCode =
        new(@"`(.+?)`", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxHeader3 =
        new(@"^### (.+)$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex RxHeader2 =
        new(@"^## (.+)$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex RxHeader1 =
        new(@"^# (.+)$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);
    private static readonly System.Text.RegularExpressions.Regex RxBoldItalic =
        new(@"\*\*\*(.+?)\*\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxBold =
        new(@"\*\*(.+?)\*\*", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex RxItalic =
        new(@"\*(.+?)\*", System.Text.RegularExpressions.RegexOptions.Compiled);

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
            ApplyStyleProps(control, props);
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
            // Containers
            "vbox" => MakeVBox(props),
            "hbox" => MakeHBox(props),
            "panel" => MakePanel(props),
            "margin" => MakeMargin(props),
            "scroll" => MakeScroll(props),
            "grid" => MakeGrid(props),
            "tab" => MakeTabContainer(props),
            "split" => MakeSplitContainer(props),

            // Content display
            "label" or "text" => MakeLabel(props),
            "rich_text" => MakeRichText(props),
            "code" => MakeCodeBlock(props),
            "diff" => MakeDiffBlock(props),
            "markdown" => MakeMarkdownBlock(props),
            "json" => MakeJsonBlock(props),

            // Interactive elements
            "button" => MakeButton(props, onAction),
            "line_edit" => MakeLineEdit(props),
            "text_edit" => MakeTextEdit(props),
            "spin_box" => MakeSpinBox(props),
            "slider" => MakeSlider(props),

            // Progress and status
            "progress_bar" => MakeProgressBar(props),
            "status_indicator" => MakeStatusIndicator(props),
            "badge" => MakeBadge(props),

            // Separators
            "separator" or "hseparator" => new HSeparator(),
            "vseparator" => new VSeparator(),

            // Fallback
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
        if (TryGetEnum<HBoxContainer.AlignmentMode>(props, "alignment", out var align))
            vbox.Alignment = align;
        return vbox;
    }

    private static HBoxContainer MakeHBox(JsonElement props)
    {
        var hbox = new HBoxContainer();
        if (TryGetInt(props, "separation", out var sep))
            hbox.AddThemeConstantOverride("separation", sep);
        if (TryGetEnum<HBoxContainer.AlignmentMode>(props, "alignment", out var align))
            hbox.Alignment = align;
        return hbox;
    }

    private static PanelContainer MakePanel(JsonElement props)
    {
        var panel = new PanelContainer();

        // Apply custom panel style if specified
        if (TryGetString(props, "style", out var style))
        {
            var styleBox = new StyleBoxFlat();
            styleBox = style switch
            {
                "info" => new StyleBoxFlat { BgColor = new Color(0.15f, 0.2f, 0.3f), BorderColor = new Color(0.3f, 0.5f, 0.8f) },
                "success" => new StyleBoxFlat { BgColor = new Color(0.1f, 0.25f, 0.15f), BorderColor = new Color(0.3f, 0.7f, 0.4f) },
                "warning" => new StyleBoxFlat { BgColor = new Color(0.25f, 0.2f, 0.1f), BorderColor = new Color(0.8f, 0.7f, 0.3f) },
                "error" => new StyleBoxFlat { BgColor = new Color(0.25f, 0.1f, 0.1f), BorderColor = new Color(0.8f, 0.3f, 0.3f) },
                _ => styleBox
            };
            styleBox.BorderWidthBottom = 2;
            styleBox.BorderWidthLeft = 2;
            styleBox.BorderWidthRight = 2;
            styleBox.BorderWidthTop = 2;
            panel.AddThemeStyleboxOverride("panel", styleBox);
        }

        return panel;
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
        if (TryGetInt(props, "margin", out var all))
        {
            margin.AddThemeConstantOverride("margin_left", all);
            margin.AddThemeConstantOverride("margin_top", all);
            margin.AddThemeConstantOverride("margin_right", all);
            margin.AddThemeConstantOverride("margin_bottom", all);
        }
        return margin;
    }

    private static ScrollContainer MakeScroll(JsonElement props)
    {
        var scroll = new ScrollContainer();
        if (TryGetBool(props, "horizontal", out var h))
            scroll.HorizontalScrollMode = h ? ScrollContainer.ScrollMode.ShowAlways : ScrollContainer.ScrollMode.Disabled;
        if (TryGetBool(props, "vertical", out var v))
            scroll.VerticalScrollMode = v ? ScrollContainer.ScrollMode.ShowAlways : ScrollContainer.ScrollMode.Disabled;
        return scroll;
    }

    private static GridContainer MakeGrid(JsonElement props)
    {
        var grid = new GridContainer();
        if (TryGetInt(props, "columns", out var cols))
            grid.Columns = Math.Max(1, cols);
        return grid;
    }

    private static TabContainer MakeTabContainer(JsonElement props)
    {
        var tabs = new TabContainer();
        if (TryGetString(props, "tab_position", out var pos))
        {
            tabs.TabAlignment = pos switch
            {
                "left" => TabBar.AlignmentMode.Left,
                "center" => TabBar.AlignmentMode.Center,
                "right" => TabBar.AlignmentMode.Right,
                _ => TabBar.AlignmentMode.Left
            };
        }
        return tabs;
    }

    private static SplitContainer MakeSplitContainer(JsonElement props)
    {
        var split = new SplitContainer();
        if (TryGetInt(props, "offset", out var offset))
            split.SplitOffsets = [offset];
        return split;
    }

    // --- Content builders ---

    private static Label MakeLabel(JsonElement props)
    {
        var label = new Label();
        label.Text = GetString(props, "text") ?? GetString(props, "label") ?? "";
        if (TryGetBool(props, "autowrap", out var autowrap) && autowrap)
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (TryGetInt(props, "font_size", out var fontSize))
            label.AddThemeFontSizeOverride("font_size", fontSize);
        if (TryGetEnum<HorizontalAlignment>(props, "halign", out var halign))
            label.HorizontalAlignment = halign;
        if (TryGetEnum<VerticalAlignment>(props, "valign", out var valign))
            label.VerticalAlignment = valign;

        // Color support
        if (TryGetString(props, "font_color", out var fontColorStr))
        {
            label.AddThemeColorOverride("font_color", ParseColor(fontColorStr));
        }
        else if (TryGetString(props, "color", out var colorStr))
        {
            label.AddThemeColorOverride("font_color", ParseColor(colorStr));
        }

        return label;
    }

    private static RichTextLabel MakeRichText(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var text = GetString(props, "text") ?? "";

        rtl.BbcodeEnabled = true;
        rtl.Text = text;

        if (TryGetBool(props, "scroll", out var scroll))
            rtl.ScrollActive = scroll;

        rtl.FitContent = TryGetBool(props, "fit_content", out var fit) ? fit : true;

        if (TryGetInt(props, "font_size", out var fontSize))
            rtl.AddThemeFontSizeOverride("normal_font_size", fontSize);

        if (TryGetString(props, "font_color", out var fontColorStr))
            rtl.AddThemeColorOverride("default_color", ParseColor(fontColorStr));

        return rtl;
    }

    private static RichTextLabel MakeCodeBlock(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var code = GetString(props, "code") ?? GetString(props, "text") ?? "";
        var language = GetString(props, "language") ?? "text";

        rtl.BbcodeEnabled = true;
        rtl.Text = $"[color=#6c99ff]{language}[/color]\n[code]{EscapeBbcode(code)}[/code]";
        rtl.ScrollActive = true;
        rtl.FitContent = false;

        // Apply monospace font styling if available
        rtl.AddThemeFontSizeOverride("normal_font_size", 12);

        // Set custom minimum size for code blocks
        if (TryGetInt(props, "lines", out var lines))
        {
            rtl.CustomMinimumSize = new Vector2(0, Math.Max(60, lines * 16));
        }
        else
        {
            rtl.CustomMinimumSize = new Vector2(0, 120);
        }

        return rtl;
    }

    private static RichTextLabel MakeDiffBlock(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var diff = GetString(props, "diff") ?? GetString(props, "text") ?? "";

        rtl.BbcodeEnabled = true;

        // Process diff lines with color coding
        var lines = diff.Split('\n');
        var formatted = new System.Text.StringBuilder();
        formatted.AppendLine("[b]Diff[/b]");

        foreach (var line in lines)
        {
            var escaped = EscapeBbcode(line);
            if (line.StartsWith("+"))
            {
                formatted.AppendLine($"[color=#4caf50]{escaped}[/color]");
            }
            else if (line.StartsWith("-"))
            {
                formatted.AppendLine($"[color=#f44336]{escaped}[/color]");
            }
            else if (line.StartsWith("@@"))
            {
                formatted.AppendLine($"[color=#2196f3]{escaped}[/color]");
            }
            else
            {
                formatted.AppendLine(escaped);
            }
        }

        rtl.Text = formatted.ToString();
        rtl.ScrollActive = true;
        rtl.FitContent = false;
        rtl.CustomMinimumSize = new Vector2(0, 150);

        return rtl;
    }

    private static RichTextLabel MakeMarkdownBlock(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var markdown = GetString(props, "markdown") ?? GetString(props, "text") ?? "";

        rtl.BbcodeEnabled = true;

        // Simple markdown to BBCode conversion
        var bbcode = ConvertMarkdownToBbcode(markdown);
        rtl.Text = bbcode;

        rtl.FitContent = true;
        if (TryGetBool(props, "scroll", out var scroll))
            rtl.ScrollActive = scroll;

        return rtl;
    }

    private static RichTextLabel MakeJsonBlock(JsonElement props)
    {
        var rtl = new RichTextLabel();
        var json = GetString(props, "json") ?? "";

        rtl.BbcodeEnabled = true;

        // Pretty print and syntax highlight JSON
        try
        {
            using var doc = JsonDocument.Parse(json);
            var pretty = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            var highlighted = SyntaxHighlightJson(pretty);
            rtl.Text = $"[code]{highlighted}[/code]";
        }
        catch
        {
            rtl.Text = $"[code]{EscapeBbcode(json)}[/code]";
        }

        rtl.ScrollActive = true;
        rtl.FitContent = false;
        rtl.CustomMinimumSize = new Vector2(0, 120);

        return rtl;
    }

    // --- Interactive builders ---

    private static Button MakeButton(JsonElement props, Action<string>? onAction)
    {
        var button = new Button();
        button.Text = GetString(props, "label") ?? GetString(props, "text") ?? "Action";

        if (TryGetBool(props, "disabled", out var disabled))
            button.Disabled = disabled;

        if (TryGetString(props, "variant", out var variant))
        {
            button.ThemeTypeVariation = variant switch
            {
                "primary" => "PrimaryButton",
                "secondary" => "SecondaryButton",
                "danger" => "DangerButton",
                _ => ""
            };
        }

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
        if (TryGetBool(props, "secret", out var secret))
            edit.Secret = secret;
        if (TryGetBool(props, "clear_button", out var clearButton))
            edit.ClearButtonEnabled = clearButton;
        return edit;
    }

    private static TextEdit MakeTextEdit(JsonElement props)
    {
        var edit = new TextEdit();
        edit.Text = GetString(props, "text") ?? "";
        edit.PlaceholderText = GetString(props, "placeholder") ?? "";
        if (TryGetBool(props, "editable", out var editable))
            edit.Editable = editable;
        if (TryGetBool(props, "wrap", out var wrap))
            edit.WrapMode = wrap ? TextEdit.LineWrappingMode.Boundary : TextEdit.LineWrappingMode.None;

        // Set minimum lines
        if (TryGetInt(props, "min_lines", out var minLines))
        {
            edit.CustomMinimumSize = new Vector2(0, Math.Max(60, minLines * 20));
        }
        else
        {
            edit.CustomMinimumSize = new Vector2(0, 100);
        }

        return edit;
    }

    private static SpinBox MakeSpinBox(JsonElement props)
    {
        var spin = new SpinBox();
        if (TryGetDouble(props, "min", out var min))
            spin.MinValue = min;
        if (TryGetDouble(props, "max", out var max))
            spin.MaxValue = max;
        if (TryGetDouble(props, "value", out var val))
            spin.Value = val;
        if (TryGetDouble(props, "step", out var step))
            spin.Step = step;
        return spin;
    }

    private static Slider MakeSlider(JsonElement props)
    {
        Slider slider = TryGetBool(props, "vertical", out var vertical) && vertical
            ? new VSlider()
            : new HSlider();

        if (TryGetDouble(props, "min", out var min))
            slider.MinValue = min;
        if (TryGetDouble(props, "max", out var max))
            slider.MaxValue = max;
        if (TryGetDouble(props, "value", out var val))
            slider.Value = val;
        if (TryGetDouble(props, "step", out var step))
            slider.Step = step;

        return slider;
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

        if (TryGetBool(props, "show_percent", out var showPercent))
            bar.ShowPercentage = showPercent;

        return bar;
    }

    private static Control MakeStatusIndicator(JsonElement props)
    {
        var status = GetString(props, "status") ?? "unknown";
        var text = GetString(props, "text") ?? status;

        var container = new HBoxContainer();

        // Status dot
        var dot = new Panel
        {
            CustomMinimumSize = new Vector2(12, 12)
        };
        var dotStyle = new StyleBoxFlat
        {
            BgColor = status.ToLower() switch
            {
                "success" or "ok" or "completed" => new Color(0.2f, 0.8f, 0.2f),
                "error" or "failed" or "critical" => new Color(0.8f, 0.2f, 0.2f),
                "warning" or "warn" or "degraded" => new Color(0.9f, 0.7f, 0.2f),
                "info" or "running" or "active" => new Color(0.2f, 0.6f, 0.9f),
                "pending" or "queued" => new Color(0.6f, 0.6f, 0.6f),
                _ => new Color(0.5f, 0.5f, 0.5f)
            },
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6
        };
        dot.AddThemeStyleboxOverride("panel", dotStyle);
        container.AddChild(dot);

        // Status text
        var label = new Label { Text = text };
        container.AddChild(label);

        return container;
    }

    private static Label MakeBadge(JsonElement props)
    {
        var label = new Label();
        label.Text = GetString(props, "text") ?? "";

        var variant = GetString(props, "variant") ?? "default";
        var style = new StyleBoxFlat
        {
            BgColor = variant.ToLower() switch
            {
                "success" => new Color(0.15f, 0.6f, 0.25f),
                "error" => new Color(0.7f, 0.2f, 0.2f),
                "warning" => new Color(0.8f, 0.6f, 0.15f),
                "info" => new Color(0.2f, 0.5f, 0.8f),
                "primary" => new Color(0.3f, 0.4f, 0.8f),
                _ => new Color(0.4f, 0.4f, 0.4f)
            },
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginBottom = 2,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 2
        };

        label.AddThemeStyleboxOverride("normal", style);
        label.AddThemeColorOverride("font_color", new Color(1, 1, 1));

        return label;
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
        if (TryGetInt(props, "min_width", out var mw)) minX = mw;
        if (TryGetInt(props, "min_height", out var mh)) minY = mh;
        if (minX > 0 || minY > 0)
            control.CustomMinimumSize = new Vector2(minX, minY);

        var tooltip = GetString(props, "tooltip");
        if (!string.IsNullOrWhiteSpace(tooltip))
            control.TooltipText = tooltip;

        if (TryGetBool(props, "visible", out var visible))
            control.Visible = visible;

        if (TryGetBool(props, "disabled", out var disabled) && control is BaseButton btn)
            btn.Disabled = disabled;
    }

    private static void ApplyStyleProps(Control control, JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object)
            return;

        if (TryGetString(props, "font_color", out var fontColorStr))
        {
            var color = ParseColor(fontColorStr);
            control.AddThemeColorOverride("font_color", color);
            if (control is RichTextLabel rtl)
                rtl.AddThemeColorOverride("default_color", color);
        }

        if (TryGetInt(props, "font_size", out var fontSize) && fontSize > 0)
        {
            control.AddThemeFontSizeOverride("font_size", fontSize);
            if (control is RichTextLabel rtl)
                rtl.AddThemeFontSizeOverride("normal_font_size", fontSize);
        }

        if (control is PanelContainer panel && TryGetString(props, "bg_color", out var bgColorStr))
        {
            var styleBox = new StyleBoxFlat
            {
                BgColor = ParseColor(bgColorStr),
                BorderWidthBottom = 1,
                BorderWidthLeft = 1,
                BorderWidthRight = 1,
                BorderWidthTop = 1,
                BorderColor = new Color(0.25f, 0.25f, 0.25f)
            };
            panel.AddThemeStyleboxOverride("panel", styleBox);
        }
    }

    // --- Helper methods ---

    private static string EscapeBbcode(string text)
    {
        // Two-pass: use placeholder to avoid mangling the [lb] marker itself
        return text
            .Replace("[", "\x01OPEN\x01")
            .Replace("]", "[rb]")
            .Replace("\x01OPEN\x01", "[lb]");
    }

    private static string ConvertMarkdownToBbcode(string markdown)
    {
        // Step 1: replace fenced code blocks, escaping their content so BBCode isn't injected
        var result = RxCodeBlock.Replace(markdown,
            m => "[code]" + EscapeBbcode(m.Groups[2].Value) + "[/code]");

        // Step 2: replace inline code, escaping their content
        result = RxInlineCode.Replace(result,
            m => "[code]" + EscapeBbcode(m.Groups[1].Value) + "[/code]");

        // Step 3: apply markdown transforms only on non-code segments
        result = RxHeader3.Replace(result, "[b]$1[/b]");
        result = RxHeader2.Replace(result, "[b][size=14]$1[/size][/b]");
        result = RxHeader1.Replace(result, "[b][size=16]$1[/size][/b]");
        result = RxBoldItalic.Replace(result, "[b][i]$1[/i][/b]");
        result = RxBold.Replace(result, "[b]$1[/b]");
        result = RxItalic.Replace(result, "[i]$1[/i]");

        return result;
    }

    private static string SyntaxHighlightJson(string json)
    {
        // Simple JSON syntax highlighting
        var result = EscapeBbcode(json);

        // Strings (property names and values)
        result = System.Text.RegularExpressions.Regex.Replace(result,
            "\"([^\"]+)\"",
            "[color=#ce9178]\"$1\"[/color]");

        // Numbers
        result = System.Text.RegularExpressions.Regex.Replace(result,
            ": ([0-9]+(\\.[0-9]+)?)",
            ": [color=#b5cea8]$1[/color]");

        // Booleans and null
        result = System.Text.RegularExpressions.Regex.Replace(result,
            ": (true|false|null)",
            ": [color=#569cd6]$1[/color]");

        return result;
    }

    private static Color ParseColor(string colorStr)
    {
        try
        {
            if (colorStr.StartsWith("#") && colorStr.Length == 7)
            {
                var r = Convert.ToInt32(colorStr.Substring(1, 2), 16) / 255f;
                var g = Convert.ToInt32(colorStr.Substring(3, 2), 16) / 255f;
                var b = Convert.ToInt32(colorStr.Substring(5, 2), 16) / 255f;
                return new Color(r, g, b);
            }
        }
        catch { }

        // Named colors
        return colorStr.ToLower() switch
        {
            "red" => new Color(0.9f, 0.2f, 0.2f),
            "green" => new Color(0.2f, 0.8f, 0.2f),
            "blue" => new Color(0.2f, 0.5f, 0.9f),
            "yellow" => new Color(0.9f, 0.9f, 0.2f),
            "orange" => new Color(0.9f, 0.6f, 0.2f),
            "purple" => new Color(0.7f, 0.3f, 0.9f),
            "gray" or "grey" => new Color(0.5f, 0.5f, 0.5f),
            "white" => new Color(1, 1, 1),
            "black" => new Color(0, 0, 0),
            _ => new Color(1, 1, 1)
        };
    }

    // --- JSON helpers ---

    private static string? GetString(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static bool TryGetString(JsonElement el, string key, out string value)
    {
        value = "";
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString() ?? "";
            return true;
        }
        return false;
    }

    private static bool TryGetInt(JsonElement el, string key, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value)) return true;
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

    private static bool TryGetEnum<T>(JsonElement el, string key, out T value) where T : struct, Enum
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty(key, out var v)) return false;
        if (v.ValueKind != JsonValueKind.String) return false;

        var str = v.GetString();
        if (str is null) return false;

        return Enum.TryParse(str, true, out value);
    }
}
