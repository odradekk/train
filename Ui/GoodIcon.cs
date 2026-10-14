using Godot;

namespace Train.Ui;

internal partial class GoodIcon : Control
{
    private readonly ColorRect _frame = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly TextureRect _image = new() { MouseFilter = MouseFilterEnum.Ignore,
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
    private readonly Label _quantity = new() { MouseFilter = MouseFilterEnum.Ignore,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Label _missing = new() { Text = "?", MouseFilter = MouseFilterEnum.Ignore,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Shader _frameShader = new() { Code = """
        shader_type canvas_item;
        uniform vec4 border : source_color = vec4(0.42, 0.46, 0.50, 1.0);
        uniform float width = 0.03;
        uniform bool unavailable = false;
        void fragment() {
            vec4 tint = COLOR;
            vec2 q = abs(UV - vec2(0.5)) - vec2(0.39);
            float distance = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - 0.11;
            vec4 fill = mix(vec4(0.8902, 0.9059, 0.9176, 1.0), vec4(0.7608, 0.7882, 0.8157, 1.0), UV.y);
            COLOR = distance > -width ? border : fill;
            COLOR.a *= 1.0 - smoothstep(-0.005, 0.0, distance);
            if (unavailable) COLOR.rgb = vec3(dot(COLOR.rgb, vec3(0.299, 0.587, 0.114)));
            COLOR *= tint;
        }
        """ };
    private readonly Shader _imageShader = new() { Code = """
        shader_type canvas_item;
        uniform bool unavailable = false;
        void fragment() {
            COLOR = texture(TEXTURE, UV) * COLOR;
            if (unavailable) COLOR.rgb = vec3(dot(COLOR.rgb, vec3(0.299, 0.587, 0.114)));
        }
        """ };
    private readonly ShaderMaterial _frameMaterial;
    private readonly ShaderMaterial _imageMaterial;
    private readonly FontVariation _font = UiStyle.NumericFont(true);
    private bool _selected, _unavailable, _hover;
    internal bool Selected { get => _selected; set { _selected = value; Refresh(); } }
    internal bool Unavailable { get => _unavailable; set { _unavailable = value; Refresh(); } }
    private long? _quantityValue;
    internal long? Quantity { get => _quantityValue; set { _quantityValue = value; _quantity.Text = value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""; } }

    internal GoodIcon(int size)
    {
        CustomMinimumSize = new(size, size);
        Size = CustomMinimumSize;
        SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        MouseFilter = MouseFilterEnum.Pass;
        _frameMaterial = new() { Shader = _frameShader };
        _imageMaterial = new() { Shader = _imageShader };
        _frame.Material = _frameMaterial;
        _image.Material = _imageMaterial;
        AddChild(_frame); AddChild(_image); AddChild(_missing); AddChild(_quantity);
        foreach (var child in new Control[] { _frame, _missing, _quantity }) child.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _image.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _image.AnchorLeft = _image.AnchorTop = 0.11f;
        _image.AnchorRight = _image.AnchorBottom = 0.89f;
        _quantity.AddThemeFontOverride("font", _font);
        _quantity.AddThemeFontSizeOverride("font_size", Math.Max(12, size / 3));
        _quantity.AddThemeColorOverride("font_color", new Color("1a1a1a"));
        _quantity.AddThemeColorOverride("font_outline_color", Colors.White);
        _quantity.AddThemeConstantOverride("outline_size", 3);
        _missing.AddThemeColorOverride("font_color", new Color("c9423a"));
        _missing.AddThemeFontSizeOverride("font_size", size / 2);
        MouseEntered += () => { _hover = true; Refresh(); };
        MouseExited += () => { _hover = false; Refresh(); };
        Refresh();
    }

    internal void SetTexture(Texture2D? texture, string? failure = null)
    {
        _image.Texture = texture;
        _missing.Visible = texture is null;
        TooltipText = failure ?? "";
    }

    private void Refresh()
    {
        _frameMaterial.SetShaderParameter("border", new Color(_selected ? "e0a22e" : _hover ? "3f4751" : "6b7580"));
        _frameMaterial.SetShaderParameter("width", _selected ? 0.06f : 0.03f);
        _frameMaterial.SetShaderParameter("unavailable", _unavailable);
        _imageMaterial.SetShaderParameter("unavailable", _unavailable);
        _missing.Modulate = _unavailable ? new Color(0.65f, 0.65f, 0.65f) : Colors.White;
        Modulate = new Color(1, 1, 1, _unavailable ? 0.45f : 1);
    }

    public override void _ExitTree()
    {
        _image.Texture = null;
        _frame.Material = null; _image.Material = null;
        _frameMaterial.Dispose(); _imageMaterial.Dispose();
        _frameShader.Dispose(); _imageShader.Dispose(); _font.Dispose();
    }
}

