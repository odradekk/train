using Godot;
using System.Diagnostics;
using Train.Content;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

public partial class VoxelDeveloperScene : Node3D
{
    private const string VoxelSmokeEnvironmentVariable = "TRAIN_VOXEL_SMOKE";
    private const string VoxelSmokeReenterEnvironmentVariable = "TRAIN_VOXEL_SMOKE_REENTER";
    private const string VoxelObserveEnvironmentVariable = "TRAIN_VOXEL_OBSERVE";
    private WorldVoxelCoord _selectedPosition = new(4, 4, 4);
    private VoxelDevelopmentSession _session = null!;
    private readonly VoxelWorldMeshPresenter _meshes = new();
    private VoxelChunkMesh _mesh = null!;
    private Label _title = null!;
    private Label _cell = null!;
    private Label _result = null!;
    private bool _dirty = true;
    private VoxelSmokePhase _smokePhase;
    private VoxelSlice2SmokePhase _slice2SmokePhase;
    private VoxelObservePhase _observePhase;
    private VoxelSlice2ObservePhase _slice2ObservePhase;
    private int _observeHoldFrames;
    private readonly List<double> _frameMilliseconds = new();
    private Stopwatch? _mutationStopwatch;
    private ulong _lastFrameTicks;
    private int _baselineRun;
    private int _baselineStage;
    private double _stableSeconds;
    private bool _baselineEnabled;
    private VoxelLiveReloadDriver? _liveReload;
    private string? _displayFingerprintBeforeAsset;
    private long _generationBeforeReject;

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
        ReloadReset,
        ReloadDamage,
        AwaitReloadDamage,
        ReloadMax150,
        AwaitReloadMax150,
        ReloadText,
        AwaitReloadText,
        ReloadAsset,
        AwaitReloadAsset,
        ReloadOrient,
        AwaitReloadOrient,
        ReloadMax60,
        AwaitReloadMax60,
        ReloadAddProbe,
        AwaitReloadAddProbe,
        ReloadReject,
        AwaitReloadReject,
        OpenConsole,
        AwaitConsoleOpen,
        CloseConsole,
        AwaitConsoleClosed,
        LeaveScene,
        Done
    }

    private enum VoxelSlice2SmokePhase
    {
        None,
        RemoveX,
        AwaitRemoveX,
        RestoreX,
        AwaitRestoreX,
        RemoveY,
        AwaitRemoveY,
        RestoreY,
        AwaitRestoreY,
        RemoveZ,
        AwaitRemoveZ,
        RestoreZ,
        AwaitRestoreZ,
        RemoveJunction,
        AwaitJunction,
        RestoreJunction,
        AwaitRestoreJunction,
        CrossDamage,
        AwaitCrossDamage,
        RejectDamage,
        HandoffReload
    }

    private enum VoxelSlice2ObservePhase
    {
        None,
        CaptureInitial,
        AwaitInitial,
        RemoveX,
        AwaitRemoveX,
        RemoveY,
        AwaitRemoveY,
        RemoveZ,
        AwaitRemoveZ,
        Junction,
        AwaitJunction,
        CrossDamage,
        AwaitCrossDamage,
        ReloadDamage,
        AwaitReloadDamage,
        ReloadMax150,
        AwaitReloadMax150,
        ReloadReject,
        AwaitReloadReject,
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
        ReloadDamage,
        AwaitReloadDamage,
        ReloadMax150,
        AwaitReloadMax150,
        ReloadDisplay,
        AwaitReloadDisplay,
        ReloadOrient,
        AwaitReloadOrient,
        ReloadMax60,
        AwaitReloadMax60,
        ReloadReject,
        AwaitReloadReject,
        Done
    }

    public override void _Ready()
    {
        var main = GetNode<Main>("/root/Main");
        AttachSession(new VoxelDevelopmentSession(
            main.Reloader?.Catalog ?? throw new InvalidOperationException("Content reloader is unavailable."),
            main.DevelopmentIdentity));
        _selectedPosition = SelectedCell(_session.Identity);

        AddChild(new DirectionalLight3D
        {
            Position = new Vector3(8, 20, 8),
            RotationDegrees = new Vector3(-50, -25, 0),
            LightEnergy = 1.4f,
            ShadowEnabled = true
        });
        var camera = new Camera3D();
        AddChild(camera);
        PlaceCamera(camera, _session.Identity);
        _meshes.Rebuild(this, _session.Identity);
        _mesh = _meshes.PrimaryMesh;
        CreateHud();
        if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal) ||
            string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            _liveReload = new VoxelLiveReloadDriver(
                main.IsolatedUserDataDirectory,
                main.IsolatedBuiltInResourcesDirectory);
        }

        Refresh();

        if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE_ISOLATION_ABORT"), "1", StringComparison.Ordinal))
            {
                RequireLiveReload().WriteIronOre(maxDurability: 150, name: "隔离中止铁矿");
                RequireLiveReload().WriteAsymmetricCubePngs();
                RequireCommittedReload("isolation abort");
                GD.Print("VOXEL_SMOKE_ISOLATION_WRITE_DONE");
                GetTree().Quit();
                return;
            }

            ValidateGeneratedMesh();
            GD.Print("VOXEL_SMOKE_GEOMETRY_VALID");
            GD.Print("VOXEL_SMOKE_SCENE_READY");
            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
            {
                GD.Print(
                    $"VOXEL_SMOKE_SLICE2_BASE voxels={_session.World.VoxelCount} chunk_size={_session.World.ChunkSize} bounds=32,32,32 chunks=8 seed={_session.Identity.Seed} generator={_session.Identity.GeneratorVersion}");
            }
            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                System.Environment.SetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable, "0");
                GD.Print("VOXEL_SMOKE_MENU_REENTERED");
                GetTree().Quit();
                return;
            }

            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveX;
            else
                _smokePhase = VoxelSmokePhase.Remove;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
                _slice2ObservePhase = VoxelSlice2ObservePhase.CaptureInitial;
            else
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
        AdvanceSlice2Smoke();
        AdvanceObserve();
        AdvanceSlice2Observe();
        AdvanceBaseline();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;
        if (key.Keycode == Key.R)
        {
            _session.Reset();
            _result.Text = _session.Identity.Slice == VoxelDevelopmentSlice.Slice2
                ? "最近命令：已重置正式切片 2 多 Chunk 世界。"
                : "最近命令：已重置正式 16³ 开发世界。";
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
            LeaveDevelopmentScene();
        }
    }

    private void CreateHud()
    {
        var layer = new CanvasLayer();
        var panel = new PanelContainer
        {
            Position = new Vector2(24, 24),
            Size = new Vector2(760, 190)
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        _title = new Label();
        box.AddChild(_title);
        box.AddChild(new Label { Text = "Delete 移除目标格 · Insert 添加 · R 重置 · ~ 控制台伤害/查询 · Esc 返回" });
        _cell = new Label();
        _result = new Label { Text = "最近命令：场景已进入。" };
        box.AddChild(_cell);
        box.AddChild(_result);
        panel.AddChild(box);
        layer.AddChild(panel);
        AddChild(layer);
    }

    public override void _ExitTree()
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.Detach();
        }

        if (GetNodeOrNull<Main>("/root/Main") is { } main)
            main.SetVoxelDevelopmentSession(null);
    }

    private void LeaveDevelopmentScene()
    {
        GetNode<Main>("/root/Main").SetVoxelDevelopmentSession(null);
        GetTree().ChangeSceneToFile("res://Ui/StartMenu.tscn");
    }

    private void MarkDirty() => _dirty = true;

    private string CurrentName(WorldVoxelQuery query)
    {
        if (query.Voxel is null)
            return "";
        return GetNode<Main>("/root/Main").Reloader?.Catalog.Current is { } catalog &&
               catalog.TryGet(query.Voxel.DefinitionId, out var entry)
            ? entry.Definition.Name
            : query.Voxel.DefinitionId.Value;
    }

    private void Refresh()
    {
        _meshes.Refresh(_session, GetNode<Main>("/root/Main").Reloader?.Catalog.Current);
        _mesh = _meshes.PrimaryMesh;
        _dirty = false;
        var query = _session.Query(_selectedPosition);
        _title.Text = _session.Identity.Slice == VoxelDevelopmentSlice.Slice2
            ? "Voxel 开发场景 · 切片 2 · 固定视角 · 2×2×2 Chunk · 32³"
            : "Voxel 开发场景 · 切片 1 · 固定视角 · 单 Chunk 16³";
        _cell.Text = query.Voxel is null
            ? $"目标格 ({_selectedPosition.X},{_selectedPosition.Y},{_selectedPosition.Z})：空。"
            : $"目标格 ({_selectedPosition.X},{_selectedPosition.Y},{_selectedPosition.Z})：占用 · {query.Voxel.DefinitionId.Value} · {CurrentName(query)} · 朝向 {query.Voxel.Orientation} · 耐久 {query.Voxel.CurrentDurability} · value {query.Value} · mass {query.Mass} · 上限 {query.MaxDurability}。";
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

    private VoxelLiveReloadDriver RequireLiveReload() =>
        _liveReload ?? throw new InvalidOperationException("Live reload driver is unavailable.");

    private int BaseVoxelCount => _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? 1088 : 512;

    private WorldVoxelCoord ProbePosition =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(0, 1, 0) : new(0, 0, 0);

    private WorldVoxelCoord YawCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(16, 17, 15) : new(4, 11, 4);

    private WorldVoxelCoord TiltCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(15, 17, 16) : new(5, 11, 4);

    private WorldVoxelCoord IdentityTopCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(14, 17, 14) : new(6, 11, 4);

    private static WorldVoxelCoord SelectedCell(VoxelWorldIdentity identity) =>
        identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(15, 16, 15) : new(4, 4, 4);

    private void AttachSession(VoxelDevelopmentSession session)
    {
        _session = session;
        _session.Changed += MarkDirty;
        GetNode<Main>("/root/Main").SetVoxelDevelopmentSession(_session);
    }

    private void ReplaceSession(VoxelWorldIdentity identity)
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.Detach();
        }

        var catalog = GetNode<Main>("/root/Main").Reloader?.Catalog
            ?? throw new InvalidOperationException("Content reloader is unavailable.");
        AttachSession(new VoxelDevelopmentSession(catalog, identity));
        _selectedPosition = SelectedCell(identity);
        _meshes.Rebuild(this, identity);
        _mesh = _meshes.PrimaryMesh;
        MarkDirty();
    }

    private static void PlaceCamera(Camera3D camera, VoxelWorldIdentity identity)
    {
        if (identity.Slice == VoxelDevelopmentSlice.Slice2)
        {
            camera.Position = new Vector3(28, 18, 4);
            camera.LookAt(new Vector3(16, 6, 16), Vector3.Up);
            return;
        }

        camera.Position = new Vector3(-8, 16, -8);
        camera.LookAt(new Vector3(7.5f, 7.5f, 7.5f), Vector3.Up);
    }

    private void ReorientVisibleTopCells()
    {
        var yaw = YawCell;
        var tilt = TiltCell;
        var tiltOrientation = new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveZ);
        if (_session.Remove(yaw).Status != WorldCommandStatus.Removed ||
            _session.Add(yaw, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Yaw90).Status != WorldCommandStatus.Added ||
            _session.Remove(tilt).Status != WorldCommandStatus.Removed ||
            _session.Add(tilt, VoxelWorldFactory.DevelopmentDefinitionId, tiltOrientation).Status != WorldCommandStatus.Added)
            throw new InvalidOperationException("Could not reorient visible top cells after the published display reload.");
    }

    private void RequireRotatedTopUvs()
    {
        var identity = VoxelCubeUv.TopCorners(VoxelOrientation.Identity);
        var yaw90 = VoxelCubeUv.TopCorners(VoxelOrientation.Yaw90);
        var tiltOrientation = new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveZ);
        var tilt = VoxelCubeUv.TopCorners(tiltOrientation);
        if (identity[(0, 1, 0)] == yaw90[(0, 1, 0)])
            throw new InvalidOperationException("Cube top UVs did not rotate with yaw.");
        if (tiltOrientation.InverseTransform(VoxelAxis.PositiveY) != VoxelAxis.NegativeZ)
            throw new InvalidOperationException("Tilt did not map world top to the local side.");
        RequireGeneratedTopUvs(IdentityTopCell, VoxelOrientation.Identity);
        RequireGeneratedTopUvs(YawCell, VoxelOrientation.Yaw90);
        RequireGeneratedTopUvs(TiltCell, tiltOrientation);
        GD.Print($"VOXEL_RELOAD_TOP_UV identity={FormatUv(identity)} yaw90={FormatUv(yaw90)} tilt={FormatUv(tilt)}");
    }

    private void RequireGeneratedTopUvs(WorldVoxelCoord cell, VoxelOrientation orientation)
    {
        var expected = VoxelCubeUv.TopCorners(orientation);
        var actual = VoxelMeshGeometryValidator.ReadWorldTopUvs(
            _meshes.MeshContaining(cell, _session.World.ChunkSize).Mesh, cell);
        foreach (var (corner, uv) in expected)
        {
            if (!actual.TryGetValue(corner, out var generated) ||
                !Mathf.IsEqualApprox(generated.X, uv.U) ||
                !Mathf.IsEqualApprox(generated.Y, uv.V))
                throw new InvalidOperationException(
                    $"Generated top UV at {cell} {orientation} corner {corner} was {generated} expected {uv}.");
        }
    }

    private static string FormatUv(IReadOnlyDictionary<(int X, int Y, int Z), (float U, float V)> corners) =>
        string.Join(",", corners.OrderBy(pair => pair.Key.X).ThenBy(pair => pair.Key.Z)
            .Select(pair => $"({pair.Key.X},{pair.Key.Z})={pair.Value.U:0},{pair.Value.V:0}"));

    private void RequireCommittedReload(string name)
    {
        var result = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
        if (!result.IsCommitted || !result.Changed)
            throw new InvalidOperationException($"Voxel smoke live reload '{name}' was not committed with changes.");
    }

    private void RequireInspection(int durability, long value, double mass, int max)
    {
        var query = _session.Query(_selectedPosition);
        if (query.Voxel?.CurrentDurability != durability ||
            query.Value != value ||
            query.Mass != mass ||
            query.MaxDurability != max)
            throw new InvalidOperationException(
                $"Live reload query expected durability {durability}, value {value}, mass {mass}, max {max} but was {query.Voxel?.CurrentDurability}/{query.Value}/{query.Mass}/{query.MaxDurability}.");
    }

    private void ValidateGeneratedMesh()
    {
        foreach (var mesh in _meshes.Meshes)
        {
            if (mesh.FaceCount == 0 || mesh.Mesh is null)
                continue;
            VoxelMeshGeometryValidator.Validate(mesh.Mesh, mesh.FaceCount);
        }
    }

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
                _smokePhase = VoxelSmokePhase.ReloadReset;
                return;
            case VoxelSmokePhase.ReloadReset:
                _session.Reset();
                _smokePhase = VoxelSmokePhase.ReloadDamage;
                return;
            case VoxelSmokePhase.ReloadDamage:
                var reloadDamage = _session.Damage(_selectedPosition, 20);
                if (reloadDamage.Status != WorldCommandStatus.Damaged ||
                    _session.Query(_selectedPosition).Voxel?.CurrentDurability != 80)
                    throw new InvalidOperationException("Voxel smoke could not damage (4,4,4) to 80 before live reload.");
                SetResult(reloadDamage);
                GD.Print("VOXEL_SMOKE_RELOAD_DAMAGED_80");
                _smokePhase = VoxelSmokePhase.AwaitReloadDamage;
                return;
            case VoxelSmokePhase.AwaitReloadDamage:
                if (!_cell.Text.Contains("耐久 80", StringComparison.Ordinal))
                    return;
                _smokePhase = VoxelSmokePhase.ReloadMax150;
                return;
            case VoxelSmokePhase.ReloadMax150:
                RequireLiveReload().WriteIronOre(maxDurability: 150);
                RequireCommittedReload("max 150");
                RequireInspection(80, 10, 1, 150);
                GD.Print("VOXEL_SMOKE_RELOAD_MAX_150");
                _smokePhase = VoxelSmokePhase.AwaitReloadMax150;
                return;
            case VoxelSmokePhase.AwaitReloadMax150:
                if (!_cell.Text.Contains("耐久 80", StringComparison.Ordinal) ||
                    !_cell.Text.Contains("上限 150", StringComparison.Ordinal))
                    return;
                if (!_meshes.DisplayFingerprint.Contains("asset:", StringComparison.Ordinal))
                    throw new InvalidOperationException("Voxel smoke cube display did not use published assets after reload.");
                ValidateGeneratedMesh();
                _smokePhase = VoxelSmokePhase.ReloadText;
                return;
            case VoxelSmokePhase.ReloadText:
                RequireLiveReload().WriteIronOre(maxDurability: 150, name: "热重载铁矿");
                RequireCommittedReload("display text");
                RequireInspection(80, 10, 1, 150);
                GD.Print("VOXEL_SMOKE_RELOAD_TEXT_UPDATED");
                _smokePhase = VoxelSmokePhase.AwaitReloadText;
                return;
            case VoxelSmokePhase.AwaitReloadText:
                if (!_cell.Text.Contains("热重载铁矿", StringComparison.Ordinal) ||
                    !_cell.Text.Contains("耐久 80", StringComparison.Ordinal))
                    return;
                _smokePhase = VoxelSmokePhase.ReloadAsset;
                return;
            case VoxelSmokePhase.ReloadAsset:
                _displayFingerprintBeforeAsset = _meshes.DisplayFingerprint;
                RequireLiveReload().WriteAsymmetricCubePngs();
                RequireCommittedReload("cube asset");
                RequireInspection(80, 10, 1, 150);
                GD.Print("VOXEL_SMOKE_RELOAD_ASSET_QUEUED");
                _smokePhase = VoxelSmokePhase.AwaitReloadAsset;
                return;
            case VoxelSmokePhase.AwaitReloadAsset:
                if (!_cell.Text.Contains("热重载铁矿", StringComparison.Ordinal))
                    return;
                if (string.Equals(_displayFingerprintBeforeAsset, _meshes.DisplayFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Voxel smoke cube display did not change after a published asset reload.");
                GD.Print($"VOXEL_SMOKE_RELOAD_ASSET_UPDATED before={_displayFingerprintBeforeAsset} after={_meshes.DisplayFingerprint}");
                ValidateGeneratedMesh();
                _smokePhase = VoxelSmokePhase.ReloadOrient;
                return;
            case VoxelSmokePhase.ReloadOrient:
                ReorientVisibleTopCells();
                _smokePhase = VoxelSmokePhase.AwaitReloadOrient;
                return;
            case VoxelSmokePhase.AwaitReloadOrient:
                if (_session.Query(YawCell).Voxel?.Orientation != VoxelOrientation.Yaw90 ||
                    _session.Query(TiltCell).Voxel?.Orientation != new VoxelOrientation(VoxelAxis.PositiveX, VoxelAxis.PositiveZ))
                    return;
                RequireRotatedTopUvs();
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_RELOAD_ORIENTATION_MAPPED");
                _smokePhase = VoxelSmokePhase.ReloadMax60;
                return;
            case VoxelSmokePhase.ReloadMax60:
                RequireLiveReload().WriteIronOre(maxDurability: 60, name: "热重载铁矿");
                RequireCommittedReload("max 60");
                RequireInspection(60, 10, 1, 60);
                GD.Print("VOXEL_SMOKE_RELOAD_MAX_60");
                _smokePhase = VoxelSmokePhase.AwaitReloadMax60;
                return;
            case VoxelSmokePhase.AwaitReloadMax60:
                if (!_cell.Text.Contains("耐久 60", StringComparison.Ordinal) ||
                    !_cell.Text.Contains("上限 60", StringComparison.Ordinal))
                    return;
                _smokePhase = VoxelSmokePhase.ReloadAddProbe;
                return;
            case VoxelSmokePhase.ReloadAddProbe:
                RequireLiveReload().WriteProbeMod();
                RequireCommittedReload("probe mod");
                if (_session.Add(ProbePosition, RequireLiveReload().ProbeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Voxel smoke could not add the isolated probe voxel.");
                GD.Print("VOXEL_SMOKE_RELOAD_PROBE_ADDED");
                _smokePhase = VoxelSmokePhase.AwaitReloadAddProbe;
                return;
            case VoxelSmokePhase.AwaitReloadAddProbe:
                if (_session.World.VoxelCount != BaseVoxelCount + 1)
                    return;
                _smokePhase = VoxelSmokePhase.ReloadReject;
                return;
            case VoxelSmokePhase.ReloadReject:
                _generationBeforeReject = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                var fingerprintBeforeReject = _meshes.DisplayFingerprint;
                RequireLiveReload().InvalidateProbeMod();
                var rejected = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejected.Status != ContentReloadStatus.Rejected ||
                    rejected.Changed ||
                    rejected.CatalogVersion != _generationBeforeReject ||
                    _session.Query(ProbePosition).Voxel?.DefinitionId != RequireLiveReload().ProbeId ||
                    _session.Query(_selectedPosition).Voxel?.CurrentDurability != 60)
                    throw new InvalidOperationException("Voxel smoke live reload did not reject a missing required definition atomically.");
                if (!string.Equals(fingerprintBeforeReject, _meshes.DisplayFingerprint, StringComparison.Ordinal) ||
                    !_cell.Text.Contains("热重载铁矿", StringComparison.Ordinal))
                    throw new InvalidOperationException("Voxel smoke discarded the published display after a rejected reload.");
                GD.Print("VOXEL_SMOKE_RELOAD_REJECTED");
                GD.Print("VOXEL_SMOKE_RELOAD_DISPLAY_RETAINED");
                _smokePhase = VoxelSmokePhase.AwaitReloadReject;
                return;
            case VoxelSmokePhase.AwaitReloadReject:
                if (_session.World.VoxelCount != BaseVoxelCount + 1 ||
                    !_cell.Text.Contains("耐久 60", StringComparison.Ordinal))
                    return;
                ValidateGeneratedMesh();
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
                LeaveDevelopmentScene();
                _smokePhase = VoxelSmokePhase.Done;
                return;
        }
    }

    private void AdvanceSlice2Smoke()
    {
        switch (_slice2SmokePhase)
        {
            case VoxelSlice2SmokePhase.RemoveX:
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 15), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_X_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveX;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveX:
                if (_meshes.LastWorldVoxelCount != 1087)
                    return;
                RequireExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveX);
                RequireMissingVoxelFaces(new WorldVoxelCoord(16, 16, 15));
                RequirePartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 0));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_X_BOUNDARY_MESH dirty={FormatDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreX;
                return;
            case VoxelSlice2SmokePhase.RestoreX:
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 15), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreX;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreX:
                if (_meshes.LastWorldVoxelCount != 1088)
                    return;
                RequireHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveX);
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveY;
                return;
            case VoxelSlice2SmokePhase.RemoveY:
                RequireOccupancyEdit(new WorldVoxelCoord(15, 15, 15), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_Y_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveY;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveY:
                if (_meshes.LastWorldVoxelCount != 1087)
                    return;
                RequireExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.NegativeY);
                RequireMissingVoxelFaces(new WorldVoxelCoord(15, 15, 15));
                RequirePartialRebuild(new ChunkCoord(0, 0, 0), new ChunkCoord(0, 1, 0));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_Y_BOUNDARY_MESH dirty={FormatDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreY;
                return;
            case VoxelSlice2SmokePhase.RestoreY:
                RequireOccupancyEdit(new WorldVoxelCoord(15, 15, 15), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreY;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreY:
                if (_meshes.LastWorldVoxelCount != 1088)
                    return;
                RequireHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.NegativeY);
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveZ;
                return;
            case VoxelSlice2SmokePhase.RemoveZ:
                RequireOccupancyEdit(new WorldVoxelCoord(15, 16, 16), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_Z_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveZ;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveZ:
                if (_meshes.LastWorldVoxelCount != 1087)
                    return;
                RequireExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveZ);
                RequireMissingVoxelFaces(new WorldVoxelCoord(15, 16, 16));
                RequirePartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(0, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_Z_BOUNDARY_MESH dirty={FormatDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreZ;
                return;
            case VoxelSlice2SmokePhase.RestoreZ:
                RequireOccupancyEdit(new WorldVoxelCoord(15, 16, 16), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreZ;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreZ:
                if (_meshes.LastWorldVoxelCount != 1088)
                    return;
                RequireHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveZ);
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveJunction;
                return;
            case VoxelSlice2SmokePhase.RemoveJunction:
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 16), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_JUNCTION");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitJunction;
                return;
            case VoxelSlice2SmokePhase.AwaitJunction:
                if (_meshes.LastWorldVoxelCount != 1087)
                    return;
                RequireExposedFace(new WorldVoxelCoord(15, 16, 16), VoxelFaceNormal.PositiveX);
                RequireExposedFace(new WorldVoxelCoord(16, 15, 16), VoxelFaceNormal.PositiveY);
                RequireExposedFace(new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.PositiveZ);
                RequireMissingVoxelFaces(new WorldVoxelCoord(16, 16, 16));
                RequirePartialRebuild(new ChunkCoord(1, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_JUNCTION_MESH dirty={FormatDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreJunction;
                return;
            case VoxelSlice2SmokePhase.RestoreJunction:
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 16), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreJunction;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreJunction:
                if (_meshes.LastWorldVoxelCount != 1088)
                    return;
                _slice2SmokePhase = VoxelSlice2SmokePhase.CrossDamage;
                return;
            case VoxelSlice2SmokePhase.CrossDamage:
                var multi = _session.Damage(new[]
                {
                    new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 15), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 16), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(15, 16, 16), 25)
                });
                if (!multi.Succeeded ||
                    _session.World.VoxelCount != 1086 ||
                    _session.Query(new WorldVoxelCoord(15, 16, 16)).Voxel?.CurrentDurability != 75)
                    throw new InvalidOperationException("Slice 2 smoke could not apply the cross-chunk damage event.");
                SetResult(multi);
                GD.Print("VOXEL_SMOKE_CROSS_DAMAGE");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitCrossDamage;
                return;
            case VoxelSlice2SmokePhase.AwaitCrossDamage:
                if (_meshes.LastWorldVoxelCount != 1086)
                    return;
                RequireExposedFace(new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.NegativeX);
                RequireMissingVoxelFaces(new WorldVoxelCoord(15, 16, 15));
                RequirePartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_CROSS_DAMAGE_MESH dirty={FormatDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RejectDamage;
                return;
            case VoxelSlice2SmokePhase.RejectDamage:
                var before = _session.World.VoxelCount;
                var rejected = _session.Damage(new[]
                {
                    new VoxelDamageTarget(new WorldVoxelCoord(16, 16, 15), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(0, 1, 0), 25)
                });
                if (rejected.Status != WorldCommandStatus.Empty || _session.World.VoxelCount != before)
                    throw new InvalidOperationException("Slice 2 smoke rejected damage mutated the world.");
                GD.Print("VOXEL_SMOKE_REJECT_PRESERVED");
                _slice2SmokePhase = VoxelSlice2SmokePhase.HandoffReload;
                return;
            case VoxelSlice2SmokePhase.HandoffReload:
                _slice2SmokePhase = VoxelSlice2SmokePhase.None;
                _smokePhase = VoxelSmokePhase.ReloadReset;
                return;
        }
    }

    private void RequireOccupancyEdit(WorldVoxelCoord position, bool remove, int expectedCount)
    {
        var status = remove
            ? _session.Remove(position).Status
            : _session.Add(position, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status;
        var expected = remove ? WorldCommandStatus.Removed : WorldCommandStatus.Added;
        if (status != expected || _session.World.VoxelCount != expectedCount)
            throw new InvalidOperationException($"Slice 2 smoke could not {(remove ? "remove" : "restore")} {position}.");
        SetResult(new WorldCommandResult(status, position));
    }

    private void RequireExposedFace(WorldVoxelCoord position, VoxelFaceNormal normal)
    {
        if (!HasFace(position, normal))
            throw new InvalidOperationException($"Expected exposed face {position} {normal}.");
    }

    private void RequireHiddenFace(WorldVoxelCoord position, VoxelFaceNormal normal)
    {
        if (HasFace(position, normal))
            throw new InvalidOperationException($"Expected culled face {position} {normal}.");
    }

    private void RequireMissingVoxelFaces(WorldVoxelCoord position)
    {
        if (VoxelSurfaceMesher.Build(_session.World).Faces.Any(face => face.Position == position))
            throw new InvalidOperationException($"Removed voxel {position} still contributed a face.");
    }

    private void RequirePartialRebuild(params ChunkCoord[] required)
    {
        var rebuilt = _meshes.LastRebuiltChunks;
        if (rebuilt.Count >= VoxelChunks.All(_session.Identity).Count)
            throw new InvalidOperationException("A local edit rebuilt every chunk.");
        foreach (var chunk in required)
        {
            if (!rebuilt.Contains(chunk))
                throw new InvalidOperationException($"Expected dirty chunk {chunk} but rebuilt {FormatDirty()}.");
        }
    }

    private bool HasFace(WorldVoxelCoord position, VoxelFaceNormal normal) =>
        VoxelSurfaceMesher.Build(_session.World).Faces.Any(face => face.Position == position && face.Normal == normal);

    private string FormatDirty() =>
        string.Join(";", _meshes.LastRebuiltChunks.Select(chunk => $"{chunk.X},{chunk.Y},{chunk.Z}"));

    private void AdvanceObserve()
    {
        if (_observePhase == VoxelObservePhase.None)
            return;
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
                _observePhase = VoxelObservePhase.ReloadDamage;
                return;
            case VoxelObservePhase.ReloadDamage:
                _session.Reset();
                SetResult(_session.Damage(_selectedPosition, 20));
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadDamage;
                return;
            case VoxelObservePhase.AwaitReloadDamage:
                RequireObserve("reload-80", 512, 80, occupied: true);
                CaptureObserve("issue46-damaged-80", "formal damage 20 at (4,4,4), durability 80 before live reload");
                _observePhase = VoxelObservePhase.ReloadMax150;
                return;
            case VoxelObservePhase.ReloadMax150:
                RequireLiveReload().WriteIronOre(maxDurability: 150);
                RequireCommittedReload("observe max 150");
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadMax150;
                return;
            case VoxelObservePhase.AwaitReloadMax150:
                RequireInspection(80, 10, 1, 150);
                CaptureObserve("issue46-reload-150", "live reload max 150, absolute durability still 80");
                _observePhase = VoxelObservePhase.ReloadDisplay;
                return;
            case VoxelObservePhase.ReloadDisplay:
                RequireLiveReload().WriteIronOre(maxDurability: 150, name: "热重载铁矿");
                RequireLiveReload().WriteAsymmetricCubePngs();
                RequireCommittedReload("observe display");
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadDisplay;
                return;
            case VoxelObservePhase.AwaitReloadDisplay:
                if (!_cell.Text.Contains("热重载铁矿", StringComparison.Ordinal))
                    throw new InvalidOperationException("Observe display reload did not update the HUD name.");
                CaptureObserve("issue46-display", "asymmetric top on identity voxels after published display reload");
                _observePhase = VoxelObservePhase.ReloadOrient;
                return;
            case VoxelObservePhase.ReloadOrient:
                ReorientVisibleTopCells();
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadOrient;
                return;
            case VoxelObservePhase.AwaitReloadOrient:
                if (_session.Query(new WorldVoxelCoord(4, 11, 4)).Voxel?.Orientation != VoxelOrientation.Yaw90)
                    throw new InvalidOperationException("Observe yaw cell did not keep pz_py.");
                RequireRotatedTopUvs();
                CaptureObserve("issue46-oriented", "asymmetric top rotated on yaw90; tilted cell world top uses mapped side pattern");
                _observePhase = VoxelObservePhase.ReloadMax60;
                return;
            case VoxelObservePhase.ReloadMax60:
                RequireLiveReload().WriteIronOre(maxDurability: 60, name: "热重载铁矿");
                RequireCommittedReload("observe max 60");
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadMax60;
                return;
            case VoxelObservePhase.AwaitReloadMax60:
                RequireInspection(60, 10, 1, 60);
                CaptureObserve("issue46-reload-60", "live reload max 60 clamped absolute durability to 60");
                _observePhase = VoxelObservePhase.ReloadReject;
                return;
            case VoxelObservePhase.ReloadReject:
                RequireLiveReload().WriteProbeMod();
                RequireCommittedReload("observe probe mod");
                if (_session.Add(new WorldVoxelCoord(0, 0, 0), RequireLiveReload().ProbeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Observe could not add the isolated probe voxel.");
                _generationBeforeReject = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                RequireLiveReload().InvalidateProbeMod();
                var rejected = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejected.IsCommitted || rejected.CatalogVersion != _generationBeforeReject)
                    throw new InvalidOperationException("Observe live reload did not reject the missing probe definition.");
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadReject;
                return;
            case VoxelObservePhase.AwaitReloadReject:
                if (_session.Query(_selectedPosition).Voxel?.CurrentDurability != 60 ||
                    !_cell.Text.Contains("热重载铁矿", StringComparison.Ordinal) ||
                    _session.Query(new WorldVoxelCoord(0, 0, 0)).Voxel?.DefinitionId != RequireLiveReload().ProbeId ||
                    _session.Query(new WorldVoxelCoord(4, 11, 4)).Voxel?.Orientation != VoxelOrientation.Yaw90)
                    throw new InvalidOperationException("Observe rejected reload leaked a new published world or display.");
                CaptureObserve("issue46-rejected", "rejected live reload keeps old published world, orientations, and display");
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

        GD.Print($"VOXEL_OBSERVE_CAPTURE {fileName} voxels={_session.World.VoxelCount} faces={_meshes.FaceCount} hud={_cell.Text} result={_result.Text} {note}");
    }

    private void AdvanceSlice2Observe()
    {
        if (_slice2ObservePhase == VoxelSlice2ObservePhase.None)
            return;
        if (_observeHoldFrames > 0)
        {
            _observeHoldFrames--;
            return;
        }

        try
        {
            AdvanceSlice2ObserveStep();
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_ERROR {_slice2ObservePhase}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void AdvanceSlice2ObserveStep()
    {
        switch (_slice2ObservePhase)
        {
            case VoxelSlice2ObservePhase.CaptureInitial:
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitInitial;
                return;
            case VoxelSlice2ObservePhase.AwaitInitial:
                if (_session.World.VoxelCount != 1088)
                    throw new InvalidOperationException("Slice 2 observe initial world was not the 1088-voxel fixture.");
                CaptureObserve("issue50-initial", "slice 2 ground plus floating 14..17 cube, 1088 voxels");
                _slice2ObservePhase = VoxelSlice2ObservePhase.RemoveX;
                return;
            case VoxelSlice2ObservePhase.RemoveX:
                _session.Reset();
                SetResult(_session.Remove(new WorldVoxelCoord(16, 0, 8)));
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitRemoveX;
                return;
            case VoxelSlice2ObservePhase.AwaitRemoveX:
                RequireExposedFace(new WorldVoxelCoord(15, 0, 8), VoxelFaceNormal.PositiveX);
                CaptureObserve("issue50-x-boundary", "ground hole at (16,0,8), +X wall of (15,0,8) visible");
                _slice2ObservePhase = VoxelSlice2ObservePhase.RemoveY;
                return;
            case VoxelSlice2ObservePhase.RemoveY:
                _session.Reset();
                if (_session.Add(new WorldVoxelCoord(24, 15, 8), VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.Add(new WorldVoxelCoord(24, 16, 8), VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 observe could not build the Y-boundary pillar.");
                SetResult(_session.Remove(new WorldVoxelCoord(24, 16, 8)));
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitRemoveY;
                return;
            case VoxelSlice2ObservePhase.AwaitRemoveY:
                RequireExposedFace(new WorldVoxelCoord(24, 15, 8), VoxelFaceNormal.PositiveY);
                CaptureObserve("issue50-y-boundary", "Y 15/16 pillar after removing (24,16,8), top of (24,15,8) exposed near camera");
                _slice2ObservePhase = VoxelSlice2ObservePhase.RemoveZ;
                return;
            case VoxelSlice2ObservePhase.RemoveZ:
                _session.Reset();
                SetResult(_session.Remove(new WorldVoxelCoord(8, 0, 16)));
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitRemoveZ;
                return;
            case VoxelSlice2ObservePhase.AwaitRemoveZ:
                RequireExposedFace(new WorldVoxelCoord(8, 0, 15), VoxelFaceNormal.PositiveZ);
                CaptureObserve("issue50-z-boundary", "ground hole at (8,0,16), +Z wall of (8,0,15) visible");
                _slice2ObservePhase = VoxelSlice2ObservePhase.Junction;
                return;
            case VoxelSlice2ObservePhase.Junction:
                _session.Reset();
                SetResult(_session.Remove(new WorldVoxelCoord(16, 0, 16)));
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitJunction;
                return;
            case VoxelSlice2ObservePhase.AwaitJunction:
                RequireExposedFace(new WorldVoxelCoord(15, 0, 16), VoxelFaceNormal.PositiveX);
                RequireExposedFace(new WorldVoxelCoord(16, 0, 15), VoxelFaceNormal.PositiveZ);
                CaptureObserve("issue50-junction", "ground hole at (16,0,16) where four chunks meet");
                _slice2ObservePhase = VoxelSlice2ObservePhase.CrossDamage;
                return;
            case VoxelSlice2ObservePhase.CrossDamage:
                _session.Reset();
                SetResult(_session.Damage(new[]
                {
                    new VoxelDamageTarget(new WorldVoxelCoord(15, 0, 8), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(16, 0, 8), 100),
                    new VoxelDamageTarget(new WorldVoxelCoord(15, 0, 16), 25)
                }));
                _observeHoldFrames = 12;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitCrossDamage;
                return;
            case VoxelSlice2ObservePhase.AwaitCrossDamage:
                if (_session.World.VoxelCount != 1086 ||
                    _session.Query(new WorldVoxelCoord(15, 0, 16)).Voxel?.CurrentDurability != 75)
                    throw new InvalidOperationException("Slice 2 observe cross-chunk damage did not match the formal query.");
                CaptureObserve("issue50-cross-damage", "same-event ground holes at x=15/16 and a surviving cell at durability 75");
                _slice2ObservePhase = VoxelSlice2ObservePhase.ReloadDamage;
                return;
            case VoxelSlice2ObservePhase.ReloadDamage:
                _session.Reset();
                SetResult(_session.Damage(_selectedPosition, 20));
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitReloadDamage;
                return;
            case VoxelSlice2ObservePhase.AwaitReloadDamage:
                if (_session.Query(_selectedPosition).Voxel?.CurrentDurability != 80)
                    throw new InvalidOperationException("Slice 2 observe did not damage the selected cell to 80.");
                _slice2ObservePhase = VoxelSlice2ObservePhase.ReloadMax150;
                return;
            case VoxelSlice2ObservePhase.ReloadMax150:
                RequireLiveReload().WriteIronOre(maxDurability: 150);
                RequireCommittedReload("observe slice 2 max 150");
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitReloadMax150;
                return;
            case VoxelSlice2ObservePhase.AwaitReloadMax150:
                RequireInspection(80, 10, 1, 150);
                CaptureObserve("issue50-reload-150", "slice 2 live reload max 150, absolute durability still 80");
                _slice2ObservePhase = VoxelSlice2ObservePhase.ReloadReject;
                return;
            case VoxelSlice2ObservePhase.ReloadReject:
                RequireLiveReload().WriteProbeMod();
                RequireCommittedReload("observe slice 2 probe");
                if (_session.Add(ProbePosition, RequireLiveReload().ProbeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 observe could not add the isolated probe voxel.");
                _generationBeforeReject = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                RequireLiveReload().InvalidateProbeMod();
                var rejected = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejected.IsCommitted || rejected.CatalogVersion != _generationBeforeReject)
                    throw new InvalidOperationException("Slice 2 observe live reload did not reject the missing probe definition.");
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitReloadReject;
                return;
            case VoxelSlice2ObservePhase.AwaitReloadReject:
                if (_session.Query(_selectedPosition).Voxel?.CurrentDurability != 80 ||
                    _session.Query(ProbePosition).Voxel?.DefinitionId != RequireLiveReload().ProbeId)
                    throw new InvalidOperationException("Slice 2 observe rejected reload leaked a new published world.");
                CaptureObserve("issue50-rejected", "slice 2 rejected live reload keeps old published world");
                GD.Print("VOXEL_OBSERVE_COMPLETE");
                _slice2ObservePhase = VoxelSlice2ObservePhase.Done;
                GetTree().Quit();
                return;
        }
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
        var marker = _baselineStage == 0 ? "VOXEL_BASELINE_RUN" : "VOXEL_BASELINE_SLICE2_RUN";
        GD.Print(
            $"{marker} run={_baselineRun + 1} definitions={_session.DefinitionCount} voxels={_session.World.VoxelCount} mutation_ms={_mutationStopwatch.Elapsed.TotalMilliseconds:F3} stable_seconds={_stableSeconds:F3} frame_avg_ms={average:F3} frame_max_ms={maximum:F3} memory_bytes={memory:F0}");
        _baselineRun++;
        if (_baselineRun == 3 && _baselineStage == 0)
        {
            ReplaceSession(VoxelWorldIdentity.Slice2);
            PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice2);
            Refresh();
            _baselineStage = 1;
            _baselineRun = 0;
            StartBaselineRun();
            return;
        }

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
        if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
        {
            for (var index = 0; index < 100; index++)
            {
                var position = new WorldVoxelCoord(15 + index % 2, 1, (index / 2) % 32);
                if (_session.Add(position, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.Remove(position).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Voxel slice 2 baseline mutation did not complete.");
            }

            return;
        }

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
