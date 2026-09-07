using Godot;
using System.Diagnostics;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

public partial class VoxelDeveloperScene : Node3D
{
    private const string VoxelSmokeEnvironmentVariable = "TRAIN_VOXEL_SMOKE";
    private const string VoxelSmokeReenterEnvironmentVariable = "TRAIN_VOXEL_SMOKE_REENTER";
    private const string VoxelObserveEnvironmentVariable = "TRAIN_VOXEL_OBSERVE";
    private readonly WorldVoxelCoord _selectedPosition = new(4, 4, 4);
    private VoxelDevelopmentSession _session = null!;
    private VoxelChunkMesh _mesh = null!;
    private Label _cell = null!;
    private Label _result = null!;
    private bool _dirty = true;
    private VoxelSmokePhase _smokePhase;
    private VoxelObservePhase _observePhase;
    private int _observeHoldFrames;
    private readonly List<double> _frameMilliseconds = new();
    private Stopwatch? _mutationStopwatch;
    private ulong _lastFrameTicks;
    private int _baselineRun;
    private double _stableSeconds;
    private bool _baselineEnabled;

    private enum VoxelSmokePhase
    {
        None,
        Remove,
        AwaitRemoveRefresh,
        Add,
        AwaitAddRefresh,
        Orient,
        AwaitOrientationRefresh,
        DamagePartial,
        AwaitDamagePartialRefresh,
        DamageDestroy,
        AwaitDamageDestroyRefresh,
        RestoreAfterDamage,
        AwaitRestoreAfterDamage,
        RemoveInternal,
        AwaitInternalRefresh,
        DamageInternal,
        AwaitDamageInternalRefresh,
        DamageMulti,
        AwaitDamageMultiRefresh,
        OpenConsole,
        AwaitConsoleOpen,
        CloseConsole,
        AwaitConsoleClosed,
        LeaveScene,
        Done
    }

    private enum VoxelObservePhase
    {
        None,
        CaptureInitial,
        AwaitInitial,
        Partial,
        AwaitPartial,
        Destroy,
        AwaitDestroy,
        Internal,
        AwaitInternal,
        OpenCavity,
        AwaitOpenCavity,
        Multi,
        AwaitMulti,
        Done
    }

