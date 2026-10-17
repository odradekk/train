using Godot;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal enum InspectionLighting { Game, Daylight, Overcast, Night }

internal partial class WorldSceneView : Node3D
{
    private readonly CanvasLayer _overlay = new() { Layer = 5 };
    private readonly Label _message = new() { Text = "正在加载场景…",
        Position = new(16, 64), MouseFilter = Control.MouseFilterEnum.Ignore };
    internal WorldCamera CameraRig { get; } = new() { Name = "WorldCamera" };
    internal bool Loading => _message.Visible;
    internal bool LoadingOverlayVisible => _overlay.Visible;
    internal bool Interactive { get; set; }
    internal InspectionLighting Lighting { get; private set; }
    internal event Action<Vector2>? PickRequested;
    internal event Action? ReturnRequested;
    private readonly Godot.Environment _environment;
    private readonly WorldEnvironment _worldEnvironment;
    private readonly Sky _sky;
    private readonly ShaderMaterial _skyMaterial;
    private readonly DirectionalLight3D _sun;
    private readonly Godot.Environment _inspectionEnvironment;
    private readonly Sky _inspectionSky;
    private readonly ProceduralSkyMaterial _inspectionSkyMaterial = new();
    private readonly Node3D _workLights = new() { Name = "InspectionWorkLights", Visible = false };
    private Godot.Environment CurrentEnvironment => Lighting is InspectionLighting.Game or InspectionLighting.Daylight
        ? _environment : _inspectionEnvironment;

    internal WorldSceneView()
    {
        Name = "WorldSceneView";
        AddChild(CameraRig);
        _skyMaterial = new() { Shader = GD.Load<Shader>("res://Rendering/sky.gdshader") };
        _sky = new() { SkyMaterial = _skyMaterial };
        _environment = new()
        {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = _sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx, SsaoEnabled = true,
            // 强工作灯下的普通表面也超过 HDR 1；辉光只留给更亮的发光材质。
            SsaoIntensity = 0.5f, GlowEnabled = true, GlowBloom = 0, GlowHdrThreshold = 32,
            FogEnabled = true, FogLightColor = new Color("c9d6e0"), FogDensity = 0.001f, FogSkyAffect = 0
        };
        _worldEnvironment = new() { Name = "WorldEnvironment", Environment = _environment };
        AddChild(_worldEnvironment);
        _sun = new() { Name = "Sun", RotationDegrees = new Vector3(-40, -30, 0),
            LightColor = new Color(1, 0.96f, 0.9f), ShadowEnabled = true,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits };
        AddChild(_sun);
        _inspectionSky = new() { SkyMaterial = _inspectionSkyMaterial };
        _inspectionEnvironment = (Godot.Environment)_environment.Duplicate();
        _inspectionEnvironment.Sky = _inspectionSky;
        _inspectionEnvironment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
        _inspectionEnvironment.AmbientLightSkyContribution = 0;
        AddChild(_workLights);
        _workLights.AddChild(new OmniLight3D { Position = new(-12, 14, 8), LightColor = new Color("ffd9a0"),
            LightEnergy = 24, OmniRange = 80, ShadowEnabled = true });
        _workLights.AddChild(new OmniLight3D { Position = new(10, 9, -12), LightColor = new Color("ffd9a0"),
            LightEnergy = 16, OmniRange = 80, ShadowEnabled = true });
        AddChild(_overlay);
        _overlay.AddChild(_message);
    }

    public override void _Ready()
    {
        CameraRig.Camera.Current = Visible;
        GetViewport().Msaa3D = Viewport.Msaa.Msaa4X;
        GetViewport().UseDebanding = true;
    }

    internal void SetLoading(bool loading) => _message.Visible = loading;
    internal bool SdfgiEnabled => _environment.SdfgiEnabled;
    internal void SetMeasurementEffects(bool sdfgi, bool taa)
    {
        _environment.SdfgiEnabled = sdfgi;
        GetViewport().UseTaa = taa;
    }
    internal void SetLighting(InspectionLighting mode)
    {
        Lighting = mode;
        _sun.Visible = mode is InspectionLighting.Game or InspectionLighting.Daylight;
        _workLights.Visible = mode == InspectionLighting.Night;
        _workLights.Position = CameraRig.Focus;
        if (mode is InspectionLighting.Overcast or InspectionLighting.Night)
        {
            var night = mode == InspectionLighting.Night;
            var horizon = new Color(night ? "101928" : "c9d6e0");
            _inspectionSkyMaterial.SkyTopColor = new Color(night ? "080e1a" : "c9d6e0");
            _inspectionSkyMaterial.SkyHorizonColor = horizon;
            _inspectionSkyMaterial.GroundHorizonColor = horizon;
            _inspectionSkyMaterial.GroundBottomColor = new Color(night ? "050811" : "aab5be");
            _inspectionEnvironment.AmbientLightColor = new Color(night ? "29344a" : "c9d6e0");
            _inspectionEnvironment.AmbientLightEnergy = night ? 0.12f : 0.8f;
            _inspectionEnvironment.FogLightColor = horizon;
        }
        _worldEnvironment.Environment = Visible ? CurrentEnvironment : null;
    }

    public override void _Process(double delta)
    {
        if (Lighting == InspectionLighting.Night) _workLights.Position = CameraRig.Focus;
    }

    internal void SetActive(bool active)
    {
        Visible = active;
        CameraRig.Camera.Current = active;
        _worldEnvironment.Environment = active ? CurrentEnvironment : null;
    }
    internal void ShowFrame(ISceneFrame frame, WorldObjectId? follow, bool railwayPan = false)
    {
        CameraRig.DisplayedFrame(frame, follow, railwayPan);
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
        _inspectionEnvironment.Dispose();
        _inspectionSky.Dispose();
        _inspectionSkyMaterial.Dispose();
    }
}
