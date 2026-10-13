using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class WorldSceneView : Node3D
{
    private readonly CanvasLayer _overlay = new() { Layer = 5 };
    private readonly ColorRect _loading = new()
    {
        Color = new Color(0.06f, 0.08f, 0.1f, 0.85f), MouseFilter = Control.MouseFilterEnum.Stop
    };
    private readonly Label _message = new() { Text = "正在加载场景…", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
    internal WorldCamera CameraRig { get; } = new() { Name = "WorldCamera" };
    internal bool Loading => _loading.Visible;
    internal bool Interactive { get; set; }
    internal event Action<Vector2>? PickRequested;
    internal event Action? ReturnRequested;
    private readonly Godot.Environment _environment;
    private readonly Sky _sky;
    private readonly ProceduralSkyMaterial _skyMaterial;

    internal WorldSceneView()
    {
        Name = "WorldSceneView";
        AddChild(CameraRig);
        _skyMaterial = new() { SkyHorizonColor = new Color("c9d6e0"), GroundHorizonColor = new Color("c9d6e0") };
        _sky = new() { SkyMaterial = _skyMaterial };
        _environment = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = _sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx, SsaoEnabled = true,
            SsaoIntensity = 0.5f, GlowEnabled = true, GlowBloom = 0,
            FogEnabled = true, FogLightColor = new Color("c9d6e0"), FogDensity = 0.001f
        };
        AddChild(new WorldEnvironment { Environment = _environment });
        AddChild(new DirectionalLight3D { Name = "Sun", RotationDegrees = new Vector3(-40, -30, 0),
            LightColor = new Color(1, 0.96f, 0.9f), ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits });
        AddChild(_overlay);
        _overlay.AddChild(_loading);
        _loading.AddChild(_message);
        _loading.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _message.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        CameraRig.Camera.Current = true;
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        GetViewport().UseDebanding = true;
    }

    internal void SetLoading(bool loading) => _loading.Visible = loading;
    internal void ShowFrame(ISceneFrame frame, WorldObjectId? follow)
    {
        CameraRig.DisplayedFrame(frame, follow);
        SetLoading(false);
        Interactive = true;
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if (!IsVisibleInTree()) return;
        if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            ReturnRequested?.Invoke();
        else if (Interactive && input is InputEventMouseButton { Pressed: true } button)
        {
            if (button.ButtonIndex == MouseButton.Left) PickRequested?.Invoke(button.Position);
            else if (button.ButtonIndex == MouseButton.WheelUp) CameraRig.Zoom(1);
            else if (button.ButtonIndex == MouseButton.WheelDown) CameraRig.Zoom(-1);
            else return;
        }
        else if (Interactive && input is InputEventMouseMotion motion)
        {
            if (motion.ButtonMask.HasFlag(MouseButtonMask.Right)) CameraRig.Orbit(motion.Relative.X, motion.Relative.Y);
            else if (motion.ButtonMask.HasFlag(MouseButtonMask.Middle))
                CameraRig.Pan(new Vector3(-motion.Relative.X, 0, -motion.Relative.Y) * 0.2f);
            else return;
        }
        else return;
        GetViewport().SetInputAsHandled();
    }

    internal void Release()
    {
        Free();
        _environment.Dispose();
        _sky.Dispose();
        _skyMaterial.Dispose();
    }
}