    public override void _Ready()
    {
        var main = GetNode<Main>("/root/Main");
        _session = new VoxelDevelopmentSession(main.Reloader?.Catalog
            ?? throw new InvalidOperationException("Content reloader is unavailable."));
        _session.Changed += MarkDirty;
        main.SetVoxelDevelopmentSession(_session);

        AddChild(new DirectionalLight3D
        {
            Position = new Vector3(8, 20, 8),
            RotationDegrees = new Vector3(-50, -25, 0),
            LightEnergy = 1.4f,
            ShadowEnabled = true
        });
        var camera = new Camera3D { Position = new Vector3(-8, 16, -8) };
        AddChild(camera);
        camera.LookAt(new Vector3(7.5f, 7.5f, 7.5f), Vector3.Up);
        _mesh = new VoxelChunkMesh();
        AddChild(_mesh);
        CreateHud();
        Refresh();

        if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            ValidateGeneratedMesh();
            GD.Print("VOXEL_SMOKE_GEOMETRY_VALID");
            GD.Print("VOXEL_SMOKE_SCENE_READY");
            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                System.Environment.SetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable, "0");
                GD.Print("VOXEL_SMOKE_MENU_REENTERED");
                GetTree().Quit();
                return;
            }

            _smokePhase = VoxelSmokePhase.Remove;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            _observePhase = VoxelObservePhase.CaptureInitial;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_BASELINE"), "1", StringComparison.Ordinal))
        {
            _baselineEnabled = true;
            RunMutations(); // One unrecorded warm-up at the identical formal command boundary.
            StartBaselineRun();
        }
    }

    public override void _Process(double delta)
    {
        if (_dirty)
            Refresh();
        AdvanceSmoke();
        AdvanceObserve();
        AdvanceBaseline();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;
        if (key.Keycode == Key.R)
        {
            _session.Reset();
            _result.Text = "最近命令：已重置正式 16³ 开发世界。";
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Delete)
        {
            SetResult(_session.Remove(_selectedPosition));
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Insert)
        {
            SetResult(_session.Add(_selectedPosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity));
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Escape)
        {
            GetViewport().SetInputAsHandled();
            GetNode<Main>("/root/Main").SetVoxelDevelopmentSession(null);
            GetTree().ChangeSceneToFile("res://Ui/StartMenu.tscn");
        }
    }

    private void CreateHud()
    {
        var layer = new CanvasLayer();
        var panel = new PanelContainer
        {
            Position = new Vector2(24, 24),
            Size = new Vector2(690, 170)
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        box.AddChild(new Label { Text = "Voxel 开发场景 · 固定视角 · 单 Chunk 16³" });
        box.AddChild(new Label { Text = "Delete 移除 (4,4,4) · Insert 添加 · R 重置 · ~ 控制台伤害/查询 · Esc 返回" });
        _cell = new Label();
        _result = new Label { Text = "最近命令：场景已进入。" };
        box.AddChild(_cell);
        box.AddChild(_result);
        panel.AddChild(box);
        layer.AddChild(panel);
        AddChild(layer);
    }

    private void MarkDirty() => _dirty = true;

    private void Refresh()
    {
        _mesh.Refresh(_session.World);
        _dirty = false;
        var query = _session.Query(_selectedPosition);
        _cell.Text = query.Voxel is null
            ? "目标格 (4,4,4)：空。"
            : $"目标格 (4,4,4)：占用 · {query.Voxel.DefinitionId.Value} · 朝向 {query.Voxel.Orientation} · 耐久 {query.Voxel.CurrentDurability}。";
    }

    private void SetResult(WorldCommandResult result)
    {
        _result.Text = $"最近命令：{result.Status} ({result.Position.X},{result.Position.Y},{result.Position.Z})。";
    }

    private void SetResult(WorldDamageResult result)
    {
        if (result.Targets.Count == 0)
        {
            SetResult(new WorldCommandResult(result.Status, result.Position));
            return;
        }

        var summary = string.Join("；", result.Targets.Select(target =>
            $"{target.Status} ({target.Position.X},{target.Position.Y},{target.Position.Z}) by {target.Amount} → {target.RemainingDurability}"));
        _result.Text = $"最近命令：{summary}。";
    }

    private void ValidateGeneratedMesh() =>
        VoxelMeshGeometryValidator.Validate(_mesh.Mesh, _mesh.FaceCount);

    private void AdvanceSmoke()
    {
        switch (_smokePhase)
        {
            case VoxelSmokePhase.Remove:
                if (_session.Remove(_selectedPosition).Status != WorldCommandStatus.Removed || _session.World.VoxelCount != 511)
                    throw new InvalidOperationException("Voxel smoke could not remove the selected voxel.");
                SetResult(new WorldCommandResult(WorldCommandStatus.Removed, _selectedPosition));
                GD.Print("VOXEL_SMOKE_REMOVED");
                _smokePhase = VoxelSmokePhase.AwaitRemoveRefresh;
                return;
            case VoxelSmokePhase.AwaitRemoveRefresh:
                if (_mesh.LastWorldVoxelCount != 511 || !_cell.Text.Contains("空", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_MESH_UPDATED");
                _smokePhase = VoxelSmokePhase.Add;
                return;
            case VoxelSmokePhase.Add:
                if (_session.Add(_selectedPosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added || _session.World.VoxelCount != 512)
                    throw new InvalidOperationException("Voxel smoke could not add the selected voxel.");
                SetResult(new WorldCommandResult(WorldCommandStatus.Added, _selectedPosition));
                GD.Print("VOXEL_SMOKE_ADDED");
                _smokePhase = VoxelSmokePhase.AwaitAddRefresh;
                return;
            case VoxelSmokePhase.AwaitAddRefresh:
                if (_mesh.LastWorldVoxelCount != 512 || !_cell.Text.Contains("耐久 100", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_DISPLAY_UPDATED");
                _smokePhase = VoxelSmokePhase.Orient;
                return;
            case VoxelSmokePhase.Orient:
                if (_session.Remove(_selectedPosition).Status != WorldCommandStatus.Removed ||
                    _session.Add(_selectedPosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Yaw90).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Voxel smoke could not set the orientation example.");
                _smokePhase = VoxelSmokePhase.AwaitOrientationRefresh;
                return;
            case VoxelSmokePhase.AwaitOrientationRefresh:
                if (_mesh.LastWorldVoxelCount != 512 || !_cell.Text.Contains("pz_py", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_ORIENTATION_UPDATED");
                _smokePhase = VoxelSmokePhase.DamagePartial;
                return;
            case VoxelSmokePhase.DamagePartial:
                _session.Reset();
                var partial = _session.Damage(_selectedPosition, 25);
                if (partial.Status != WorldCommandStatus.Damaged ||
                    _session.Query(_selectedPosition).Voxel?.CurrentDurability != 75 ||
                    _session.World.VoxelCount != 512)
                    throw new InvalidOperationException("Voxel smoke could not apply partial damage.");
                SetResult(partial);
                GD.Print("VOXEL_SMOKE_DAMAGED");
                _smokePhase = VoxelSmokePhase.AwaitDamagePartialRefresh;
                return;
            case VoxelSmokePhase.AwaitDamagePartialRefresh:
                if (_mesh.LastWorldVoxelCount != 512 || !_cell.Text.Contains("耐久 75", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_DAMAGE_DISPLAY_UPDATED");
                _smokePhase = VoxelSmokePhase.DamageDestroy;
                return;
            case VoxelSmokePhase.DamageDestroy:
                var destroyed = _session.Damage(_selectedPosition, 75);
                if (destroyed.Status != WorldCommandStatus.Removed ||
                    _session.Query(_selectedPosition).IsOccupied ||
                    _session.World.VoxelCount != 511)
                    throw new InvalidOperationException("Voxel smoke could not destroy the selected voxel by damage.");
                SetResult(destroyed);
                GD.Print("VOXEL_SMOKE_DESTROYED");
                _smokePhase = VoxelSmokePhase.AwaitDamageDestroyRefresh;
                return;
            case VoxelSmokePhase.AwaitDamageDestroyRefresh:
                if (_mesh.LastWorldVoxelCount != 511 || !_cell.Text.Contains("空", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_DESTROY_DISPLAY_UPDATED");
                _smokePhase = VoxelSmokePhase.RestoreAfterDamage;
                return;
            case VoxelSmokePhase.RestoreAfterDamage:
                if (_session.Add(_selectedPosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.Query(_selectedPosition).Voxel?.CurrentDurability != 100 ||
                    _session.World.VoxelCount != 512 ||
                    _session.Add(_selectedPosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Occupied)
                    throw new InvalidOperationException("Voxel smoke could not restore the voxel after damage.");
                GD.Print("VOXEL_SMOKE_RESTORED_AFTER_DAMAGE");
                _smokePhase = VoxelSmokePhase.AwaitRestoreAfterDamage;
                return;
            case VoxelSmokePhase.AwaitRestoreAfterDamage:
                if (_mesh.LastWorldVoxelCount != 512 || !_cell.Text.Contains("耐久 100", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                _smokePhase = VoxelSmokePhase.RemoveInternal;
                return;
            case VoxelSmokePhase.RemoveInternal:
                _session.Reset();
                if (_session.Remove(new WorldVoxelCoord(5, 5, 5)).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Voxel smoke could not remove the internal voxel.");
                _smokePhase = VoxelSmokePhase.AwaitInternalRefresh;
                return;
            case VoxelSmokePhase.AwaitInternalRefresh:
                if (_mesh.LastWorldVoxelCount != 511 || _mesh.FaceCount != 390)
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_INTERNAL_FACE_UPDATED");
                _smokePhase = VoxelSmokePhase.DamageInternal;
                return;
            case VoxelSmokePhase.DamageInternal:
                _session.Reset();
                var internalDamage = _session.Damage(new WorldVoxelCoord(5, 5, 5), 100);
                if (internalDamage.Status != WorldCommandStatus.Removed ||
                    _session.Query(new WorldVoxelCoord(5, 5, 5)).IsOccupied ||
                    _session.World.VoxelCount != 511)
                    throw new InvalidOperationException("Voxel smoke could not destroy the internal voxel by damage.");
                SetResult(internalDamage);
                GD.Print("VOXEL_SMOKE_INTERNAL_DESTROYED");
                _smokePhase = VoxelSmokePhase.AwaitDamageInternalRefresh;
                return;
            case VoxelSmokePhase.AwaitDamageInternalRefresh:
                if (_mesh.LastWorldVoxelCount != 511 || _mesh.FaceCount != 390)
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_INTERNAL_DESTROY_DISPLAY_UPDATED");
                _smokePhase = VoxelSmokePhase.DamageMulti;
                return;
            case VoxelSmokePhase.DamageMulti:
                _session.Reset();
                var settle = Stopwatch.StartNew();
                var multi = _session.Damage(new[]
                {
                    new VoxelDamageTarget(new WorldVoxelCoord(4, 4, 4), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(5, 4, 4), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(6, 4, 4), 25)
                });
                settle.Stop();
                GD.Print($"VOXEL_SMOKE_MULTI_SETTLE_MS={settle.Elapsed.TotalMilliseconds:F3}");
                if (!multi.Succeeded ||
                    _session.World.VoxelCount != 510 ||
                    _session.Query(new WorldVoxelCoord(4, 4, 4)).IsOccupied ||
                    _session.Query(new WorldVoxelCoord(5, 4, 4)).IsOccupied ||
                    _session.Query(new WorldVoxelCoord(6, 4, 4)).Voxel?.CurrentDurability != 75)
                    throw new InvalidOperationException("Voxel smoke could not apply the multi-target damage event.");
                SetResult(multi);
                GD.Print("VOXEL_SMOKE_MULTI_DAMAGED");
                _smokePhase = VoxelSmokePhase.AwaitDamageMultiRefresh;
                return;
            case VoxelSmokePhase.AwaitDamageMultiRefresh:
                if (_mesh.LastWorldVoxelCount != 510 || !_cell.Text.Contains("空", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_MULTI_DISPLAY_UPDATED");
                _smokePhase = VoxelSmokePhase.OpenConsole;
                return;
            case VoxelSmokePhase.OpenConsole:
                PressKey(Key.Quoteleft, '`');
                _smokePhase = VoxelSmokePhase.AwaitConsoleOpen;
                return;
            case VoxelSmokePhase.AwaitConsoleOpen:
                if (FindConsole()?.IsOpen != true)
                    return;
                _smokePhase = VoxelSmokePhase.CloseConsole;
                return;
            case VoxelSmokePhase.CloseConsole:
                PressKey(Key.Escape);
                _smokePhase = VoxelSmokePhase.AwaitConsoleClosed;
                return;
            case VoxelSmokePhase.AwaitConsoleClosed:
                if (FindConsole()?.IsOpen != false)
                    return;
                GD.Print("VOXEL_SMOKE_CONSOLE_CLOSED");
                _smokePhase = VoxelSmokePhase.LeaveScene;
                return;
            case VoxelSmokePhase.LeaveScene:
                System.Environment.SetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable, "1");
                PressKey(Key.Escape);
                _smokePhase = VoxelSmokePhase.Done;
                return;
        }
    }

    private void AdvanceObserve()
    {
        if (_observeHoldFrames > 0)
        {
            _observeHoldFrames--;
            return;
        }

        try
        {
            AdvanceObserveStep();
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_ERROR {_observePhase}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void AdvanceObserveStep()
    {
        switch (_observePhase)
        {
            case VoxelObservePhase.CaptureInitial:
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitInitial;
                return;
            case VoxelObservePhase.AwaitInitial:
                CaptureObserve("issue45-initial", "initial 512 occupied, (4,4,4) durability 100");
                _observePhase = VoxelObservePhase.Partial;
                return;
            case VoxelObservePhase.Partial:
                _session.Reset();
                SetResult(_session.Damage(_selectedPosition, 25));
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitPartial;
                return;
            case VoxelObservePhase.AwaitPartial:
                RequireObserve("partial", 512, 75, occupied: true);
                CaptureObserve("issue45-partial", "single-target damage 25 at (4,4,4), durability 75");
                _observePhase = VoxelObservePhase.Destroy;
                return;
            case VoxelObservePhase.Destroy:
                SetResult(_session.Damage(_selectedPosition, 75));
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitDestroy;
                return;
            case VoxelObservePhase.AwaitDestroy:
                RequireObserve("destroy", 511, remaining: null, occupied: false);
                CaptureObserve("issue45-destroyed", "single-target damage 75 at (4,4,4), empty corner");
                _observePhase = VoxelObservePhase.Internal;
                return;
            case VoxelObservePhase.Internal:
                _session.Reset();
                SetResult(_session.Damage(new WorldVoxelCoord(5, 5, 5), 100));
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitInternal;
                return;
            case VoxelObservePhase.AwaitInternal:
                if (_session.World.VoxelCount != 511 || _session.Query(new WorldVoxelCoord(5, 5, 5)).IsOccupied || _mesh.FaceCount != 390)
                    throw new InvalidOperationException("Observe internal cavity did not match the formal query.");
                CaptureObserve("issue45-internal-cavity", "internal (5,5,5) destroyed, closed cavity, 511 voxels");
                _observePhase = VoxelObservePhase.OpenCavity;
                return;
            case VoxelObservePhase.OpenCavity:
                GD.Print("VOXEL_OBSERVE_OPEN_CAVITY");
                SetResult(_session.Damage(new WorldVoxelCoord(4, 5, 5), 100));
                _observeHoldFrames = 12;
                _observePhase = VoxelObservePhase.AwaitOpenCavity;
                return;
            case VoxelObservePhase.AwaitOpenCavity:
                if (_session.World.VoxelCount != 510 || _session.Query(new WorldVoxelCoord(4, 5, 5)).IsOccupied)
                    throw new InvalidOperationException("Observe opening did not destroy (4,5,5) by damage.");
                CaptureObserve("issue45-internal-opening", "damage (4,5,5) after cavity, visible interior faces");
                _observePhase = VoxelObservePhase.Multi;
                return;
            case VoxelObservePhase.Multi:
                _session.Reset();
                var settle = Stopwatch.StartNew();
                SetResult(_session.Damage(new[]
                {
                    new VoxelDamageTarget(new WorldVoxelCoord(4, 4, 4), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(5, 4, 4), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(6, 4, 4), 25)
                }));
                settle.Stop();
                GD.Print($"VOXEL_OBSERVE_MULTI_SETTLE_MS={settle.Elapsed.TotalMilliseconds:F3}");
                if (_session.World.VoxelCount != 510 ||
                    _session.Query(new WorldVoxelCoord(4, 4, 4)).IsOccupied ||
                    _session.Query(new WorldVoxelCoord(6, 4, 4)).Voxel?.CurrentDurability != 75)
                    throw new InvalidOperationException("Observe multi-target damage did not match the formal query.");
                _observeHoldFrames = 12;
                _observePhase = VoxelObservePhase.AwaitMulti;
                return;
            case VoxelObservePhase.AwaitMulti:
                if (!_cell.Text.Contains("空", StringComparison.Ordinal) ||
                    !_result.Text.Contains("Damaged (6,4,4) by 25 → 75", StringComparison.Ordinal) ||
                    !_result.Text.Contains("Removed (4,4,4)", StringComparison.Ordinal))
                    throw new InvalidOperationException("Observe multi HUD did not match the current damage result.");
                GD.Print($"VOXEL_OBSERVE_MULTI_STATE voxels=510 durability75={_session.Query(new WorldVoxelCoord(6, 4, 4)).Voxel!.CurrentDurability} faces={_mesh.FaceCount}");
                CaptureObserve("issue45-multi", "same-event damage removed two cells, (6,4,4) durability 75");
                GD.Print("VOXEL_OBSERVE_COMPLETE");
                _observePhase = VoxelObservePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void RequireObserve(string name, int count, int? remaining, bool occupied)
    {
        var query = _session.Query(_selectedPosition);
        if (_session.World.VoxelCount != count ||
            query.IsOccupied != occupied ||
            remaining is int expected && query.Voxel?.CurrentDurability != expected)
            throw new InvalidOperationException($"Observe {name} did not match the formal query.");
    }

    private void CaptureObserve(string fileName, string note)
    {
        ValidateGeneratedMesh();
        var directory = ProjectSettings.GlobalizePath("res://.godot");
        DirAccess.MakeDirAbsolute(directory);
        var path = Path.Combine(directory, fileName + ".png");
        try
        {
            RenderingServer.ForceDraw();
            var texture = GetViewport().GetTexture();
            var image = texture?.GetImage();
            if (image is null)
                throw new InvalidOperationException($"Viewport image for {fileName} was empty.");
            var error = image.SavePng(path);
            if (error != Error.Ok)
                throw new InvalidOperationException($"Failed to save {fileName}: {error}.");
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_CAPTURE_FAILED {fileName}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }

        GD.Print($"VOXEL_OBSERVE_CAPTURE {fileName} voxels={_session.World.VoxelCount} faces={_mesh.FaceCount} hud={_cell.Text} result={_result.Text} {note}");
    }

    private GameConsole? FindConsole() =>
        GetNode<Main>("/root/Main").GetChildren().OfType<GameConsole>().SingleOrDefault();

    private static void PressKey(Key keycode, int unicode = 0)
    {
        var pressed = new InputEventKey
        {
            Keycode = keycode,
            PhysicalKeycode = keycode,
            Unicode = unicode,
            Pressed = true
        };
        Input.ParseInputEvent(pressed);
        Input.FlushBufferedEvents();
    }

    private void StartBaselineRun()
    {
        _mutationStopwatch = Stopwatch.StartNew();
        RunMutations();
        _mutationStopwatch.Stop();
        _frameMilliseconds.Clear();
        _stableSeconds = 0;
        _lastFrameTicks = Time.GetTicksUsec();
    }

    private void AdvanceBaseline()
    {
        if (!_baselineEnabled || _mutationStopwatch is null)
            return;

        var now = Time.GetTicksUsec();
        _frameMilliseconds.Add((now - _lastFrameTicks) / 1000.0);
        _lastFrameTicks = now;
        _stableSeconds += GetProcessDeltaTime();
        if (_stableSeconds < 10)
            return;

        var average = _frameMilliseconds.Average();
        var maximum = _frameMilliseconds.Max();
        var memory = Performance.GetMonitor(Performance.Monitor.MemoryStatic);
        GD.Print(
            $"VOXEL_BASELINE_RUN run={_baselineRun + 1} definitions={_session.DefinitionCount} voxels={_session.World.VoxelCount} mutation_ms={_mutationStopwatch.Elapsed.TotalMilliseconds:F3} stable_seconds={_stableSeconds:F3} frame_avg_ms={average:F3} frame_max_ms={maximum:F3} memory_bytes={memory:F0}");
        _baselineRun++;
        if (_baselineRun == 3)
        {
            GD.Print("VOXEL_BASELINE_COMPLETE");
            _baselineEnabled = false;
            GetTree().Quit();
            return;
        }

        StartBaselineRun();
    }

    private void RunMutations()
    {
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16 && (x * 16 + z) < 100; z++)
        {
            var position = new WorldVoxelCoord(x, 0, z);
            if (_session.Add(position, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                _session.Remove(position).Status != WorldCommandStatus.Removed)
                throw new InvalidOperationException("Voxel baseline mutation did not complete.");
        }
    }
}
