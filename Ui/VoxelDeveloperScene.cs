using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using Godot;
using System.Diagnostics;
using Train.Core.Content;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;
using Train.Presentation;

namespace Train;

public partial class VoxelDeveloperScene : Node3D
{
    private const string VoxelSmokeEnvironmentVariable = "TRAIN_VOXEL_SMOKE";
    private const string VoxelSmokeReenterEnvironmentVariable = "TRAIN_VOXEL_SMOKE_REENTER";
    private const string VoxelSmokeResumeEnvironmentVariable = "TRAIN_VOXEL_SMOKE_RESUME";
    private const string VoxelSmokeResumeGapEnvironmentVariable = "TRAIN_VOXEL_SMOKE_RESUME_GAP";
    private const string VoxelSmokeResumeSupportEnvironmentVariable = "TRAIN_VOXEL_SMOKE_RESUME_SUPPORT";
    private const string VoxelSmokeResumeDestroyEnvironmentVariable = "TRAIN_VOXEL_SMOKE_RESUME_DESTROY";
    private const string VoxelSmokeSaveIdEnvironmentVariable = "TRAIN_VOXEL_SMOKE_SAVE_ID";
    private const string VoxelObserveEnvironmentVariable = "TRAIN_VOXEL_OBSERVE";
    private const string VoxelObserveResumeEnvironmentVariable = "TRAIN_VOXEL_OBSERVE_RESUME";
    private const string VoxelObserveSaveIdEnvironmentVariable = "TRAIN_VOXEL_OBSERVE_SAVE_ID";
    private WorldVoxelCoord _selectedPosition = new(4, 4, 4);
    private VoxelDevelopmentSession _session = null!;
    private readonly VoxelWorldMeshPresenter _meshes = new();
    private readonly VoxelWorldCollisionPresenter _colliders = new();
    private readonly CompositeVisualPresenter _composites = new();
    private readonly TrackRendererPresenter _tracks = new();
    private VoxelChunkMesh _mesh = null!;
    private int _collisionRefreshGeneration;
    private int _physicsSeenGeneration;
    private Label _title = null!;
    private Label _halt = null!;
    private Label _cell = null!;
    private Label _result = null!;
    private UnsavedWorldChangePrompt? _worldChangePrompt;
    private VoxelSlice4BaselinePhase _slice4BaselinePhase;
    private bool _slice4BaselineRecording;
    private ulong _slice4BaselineMaxStep;
    private double _slice4EventWorkMs;
    private bool _dirty = true;
    private VoxelSmokePhase _smokePhase;
    private VoxelSlice2SmokePhase _slice2SmokePhase;
    private VoxelSlice3SmokePhase _slice3SmokePhase;
    private VoxelSlice4SmokePhase _slice4SmokePhase;
    private ulong _slice4FarMeshId;
    private ulong _slice4ZNeighborMeshId;
    private ulong[] _slice4TrainMeshIds = Array.Empty<ulong>();
    private VoxelResumeSmokePhase _resumeSmokePhase;
    private VoxelSlice3ResumeSmokePhase _slice3ResumeSmokePhase;
    private VoxelSlice4ResumeSmokePhase _slice4ResumeSmokePhase;
    private VoxelSlice4GapResumeSmokePhase _slice4GapResumeSmokePhase;
    private VoxelSlice4SupportResumeSmokePhase _slice4SupportResumeSmokePhase;
    private VoxelSlice4DestroyResumeSmokePhase _slice4DestroyResumeSmokePhase;
    private VoxelObservePhase _observePhase;
    private VoxelSlice2ObservePhase _slice2ObservePhase;
    private VoxelSlice3ObservePhase _slice3ObservePhase;
    private VoxelSlice4ObservePhase _slice4ObservePhase;
    private VoxelResumeObservePhase _resumeObservePhase;
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
    private Dictionary<ChunkCoord, long>? _chunkRevisionsBeforeEdit;
    private Dictionary<ChunkCoord, long>? _collisionRevisionsBeforeEdit;
    private static readonly VoxelDefinitionId HollowId = new("core", "hollow");
    private static readonly VoxelDefinitionId LedgeId = new("core", "ledge");
    private EntityId _refitVehicle;
    private EntityId _refitP;
    private EntityId _refitQ;
    private EntityId _refitR;
    private CompositeAuthoritySnapshot? _refitSuccess;
    private int _refitMetricRun;
    private int _refitMetricStamps;
    private double _refitMutationMs;
    private double _refitRefreshMs;
    private long _refitMemoryBefore;
    private ulong _refitTickA;
    private ulong _refitTickB;
    private ulong _refitTickC;
    private string? _reloadFingerprint;
    private CompositeAuthoritySnapshot? _reloadAuthority;
    private WorldVoxelCoord _reloadWorldCell = new(2, 2, 2);
    private double _slice3MutationMs;
    private double _slice3StructureMs;
    private double _slice3RefreshMs;
    private double _slice3SaveMs;
    private double _slice3LoadMs;
    private long _slice3Bytes;
    private int _slice3Voxels;
    private int _slice3Parts;
    private double _slice4SimMs;
    private double _slice4MutationMs;
    private double _slice4RefreshMs;
    private double _slice4SaveMs;
    private double _slice4LoadMs;
    private long _slice4Bytes;
    private int _slice4Voxels;
    private bool _slice4Baseline;
    private static readonly CompositeTransform PersistEqualBridgePose = CompositeTransform.Translation(20, 8, 8);
    private static readonly CompositeTransform PersistDropBridgePose = CompositeTransform.Translation(8, 8, 20);

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
        SaveFixedSequence,
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
        NoneOccupy,
        AwaitNoneOccupy,
        ReloadCollisionNone,
        AwaitReloadCollisionNone,
        ReloadPresentationCollision,
        AwaitReloadPresentationCollision,
        ReloadCollisionFullBox,
        AwaitReloadCollisionFullBox,
        LocalBoxPlace,
        AwaitLocalBoxPlace,
        LocalBoxRotate,
        AwaitLocalBoxRotate,
        LocalBoxRemove,
        AwaitLocalBoxRemove,
        LocalBoxReload,
        AwaitLocalBoxReload,
        LocalBoxThinTranslate,
        AwaitLocalBoxThinTranslate,
        LocalBoxReject,
        AwaitLocalBoxReject,
        LocalBoxLethal,
        AwaitLocalBoxLethal,
        HandoffReload
    }

    private enum VoxelSlice4SmokePhase
    {
        None,
        SaveEmpty,
        Construct,
        AwaitConstruct,
        Inspect,
        Assemble,
        AwaitAssemble,
        LegacyAdd,
        AwaitLegacyAdd,
        LegacyStart,
        AwaitLegacyStart,
        LegacyRemove,
        AwaitLegacyRemove,
        LegacyStop,
        AwaitLegacyStop,
        MotionStep300,
        AwaitMotionVisual,
        AwaitResetAfterMotion,
        ContactDamage,
        EdgeDamage,
        AwaitEdgeDamage,
        RepairEdge,
        AwaitRepairEdge,
        Damage,
        AwaitDamage,
        Repair,
        AwaitRepair,
        SaveAt900,
        OpenConsole,
        AwaitConsoleOpen,
        CloseConsole,
        AwaitConsoleClosed,
        LeaveScene,
        Done
    }

    private enum VoxelSlice3SmokePhase
    {
        None,
        LegacyAdd,
        AwaitLegacyAdd,
        LegacyManufacture,
        AwaitLegacyManufacture,
        Manufacture,
        AwaitManufacture,
        Query,
        RefitAdd,
        AwaitRefitAdd,
        RefitDamage,
        AwaitRefitDamage,
        RefitMove,
        AwaitRefitMove,
        RefitRotate,
        AwaitRefitRotate,
        RefitAddQ,
        AwaitRefitAddQ,
        RefitSwap,
        AwaitRefitSwap,
        RefitReplace,
        AwaitRefitReplace,
        RefitRemove,
        AwaitRefitRemove,
        RefitReject,
        RefitContinue,
        AwaitRefitContinue,
        RefitPairs,
        RefitMetricsRun,
        AwaitRefitMetrics,
        PersistBridges,
        AwaitPersist,
        PersistSave,
        Reset,
        AwaitReset,
        Bridge,
        AwaitBridge,
        Fracture,
        AwaitFracture,
        Partial,
        AwaitPartial,
        Drop,
        AwaitDrop,
        RefitFracture,
        AwaitRefitFracture,
        ReloadSetup,
        AwaitReloadSetup,
        ReloadMax150,
        AwaitReloadMax150,
        ReloadMax60,
        AwaitReloadMax60,
        ReloadDisplay,
        AwaitReloadDisplay,
        ReloadTemplate,
        AwaitReloadTemplate,
        ReloadCollision,
        AwaitReloadCollision,
        ReloadReject,
        AwaitReloadReject,
        ReloadValue,
        AwaitReloadValue,
        ReloadFracture,
        AwaitReloadFracture,
        HandoffConsole
    }

    private enum VoxelResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        ReloadMax150,
        AwaitReloadMax150,
        ContinueDamage,
        AwaitContinueDamage,
        ContinueEdit,
        Done
    }

    private enum VoxelSlice3ResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        Continue,
        AwaitContinue,
        Reload,
        AwaitReload,
        Resave,
        Done
    }

    private enum VoxelSlice4ResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        ContinueMotion,
        AwaitContinueVisual,
        Done
    }

    private enum VoxelSlice4GapResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        Done
    }

    private enum VoxelSlice4SupportResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        Done
    }

    private enum VoxelSlice4DestroyResumeSmokePhase
    {
        None,
        Load,
        AwaitLoad,
        Done
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
        LocalIdentity,
        AwaitLocalIdentity,
        LocalRotated,
        AwaitLocalRotated,
        LocalRemoved,
        AwaitLocalRemoved,
        LocalReloaded,
        AwaitLocalReloaded,
        LocalRejected,
        AwaitLocalRejected,
        Done
    }

    private enum VoxelSlice3ObservePhase
    {
        None,
        CaptureEmpty,
        AwaitEmpty,
        Manufacture,
        AwaitManufacture,
        CaptureExterior,
        AwaitExterior,
        AimInterior,
        AwaitInterior,
        AimRefit,
        AwaitAimRefit,
        RefitAdd,
        AwaitRefitAdd,
        CaptureAdded,
        AwaitAdded,
        RefitMove,
        AwaitRefitMove,
        CaptureMoved,
        AwaitMoved,
        RefitRotate,
        AwaitRefitRotate,
        CaptureRotated,
        AwaitRotated,
        RefitSwap,
        AwaitRefitSwap,
        CaptureSwapped,
        AwaitSwapped,
        RefitReplace,
        AwaitRefitReplace,
        CaptureReplaced,
        AwaitReplaced,
        RefitRemove,
        AwaitRefitRemove,
        CaptureRemoved,
        AwaitRemoved,
        RefitReject,
        CaptureRejected,
        AwaitRejected,
        Reset,
        AwaitReset,
        Bridge,
        AwaitBridge,
        CaptureBridge,
        AwaitBridgeView,
        Fracture,
        AwaitFracture,
        CaptureFracture,
        AwaitFractureView,
        Partial,
        AwaitPartial,
        CapturePartial,
        AwaitPartialView,
        Drop,
        AwaitDrop,
        CaptureDrop,
        AwaitDropView,
        RefitFracture,
        AwaitRefitFracture,
        CaptureRefitFracture,
        AwaitRefitFractureView,
        ReloadSetup,
        AwaitReloadSetup,
        CaptureReload150,
        AwaitReload150,
        ReloadMax60,
        AwaitReloadMax60,
        CaptureReload60,
        AwaitReload60,
        ReloadDisplay,
        AwaitReloadDisplay,
        CaptureReloadDisplay,
        AwaitReloadDisplayView,
        ReloadTemplate,
        AwaitReloadTemplate,
        CaptureReloadTemplate,
        AwaitReloadTemplateView,
        ReloadCollision,
        AwaitReloadCollision,
        CaptureReloadCollision,
        AwaitReloadCollisionView,
        ReloadReject,
        AwaitReloadReject,
        CaptureReloadRejected,
        AwaitReloadRejectedView,
        ReloadValue,
        AwaitReloadValue,
        ReloadFracture,
        AwaitReloadFracture,
        CaptureReloadFracture,
        Done
    }

    private enum VoxelSlice4ObservePhase
    {
        None,
        Parked,
        AwaitParked,
        Interpolate,
        AwaitInterpolate,
        Running,
        AwaitRunning,
        FollowCarriage,
        AwaitFollow,
        CrossSeam,
        AwaitCrossSeam,
        Split,
        AwaitSplit,
        SupportLoss,
        AwaitSupportLoss,
        GapStop,
        AwaitGapStop,
        Done
    }

    private enum VoxelSlice4BaselinePhase
    {
        None,
        Running,
        Damage,
        DamageAwait,
        SaveLoad,
        SaveLoadAwait
    }

    private enum VoxelResumeObservePhase
    {
        None,
        Write,
        Load,
        AwaitLoad,
        CaptureRestored,
        AimBoundary,
        AwaitBoundary,
        AimHole,
        AwaitHole,
        AimLedge,
        AwaitLedge,
        ContinueDamage,
        AwaitContinueDamage,
        CaptureHollow,
        CaptureRefit,
        CaptureFracture,
        CaptureDropped,
        CaptureSlice4Resume,
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
        AttachSession(CreateDevelopmentSession(main, main.DevelopmentIdentity));
        _selectedPosition = SelectedCell(_session.Identity);

        AddChild(new DirectionalLight3D
        {
            Position = new Vector3(8, 20, 8),
            RotationDegrees = new Vector3(-50, -25, 0),
            LightEnergy = 1.4f,
            ShadowEnabled = true
        });
        if (main.DevelopmentIdentity.Slice is VoxelDevelopmentSlice.Slice3 or VoxelDevelopmentSlice.Slice4)
        {
            AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0.42f, 0.43f, 0.45f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = Colors.White,
                    AmbientLightEnergy = 0.7f
                }
            });
            AddChild(new OmniLight3D
            {
                Position = new Vector3(12, 11, 9.5f),
                LightEnergy = 4.0f,
                OmniRange = 14,
                ShadowEnabled = false
            });
        }
        var camera = new Camera3D();
        AddChild(camera);
        PlaceCamera(camera, _session.Identity);
        _meshes.Rebuild(this, _session.Identity);
        _colliders.Rebuild(this, _session.Identity);
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
        if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4 &&
            !string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal) &&
            !EnterSlice4Fixture())
            throw new InvalidOperationException("Slice 4 could not construct the fixture railway and static train.");
        if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4 &&
            !string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal))
            _session.MarkPersistenceClean();

        if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_SMOKE_ISOLATION_ABORT"), "1", StringComparison.Ordinal))
            {
                RequireLiveReload().WriteFerrousOre(maxDurability: 150, name: "隔离中止亚铁矿");
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
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
            {
                GD.Print(
                    $"VOXEL_SMOKE_SLICE3_BASE voxels={_session.World.VoxelCount} chunk_size={_session.World.ChunkSize} bounds=32,32,32 chunks=8 seed={_session.Identity.Seed} generator={_session.Identity.GeneratorVersion}");
            }
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
            {
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_BASE voxels={_session.World.VoxelCount} chunk_size={_session.World.ChunkSize} bounds=320,32,32 chunks=80 seed={_session.Identity.Seed} generator={_session.Identity.GeneratorVersion}");
            }
            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                System.Environment.SetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable, "0");
                GD.Print("VOXEL_SMOKE_MENU_REENTERED");
                GetTree().Quit();
                return;
            }

            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeResumeGapEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                if (_session.Identity.Slice != VoxelDevelopmentSlice.Slice4)
                    throw new InvalidOperationException("Gap resume smoke is only defined for slice 4.");
                _session.PauseSimulation();
                _slice4GapResumeSmokePhase = VoxelSlice4GapResumeSmokePhase.Load;
                return;
            }

            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeResumeSupportEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                if (_session.Identity.Slice != VoxelDevelopmentSlice.Slice4)
                    throw new InvalidOperationException("Support-loss resume smoke is only defined for slice 4.");
                _session.PauseSimulation();
                _slice4SupportResumeSmokePhase = VoxelSlice4SupportResumeSmokePhase.Load;
                return;
            }

            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeResumeDestroyEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                if (_session.Identity.Slice != VoxelDevelopmentSlice.Slice4)
                    throw new InvalidOperationException("Full-destroy resume smoke is only defined for slice 4.");
                _session.PauseSimulation();
                _slice4DestroyResumeSmokePhase = VoxelSlice4DestroyResumeSmokePhase.Load;
                return;
            }

            if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeResumeEnvironmentVariable), "1", StringComparison.Ordinal))
            {
                if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
                    _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.Load;
                else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
                {
                    _session.PauseSimulation();
                    _slice4ResumeSmokePhase = VoxelSlice4ResumeSmokePhase.Load;
                }
                else
                    _resumeSmokePhase = VoxelResumeSmokePhase.Load;
                return;
            }

            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
                _slice3SmokePhase = VoxelSlice3SmokePhase.LegacyAdd;
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
            {
                _smokePhase = VoxelSmokePhase.None;
                _slice4SmokePhase = VoxelSlice4SmokePhase.SaveEmpty;
                _session.PauseSimulation();
            }
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveX;
            else
                _smokePhase = VoxelSmokePhase.Remove;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveResumeEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            GetTree().DebugCollisionsHint = true;
            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
                _session.PauseSimulation();
            var rawId = System.Environment.GetEnvironmentVariable(VoxelObserveSaveIdEnvironmentVariable);
            _resumeObservePhase = Guid.TryParse(rawId, out _)
                ? VoxelResumeObservePhase.Load
                : VoxelResumeObservePhase.Write;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveEnvironmentVariable), "1", StringComparison.Ordinal))
        {
            GetTree().DebugCollisionsHint = true;
            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
                _slice4ObservePhase = VoxelSlice4ObservePhase.Parked;
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureEmpty;
            else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice2)
                _slice2ObservePhase = VoxelSlice2ObservePhase.CaptureInitial;
            else
                _observePhase = VoxelObservePhase.CaptureInitial;
        }
        else if (string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_BASELINE"), "1", StringComparison.Ordinal))
        {
            _baselineEnabled = true;
            if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
            {
                _slice4Baseline = true;
                _slice4BaselineRecording = false;
                _baselineRun = 0;
                BeginSlice4BaselineCycle();
            }
            else
            {
                RunMutations(); // One unrecorded warm-up at the identical formal command boundary.
                StartBaselineRun();
            }
        }
    }

    public override void _Process(double delta)
    {
        if (_slice4Baseline && _baselineEnabled)
            StampSlice4BaselineFrame();
        StampRefitMetricFrame();
        if (_dirty)
        {
            if (_refitMetricStamps == 2)
            {
                var refresh = Stopwatch.StartNew();
                Refresh();
                refresh.Stop();
                _refitRefreshMs = refresh.Elapsed.TotalMilliseconds;
            }
            else
                Refresh();
        }
        AdvanceSmoke();
        AdvanceSlice2Smoke();
        AdvanceSlice3Smoke();
        AdvanceSlice4Smoke();
        AdvanceResumeSmoke();
        AdvanceSlice3ResumeSmoke();
        AdvanceSlice4ResumeSmoke();
        AdvanceSlice4GapResumeSmoke();
        AdvanceSlice4SupportResumeSmoke();
        AdvanceSlice4DestroyResumeSmoke();
        AdvanceHostSimulation(delta);
        ApplyRailPresentation();
        AdvanceObserve();
        AdvanceSlice2Observe();
        AdvanceSlice3Observe();
        AdvanceSlice4Observe();
        AdvanceResumeObserve();
        AdvanceBaseline();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_physicsSeenGeneration != _collisionRefreshGeneration)
            _physicsSeenGeneration = _collisionRefreshGeneration;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
            return;
        if (key.Keycode == Key.P && _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
        {
            var paused = _session.SimulationPaused
                ? _session.ResumeSimulation()
                : _session.PauseSimulation();
            _result.Text = paused.Status == TrainMotionStatus.Paused
                ? "最近命令：模拟已暂停。"
                : "最近命令：模拟已恢复。";
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.T && _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
        {
            var started = _session.StartTrain(RailHeading.AlongAlignment, confirmEndHistory: true);
            _result.Text = started.Succeeded
                ? $"最近命令：列车已启动，步 {_session.SimulationStep}。"
                : $"最近命令：启动失败 {started.Status}。";
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Y && _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
        {
            var stopped = _session.StopTrain(confirmEndHistory: true);
            _result.Text = stopped.Succeeded
                ? "最近命令：列车已停车。"
                : $"最近命令：停车失败 {stopped.Status}。";
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.F && _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
        {
            CycleFollowedVehicle();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.R)
        {
            PresentSliceReset(_session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, null)));
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.M && _session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
        {
            SetCompositeResult(_session.Manufacture(HollowShellFixture.DefinitionId, confirmEndHistory: true));
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
            Position = new Vector2(16, 16),
            CustomMinimumSize = new Vector2(640, 0)
        };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        _title = WrapLabel();
        _halt = WrapLabel();
        box.AddChild(_title);
        box.AddChild(_halt);
        box.AddChild(WrapLabel("Delete 移除 · Insert 添加 · M 夹具 · R 重新建立（未保存时确认） · T 启动 · Y 停车 · P 暂停 · F 跟随 · ~ 控制台 · Esc 返回（未保存时确认）"));
        _cell = WrapLabel();
        _result = WrapLabel("最近命令：场景已进入。");
        box.AddChild(_cell);
        box.AddChild(_result);
        panel.AddChild(box);
        layer.AddChild(panel);
        AddChild(layer);
        _worldChangePrompt = new UnsavedWorldChangePrompt();
        _worldChangePrompt.Chosen += choice =>
        {
            if (_departurePending)
                _session.ChooseWorldChange(choice);
            else
                PresentSliceReset(_session.TryChangeWorld(new WorldChangeRequest(WorldChangeKind.Reset, choice)));
        };
        AddChild(_worldChangePrompt);
    }

    private void PresentSliceReset(WorldChangeResult result)
    {
        if (result.Status == WorldChangeStatus.ChoiceRequired)
        {
            _worldChangePrompt?.Open(WorldChangeKind.Reset, _session.Fault is null);
            _result.Text = "最近命令：有未保存变化。请选择保存后重置、放弃或取消。";
            return;
        }

        _worldChangePrompt?.Close();
        if (result.Status == WorldChangeStatus.Completed &&
            _session.Identity.Slice == VoxelDevelopmentSlice.Slice4 &&
            !EnterSlice4Fixture())
            throw new InvalidOperationException("Slice 4 reset could not reconstruct the fixture railway and static train.");
        if (result.Status == WorldChangeStatus.Completed &&
            _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
            _session.MarkPersistenceClean();
        _result.Text = result.Status switch
        {
            WorldChangeStatus.Completed => _session.Identity.Slice switch
            {
                VoxelDevelopmentSlice.Slice4 => "最近命令：已重置并施工正式切片 4 直轨与静态编组。",
                VoxelDevelopmentSlice.Slice3 => "最近命令：已重置正式切片 3 静置车辆世界。",
                VoxelDevelopmentSlice.Slice2 => "最近命令：已重置正式切片 2 多 Chunk 世界。",
                _ => "最近命令：已重置正式 16³ 开发世界。"
            },
            WorldChangeStatus.Cancelled => "最近命令：已取消重置。",
            WorldChangeStatus.SaveFailed => $"最近命令：保存失败 {result.SaveStatus}，重置已取消。{DiagnosticText(result.Diagnostic)}",
            WorldChangeStatus.CandidateFailed => $"最近命令：重置失败 {result.CandidateStatus}。{DiagnosticText(result.Diagnostic)}",
            _ => $"最近命令：重置 {result.Status}。"
        };
    }

    private static string DiagnosticText(WorldSaveDiagnostic? diagnostic) =>
        diagnostic is null
            ? ""
            : $"阶段 {diagnostic.Stage}，对象 {diagnostic.Subject}，关系 {diagnostic.Relationship}。";

    private static Label WrapLabel(string text = "")
    {
        var label = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(600, 0)
        };
        return label;
    }

    public override void _ExitTree()
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.WorldReplaced -= DismissStaleWorldChange;
            _session.WorldChangeChanged -= PresentDeparture;
            _session.Detach();
        }

        if (GetNodeOrNull<Main>("/root/Main") is { } main)
            main.SetVoxelDevelopmentSession(null);
    }

    private void LeaveDevelopmentScene()
    {
        GetNode<Main>("/root/Main").ReturnToMainMenu();
    }

    private void LeaveSmokeScene()
    {
        var dirty = _session.HasUnsavedChanges;
        var world = _session.World;
        var paused = _session.SimulationPaused;
        var step = _session.SimulationStep;
        LeaveDevelopmentScene();
        if (!dirty)
            return;
        RequireSmokeDepartureChoice();
        _worldChangePrompt!.Press("cancel");
        if (_session.IsChangingWorld || _worldChangePrompt.Visible || !ReferenceEquals(_session.World, world) ||
            _session.SimulationPaused != paused || _session.SimulationStep != step || !_session.HasUnsavedChanges)
            throw new InvalidOperationException("Legacy smoke departure cancellation changed the current world or run state.");
        LeaveDevelopmentScene();
        RequireSmokeDepartureChoice();
        _worldChangePrompt.Press("discard");
    }

    private void RequireSmokeDepartureChoice()
    {
        if (!_session.IsChangingWorld || _worldChangePrompt?.Visible != true ||
            _session.AdvanceSimulation(1).Status != TrainMotionStatus.WorldChanging)
            throw new InvalidOperationException("Legacy smoke departure did not expose a choice and suspend simulation.");
    }

    private bool _departurePending;

    internal void RequestDeparture(WorldChangeRequest request) => _session.BeginWorldChange(request);

    private void PresentDeparture(WorldChangeRequest request, WorldChangeResult result)
    {
        if (!request.LeavesScene)
            return;
        _departurePending = result.Status is WorldChangeStatus.ChoiceRequired or WorldChangeStatus.Switching;
        if (result.Status == WorldChangeStatus.ChoiceRequired)
            _worldChangePrompt?.Open(request.Kind, _session.Fault is null);
        else if (result.Status == WorldChangeStatus.Switching)
            _worldChangePrompt?.ShowProgress("正在离开，模拟与修改已暂挂。");
        else
        {
            _worldChangePrompt?.Close();
            if (result.Status == WorldChangeStatus.Completed)
                GetNode<Main>("/root/Main").CompleteWorldDeparture(request.Kind);
            else
            {
                GetNode<Main>("/root/Main").CancelSceneDeparture();
                _result.Text = $"最近操作：离开 {result.Status} {result.SaveStatus}，当前世界保留。";
            }
        }
    }

    private void MarkDirty() => _dirty = true;

    private void DismissStaleWorldChange(WorldReplacement _) => _worldChangePrompt?.Close();

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
        var catalog = GetNode<Main>("/root/Main").Reloader?.Catalog.Current;
        _meshes.Refresh(_session, catalog);
        _colliders.Refresh(_session, catalog);
        _composites.Refresh(this, _session, catalog);
        _tracks.Refresh(this, _session);
        _collisionRefreshGeneration++;
        _mesh = _meshes.PrimaryMesh;
        _dirty = false;
        ApplyRailPresentation();
        var query = _session.Query(_selectedPosition);
        var collide = _session.QueryCollision(_selectedPosition);
        _title.Text = _session.Identity.Slice switch
        {
            VoxelDevelopmentSlice.Slice4 => FollowTitle(),
            VoxelDevelopmentSlice.Slice3 => "Voxel 开发场景 · 切片 3 · 固定视角 · 32³ · 静置车辆",
            VoxelDevelopmentSlice.Slice2 => "Voxel 开发场景 · 切片 2 · 固定视角 · 2×2×2 Chunk · 32³",
            _ => "Voxel 开发场景 · 切片 1 · 固定视角 · 单 Chunk 16³"
        };
        var occupancy = query.Voxel is null
            ? $"目标格 ({_selectedPosition.X},{_selectedPosition.Y},{_selectedPosition.Z})：空。"
            : $"目标格 ({_selectedPosition.X},{_selectedPosition.Y},{_selectedPosition.Z})：占用 · {query.Voxel.DefinitionId.Value} · {CurrentName(query)} · 朝向 {query.Voxel.Orientation} · 耐久 {query.Voxel.CurrentDurability} · value {query.Value} · mass {query.Mass} · 上限 {query.MaxDurability}。";
        var collision = collide.Status switch
        {
            VoxelCollisionQueryStatus.Hit => "碰撞：命中 " + VoxelDefinition.ToWire(collide.Hits[0].Kind),
            VoxelCollisionQueryStatus.Miss => "碰撞：未命中",
            VoxelCollisionQueryStatus.OutOfBounds => "碰撞：越界",
            _ => "碰撞：非法查询"
        };
        if (_halt is not null && _session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
            _halt.Text = HaltHud();
        _cell.Text = occupancy + "\n" + collision + "\n" + FormatVehicleHud();
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

    private void SetCompositeResult(CompositeManufactureResult result)
    {
        _result.Text = result.Succeeded
            ? $"最近命令：已制造车辆 {result.VehicleId}，零件 {result.Parts!.Count}。"
            : $"最近命令：制造失败 {result.Status}。";
    }

    private void SetRefitResult(CompositeRefitResult result)
    {
        _result.Text = result.Succeeded
            ? $"最近命令：改装成功 {result.Status}，零件 {result.Parts!.Count}。"
            : $"最近命令：改装失败 {result.Status}。";
    }

    private CompositeRefitResult RequireCommittedRefit(CompositeRefitResult result, string step)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"Slice 3 {step} failed: {result.Status}.");
        SetRefitResult(result);
        return result;
    }

    private static EntityId RequireSingleAllocated(CompositeRefitResult result, string step)
    {
        if (result.AllocatedIds is not { Count: 1 })
            throw new InvalidOperationException($"Slice 3 {step} did not allocate exactly one id.");
        return result.AllocatedIds[0];
    }

    private void RequirePart(
        EntityId vehicleId,
        LocalVoxelCoord cell,
        EntityId partId,
        int durability,
        VoxelOrientation orientation,
        VoxelDefinitionId definitionId)
    {
        var part = _session.QueryComposite(vehicleId)?.Parts.SingleOrDefault(item => item.Id == partId);
        if (part is null ||
            part.Cell != cell ||
            part.CurrentDurability != durability ||
            part.Orientation != orientation ||
            part.DefinitionId != definitionId)
            throw new InvalidOperationException(
                $"Slice 3 official query mismatch for {partId}: cell={part?.Cell} durability={part?.CurrentDurability} orientation={part?.Orientation} definition={part?.DefinitionId}.");
    }

    private void RequireDamagedMax(EntityId partId, int durability, int max)
    {
        var damaged = _session.QueryDamagedParts().SingleOrDefault(part => part.PartId == partId);
        if (damaged is null || damaged.CurrentDurability != durability || damaged.MaxDurability != max)
            throw new InvalidOperationException(
                $"Damaged part {partId} expected durability {durability}/{max} but was {damaged?.CurrentDurability}/{damaged?.MaxDurability}.");
    }

    private string CurrentCompositeFingerprint() =>
        string.Join("|", _composites.Visuals.Select(item => item.DisplayFingerprint));

    private void RequireContinueAfterRejectedReload()
    {
        if (_session.QueryComposite(_refitVehicle)?.Parts.Count != 169)
            throw new InvalidOperationException("Rejected reload left official composite query unusable.");
        var before = _session.CaptureAuthority();
        if (_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Occupied)).Succeeded ||
            before != _session.CaptureAuthority())
            throw new InvalidOperationException("Rejected reload did not keep later illegal refit atomic.");
        if (!_session.Damage(_refitVehicle, new LocalVoxelCoord(0, 1, 0), 10).Succeeded)
            throw new InvalidOperationException("Rejected reload blocked later official damage.");
    }

    private void RunSlice3ReloadSetup()
    {
        _session.Reset();
        PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
        var manufactured = _session.Manufacture(HollowShellFixture.DefinitionId);
        if (!manufactured.Succeeded)
            throw new InvalidOperationException($"Slice 3 reload setup manufacture failed: {manufactured.Status}.");
        _refitVehicle = manufactured.VehicleId!.Value;
        var added = RequireCommittedRefit(
            _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
            "reload add P");
        _refitP = RequireSingleAllocated(added, "reload add P");
        if (!_session.Damage(_refitVehicle, HollowShellRefit.FirstAdd, 25).Succeeded)
            throw new InvalidOperationException("Slice 3 reload setup could not damage P to 75.");
        RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.SecondAdd)), "reload move P");
        RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Rotate(_refitP, HollowShellRefit.MarkerOrientation)), "reload rotate P");
        RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
        AimRefitCamera(HollowShellRefit.SecondAdd);
        _result.Text = "最近命令：已准备切片 3 热重载改装车。";
    }

    private bool AwaitCommittedVehicleVisual(int vehicles, int parts)
    {
        if (_dirty)
            return false;
        var query = _session.QueryComposites();
        if (query.Count != vehicles || query[0].Parts.Count != parts)
            return false;
        if (_composites.VehicleCount != vehicles || _composites.FaceCount == 0 || _composites.MeshInstanceCount != vehicles)
            return false;
        if (_session.World.VoxelCount != 0 || _meshes.FaceCount != 0)
            throw new InvalidOperationException("Slice 3 refit published vehicle voxels into the world mesh.");
        ValidateCompositeVisual();
        AssertUniqueOwnership();
        return true;
    }

    private void RunOfficialRefitFailures()
    {
        var before = _refitSuccess ?? throw new InvalidOperationException("Slice 3 refit success snapshot is missing.");
        RequireRejectedRefit(
            _session.Refit(_refitVehicle, new[]
            {
                CompositeRefitOperation.Rotate(_refitP, VoxelOrientation.Identity),
                CompositeRefitOperation.Add(HollowShellRefit.OutOfEnvelope, HollowShellRefit.MarkerId, VoxelOrientation.Identity)
            }),
            ConstructionStatus.EnvelopeExceeded,
            before,
            "envelope batch");
        RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
        RequireRejectedRefit(
            _session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Occupied)),
            ConstructionStatus.DuplicateOccupancy,
            before,
            "occupied move");
        RequireRejectedRefit(
            _session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Disconnected)),
            ConstructionStatus.Disconnected,
            before,
            "disconnected move");
        var locked = RequireCommittedRefit(
            _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.LockedCell, HollowShellRefit.LockedId, VoxelOrientation.Identity)),
            "add locked");
        var lockedId = RequireSingleAllocated(locked, "add locked");
        var afterLocked = _session.CaptureAuthority();
        RequireRejectedRefit(
            _session.Refit(_refitVehicle, CompositeRefitOperation.Rotate(lockedId, HollowShellRefit.MarkerOrientation)),
            ConstructionStatus.DisallowedOrientation,
            afterLocked,
            "locked rotate");
        RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Remove(lockedId)), "remove locked");
        var restored = _session.QueryComposite(_refitVehicle)!;
        if (restored.Parts.Count != before.Vehicles[0].Parts.Count ||
            restored.Id != before.Vehicles[0].Id ||
            _session.World.VoxelCount != 0)
            throw new InvalidOperationException("Slice 3 official failures did not restore the success layout.");
        RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
    }

    private void RequireRejectedRefit(
        CompositeRefitResult result,
        ConstructionStatus expected,
        CompositeAuthoritySnapshot before,
        string step)
    {
        if (result.Status != expected || result.Succeeded)
            throw new InvalidOperationException($"Slice 3 {step} expected {expected} but was {result.Status}.");
        if (!before.Equals(_session.CaptureAuthority()))
            throw new InvalidOperationException($"Slice 3 {step} mutated authority.");
        if (_session.World.VoxelCount != 0 || _session.QueryComposites().Count != 1)
            throw new InvalidOperationException($"Slice 3 {step} turned illegal construction into a drop.");
        SetRefitResult(result);
    }

    private void StampRefitMetricFrame()
    {
        if (_slice3SmokePhase == VoxelSlice3SmokePhase.RefitMetricsRun && _refitMetricStamps == 0)
        {
            _refitTickA = Time.GetTicksUsec();
            _refitMetricStamps = 1;
            return;
        }

        if (_slice3SmokePhase != VoxelSlice3SmokePhase.AwaitRefitMetrics)
            return;

        var now = Time.GetTicksUsec();
        if (_refitMetricStamps == 1)
        {
            _refitTickB = now;
            _refitMetricStamps = 2;
        }
        else if (_refitMetricStamps == 2)
        {
            _refitTickC = now;
            _refitMetricStamps = 3;
        }
    }

    private void AimRefitCamera(LocalVoxelCoord cell)
    {
        var target = new Vector3(8f + cell.X + 0.5f, 8f + cell.Y + 0.5f, 8f + cell.Z + 0.5f);
        var camera = GetChildren().OfType<Camera3D>().Single();
        camera.Position = target + new Vector3(0.05f, 2.65f, 1.4f);
        camera.LookAt(target, Vector3.Up);
    }

    private void RequireSuccessorSaveLocal(string staleRevision)
    {
        foreach (var vehicle in _session.Vehicles)
        {
            var saved = vehicle.SaveLocalDefinition
                ?? throw new InvalidOperationException("Slice 3 refit-fracture successor lost its save-local definition.");
            var actualCells = vehicle.Parts.Select(part => part.Cell).ToHashSet();
            if (saved.Voxels.Count != vehicle.Parts.Count ||
                saved.Contacts.Count != vehicle.Contacts.Count ||
                saved.Voxels.Any(spec => !actualCells.Contains(spec.Cell)) ||
                !string.Equals(vehicle.SaveLocalRevision, vehicle.LastUsedRevision, StringComparison.Ordinal) ||
                string.Equals(vehicle.SaveLocalRevision, staleRevision, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Slice 3 refit-fracture save-local mismatch id={vehicle.Id} parts={vehicle.Parts.Count} saved={saved.Voxels.Count} contacts={vehicle.Contacts.Count}/{saved.Contacts.Count}.");
        }

        if (_session.Vehicles.Count == 2 &&
            string.Equals(_session.Vehicles[0].SaveLocalRevision, _session.Vehicles[1].SaveLocalRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Slice 3 refit-fracture successors kept the same save-local revision.");
    }

    private string FormatVehicleHud()
    {
        var vehicles = _session.QueryComposites();
        if (vehicles.Count == 0)
            return $"车辆 0。世界体素 {_session.World.VoxelCount}。世界面 {_meshes.FaceCount}。显示面 {_composites.FaceCount}。";
        if (vehicles.Count > 1)
        {
            var summary = string.Join("；", vehicles.Select(item =>
                $"{item.Id} 零件 {item.Parts.Count} {item.IdentityRetention}/{item.OperationalCondition}"));
            var assemblies = _session.QueryTrainAssemblies();
            var assemblyText = assemblies.Count == 0
                ? ""
                : "\n" + string.Join("\n", assemblies.Select(item =>
                {
                    var coupler = item.Connections.Count == 0
                        ? "无车钩"
                        : $"车钩 {item.Connections[0].FromPartId}->{item.Connections[0].ToPartId}";
                    var front = item.Vehicles[0].FrontContact.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                    var halt = AlongRailDisplay.MotionStatusText(item, paused: false);
                    return $"编组 {item.Id} 车头 {item.ActiveLocomotiveId?.ToString() ?? "无"} {coupler} 前 {front} 速 {item.Speed.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} {halt}";
                })) + $"\n图 {_session.AssemblyGraphGeneration} 步 {_session.SimulationStep}{(_session.SimulationPaused ? " 暂停" : "")}";
            return $"车辆 {vehicles.Count}：{summary}。{assemblyText}\n显示批次 {_composites.MeshInstanceCount} 面 {_composites.FaceCount}。世界体素 {_session.World.VoxelCount}。";
        }

        var vehicle = vehicles[0];
        var envelope = vehicle.Envelope is { } box
            ? $"包络 [{box.MinX},{box.MinY},{box.MinZ}]-[{box.MaxX},{box.MaxY},{box.MaxZ}]"
            : "包络 none";
        return $"车辆 {vehicle.Id} {vehicle.DefinitionId.Value} 零件 {vehicle.Parts.Count} {envelope} 显示批次 {_composites.MeshInstanceCount} 面 {_composites.FaceCount}。世界体素 {_session.World.VoxelCount}。";
    }

    private VoxelLiveReloadDriver RequireLiveReload() =>
        _liveReload ?? throw new InvalidOperationException("Live reload driver is unavailable.");

    private int BaseVoxelCount => _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? 1088 : 512;

    private WorldVoxelCoord ProbePosition =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(0, 1, 0) : new(0, 0, 0);

    private static WorldVoxelCoord LedgeCell => new(15, 1, 8);

    private static WorldVoxelCoord ObserveLedgeCell => new(16, 15, 8);

    private WorldVoxelCoord YawCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(16, 17, 15) : new(4, 11, 4);

    private WorldVoxelCoord TiltCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(15, 17, 16) : new(5, 11, 4);

    private WorldVoxelCoord IdentityTopCell =>
        _session.Identity.Slice == VoxelDevelopmentSlice.Slice2 ? new(14, 17, 14) : new(6, 11, 4);

    private static WorldVoxelCoord SelectedCell(VoxelWorldIdentity identity) =>
        identity.Slice switch
        {
            VoxelDevelopmentSlice.Slice4 => StraightRailFixture.LeftRailSample,
            VoxelDevelopmentSlice.Slice2 => new(15, 16, 15),
            _ => new(4, 4, 4)
        };

    private static FileWorldSaveStore CreateSaveStore(Main main) =>
        new(main.IsolatedUserDataDirectory);

    private static VoxelDevelopmentSession CreateDevelopmentSession(Main main, VoxelWorldIdentity identity) =>
        new(
            main.Reloader?.Catalog ?? throw new InvalidOperationException("Content reloader is unavailable."),
            identity,
            CreateSaveStore(main),
            main.Reloader!.Composites);

    private void AttachSession(VoxelDevelopmentSession session)
    {
        _session = session;
        _session.WorldChangeChanged += PresentDeparture;
        _session.Changed += MarkDirty;
        _session.WorldReplaced += DismissStaleWorldChange;
        GetNode<Main>("/root/Main").SetVoxelDevelopmentSession(_session);
    }

    private void ReplaceSession(VoxelWorldIdentity identity)
    {
        if (_session is not null)
        {
            _session.Changed -= MarkDirty;
            _session.WorldReplaced -= DismissStaleWorldChange;
            _session.WorldChangeChanged -= PresentDeparture;
            _session.Detach();
        }

        AttachSession(CreateDevelopmentSession(GetNode<Main>("/root/Main"), identity));
        _selectedPosition = SelectedCell(identity);
        _meshes.Rebuild(this, identity);
        _colliders.Rebuild(this, identity);
        _composites.Clear();
        _tracks.Clear();
        _mesh = _meshes.PrimaryMesh;
        MarkDirty();
    }

    private static void PlaceCamera(Camera3D camera, VoxelWorldIdentity identity)
    {
        if (identity.Slice == VoxelDevelopmentSlice.Slice4)
        {
            camera.Position = new Vector3(76, 16, -8);
            camera.LookAt(new Vector3(76, 2, 16), Vector3.Up);
            return;
        }

        if (identity.Slice == VoxelDevelopmentSlice.Slice3)
        {
            camera.Position = new Vector3(24, 18, 0);
            camera.LookAt(new Vector3(12, 11, 9.5f), Vector3.Up);
            return;
        }

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
                RequireLiveReload().WriteFerrousOre(maxDurability: 150);
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
                RequireLiveReload().WriteFerrousOre(maxDurability: 150, name: "热重载亚铁矿");
                RequireCommittedReload("display text");
                RequireInspection(80, 10, 1, 150);
                GD.Print("VOXEL_SMOKE_RELOAD_TEXT_UPDATED");
                _smokePhase = VoxelSmokePhase.AwaitReloadText;
                return;
            case VoxelSmokePhase.AwaitReloadText:
                if (!_cell.Text.Contains("热重载亚铁矿", StringComparison.Ordinal) ||
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
                if (!CollisionPhysicsReady || !_cell.Text.Contains("热重载亚铁矿", StringComparison.Ordinal))
                    return;
                if (string.Equals(_displayFingerprintBeforeAsset, _meshes.DisplayFingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException("Voxel smoke cube display did not change after a published asset reload.");
                RequireDomainAndPublishedCollision(_selectedPosition, expectHit: true);
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
                RequireLiveReload().WriteFerrousOre(maxDurability: 60, name: "热重载亚铁矿");
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
                    !_cell.Text.Contains("热重载亚铁矿", StringComparison.Ordinal))
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
                _smokePhase = _session.Identity.Slice == VoxelDevelopmentSlice.Slice2
                    ? VoxelSmokePhase.SaveFixedSequence
                    : VoxelSmokePhase.OpenConsole;
                return;
            case VoxelSmokePhase.SaveFixedSequence:
                WriteSmokeSave();
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
                LeaveSmokeScene();
                _smokePhase = VoxelSmokePhase.Done;
                return;
        }
    }

    private void AdvanceSlice2Smoke()
    {
        switch (_slice2SmokePhase)
        {
            case VoxelSlice2SmokePhase.RemoveX:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 15), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_X_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveX;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveX:
                if (_meshes.LastWorldVoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequirePublishedExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveX);
                RequirePublishedMissingVoxel(new WorldVoxelCoord(16, 16, 15));
                RequirePublishedPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 0));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 15), expectHit: true);
                RequireDomainAndPublishedBox(15.25, 16.25, 15.25, 16.75, 16.75, 15.75, new WorldVoxelCoord(15, 16, 15));
                RequirePublishedCollisionPartialRebuild(new ChunkCoord(1, 1, 0));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_X_BOUNDARY_MESH dirty={FormatDirty()} published=1");
                GD.Print($"VOXEL_SMOKE_X_BOUNDARY_COLLIDE domain=miss godot=miss neighbor=hit published=1 straddle=1 dirty={FormatCollisionDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreX;
                return;
            case VoxelSlice2SmokePhase.RestoreX:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 15), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreX;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreX:
                if (_meshes.LastWorldVoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                RequirePublishedHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveX);
                RequirePublishedPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 0));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: true);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 15), expectHit: true);
                RequireDomainAndPublishedBox(
                    15.25, 16.25, 15.25, 16.75, 16.75, 15.75,
                    new WorldVoxelCoord(15, 16, 15),
                    new WorldVoxelCoord(16, 16, 15));
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveY;
                return;
            case VoxelSlice2SmokePhase.RemoveY:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(15, 15, 15), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_Y_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveY;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveY:
                if (_meshes.LastWorldVoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequirePublishedExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.NegativeY);
                RequirePublishedMissingVoxel(new WorldVoxelCoord(15, 15, 15));
                RequirePublishedPartialRebuild(new ChunkCoord(0, 0, 0), new ChunkCoord(0, 1, 0));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 15, 15), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 15), expectHit: true);
                RequirePublishedCollisionPartialRebuild(new ChunkCoord(0, 0, 0));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_Y_BOUNDARY_MESH dirty={FormatDirty()} published=1");
                GD.Print($"VOXEL_SMOKE_Y_BOUNDARY_COLLIDE domain=miss godot=miss neighbor=hit published=1 dirty={FormatCollisionDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreY;
                return;
            case VoxelSlice2SmokePhase.RestoreY:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(15, 15, 15), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreY;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreY:
                if (_meshes.LastWorldVoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                RequirePublishedHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.NegativeY);
                RequirePublishedPartialRebuild(new ChunkCoord(0, 0, 0), new ChunkCoord(0, 1, 0));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 15, 15), expectHit: true);
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveZ;
                return;
            case VoxelSlice2SmokePhase.RemoveZ:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(15, 16, 16), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_Z_BOUNDARY");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRemoveZ;
                return;
            case VoxelSlice2SmokePhase.AwaitRemoveZ:
                if (_meshes.LastWorldVoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequirePublishedExposedFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveZ);
                RequirePublishedMissingVoxel(new WorldVoxelCoord(15, 16, 16));
                RequirePublishedPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(0, 1, 1));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 15), expectHit: true);
                RequirePublishedCollisionPartialRebuild(new ChunkCoord(0, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_Z_BOUNDARY_MESH dirty={FormatDirty()} published=1");
                GD.Print($"VOXEL_SMOKE_Z_BOUNDARY_COLLIDE domain=miss godot=miss neighbor=hit published=1 dirty={FormatCollisionDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreZ;
                return;
            case VoxelSlice2SmokePhase.RestoreZ:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(15, 16, 16), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreZ;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreZ:
                if (_meshes.LastWorldVoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                RequirePublishedHiddenFace(new WorldVoxelCoord(15, 16, 15), VoxelFaceNormal.PositiveZ);
                RequirePublishedPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(0, 1, 1));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: true);
                _slice2SmokePhase = VoxelSlice2SmokePhase.RemoveJunction;
                return;
            case VoxelSlice2SmokePhase.RemoveJunction:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 16), remove: true, 1087);
                GD.Print("VOXEL_SMOKE_JUNCTION");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitJunction;
                return;
            case VoxelSlice2SmokePhase.AwaitJunction:
                if (_meshes.LastWorldVoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequirePublishedExposedFace(new WorldVoxelCoord(15, 16, 16), VoxelFaceNormal.PositiveX);
                RequirePublishedExposedFace(new WorldVoxelCoord(16, 15, 16), VoxelFaceNormal.PositiveY);
                RequirePublishedExposedFace(new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.PositiveZ);
                RequirePublishedMissingVoxel(new WorldVoxelCoord(16, 16, 16));
                RequirePublishedPartialRebuild(new ChunkCoord(1, 1, 1));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 16), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: true);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 15, 16), expectHit: true);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: true);
                RequirePublishedCollisionPartialRebuild(new ChunkCoord(1, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_JUNCTION_MESH dirty={FormatDirty()} published=1");
                GD.Print($"VOXEL_SMOKE_JUNCTION_COLLIDE domain=miss godot=miss neighbor=hit published=1 dirty={FormatCollisionDirty()}");
                _slice2SmokePhase = VoxelSlice2SmokePhase.RestoreJunction;
                return;
            case VoxelSlice2SmokePhase.RestoreJunction:
                NoteChunkRevisions();
                RequireOccupancyEdit(new WorldVoxelCoord(16, 16, 16), remove: false, 1088);
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitRestoreJunction;
                return;
            case VoxelSlice2SmokePhase.AwaitRestoreJunction:
                if (_meshes.LastWorldVoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                RequirePublishedHiddenFace(new WorldVoxelCoord(15, 16, 16), VoxelFaceNormal.PositiveX);
                RequirePublishedHiddenFace(new WorldVoxelCoord(16, 15, 16), VoxelFaceNormal.PositiveY);
                RequirePublishedHiddenFace(new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.PositiveZ);
                RequirePublishedPartialRebuild(new ChunkCoord(1, 1, 1));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 16), expectHit: true);
                _slice2SmokePhase = VoxelSlice2SmokePhase.CrossDamage;
                return;
            case VoxelSlice2SmokePhase.CrossDamage:
                NoteChunkRevisions();
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
                if (_meshes.LastWorldVoxelCount != 1086 || !CollisionPhysicsReady)
                    return;
                RequirePublishedExposedFace(new WorldVoxelCoord(16, 16, 15), VoxelFaceNormal.NegativeX);
                RequirePublishedMissingVoxel(new WorldVoxelCoord(15, 16, 15));
                RequirePublishedMissingVoxel(new WorldVoxelCoord(16, 16, 16));
                RequirePublishedPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 1));
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 15), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 16), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: true);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: true);
                RequirePublishedCollisionPartialRebuild(new ChunkCoord(0, 1, 0), new ChunkCoord(1, 1, 1));
                ValidateGeneratedMesh();
                GD.Print($"VOXEL_SMOKE_CROSS_DAMAGE_MESH dirty={FormatDirty()} published=1");
                GD.Print($"VOXEL_SMOKE_CROSS_DAMAGE_COLLIDE domain=miss godot=miss neighbor=hit published=1 dirty={FormatCollisionDirty()}");
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
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: true);
                GD.Print("VOXEL_SMOKE_REJECT_PRESERVED");
                _slice2SmokePhase = VoxelSlice2SmokePhase.NoneOccupy;
                return;
            case VoxelSlice2SmokePhase.NoneOccupy:
                if (_session.Add(ProbePosition, HollowId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.Add(ProbePosition, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Occupied)
                    throw new InvalidOperationException("Slice 2 smoke could not place a none-collision occupant.");
                if (_session.QueryCollision(ProbePosition).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Domain collision hit a none occupant before Godot refresh.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitNoneOccupy;
                return;
            case VoxelSlice2SmokePhase.AwaitNoneOccupy:
                if (_session.World.VoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(ProbePosition, expectHit: false);
                if (!_session.Query(ProbePosition).IsOccupied)
                    throw new InvalidOperationException("None occupant lost occupancy.");
                GD.Print("VOXEL_SMOKE_NONE_OCCUPIED_NO_HIT");
                _slice2SmokePhase = VoxelSlice2SmokePhase.ReloadCollisionNone;
                return;
            case VoxelSlice2SmokePhase.ReloadCollisionNone:
                RequireLiveReload().WriteFerrousOre(collisionKind: "none");
                RequireCommittedReload("collision none");
                if (_session.Query(new WorldVoxelCoord(15, 16, 16)).IsOccupied != true ||
                    _session.QueryCollision(new WorldVoxelCoord(15, 16, 16)).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Legal none collision reload did not make occupied ferrous-ore miss immediately.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitReloadCollisionNone;
                return;
            case VoxelSlice2SmokePhase.AwaitReloadCollisionNone:
                if (!CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: false);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: false);
                GD.Print("VOXEL_SMOKE_RELOAD_COLLISION_NONE");
                _slice2SmokePhase = VoxelSlice2SmokePhase.ReloadPresentationCollision;
                return;
            case VoxelSlice2SmokePhase.ReloadPresentationCollision:
                RequireLiveReload().WriteFerrousOre(name: "热重载亚铁矿", collisionKind: "none");
                RequireCommittedReload("presentation keeps none collision");
                if (_session.QueryCollision(new WorldVoxelCoord(15, 16, 16)).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Presentation reload changed collision away from none.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitReloadPresentationCollision;
                return;
            case VoxelSlice2SmokePhase.AwaitReloadPresentationCollision:
                if (!CollisionPhysicsReady ||
                    !string.Equals(CurrentName(_session.Query(new WorldVoxelCoord(15, 16, 16))), "热重载亚铁矿", StringComparison.Ordinal))
                    return;
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: false);
                GD.Print("VOXEL_SMOKE_RELOAD_PRESENTATION_COLLISION_UNCHANGED");
                _slice2SmokePhase = VoxelSlice2SmokePhase.ReloadCollisionFullBox;
                return;
            case VoxelSlice2SmokePhase.ReloadCollisionFullBox:
                RequireLiveReload().WriteFerrousOre(name: "热重载亚铁矿", collisionKind: "full_box");
                RequireCommittedReload("collision full_box");
                if (_session.QueryCollision(new WorldVoxelCoord(15, 16, 16)).Status != VoxelCollisionQueryStatus.Hit)
                    throw new InvalidOperationException("Legal full_box collision reload did not restore a domain hit.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitReloadCollisionFullBox;
                return;
            case VoxelSlice2SmokePhase.AwaitReloadCollisionFullBox:
                if (!CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(15, 16, 16), expectHit: true);
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 16, 15), expectHit: true);
                RequireDomainAndPublishedCollision(ProbePosition, expectHit: false);
                GD.Print("VOXEL_SMOKE_RELOAD_COLLISION_FULL_BOX");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxPlace;
                return;
            case VoxelSlice2SmokePhase.LocalBoxPlace:
                if (_session.Add(LedgeCell, LedgeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.Add(LedgeCell, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status != WorldCommandStatus.Occupied ||
                    _session.World.VoxelCount != 1088)
                    throw new InvalidOperationException("Slice 2 smoke could not place the eccentric local-box example.");
                if (_session.QueryCollision(LedgeCell).Hits[0].Kind != VoxelCollisionKind.LocalBox)
                    throw new InvalidOperationException("Placed ledge did not report local_box from the formal query.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxPlace;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxPlace:
                if (_session.World.VoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                RequireLedgeIdentityRegion();
                RequireDomainAndPublishedCollision(new WorldVoxelCoord(16, 1, 8), expectHit: false);
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_IDENTITY domain=hit godot=hit outside=miss neighbor=miss published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxRotate;
                return;
            case VoxelSlice2SmokePhase.LocalBoxRotate:
                if (_session.Remove(LedgeCell).Status != WorldCommandStatus.Removed ||
                    _session.Add(LedgeCell, LedgeId, VoxelOrientation.Yaw90).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 smoke could not reorient the local-box example.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxRotate;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxRotate:
                if (_session.Query(LedgeCell).Voxel?.Orientation != VoxelOrientation.Yaw90 || !CollisionPhysicsReady)
                    return;
                RequireLedgeYawRegion();
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_ROTATED domain=moved godot=moved published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxRemove;
                return;
            case VoxelSlice2SmokePhase.LocalBoxRemove:
                if (_session.Remove(LedgeCell).Status != WorldCommandStatus.Removed ||
                    _session.QueryCollision(LedgeCell).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Slice 2 smoke did not clear domain collision after removing the local box.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxRemove;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxRemove:
                if (_session.World.VoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(LedgeCell, expectHit: false);
                RequirePublishedLocalBoxMiss();
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_REMOVED domain=miss godot=miss published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxReload;
                return;
            case VoxelSlice2SmokePhase.LocalBoxReload:
                if (_session.Add(LedgeCell, LedgeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 smoke could not restore the local-box example before reload.");
                RequireLiveReload().WriteLedge(0.5, 0.5, 0.5, 0.375, 0.375, 0.375);
                RequireCommittedReload("local box geometry");
                if (_session.Query(LedgeCell).IsOccupied != true ||
                    _session.Query(LedgeCell).Voxel?.Orientation != VoxelOrientation.Identity)
                    throw new InvalidOperationException("Local-box geometry reload did not keep occupancy and orientation.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxReload;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxReload:
                if (!CollisionPhysicsReady)
                    return;
                RequireLedgeReloadedRegion();
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_RELOADED domain=moved godot=moved published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxThinTranslate;
                return;
            case VoxelSlice2SmokePhase.LocalBoxThinTranslate:
                RequireLiveReload().WriteLedge(0, 0, 0.25, 1e-20, 0.875, 0.5);
                RequireCommittedReload("thin local box");
                if (_session.QueryCollision(LedgeCell).Status != VoxelCollisionQueryStatus.Hit)
                    throw new InvalidOperationException("Thin local box lost domain collision after translation to the slice-2 cell.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxThinTranslate;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxThinTranslate:
                if (!CollisionPhysicsReady)
                    return;
                RequirePublishedWholeCell(LedgeCell, expectHit: true);
                RequirePublishedDerivedSize(LedgeCell, expectedSizeX: 1e-20);
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_TRANSLATED_EDGE domain=hit godot=hit size=local published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxReject;
                return;
            case VoxelSlice2SmokePhase.LocalBoxReject:
                var generation = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                var snapshot = GetNode<Main>("/root/Main").Reloader?.Catalog.Current;
                foreach (var sizeX in new[] { 1e-50, double.Epsilon })
                {
                    RequireLiveReload().WriteLedge(0, 0, 0.25, sizeX, 0.875, 0.5);
                    var underflow = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                    if (underflow.Status != ContentReloadStatus.Rejected ||
                        underflow.Changed ||
                        underflow.CatalogVersion != generation ||
                        !ReferenceEquals(snapshot, GetNode<Main>("/root/Main").Reloader?.Catalog.Current) ||
                        !_session.Query(LedgeCell).IsOccupied)
                        throw new InvalidOperationException(
                            $"Adapter-underflow local-box reload size.x={sizeX:G17} did not reject atomically.");
                }

                RequireLiveReload().WriteInvalidLedge();
                var rejectedBox = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejectedBox.Status != ContentReloadStatus.Rejected ||
                    rejectedBox.Changed ||
                    rejectedBox.CatalogVersion != generation ||
                    !ReferenceEquals(snapshot, GetNode<Main>("/root/Main").Reloader?.Catalog.Current) ||
                    !_session.Query(LedgeCell).IsOccupied)
                    throw new InvalidOperationException("Illegal local-box reload did not reject atomically.");
                RequireLiveReload().WriteLedge();
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxReject;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxReject:
                if (!CollisionPhysicsReady)
                    return;
                RequirePublishedWholeCell(LedgeCell, expectHit: true);
                RequirePublishedDerivedSize(LedgeCell, expectedSizeX: 1e-20);
                if (!_colliders.ColliderFor(VoxelChunks.Of(LedgeCell, _session.Identity.ChunkSize))
                        .TryGetBoxSize(LedgeCell, out var keptSize) ||
                    keptSize.X <= 0)
                    throw new InvalidOperationException(
                        $"Rejected underflow left a non-positive published Godot size {keptSize}.");
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_ADAPTER_UNDERFLOW domain=kept godot=kept size=local published=1");
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_REJECTED domain=kept godot=kept published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.LocalBoxLethal;
                return;
            case VoxelSlice2SmokePhase.LocalBoxLethal:
                if (_session.Damage(LedgeCell, 100).Status != WorldCommandStatus.Removed ||
                    _session.QueryCollision(LedgeCell).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Slice 2 smoke did not clear domain collision after lethal local-box damage.");
                _slice2SmokePhase = VoxelSlice2SmokePhase.AwaitLocalBoxLethal;
                return;
            case VoxelSlice2SmokePhase.AwaitLocalBoxLethal:
                if (_session.World.VoxelCount != 1087 || !CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(LedgeCell, expectHit: false);
                RequirePublishedLocalBoxMiss();
                GD.Print("VOXEL_SMOKE_LOCAL_BOX_LETHAL domain=miss godot=miss published=1");
                _slice2SmokePhase = VoxelSlice2SmokePhase.HandoffReload;
                return;
            case VoxelSlice2SmokePhase.HandoffReload:
                _slice2SmokePhase = VoxelSlice2SmokePhase.None;
                _smokePhase = VoxelSmokePhase.ReloadReset;
                return;
        }
    }

    private void AdvanceSlice3Smoke()
    {
        switch (_slice3SmokePhase)
        {
            case VoxelSlice3SmokePhase.LegacyAdd:
                _session.PauseSimulation();
                PressKey(Key.Insert);
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitLegacyAdd;
                return;
            case VoxelSlice3SmokePhase.AwaitLegacyAdd:
                if (_session.Query(_selectedPosition).Voxel is null || _session.UndoCount != 1)
                    throw new InvalidOperationException($"Slice 3 Insert shortcut did not add a voxel with one undo record; undo={_session.UndoCount}.");
                _slice3SmokePhase = VoxelSlice3SmokePhase.LegacyManufacture;
                return;
            case VoxelSlice3SmokePhase.LegacyManufacture:
                PressKey(Key.M);
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitLegacyManufacture;
                return;
            case VoxelSlice3SmokePhase.AwaitLegacyManufacture:
                if (_session.Vehicles.Count != 1 || _session.HasEditHistory)
                    throw new InvalidOperationException("Slice 3 M shortcut did not manufacture after ending non-empty history.");
                GD.Print("VOXEL_SMOKE_SLICE3_LEGACY_SHORTCUT key=M history=nonempty manufactured=true");
                _session.Reset();
                _slice3SmokePhase = VoxelSlice3SmokePhase.Manufacture;
                return;
            case VoxelSlice3SmokePhase.Manufacture:
                if (GetNode<Main>("/root/Main").Reloader?.Composites.Current.TryGet(HollowShellFixture.DefinitionId, out _) != true)
                    throw new InvalidOperationException("Slice 3 smoke did not load core:dev-hollow-shell from JSON.");
                GD.Print("VOXEL_SMOKE_SLICE3_LOADED");
                var manufactured = _session.Manufacture(HollowShellFixture.DefinitionId);
                if (!manufactured.Succeeded || manufactured.Parts?.Count != 168)
                    throw new InvalidOperationException($"Slice 3 smoke manufacture failed: {manufactured.Status}.");
                SetCompositeResult(manufactured);
                GD.Print($"VOXEL_SMOKE_SLICE3_MANUFACTURED vehicle={manufactured.VehicleId} parts={manufactured.Parts.Count} next={_session.NextEntityId}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitManufacture;
                return;
            case VoxelSlice3SmokePhase.AwaitManufacture:
                if (_composites.VehicleCount != 1 || _composites.FaceCount == 0 || _composites.MeshInstanceCount != 1)
                    return;
                ValidateGeneratedMesh();
                ValidateCompositeVisual();
                GD.Print($"VOXEL_SMOKE_SLICE3_VISUAL vehicles={_composites.VehicleCount} meshes={_composites.MeshInstanceCount} faces={_composites.FaceCount} world_faces={_meshes.FaceCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.Query;
                return;
            case VoxelSlice3SmokePhase.Query:
                var query = _session.QueryComposites();
                if (query.Count != 1 || query[0].Parts.Count != 168 || query[0].Contacts.Count != 2)
                    throw new InvalidOperationException("Slice 3 smoke query did not return the formal fixture.");
                if (_session.World.VoxelCount != 0 || _meshes.FaceCount != 0)
                    throw new InvalidOperationException("Slice 3 smoke published vehicle voxels into the world mesh.");
                if (query[0].Envelope != HollowShellFixture.Envelope ||
                    query[0].Contacts[0].BoundCell != HollowShellFixture.CoreCell ||
                    query[0].Contacts[0].Y != -0.75)
                    throw new InvalidOperationException("Slice 3 smoke query did not keep contact coordinates separate from bound cells.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE3_QUERY id={query[0].Id} parts={query[0].Parts.Count} construction={query[0].Construction} identity={query[0].IdentityRetention} envelope=0,0,-1->8,6,4 world={_session.World.VoxelCount}");
                GD.Print("VOXEL_SMOKE_SLICE3_WORLD_EMPTY");
                _refitVehicle = query[0].Id;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitAdd;
                return;
            case VoxelSlice3SmokePhase.RefitAdd:
                var added = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "add P");
                _refitP = RequireSingleAllocated(added, "add P");
                RequirePart(_refitVehicle, HollowShellRefit.FirstAdd, _refitP, 100, VoxelOrientation.Identity, HollowShellRefit.MarkerId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_ADDED id={_refitP} cell=2,1,0 durability=100");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitAdd;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitAdd:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitDamage;
                return;
            case VoxelSlice3SmokePhase.RefitDamage:
                var damaged = _session.Damage(_refitVehicle, HollowShellRefit.FirstAdd, 25);
                if (!damaged.Succeeded || damaged.Targets?[0].RemainingDurability != 75)
                    throw new InvalidOperationException($"Slice 3 smoke refit damage failed: {damaged.Status}.");
                RequirePart(_refitVehicle, HollowShellRefit.FirstAdd, _refitP, 75, VoxelOrientation.Identity, HollowShellRefit.MarkerId);
                _result.Text = "最近命令：改装标记件已伤害 25。";
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_DAMAGED id={_refitP} durability=75");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitDamage;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitDamage:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitMove;
                return;
            case VoxelSlice3SmokePhase.RefitMove:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Moved)), "move P");
                RequirePart(_refitVehicle, HollowShellRefit.Moved, _refitP, 75, VoxelOrientation.Identity, HollowShellRefit.MarkerId);
                if (_session.QueryComposite(_refitVehicle)!.Parts.Any(part => part.Cell == HollowShellRefit.FirstAdd))
                    throw new InvalidOperationException("Slice 3 smoke move left the old cell occupied.");
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_MOVED id={_refitP} cell=3,1,0");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitMove;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitMove:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitRotate;
                return;
            case VoxelSlice3SmokePhase.RefitRotate:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Rotate(_refitP, HollowShellRefit.MarkerOrientation)), "rotate P");
                RequirePart(_refitVehicle, HollowShellRefit.Moved, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_ROTATED id={_refitP} orientation=nz_py");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitRotate;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitRotate:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitAddQ;
                return;
            case VoxelSlice3SmokePhase.RefitAddQ:
                var addedQ = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "add Q");
                _refitQ = RequireSingleAllocated(addedQ, "add Q");
                if (_refitP == _refitQ)
                    throw new InvalidOperationException("Slice 3 smoke allocated the same id to P and Q.");
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_ADDED_Q id={_refitQ} cell=4,1,0");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitAddQ;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitAddQ:
                if (!AwaitCommittedVehicleVisual(1, 170))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitSwap;
                return;
            case VoxelSlice3SmokePhase.RefitSwap:
                var sequential = _session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.SecondAdd));
                if (sequential.Status != ConstructionStatus.DuplicateOccupancy)
                    throw new InvalidOperationException($"Slice 3 smoke sequential swap was {sequential.Status}.");
                RequireCommittedRefit(
                    _session.Refit(_refitVehicle, new[]
                    {
                        CompositeRefitOperation.Move(_refitP, HollowShellRefit.SecondAdd),
                        CompositeRefitOperation.Move(_refitQ, HollowShellRefit.Moved)
                    }),
                    "swap");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                RequirePart(_refitVehicle, HollowShellRefit.Moved, _refitQ, 100, VoxelOrientation.Identity, HollowShellRefit.MarkerId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_SWAPPED p={_refitP} q={_refitQ}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitSwap;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitSwap:
                if (!AwaitCommittedVehicleVisual(1, 170))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitReplace;
                return;
            case VoxelSlice3SmokePhase.RefitReplace:
                var replaced = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Replace(HollowShellRefit.Moved, HollowShellRefit.ReplacementId, VoxelOrientation.Identity)),
                    "replace Q");
                _refitR = RequireSingleAllocated(replaced, "replace Q");
                if (_refitR == _refitP || _refitR == _refitQ)
                    throw new InvalidOperationException("Slice 3 smoke replace reused an old identity.");
                if (_session.QueryComposite(_refitVehicle)!.Parts.Any(part => part.Id == _refitQ))
                    throw new InvalidOperationException("Slice 3 smoke replace kept Q.");
                RequirePart(_refitVehicle, HollowShellRefit.Moved, _refitR, 150, VoxelOrientation.Identity, HollowShellRefit.ReplacementId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_REPLACED r={_refitR} durability=150");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitReplace;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitReplace:
                if (!AwaitCommittedVehicleVisual(1, 170))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitRemove;
                return;
            case VoxelSlice3SmokePhase.RefitRemove:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Remove(_refitR)), "remove R");
                var success = _session.QueryComposite(_refitVehicle)!;
                if (success.Parts.Any(part => part.Id == _refitQ || part.Id == _refitR) || success.Parts.Count != 169)
                    throw new InvalidOperationException("Slice 3 smoke success layout was not 169 parts with P retained.");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                _refitSuccess = _session.CaptureAuthority();
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_REMOVED p={_refitP} parts=169");
                GD.Print("VOXEL_SMOKE_SLICE3_REFIT_SUCCESS");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitRemove;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitRemove:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitReject;
                return;
            case VoxelSlice3SmokePhase.RefitReject:
                RunOfficialRefitFailures();
                GD.Print("VOXEL_SMOKE_SLICE3_REFIT_REJECT_ENVELOPE");
                GD.Print("VOXEL_SMOKE_SLICE3_REFIT_REJECT_OCCUPIED");
                GD.Print("VOXEL_SMOKE_SLICE3_REFIT_REJECT_DISCONNECTED");
                GD.Print("VOXEL_SMOKE_SLICE3_REFIT_REJECT_ORIENTATION");
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitContinue;
                return;
            case VoxelSlice3SmokePhase.RefitContinue:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Moved)), "continue move");
                RequirePart(_refitVehicle, HollowShellRefit.Moved, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.SecondAdd)), "continue return");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_CONTINUE id={_refitP}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitContinue;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitContinue:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitPairs;
                return;
            case VoxelSlice3SmokePhase.RefitPairs:
                HollowShellRefit.RunMoveReturnPairs(_session, _refitVehicle, _refitP, 100);
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                GD.Print($"VOXEL_SMOKE_SLICE3_REFIT_PAIRS id={_refitP} pairs=100 parts={_session.QueryComposite(_refitVehicle)!.Parts.Count}");
                _refitMetricRun = 1;
                _refitMetricStamps = 0;
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitMetricsRun;
                return;
            case VoxelSlice3SmokePhase.RefitMetricsRun:
                _refitRefreshMs = 0;
                _refitMemoryBefore = GC.GetTotalMemory(true);
                var mutation = Stopwatch.StartNew();
                HollowShellRefit.RunMoveReturnPairs(_session, _refitVehicle, _refitP, 100);
                mutation.Stop();
                _refitMutationMs = mutation.Elapsed.TotalMilliseconds;
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitMetrics;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitMetrics:
                if (_refitMetricStamps < 3 || !AwaitCommittedVehicleVisual(1, 169))
                    return;
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                var memoryAfter = GC.GetTotalMemory(false);
                var mutationFrameMs = (_refitTickB - _refitTickA) / 1000.0;
                var refreshFrameMs = (_refitTickC - _refitTickB) / 1000.0;
                var frameMs = (_refitTickC - _refitTickA) / 1000.0;
                GD.Print(
                    $"REFIT_METRICS run={_refitMetricRun} parts={_session.QueryComposite(_refitVehicle)!.Parts.Count} world={_session.World.VoxelCount} mutation_ms={_refitMutationMs:F3} refresh_ms={_refitRefreshMs:F3} frame_ms={frameMs:F3} mutation_frame_ms={mutationFrameMs:F3} refresh_frame_ms={refreshFrameMs:F3} process_delta_ms={GetProcessDeltaTime() * 1000.0:F3} memory_bytes={memoryAfter} memory_delta={memoryAfter - _refitMemoryBefore}");
                if (_refitMetricRun < 3)
                {
                    _refitMetricRun++;
                    _refitMetricStamps = 0;
                    _slice3SmokePhase = VoxelSlice3SmokePhase.RefitMetricsRun;
                    return;
                }

                GD.Print("COMPOSITE_REFIT_SMOKE_OK");
                _slice3SmokePhase = VoxelSlice3SmokePhase.PersistBridges;
                return;
            case VoxelSlice3SmokePhase.PersistBridges:
                AddSlice3PersistBridges();
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitPersist;
                return;
            case VoxelSlice3SmokePhase.AwaitPersist:
                if (!AwaitSlice3PersistVisual())
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.PersistSave;
                return;
            case VoxelSlice3SmokePhase.PersistSave:
                WriteSlice3PersistSave();
                _slice3SmokePhase = VoxelSlice3SmokePhase.Reset;
                return;
            case VoxelSlice3SmokePhase.Reset:
                _session.Reset();
                _result.Text = "最近命令：已重置正式切片 3 静置车辆世界。";
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReset;
                return;
            case VoxelSlice3SmokePhase.AwaitReset:
                if (_session.QueryComposites().Count != 0 || _composites.VehicleCount != 0)
                    return;
                GD.Print($"VOXEL_SMOKE_SLICE3_RESET vehicles=0 next={_session.NextEntityId} transactions={_session.TransactionCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.Bridge;
                return;
            case VoxelSlice3SmokePhase.Bridge:
                if (GetNode<Main>("/root/Main").Reloader?.Composites.Current.TryGet(BridgeFixture.DefinitionId, out _) != true)
                    throw new InvalidOperationException("Slice 3 smoke did not load core:dev-bridge from JSON.");
                var bridged = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!bridged.Succeeded || bridged.Parts?.Count != 55)
                    throw new InvalidOperationException($"Slice 3 smoke bridge manufacture failed: {bridged.Status}.");
                SetCompositeResult(bridged);
                GD.Print($"VOXEL_SMOKE_SLICE3_BRIDGE vehicle={bridged.VehicleId} parts={bridged.Parts.Count}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitBridge;
                return;
            case VoxelSlice3SmokePhase.AwaitBridge:
                if (_composites.VehicleCount != 1 || _composites.FaceCount == 0)
                    return;
                ValidateCompositeVisual();
                _slice3SmokePhase = VoxelSlice3SmokePhase.Fracture;
                return;
            case VoxelSlice3SmokePhase.Fracture:
                var bridgeVehicle = _session.QueryComposites().Single();
                var fractured = _session.Damage(bridgeVehicle.Id, BridgeFixture.BridgeCell, 100);
                if (!fractured.Succeeded || _session.QueryComposites().Count != 2)
                    throw new InvalidOperationException($"Slice 3 smoke bridge damage failed: {fractured.Status}.");
                if (_session.World.VoxelCount != 0)
                    throw new InvalidOperationException("Equal-value bridge survivors must not drop into the world.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：桥接伤害已结算断裂。";
                GD.Print($"VOXEL_SMOKE_SLICE3_FRACTURE vehicles=2 world={_session.World.VoxelCount} primary={_session.QueryComposites()[0].Id} secondary={_session.QueryComposites()[1].Id}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitFracture;
                return;
            case VoxelSlice3SmokePhase.AwaitFracture:
                if (_composites.VehicleCount != 2 || _composites.MeshInstanceCount != 2 || _meshes.FaceCount != 0)
                    return;
                ValidateCompositeVisual();
                GD.Print($"VOXEL_SMOKE_SLICE3_OWNERSHIP vehicles={_composites.VehicleCount} meshes={_composites.MeshInstanceCount} world_faces={_meshes.FaceCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.Partial;
                return;
            case VoxelSlice3SmokePhase.Partial:
                _session.Reset();
                var partialVehicle = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!partialVehicle.Succeeded)
                    throw new InvalidOperationException($"Slice 3 smoke partial manufacture failed: {partialVehicle.Status}.");
                var partial = _session.Damage(new[]
                {
                    new CompositeDamageTarget(partialVehicle.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(partialVehicle.VehicleId.Value, BridgeFixture.LeftCoreCell, 100)
                });
                if (!partial.Succeeded || _session.QueryComposites().Count != 1 || _session.World.VoxelCount != 26)
                    throw new InvalidOperationException($"Slice 3 smoke partial drop failed: {partial.Status} vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount}.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：桥和左核心伤害已结算。";
                GD.Print($"VOXEL_SMOKE_SLICE3_PARTIAL vehicles=1 world={_session.World.VoxelCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitPartial;
                return;
            case VoxelSlice3SmokePhase.AwaitPartial:
                if (_composites.VehicleCount != 1 ||
                    _session.World.VoxelCount != 26 ||
                    _composites.FaceCount == 0 ||
                    _meshes.FaceCount == 0 ||
                    !CollisionPhysicsReady)
                    return;
                RequirePublishedWholeCell(new WorldVoxelCoord(8, 8, 8), true);
                RequirePublishedWholeCell(new WorldVoxelCoord(11, 9, 9), false);
                ValidateGeneratedMesh();
                ValidateCompositeVisual();
                GD.Print($"VOXEL_SMOKE_SLICE3_PARTIAL_OWNERSHIP vehicles={_composites.VehicleCount} world_faces={_meshes.FaceCount} collide=hit");
                _slice3SmokePhase = VoxelSlice3SmokePhase.Drop;
                return;
            case VoxelSlice3SmokePhase.Drop:
                _session.Reset();
                var dropVehicle = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!dropVehicle.Succeeded)
                    throw new InvalidOperationException($"Slice 3 smoke drop manufacture failed: {dropVehicle.Status}.");
                var dropped = _session.Damage(new[]
                {
                    new CompositeDamageTarget(dropVehicle.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(dropVehicle.VehicleId.Value, BridgeFixture.LeftCoreCell, 100),
                    new CompositeDamageTarget(dropVehicle.VehicleId.Value, BridgeFixture.RightCoreCell, 100)
                });
                if (dropped.Status != CompositeDamageStatus.Removed || _session.QueryComposites().Count != 0 || _session.World.VoxelCount != 52)
                    throw new InvalidOperationException($"Slice 3 smoke drop failed: {dropped.Status} vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount}.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：双核心伤害已整体脱落。";
                GD.Print($"VOXEL_SMOKE_SLICE3_DROP vehicles=0 world={_session.World.VoxelCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitDrop;
                return;
            case VoxelSlice3SmokePhase.AwaitDrop:
                if (_composites.VehicleCount != 0 ||
                    _session.World.VoxelCount != 52 ||
                    _meshes.FaceCount == 0 ||
                    !CollisionPhysicsReady)
                    return;
                RequirePublishedWholeCell(new WorldVoxelCoord(8, 8, 8), true);
                RequirePublishedWholeCell(new WorldVoxelCoord(11, 9, 9), false);
                ValidateGeneratedMesh();
                GD.Print("COMPOSITE_SMOKE_OK");
                _slice3SmokePhase = VoxelSlice3SmokePhase.RefitFracture;
                return;
            case VoxelSlice3SmokePhase.RefitFracture:
                _session.Reset();
                var refitBridge = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!refitBridge.Succeeded || refitBridge.Parts?.Count != 55)
                    throw new InvalidOperationException($"Slice 3 smoke refit-fracture manufacture failed: {refitBridge.Status}.");
                RequireCommittedRefit(
                    _session.Refit(
                        refitBridge.VehicleId!.Value,
                        CompositeRefitOperation.Add(BridgeFixture.SecondBridgeCell, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "second bridge");
                var staleRevision = _session.Vehicles.Single().SaveLocalRevision
                    ?? throw new InvalidOperationException("Slice 3 smoke refit-fracture did not write a save-local revision.");
                if (_session.Vehicles.Single().Parts.Count != 56)
                    throw new InvalidOperationException("Slice 3 smoke second bridge did not produce 56 parts.");
                var refitCut = _session.Damage(new[]
                {
                    new CompositeDamageTarget(refitBridge.VehicleId.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(refitBridge.VehicleId.Value, BridgeFixture.SecondBridgeCell, 100)
                });
                if (!refitCut.Succeeded ||
                    _session.Vehicles.Count != 2 ||
                    _session.World.VoxelCount != 0 ||
                    _session.Vehicles.Any(vehicle => vehicle.Parts.Count != 27 || vehicle.Contacts.Count != 1))
                    throw new InvalidOperationException(
                        $"Slice 3 smoke refit-fracture failed: {refitCut.Status} vehicles={_session.Vehicles.Count} world={_session.World.VoxelCount}.");
                RequireSuccessorSaveLocal(staleRevision);
                AssertUniqueOwnership();
                _result.Text = "最近命令：改装后双桥同事件断裂已结算。";
                GD.Print(
                    $"VOXEL_SMOKE_SLICE3_REFIT_FRACTURE vehicles=2 parts={_session.Vehicles[0].Parts.Count},{_session.Vehicles[1].Parts.Count} world={_session.World.VoxelCount}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitRefitFracture;
                return;
            case VoxelSlice3SmokePhase.AwaitRefitFracture:
                if (!AwaitCommittedVehicleVisual(2, 27))
                    return;
                ValidateCompositeVisual();
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadSetup;
                return;
            case VoxelSlice3SmokePhase.ReloadSetup:
                RunSlice3ReloadSetup();
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadSetup;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadSetup:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadMax150;
                return;
            case VoxelSlice3SmokePhase.ReloadMax150:
                RequireLiveReload().WriteRefitMarkerMax(150);
                RequireCommittedReload("slice3-marker-max-150");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                RequireDamagedMax(_refitP, 75, 150);
                GD.Print($"VOXEL_SMOKE_SLICE3_RELOAD_MAX_150 id={_refitP} durability=75 max=150");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadMax150;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadMax150:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadMax60;
                return;
            case VoxelSlice3SmokePhase.ReloadMax60:
                RequireLiveReload().WriteRefitMarkerMax(60);
                RequireCommittedReload("slice3-marker-max-60");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 60, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                if (_session.QueryDamagedParts().Any(part => part.PartId == _refitP))
                    throw new InvalidOperationException("Clamped marker durability should no longer be below the new maximum.");
                GD.Print($"VOXEL_SMOKE_SLICE3_RELOAD_MAX_60 id={_refitP} durability=60 max=60");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadMax60;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadMax60:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadDisplay;
                return;
            case VoxelSlice3SmokePhase.ReloadDisplay:
                _reloadFingerprint = CurrentCompositeFingerprint();
                RequireLiveReload().WriteRefitMarkerName("显示改装标记");
                RequireLiveReload().WriteAsymmetricMarkerTop();
                RequireCommittedReload("slice3-marker-display");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 60, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadDisplay;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadDisplay:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                if (string.Equals(_reloadFingerprint, CurrentCompositeFingerprint(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Slice 3 display reload did not refresh existing vehicle textures.");
                GD.Print("VOXEL_SMOKE_SLICE3_RELOAD_DISPLAY");
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadTemplate;
                return;
            case VoxelSlice3SmokePhase.ReloadTemplate:
                RequireLiveReload().AddHollowShellExtraCell();
                RequireCommittedReload("slice3-hollow-template");
                if (_session.QueryComposite(_refitVehicle)!.Parts.Count != 169 ||
                    _session.QueryComposite(_refitVehicle)!.Parts.Any(part => part.Cell == HollowShellRefit.FirstAdd))
                    throw new InvalidOperationException("Template reload rebuilt the existing refitted vehicle.");
                var fresh = _session.Manufacture(HollowShellFixture.DefinitionId);
                if (!fresh.Succeeded || fresh.Parts?.Count != 169 || fresh.Parts.All(part => part.Cell != HollowShellRefit.FirstAdd))
                    throw new InvalidOperationException("New manufacture did not use the updated hollow-shell template.");
                GD.Print($"VOXEL_SMOKE_SLICE3_RELOAD_TEMPLATE existing=169 manufactured={fresh.Parts.Count}");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadTemplate;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadTemplate:
                if (!AwaitCommittedVehicleVisual(2, 169))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadCollision;
                return;
            case VoxelSlice3SmokePhase.ReloadCollision:
                if (_session.Add(_reloadWorldCell, HollowShellFixture.ShellId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added ||
                    _session.QueryCollision(_reloadWorldCell).Status != VoxelCollisionQueryStatus.Hit)
                    throw new InvalidOperationException("Slice 3 collision setup did not place a full_box world shell.");
                RequireLiveReload().WriteVehicleShellCollision("none");
                RequireCommittedReload("slice3-shell-collision-none");
                if (_session.QueryCollision(_reloadWorldCell).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Vehicle-shell collision reload did not update supported world collision.");
                GD.Print("VOXEL_SMOKE_SLICE3_RELOAD_COLLISION domain=miss");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadCollision;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadCollision:
                if (!CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(_reloadWorldCell, expectHit: false);
                GD.Print("VOXEL_SMOKE_SLICE3_RELOAD_COLLISION_PUBLISHED published=1");
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadReject;
                return;
            case VoxelSlice3SmokePhase.ReloadReject:
                _reloadFingerprint = CurrentCompositeFingerprint();
                _reloadAuthority = _session.CaptureAuthority();
                _generationBeforeReject = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                var compositeGeneration = GetNode<Main>("/root/Main").Reloader?.Composites.Current.Generation ?? 0;
                RequireLiveReload().RemoveIdentityCore();
                var rejected = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejected.Status != ContentReloadStatus.Rejected ||
                    rejected.Changed ||
                    rejected.CatalogVersion != _generationBeforeReject ||
                    GetNode<Main>("/root/Main").Reloader!.Composites.Current.Generation != compositeGeneration ||
                    _reloadAuthority != _session.CaptureAuthority())
                    throw new InvalidOperationException("Slice 3 missing-core reload did not reject atomically.");
                if (!string.Equals(_reloadFingerprint, CurrentCompositeFingerprint(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Slice 3 rejected reload changed existing vehicle display.");
                RequireContinueAfterRejectedReload();
                RequireLiveReload().RestoreIdentityCore();
                GD.Print("VOXEL_SMOKE_SLICE3_RELOAD_REJECTED");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadReject;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadReject:
                if (_session.QueryComposites().Count != 2)
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadValue;
                return;
            case VoxelSlice3SmokePhase.ReloadValue:
                _session.Reset();
                RequireLiveReload().WriteIdentityCoreHeavyValue(10);
                RequireCommittedReload("slice3-heavy-core-10");
                var rightHeavy = _session.Manufacture(BridgeFixture.RightHeavyDefinitionId);
                if (!rightHeavy.Succeeded)
                    throw new InvalidOperationException($"Slice 3 right-heavy manufacture failed: {rightHeavy.Status}.");
                _refitVehicle = rightHeavy.VehicleId!.Value;
                RequireLiveReload().WriteIdentityCoreHeavyValue(20);
                RequireCommittedReload("slice3-heavy-core-20");
                if (GetNode<Main>("/root/Main").Reloader!.Catalog.Current!.Entries[BridgeFixture.HeavyCoreDefinitionId].Definition.Value != 20)
                    throw new InvalidOperationException("Reloaded right-core value was not 20.");
                GD.Print("VOXEL_SMOKE_SLICE3_RELOAD_VALUE value=20");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadValue;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadValue:
                if (!AwaitCommittedVehicleVisual(1, 55))
                    return;
                _slice3SmokePhase = VoxelSlice3SmokePhase.ReloadFracture;
                return;
            case VoxelSlice3SmokePhase.ReloadFracture:
                if (!_session.Damage(_refitVehicle, BridgeFixture.BridgeCell, 100).Succeeded)
                    throw new InvalidOperationException("Slice 3 value-fracture damage failed.");
                var successors = _session.QueryComposites();
                if (successors.Count != 2 ||
                    successors[0].Id != _refitVehicle ||
                    successors[0].Parts.Any(part => !BridgeFixture.IsRightCell(part.Cell)) ||
                    successors[0].OperationalCondition != OperationalCondition.Disabled)
                    throw new InvalidOperationException("Reloaded right-core value did not become the primary successor.");
                GD.Print($"VOXEL_SMOKE_SLICE3_RELOAD_FRACTURE primary={successors[0].Id} side=right");
                GD.Print("COMPOSITE_RELOAD_SMOKE_OK");
                _slice3SmokePhase = VoxelSlice3SmokePhase.AwaitReloadFracture;
                return;
            case VoxelSlice3SmokePhase.AwaitReloadFracture:
                if (!AwaitCommittedVehicleVisual(2, 27))
                    return;
                ValidateCompositeVisual();
                _slice3SmokePhase = VoxelSlice3SmokePhase.HandoffConsole;
                return;
            case VoxelSlice3SmokePhase.HandoffConsole:
                _slice3SmokePhase = VoxelSlice3SmokePhase.None;
                _smokePhase = VoxelSmokePhase.OpenConsole;
                return;
        }
    }

    private void AssertUniqueOwnership()
    {
        var partIds = _session.QueryComposites().SelectMany(item => item.Parts).Select(part => part.Id.Value).ToArray();
        if (partIds.Length != partIds.Distinct().Count())
            throw new InvalidOperationException("Slice 3 smoke duplicated part identities.");
        var worldIds = _session.World.OccupiedVoxels()
            .Select(item => item.Value.RuntimeId?.Value)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
        if (worldIds.Length != worldIds.Distinct().Count() || partIds.Intersect(worldIds).Any())
            throw new InvalidOperationException("Slice 3 smoke duplicated runtime identities across owners.");
        foreach (var vehicle in _session.QueryComposites())
        {
            foreach (var part in vehicle.Parts)
            {
                var worldCell = FragmentGridSnap.TransformCell(
                    vehicle.Transform.Orientation,
                    part.Cell,
                    FragmentGridSnap.SnapAnchor(vehicle.Transform.OriginX, vehicle.Transform.OriginY, vehicle.Transform.OriginZ));
                if (_session.Query(worldCell).IsOccupied)
                    throw new InvalidOperationException($"Slice 3 smoke found shared ownership at {worldCell}.");
            }
        }
    }

    private void ValidateCompositeVisual()
    {
        if (_composites.MeshInstanceCount != _composites.VehicleCount)
            throw new InvalidOperationException("Composite visual created more mesh instances than vehicles.");
        foreach (var visual in _composites.Visuals)
        {
            if (visual.GeneratedMesh is null || visual.FaceCount == 0)
                throw new InvalidOperationException("Composite visual has no batched mesh.");
            if (visual.Scale != Vector3.One)
                throw new InvalidOperationException("Composite visual applied a non-uniform scale.");
            VoxelMeshGeometryValidator.Validate(visual.GeneratedMesh, visual.FaceCount);
        }
    }

    private void AddSlice3PersistBridges()
    {
        if (_session.QueryComposites().Count != 1 ||
            _session.QueryComposite(_refitVehicle)?.Parts.Count != 169)
            throw new InvalidOperationException("Slice 3 persist expected the official refit shell before adding bridges.");
        var equal = _session.Manufacture(BridgeFixture.DefinitionId, PersistEqualBridgePose);
        if (!equal.Succeeded)
            throw new InvalidOperationException($"Slice 3 persist equal-bridge manufacture failed: {equal.Status}.");
        if (!_session.Damage(equal.VehicleId!.Value, BridgeFixture.BridgeCell, 100).Succeeded ||
            _session.QueryComposites().Count != 3 ||
            _session.World.VoxelCount != 0)
            throw new InvalidOperationException("Slice 3 persist equal-value fracture did not keep two successors.");
        var drop = _session.Manufacture(BridgeFixture.DefinitionId, PersistDropBridgePose);
        if (!drop.Succeeded)
            throw new InvalidOperationException($"Slice 3 persist drop-bridge manufacture failed: {drop.Status}.");
        if (!_session.Damage(new[]
            {
                new CompositeDamageTarget(drop.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                new CompositeDamageTarget(drop.VehicleId.Value, BridgeFixture.LeftCoreCell, 100)
            }).Succeeded ||
            _session.QueryComposites().Count != 4 ||
            _session.World.VoxelCount != 26)
            throw new InvalidOperationException(
                $"Slice 3 persist partial drop failed: vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount}.");
        RequireRestoredSlice3Persist();
        _result.Text = "最近命令：已准备切片 3 存档夹具。";
    }

    private bool AwaitSlice3PersistVisual()
    {
        if (_dirty)
            return false;
        if (_session.QueryComposites().Count != 4 || _session.World.VoxelCount != 26)
            return false;
        if (_composites.VehicleCount != 4 ||
            _composites.MeshInstanceCount != 4 ||
            _composites.FaceCount == 0 ||
            _meshes.FaceCount == 0 ||
            !CollisionPhysicsReady)
            return false;
        ValidateCompositeVisual();
        ValidateGeneratedMesh();
        AssertUniqueOwnership();
        var dropped = _session.World.OccupiedVoxels().First(item => item.Value.RuntimeId is not null).Key;
        RequirePublishedWholeCell(dropped, true);
        return true;
    }

    private void WriteSlice3PersistSave()
    {
        var saved = _session.Save("Slice 3 Persist");
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Slice 3 persist could not write the save: {saved.Status}.");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Slice 3 persist save file was not created at {path}.");
        var persistShell = _session.QueryComposites().Single(item => item.DefinitionId == HollowShellFixture.DefinitionId);
        var persistP = persistShell.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id;
        GD.Print($"VOXEL_SMOKE_SLICE3_SAVE_WRITTEN id={saved.SaveId:D} bytes={new FileInfo(path).Length} path={path} vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount} vehicle={persistShell.Id} p_id={persistP}");
    }

    private void RequireRestoredSlice3Persist()
    {
        var vehicles = _session.QueryComposites();
        if (vehicles.Count != 4 || _session.World.VoxelCount != 26)
            throw new InvalidOperationException(
                $"Slice 3 persist authority was vehicles={vehicles.Count} world={_session.World.VoxelCount}.");
        var hollows = vehicles.Where(item => item.DefinitionId == HollowShellFixture.DefinitionId).ToArray();
        if (hollows.Length != 1 || hollows[0].Parts.Count != 169)
            throw new InvalidOperationException("Slice 3 persist did not keep the official refit shell.");
        var marker = hollows[0].Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd);
        if (marker.Id.Value == 0 ||
            marker.CurrentDurability != 75 ||
            marker.Orientation != HollowShellRefit.MarkerOrientation ||
            marker.DefinitionId != HollowShellRefit.MarkerId)
            throw new InvalidOperationException("Slice 3 persist lost P id, durability, or rotation.");
        if (hollows[0].Parts.Count(part => part.DefinitionId == HollowShellRefit.MarkerId) != 1)
            throw new InvalidOperationException("Slice 3 persist still has Q or another marker.");
        var fragments = vehicles.Where(item => item.DefinitionId == BridgeFixture.DefinitionId).ToArray();
        if (fragments.Length != 3 ||
            fragments.Any(item => item.Construction != ConstructionStatus.MissingContact) ||
            fragments.Any(item => item.OperationalCondition != OperationalCondition.Disabled) ||
            fragments.Any(item => item.IdentityRetention != IdentityRetentionStatus.Retained))
            throw new InvalidOperationException("Slice 3 persist did not keep primary/secondary fragments and the drop successor.");
        if (_session.World.OccupiedVoxels().Any(item => item.Value.RuntimeId is null))
            throw new InvalidOperationException("Slice 3 persist dropped voxels are missing runtime ids.");
        AssertUniqueOwnership();
    }

    private void AdvanceSlice3ResumeSmoke()
    {
        switch (_slice3ResumeSmokePhase)
        {
            case VoxelSlice3ResumeSmokePhase.Load:
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Slice 3 persist smoke is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Slice 3 persist smoke could not load {saveId:D}: {loaded.Status}.");
                RequireRestoredSlice3Persist();
                var resumedShell = _session.QueryComposites().Single(item => item.DefinitionId == HollowShellFixture.DefinitionId);
                var resumedP = resumedShell.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id;
                GD.Print($"VOXEL_SMOKE_SLICE3_RESUME_LOADED id={saveId:D} vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount} name={loaded.DisplayName} vehicle={resumedShell.Id} p_id={resumedP}");
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.AwaitLoad;
                return;
            case VoxelSlice3ResumeSmokePhase.AwaitLoad:
                if (!AwaitSlice3PersistVisual())
                    return;
                GD.Print($"VOXEL_SMOKE_SLICE3_RESUME_OWNERSHIP vehicles={_composites.VehicleCount} meshes={_composites.MeshInstanceCount} world_faces={_meshes.FaceCount}");
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.Continue;
                return;
            case VoxelSlice3ResumeSmokePhase.Continue:
                var hollow = _session.QueryComposites().Single(item => item.DefinitionId == HollowShellFixture.DefinitionId);
                var persistP = hollow.Parts.Single(part => part.Cell == HollowShellRefit.SecondAdd).Id;
                HollowShellRefit.RunMoveReturnPairs(_session, hollow.Id, persistP, 1);
                if (!_session.Damage(hollow.Id, HollowShellRefit.Occupied, 10).Succeeded)
                    throw new InvalidOperationException("Slice 3 persist resume could not continue damage.");
                _refitVehicle = hollow.Id;
                _refitP = persistP;
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.AwaitContinue;
                return;
            case VoxelSlice3ResumeSmokePhase.AwaitContinue:
                if (!AwaitSlice3PersistVisual())
                    return;
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.Reload;
                return;
            case VoxelSlice3ResumeSmokePhase.Reload:
                RequireLiveReload().WriteRefitMarkerMax(150);
                RequireCommittedReload("slice3-persist-max-150");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                GD.Print("VOXEL_SMOKE_SLICE3_RESUME_CONTINUE durability=75 max=150");
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.AwaitReload;
                return;
            case VoxelSlice3ResumeSmokePhase.AwaitReload:
                if (!AwaitSlice3PersistVisual())
                    return;
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.Resave;
                return;
            case VoxelSlice3ResumeSmokePhase.Resave:
                var saved = _session.Save("Slice 3 Persist");
                if (saved.Status != WorldSaveStatus.Saved || saved.SaveId != _session.SaveId)
                    throw new InvalidOperationException($"Slice 3 persist resume could not save again: {saved.Status}.");
                GD.Print($"VOXEL_SMOKE_SLICE3_RESUME_SAVED id={saved.SaveId:D}");
                GD.Print("COMPOSITE_PERSIST_SMOKE_OK");
                _slice3ResumeSmokePhase = VoxelSlice3ResumeSmokePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void AdvanceResumeSmoke()
    {
        switch (_resumeSmokePhase)
        {
            case VoxelResumeSmokePhase.Load:
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Voxel resume smoke is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Voxel resume smoke could not load {saveId:D}: {loaded.Status}.");
                GD.Print($"VOXEL_SMOKE_RESUME_LOADED id={saveId:D} voxels={_session.World.VoxelCount} name={loaded.DisplayName}");
                _resumeSmokePhase = VoxelResumeSmokePhase.AwaitLoad;
                return;
            case VoxelResumeSmokePhase.AwaitLoad:
                if (_session.World.VoxelCount != 1089 || !CollisionPhysicsReady)
                    return;
                RequireRestoredSaveWorld();
                ValidateGeneratedMesh();
                GD.Print("VOXEL_SMOKE_RESUME_COLLIDE_NONE domain=miss godot=miss published=1");
                GD.Print("VOXEL_SMOKE_RESUME_COLLIDE_FULL_BOX domain=hit godot=hit published=1");
                GD.Print("VOXEL_SMOKE_RESUME_COLLIDE_LOCAL_BOX domain=moved godot=moved published=1");
                _resumeSmokePhase = VoxelResumeSmokePhase.ReloadMax150;
                return;
            case VoxelResumeSmokePhase.ReloadMax150:
                RequireLiveReload().WriteFerrousOre(maxDurability: 150);
                RequireCommittedReload("resume max 150");
                if (_session.Query(WorldSaveFixture.DamagedCube).Voxel?.CurrentDurability != 75 ||
                    _session.Query(WorldSaveFixture.DamagedCube).MaxDurability != 150)
                    throw new InvalidOperationException("Voxel resume smoke did not keep absolute durability after reload.");
                GD.Print("VOXEL_SMOKE_RESUME_RELOAD_MAX_150 durability=75 max=150");
                _resumeSmokePhase = VoxelResumeSmokePhase.AwaitReloadMax150;
                return;
            case VoxelResumeSmokePhase.AwaitReloadMax150:
                if (!CollisionPhysicsReady)
                    return;
                RequireRestoredSaveWorld(expectMax: 150);
                _resumeSmokePhase = VoxelResumeSmokePhase.ContinueDamage;
                return;
            case VoxelResumeSmokePhase.ContinueDamage:
                if (_session.Damage(WorldSaveFixture.DamagedCube, 75).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Voxel resume smoke could not continue damage (14,14,14) by 75.");
                GD.Print($"VOXEL_SMOKE_RESUME_CONTINUE_DAMAGE voxels={_session.World.VoxelCount}");
                _resumeSmokePhase = VoxelResumeSmokePhase.AwaitContinueDamage;
                return;
            case VoxelResumeSmokePhase.AwaitContinueDamage:
                if (_session.World.VoxelCount != 1088 || !CollisionPhysicsReady)
                    return;
                if (_session.Query(WorldSaveFixture.DamagedCube).IsOccupied)
                    throw new InvalidOperationException("Voxel resume smoke left (14,14,14) occupied after lethal damage.");
                RequirePublishedWholeCell(WorldSaveFixture.DamagedCube, false);
                RequirePublishedWholeCell(WorldSaveFixture.AddedHollow, false);
                RequirePublishedWholeCell(WorldSaveFixture.ReorientedCube, true);
                RequireLedgeYawRegion();
                ValidateGeneratedMesh();
                _resumeSmokePhase = VoxelResumeSmokePhase.ContinueEdit;
                return;
            case VoxelResumeSmokePhase.ContinueEdit:
                var extra = new WorldVoxelCoord(0, 1, 1);
                if (_session.Add(extra, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status !=
                    WorldCommandStatus.Added ||
                    _session.Remove(extra).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Voxel resume smoke could not continue editing after restore.");
                for (var index = 0; index < 100; index++)
                {
                    // y=2 stays on the 15/16 boundary and avoids the restored hollow/ledge on y=1.
                    var position = new WorldVoxelCoord(15 + index % 2, 2, (index / 2) % 32);
                    if (_session.Add(position, VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status !=
                        WorldCommandStatus.Added ||
                        _session.Remove(position).Status != WorldCommandStatus.Removed)
                        throw new InvalidOperationException("Voxel resume smoke cross-boundary pair failed.");
                }

                GD.Print("VOXEL_SMOKE_RESUME_CONTINUE_EDIT");
                GD.Print("VOXEL_SMOKE_RESUME_BOUNDARY_PAIRS pairs=100");
                var saved = _session.Save("Slice 2 Smoke");
                if (saved.Status != WorldSaveStatus.Saved || saved.SaveId != _session.SaveId)
                    throw new InvalidOperationException($"Voxel resume smoke could not save again: {saved.Status}.");
                var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId!.Value);
                GD.Print($"VOXEL_SMOKE_RESUME_SAVED id={saved.SaveId:D} bytes={new FileInfo(path).Length}");
                _resumeSmokePhase = VoxelResumeSmokePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void WriteSmokeSave()
    {
        if (_session.Query(ProbePosition).IsOccupied &&
            _session.Remove(ProbePosition).Status != WorldCommandStatus.Removed)
            throw new InvalidOperationException("Voxel smoke could not remove the probe voxel before save.");
        RequireLiveReload().WriteFerrousOre();
        RequireLiveReload().WriteLedge();
        RequireCommittedReload("restore defaults for save");
        _session.Reset();
        WorldSaveFixture.ApplyFixedSequence(_session, includeLedge: true);
        var saved = _session.Save("Slice 2 Smoke");
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Voxel smoke could not write the slice 2 save: {saved.Status}.");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Voxel smoke save file was not created at {path}.");
        GD.Print($"VOXEL_SMOKE_SAVE_SEQUENCE voxels={_session.World.VoxelCount}");
        GD.Print($"VOXEL_SMOKE_SAVE_WRITTEN id={saved.SaveId:D} bytes={new FileInfo(path).Length} path={path}");
    }

    private void RequireRestoredSaveWorld(int expectMax = 100)
    {
        if (_session.Query(WorldSaveFixture.RemovedGround).IsOccupied)
            throw new InvalidOperationException("Restored world still occupies (4,0,4).");
        var hollow = _session.Query(WorldSaveFixture.AddedHollow);
        if (hollow.Voxel?.DefinitionId != WorldSaveFixture.HollowId ||
            hollow.Voxel.Orientation != VoxelOrientation.Identity)
            throw new InvalidOperationException("Restored (4,1,4) is not the hollow voxel.");
        var damaged = _session.Query(WorldSaveFixture.DamagedCube);
        if (damaged.Voxel?.DefinitionId != VoxelWorldFactory.DevelopmentDefinitionId ||
            damaged.Voxel.CurrentDurability != 75 ||
            damaged.MaxDurability != expectMax)
            throw new InvalidOperationException(
                $"Restored (14,14,14) durability/max was {damaged.Voxel?.CurrentDurability}/{damaged.MaxDurability}.");
        var rotated = _session.Query(WorldSaveFixture.ReorientedCube);
        if (rotated.Voxel?.DefinitionId != VoxelWorldFactory.DevelopmentDefinitionId ||
            rotated.Voxel.Orientation != WorldSaveFixture.Reoriented)
            throw new InvalidOperationException("Restored (17,17,17) did not keep nz_py.");
        var ledge = _session.Query(WorldSaveFixture.SavedLedge);
        if (ledge.Voxel?.DefinitionId != WorldSaveFixture.LedgeId ||
            ledge.Voxel.Orientation != VoxelOrientation.Yaw90)
            throw new InvalidOperationException("Restored (15,1,8) is not the yaw-90 ledge.");
        RequirePublishedWholeCell(WorldSaveFixture.AddedHollow, false);
        RequirePublishedWholeCell(WorldSaveFixture.DamagedCube, true);
        RequirePublishedWholeCell(WorldSaveFixture.ReorientedCube, true);
        RequireLedgeYawRegion();
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

    private void NoteChunkRevisions()
    {
        _chunkRevisionsBeforeEdit = VoxelChunks.All(_session.Identity)
            .ToDictionary(chunk => chunk, chunk => _meshes.MeshFor(chunk).Revision);
        _collisionRevisionsBeforeEdit = VoxelChunks.All(_session.Identity)
            .ToDictionary(chunk => chunk, chunk => _colliders.ColliderFor(chunk).Revision);
    }

    private bool CollisionPhysicsReady =>
        _physicsSeenGeneration == _collisionRefreshGeneration && _collisionRefreshGeneration > 0;

    private void RequirePublishedWholeCell(WorldVoxelCoord cell, bool expectHit)
    {
        var domain = _session.QueryCollision(cell);
        var domainHit = domain.Status == VoxelCollisionQueryStatus.Hit;
        if (domainHit != expectHit)
            throw new InvalidOperationException(
                $"Domain whole-cell collision at {cell} was {domain.Status}, expected {(expectHit ? "Hit" : "Miss")}.");
        var published = _colliders.QueryPublishedHits(GetWorld3D(), VoxelCollisionBox.ForCell(cell)).Contains(cell);
        if (published != expectHit)
            throw new InvalidOperationException(
                $"Published Godot whole-cell collision at {cell} was {published}, expected {expectHit}.");
    }

    private void RequirePublishedDerivedSize(WorldVoxelCoord cell, double expectedSizeX)
    {
        var chunk = VoxelChunks.Of(cell, _session.Identity.ChunkSize);
        if (!_colliders.ColliderFor(chunk).TryGetBoxSize(cell, out var size) ||
            size.X != (float)expectedSizeX ||
            size.X <= 0)
            throw new InvalidOperationException(
                $"Published Godot size at {cell} was {size}, expected local size {(float)expectedSizeX}.");
    }

    private void RequireDomainAndPublishedCollision(WorldVoxelCoord cell, bool expectHit)
    {
        var domain = _session.QueryCollision(cell);
        var domainHit = domain.Status == VoxelCollisionQueryStatus.Hit;
        if (domainHit != expectHit)
            throw new InvalidOperationException(
                $"Domain collision at {cell} was {domain.Status}, expected {(expectHit ? "Hit" : "Miss")}.");
        var published = _colliders.QueryPublishedCellInterior(GetWorld3D(), cell);
        if (published != expectHit)
            throw new InvalidOperationException(
                $"Published Godot collision at {cell} was {published}, expected {expectHit}.");
    }

    private void RequireDomainAndPublishedBox(
        double minX,
        double minY,
        double minZ,
        double maxX,
        double maxY,
        double maxZ,
        params WorldVoxelCoord[] expected)
    {
        if (!VoxelCollisionBox.TryCreate(minX, minY, minZ, maxX, maxY, maxZ, out var probe))
            throw new InvalidOperationException("Slice 2 smoke used an invalid collision box.");
        var domain = _session.QueryCollision(probe).Hits.Select(hit => hit.Cell).ToArray();
        if (!expected.SequenceEqual(domain))
            throw new InvalidOperationException(
                $"Domain box hits [{string.Join("; ", domain)}] expected [{string.Join("; ", expected)}].");
        var published = _colliders.QueryPublishedHits(GetWorld3D(), probe);
        if (!expected.SequenceEqual(published))
            throw new InvalidOperationException(
                $"Published Godot box hits [{string.Join("; ", published)}] expected [{string.Join("; ", expected)}].");
    }

    private void RequireLedgeIdentityRegion()
    {
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.18, LedgeCell.Y + 0.08, LedgeCell.Z + 0.48,
            LedgeCell.X + 0.22, LedgeCell.Y + 0.12, LedgeCell.Z + 0.52,
            LedgeCell);
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.68, LedgeCell.Y + 0.38, LedgeCell.Z + 0.48,
            LedgeCell.X + 0.72, LedgeCell.Y + 0.42, LedgeCell.Z + 0.52);
        if (_session.QueryCollision(LedgeCell).Hits[0].Kind != VoxelCollisionKind.LocalBox)
            throw new InvalidOperationException("Identity local-box cell query did not report local_box.");
    }

    private void RequireLedgeYawRegion()
    {
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.18, LedgeCell.Y + 0.08, LedgeCell.Z + 0.48,
            LedgeCell.X + 0.22, LedgeCell.Y + 0.12, LedgeCell.Z + 0.52);
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.48, LedgeCell.Y + 0.38, LedgeCell.Z + 0.18,
            LedgeCell.X + 0.52, LedgeCell.Y + 0.42, LedgeCell.Z + 0.22,
            LedgeCell);
    }

    private void RequireLedgeReloadedRegion()
    {
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.18, LedgeCell.Y + 0.08, LedgeCell.Z + 0.48,
            LedgeCell.X + 0.22, LedgeCell.Y + 0.12, LedgeCell.Z + 0.52);
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.68, LedgeCell.Y + 0.68, LedgeCell.Z + 0.68,
            LedgeCell.X + 0.72, LedgeCell.Y + 0.72, LedgeCell.Z + 0.72,
            LedgeCell);
    }

    private void RequirePublishedLocalBoxMiss()
    {
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.18, LedgeCell.Y + 0.08, LedgeCell.Z + 0.48,
            LedgeCell.X + 0.22, LedgeCell.Y + 0.12, LedgeCell.Z + 0.52);
        RequireDomainAndPublishedBox(
            LedgeCell.X + 0.48, LedgeCell.Y + 0.38, LedgeCell.Z + 0.18,
            LedgeCell.X + 0.52, LedgeCell.Y + 0.42, LedgeCell.Z + 0.22);
    }

    private void RequirePublishedCollisionPartialRebuild(params ChunkCoord[] required)
    {
        if (_collisionRevisionsBeforeEdit is null)
            throw new InvalidOperationException("Chunk collision revisions were not captured before the edit.");
        var rebuilt = _colliders.LastRebuiltChunks;
        if (rebuilt.Count >= VoxelChunks.All(_session.Identity).Count)
            throw new InvalidOperationException("A local collision edit rebuilt every chunk.");
        foreach (var chunk in required)
        {
            if (!rebuilt.Contains(chunk))
                throw new InvalidOperationException(
                    $"Expected dirty collision chunk {chunk} but rebuilt {FormatCollisionDirty()}.");
        }

        foreach (var chunk in VoxelChunks.All(_session.Identity))
        {
            var before = _collisionRevisionsBeforeEdit[chunk];
            var after = _colliders.ColliderFor(chunk).Revision;
            var wasRebuilt = rebuilt.Contains(chunk);
            if (wasRebuilt && after <= before)
                throw new InvalidOperationException(
                    $"Dirty collision chunk {chunk} kept revision {after}; the published collider was not rebuilt.");
            if (!wasRebuilt && after != before)
                throw new InvalidOperationException($"Unaffected collision chunk {chunk} was rebuilt.");
        }
    }

    private void RequireExposedFace(WorldVoxelCoord position, VoxelFaceNormal normal) =>
        RequirePublishedExposedFace(position, normal);

    private void RequirePublishedExposedFace(WorldVoxelCoord position, VoxelFaceNormal normal)
    {
        if (!HasPublishedFace(position, normal))
            throw new InvalidOperationException($"Published chunk mesh is missing exposed face {position} {normal}.");
        if (!HasDomainFace(position, normal))
            throw new InvalidOperationException($"Domain mesher is missing exposed face {position} {normal}.");
    }

    private void RequirePublishedHiddenFace(WorldVoxelCoord position, VoxelFaceNormal normal)
    {
        if (HasPublishedFace(position, normal))
            throw new InvalidOperationException($"Published chunk mesh still has culled face {position} {normal}.");
        if (HasDomainFace(position, normal))
            throw new InvalidOperationException($"Domain mesher still has culled face {position} {normal}.");
    }

    private void RequirePublishedMissingVoxel(WorldVoxelCoord position)
    {
        if (HasAnyPublishedFace(position))
            throw new InvalidOperationException($"Removed voxel {position} still has published faces.");
        if (VoxelSurfaceMesher.Build(_session.World).Faces.Any(face => face.Position == position))
            throw new InvalidOperationException($"Removed voxel {position} still contributed a domain face.");
    }

    private void RequirePublishedPartialRebuild(params ChunkCoord[] required)
    {
        RequirePartialRebuild(required);
        if (_chunkRevisionsBeforeEdit is null)
            throw new InvalidOperationException("Chunk mesh revisions were not captured before the edit.");
        foreach (var chunk in VoxelChunks.All(_session.Identity))
        {
            var before = _chunkRevisionsBeforeEdit[chunk];
            var after = _meshes.MeshFor(chunk).Revision;
            var rebuilt = _meshes.LastRebuiltChunks.Contains(chunk);
            if (rebuilt && after <= before)
                throw new InvalidOperationException(
                    $"Dirty chunk {chunk} kept revision {after}; the published ArrayMesh was not rebuilt.");
            if (!rebuilt && after != before)
                throw new InvalidOperationException($"Unaffected chunk {chunk} was rebuilt.");
        }
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

    private bool HasPublishedFace(WorldVoxelCoord position, VoxelFaceNormal normal) =>
        _meshes.Meshes.Any(mesh =>
            mesh.Mesh is not null &&
            VoxelMeshGeometryValidator.HasPublishedFace(mesh.Mesh, position, normal));

    private bool HasAnyPublishedFace(WorldVoxelCoord position) =>
        _meshes.Meshes.Any(mesh =>
            mesh.Mesh is not null &&
            VoxelMeshGeometryValidator.HasAnyPublishedFace(mesh.Mesh, position));

    private bool HasDomainFace(WorldVoxelCoord position, VoxelFaceNormal normal) =>
        VoxelSurfaceMesher.Build(_session.World).Faces.Any(face => face.Position == position && face.Normal == normal);

    private string FormatDirty() =>
        string.Join(";", _meshes.LastRebuiltChunks.Select(chunk => $"{chunk.X},{chunk.Y},{chunk.Z}"));

    private string FormatCollisionDirty() =>
        string.Join(";", _colliders.LastRebuiltChunks.Select(chunk => $"{chunk.X},{chunk.Y},{chunk.Z}"));

    private void AdvanceResumeObserve()
    {
        if (_resumeObservePhase == VoxelResumeObservePhase.None)
            return;
        if (_observeHoldFrames > 0)
        {
            _observeHoldFrames--;
            return;
        }

        try
        {
            AdvanceResumeObserveStep();
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_RESUME_ERROR {_resumeObservePhase}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void AdvanceResumeObserveStep()
    {
        switch (_resumeObservePhase)
        {
            case VoxelResumeObservePhase.Write:
                if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
                    WriteSlice4ResumeObserve();
                else if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
                    WriteSlice3ResumeObserve();
                else
                    WriteSlice2ResumeObserve();
                return;
            case VoxelResumeObservePhase.Load:
                var rawId = System.Environment.GetEnvironmentVariable(VoxelObserveSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Resume observe is missing TRAIN_VOXEL_OBSERVE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Resume observe could not load {saveId:D}: {loaded.Status}.");
                GD.Print($"VOXEL_OBSERVE_RESUME_LOADED id={saveId:D} voxels={_session.World.VoxelCount} vehicles={_session.QueryComposites().Count} name={loaded.DisplayName}");
                _resumeObservePhase = VoxelResumeObservePhase.AwaitLoad;
                return;
            case VoxelResumeObservePhase.AwaitLoad:
                if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice4)
                {
                    if (_dirty || _composites.VehicleCount != 2)
                        return;
                    var restored = _session.QueryDisplayPoses(0.5);
                    if (!restored.IntervalEnded || restored.Vehicles.Count != 2)
                        throw new InvalidOperationException("Slice 4 resume observe did not snap display to the committed save.");
                    ApplyRailPresentation();
                    _observeHoldFrames = 10;
                    _resumeObservePhase = VoxelResumeObservePhase.CaptureSlice4Resume;
                    return;
                }

                if (_session.Identity.Slice == VoxelDevelopmentSlice.Slice3)
                {
                    if (!AwaitSlice3PersistVisual())
                        return;
                    RequireRestoredSlice3Persist();
                    AimInteriorCamera();
                    _observeHoldFrames = 10;
                    _resumeObservePhase = VoxelResumeObservePhase.CaptureHollow;
                    return;
                }

                if (_session.World.VoxelCount != 1089 || !CollisionPhysicsReady)
                    return;
                RequireRestoredSaveWorld();
                RequireExposedFace(new WorldVoxelCoord(3, 0, 4), VoxelFaceNormal.PositiveX);
                SelectObserveCell(WorldSaveFixture.DamagedCube);
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), _session.Identity);
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.CaptureRestored;
                return;
            case VoxelResumeObservePhase.CaptureRestored:
                CaptureObserve("issue53-resume-restored", "new-process load: (14,14,14) durability 75, 1089 voxels");
                _resumeObservePhase = VoxelResumeObservePhase.AimBoundary;
                return;
            case VoxelResumeObservePhase.AimBoundary:
                if (_session.Remove(new WorldVoxelCoord(16, 0, 8)).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Resume observe could not open the x=15/16 ground seam.");
                SelectObserveCell(new WorldVoxelCoord(16, 0, 8));
                AimCamera(new Vector3(20, 8, 2), new Vector3(16, 0.4f, 8));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.AwaitBoundary;
                return;
            case VoxelResumeObservePhase.AwaitBoundary:
                RequireExposedFace(new WorldVoxelCoord(15, 0, 8), VoxelFaceNormal.PositiveX);
                CaptureObserve("issue53-resume-chunk-boundary", "new-process load then remove (16,0,8): x=15/16 ground seam");
                if (_session.Add(new WorldVoxelCoord(16, 0, 8), VoxelWorldFactory.DevelopmentDefinitionId, VoxelOrientation.Identity).Status !=
                    WorldCommandStatus.Added)
                    throw new InvalidOperationException("Resume observe could not restore (16,0,8) after the boundary shot.");
                _resumeObservePhase = VoxelResumeObservePhase.AimHole;
                return;
            case VoxelResumeObservePhase.AimHole:
                SelectObserveCell(WorldSaveFixture.RemovedGround);
                AimCamera(new Vector3(8, 7, -2), new Vector3(4, 0.4f, 4));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.AwaitHole;
                return;
            case VoxelResumeObservePhase.AwaitHole:
                if (_session.Query(WorldSaveFixture.RemovedGround).IsOccupied)
                    throw new InvalidOperationException("Resume observe ground hole at (4,0,4) was occupied.");
                CaptureObserve("issue53-resume-ground-hole", "new-process load: visible ground hole at (4,0,4)");
                _resumeObservePhase = VoxelResumeObservePhase.AimLedge;
                return;
            case VoxelResumeObservePhase.AimLedge:
                SelectObserveCell(WorldSaveFixture.SavedLedge);
                AimCamera(new Vector3(19, 6, 3), new Vector3(15, 1.2f, 8));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.AwaitLedge;
                return;
            case VoxelResumeObservePhase.AwaitLedge:
                if (_session.QueryCollision(WorldSaveFixture.SavedLedge).Hits.FirstOrDefault()?.Kind != VoxelCollisionKind.LocalBox)
                    throw new InvalidOperationException("Resume observe ledge was not local_box.");
                CaptureObserve("issue53-resume-ledge", "new-process load: yaw-90 ledge local_box at (15,1,8)");
                _resumeObservePhase = VoxelResumeObservePhase.ContinueDamage;
                return;
            case VoxelResumeObservePhase.ContinueDamage:
                var continued = _session.Damage(WorldSaveFixture.DamagedCube, 75);
                if (continued.Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Resume observe could not continue damage (14,14,14) by 75.");
                SetResult(continued);
                SelectObserveCell(WorldSaveFixture.DamagedCube);
                AimCamera(new Vector3(22, 20, 8), new Vector3(14, 14, 14));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.AwaitContinueDamage;
                return;
            case VoxelResumeObservePhase.AwaitContinueDamage:
                if (_session.World.VoxelCount != 1088 || _session.Query(WorldSaveFixture.DamagedCube).IsOccupied)
                    throw new InvalidOperationException("Resume observe left (14,14,14) occupied after continue damage.");
                CaptureObserve("issue53-resume-continue-damage", "new-process load then damage 75: (14,14,14) empty, 1088 voxels");
                GD.Print("VOXEL_OBSERVE_RESUME_COMPLETE");
                _resumeObservePhase = VoxelResumeObservePhase.Done;
                GetTree().Quit();
                return;
            case VoxelResumeObservePhase.CaptureHollow:
                CaptureObserve("issue62-resume-hollow", "new-process load: hollow interior of the restored parked shell");
                AimRefitCamera(HollowShellRefit.SecondAdd);
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.CaptureRefit;
                return;
            case VoxelResumeObservePhase.CaptureRefit:
                CaptureObserve("issue62-resume-refit", "new-process load: restored P at (4,1,0), durability 75, nz_py");
                AimCamera(new Vector3(28, 16, 4), new Vector3(20, 9, 8));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.CaptureFracture;
                return;
            case VoxelResumeObservePhase.CaptureFracture:
                CaptureObserve("issue62-resume-fracture", "new-process load: primary and secondary bridge successors");
                var dropped = _session.World.OccupiedVoxels().First(item => item.Value.RuntimeId is not null).Key;
                SelectObserveCell(dropped);
                AimCamera(new Vector3(16, 14, 28), new Vector3(8, 8, 20));
                _observeHoldFrames = 10;
                _resumeObservePhase = VoxelResumeObservePhase.CaptureDropped;
                return;
            case VoxelResumeObservePhase.CaptureDropped:
                CaptureObserve("issue62-resume-dropped", "new-process load: dropped world voxels keep runtime ids");
                GD.Print("VOXEL_OBSERVE_RESUME_COMPLETE");
                _resumeObservePhase = VoxelResumeObservePhase.Done;
                GetTree().Quit();
                return;
            case VoxelResumeObservePhase.CaptureSlice4Resume:
                CaptureIssue74("issue74-resume-running", "new-process load: display snaps to committed step 900, follow persists");
                GD.Print("VOXEL_OBSERVE_RESUME_COMPLETE");
                _resumeObservePhase = VoxelResumeObservePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void WriteSlice2ResumeObserve()
    {
        WorldSaveFixture.ApplyFixedSequence(_session, includeLedge: true);
        var saved = _session.Save("Slice 2 Resume");
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Resume observe could not save: {saved.Status}.");
        var written = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        if (!File.Exists(written))
            throw new InvalidOperationException($"Resume observe save file was not created at {written}.");
        GD.Print($"VOXEL_OBSERVE_RESUME_WRITTEN id={saved.SaveId.Value:D} bytes={new FileInfo(written).Length} voxels={_session.World.VoxelCount}");
        _resumeObservePhase = VoxelResumeObservePhase.Done;
        GetTree().Quit();
    }

    private void WriteSlice4ResumeObserve()
    {
        _session.Reset();
        if (!EnterSlice4Fixture())
            throw new InvalidOperationException("Slice 4 resume observe could not assemble the fixture.");
        if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
            !_session.AdvanceSimulation(900).Succeeded ||
            _session.SimulationStep != 900)
            throw new InvalidOperationException("Slice 4 resume observe could not reach step 900.");
        var saved = _session.Save("Slice 4 Display 900");
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Slice 4 resume observe could not save: {saved.Status}.");
        var written = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        if (!File.Exists(written))
            throw new InvalidOperationException($"Slice 4 resume observe save file was not created at {written}.");
        if (!_session.PauseSimulation().Succeeded)
            throw new InvalidOperationException("Slice 4 resume observe could not pause after save.");
        GD.Print($"VOXEL_OBSERVE_RESUME_WRITTEN id={saved.SaveId.Value:D} bytes={new FileInfo(written).Length} step={_session.SimulationStep}");
        _resumeObservePhase = VoxelResumeObservePhase.Done;
        GetTree().Quit();
    }

    private void WriteSlice3ResumeObserve()
    {
        var manufactured = _session.Manufacture(HollowShellFixture.DefinitionId);
        if (!manufactured.Succeeded)
            throw new InvalidOperationException($"Resume observe hollow manufacture failed: {manufactured.Status}.");
        _refitVehicle = manufactured.VehicleId!.Value;
        var trace = HollowShellRefit.ApplySuccess(_session, _refitVehicle);
        _refitP = trace.P;
        AddSlice3PersistBridges();
        var saved = _session.Save("Slice 3 Persist");
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Resume observe could not save slice 3: {saved.Status}.");
        var written = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        if (!File.Exists(written))
            throw new InvalidOperationException($"Resume observe save file was not created at {written}.");
        GD.Print($"VOXEL_OBSERVE_RESUME_WRITTEN id={saved.SaveId.Value:D} bytes={new FileInfo(written).Length} vehicles={_session.QueryComposites().Count} world={_session.World.VoxelCount}");
        _resumeObservePhase = VoxelResumeObservePhase.Done;
        GetTree().Quit();
    }

    private void AimInteriorCamera()
    {
        var camera = GetChildren().OfType<Camera3D>().Single();
        camera.Position = new Vector3(11.5f, 10.5f, 9f);
        camera.LookAt(new Vector3(8.6f, 8.6f, 7.6f), Vector3.Up);
    }

    private void SelectObserveCell(WorldVoxelCoord cell)
    {
        _selectedPosition = cell;
        MarkDirty();
        Refresh();
    }

    private void AimCamera(Vector3 position, Vector3 target)
    {
        var camera = GetChildren().OfType<Camera3D>().Single();
        camera.Position = position;
        camera.LookAt(target, Vector3.Up);
    }

    private void CaptureIssue74(string fileName, string note)
    {
        ApplyRailPresentation();
        CaptureObserve(fileName, note, "validation/issue74");
    }

    private void AdvanceSlice4Observe()
    {
        if (_slice4ObservePhase == VoxelSlice4ObservePhase.None)
            return;
        if (_observeHoldFrames > 0)
        {
            _observeHoldFrames--;
            return;
        }

        try
        {
            AdvanceSlice4ObserveStep();
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_ERROR {_slice4ObservePhase}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void AdvanceSlice4ObserveStep()
    {
        switch (_slice4ObservePhase)
        {
            case VoxelSlice4ObservePhase.Parked:
                if (_session.QueryTrainAssemblies().Count == 0 && !EnterSlice4Fixture())
                    throw new InvalidOperationException("Slice 4 observe could not assemble the fixture.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitParked;
                return;
            case VoxelSlice4ObservePhase.AwaitParked:
                CaptureIssue74("issue74-parked", "assembled locomotive and carriage, rigid coupler, follow loco");
                _slice4ObservePhase = VoxelSlice4ObservePhase.Interpolate;
                return;
            case VoxelSlice4ObservePhase.Interpolate:
                if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
                    !_session.AdvanceSimulation(1).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not start and step once.");
                _composites.ApplyDisplayPoses(_session.QueryDisplayPoses(0.5));
                ApplyFollowCamera();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitInterpolate;
                return;
            case VoxelSlice4ObservePhase.AwaitInterpolate:
                CaptureIssue74("issue74-interpolate", "alpha 0.5 between parked and first completed step");
                _slice4ObservePhase = VoxelSlice4ObservePhase.Running;
                return;
            case VoxelSlice4ObservePhase.Running:
                if (!_session.AdvanceSimulation(299).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not reach step 300.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitRunning;
                return;
            case VoxelSlice4ObservePhase.AwaitRunning:
                CaptureIssue74("issue74-running-300", "step 300 rigid running poses, follow locomotive");
                _slice4ObservePhase = VoxelSlice4ObservePhase.FollowCarriage;
                return;
            case VoxelSlice4ObservePhase.FollowCarriage:
                var carriage = _session.QueryTrainAssemblies().Single().VehicleIds[1];
                if (!_session.TryFollowVehicle(carriage))
                    throw new InvalidOperationException("Slice 4 observe could not follow the carriage.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitFollow;
                return;
            case VoxelSlice4ObservePhase.AwaitFollow:
                CaptureIssue74("issue74-follow-carriage", "camera heading follows selected surviving carriage");
                _slice4ObservePhase = VoxelSlice4ObservePhase.CrossSeam;
                return;
            case VoxelSlice4ObservePhase.CrossSeam:
                if (!_session.AdvanceSimulation(721).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not cross the seam.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitCrossSeam;
                return;
            case VoxelSlice4ObservePhase.AwaitCrossSeam:
                CaptureIssue74("issue74-cross-seam", "world-path interpolation across x=144, no mixed segment distances");
                _slice4ObservePhase = VoxelSlice4ObservePhase.Split;
                return;
            case VoxelSlice4ObservePhase.Split:
                ResetSlice4AfterMotion();
                if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
                    !_session.AdvanceSimulation(300).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not rebuild for coupler split.");
                var followed = _session.QueryTrainAssemblies().Single().VehicleIds[1];
                if (!_session.TryFollowVehicle(followed))
                    throw new InvalidOperationException("Slice 4 observe could not keep carriage follow before split.");
                ApplyRailPresentation();
                var beforeCamera = RequireSlice4CameraFrame();
                PrintSlice4CameraMarker("VOXEL_OBSERVE_SLICE4_CAMERA_BEFORE_SPLIT", beforeCamera);
                var coupler = _session.QueryTrainAssemblies().Single().Connections[0];
                if (!_session.Damage(_session.QueryTrainAssemblies().Single().ActiveLocomotiveId!.Value, coupler.FromCell, 100).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not destroy the coupler.");
                if (_session.FollowedVehicleId != followed)
                    throw new InvalidOperationException("Slice 4 observe follow jumped after disconnect.");
                ApplyRailPresentation();
                var afterCamera = RequireSlice4CameraFrame();
                PrintSlice4CameraMarker("VOXEL_OBSERVE_SLICE4_CAMERA_AFTER_SPLIT", afterCamera);
                RequireSlice4CameraContinuous(beforeCamera, afterCamera);
                if (!HaltHud().Contains("滑行", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Slice 4 observe split HUD is '{HaltHud()}'.");
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitSplit;
                return;
            case VoxelSlice4ObservePhase.AwaitSplit:
                CaptureIssue74("issue74-split", "coupler split ends interval; follow stays on selected carriage");
                _slice4ObservePhase = VoxelSlice4ObservePhase.SupportLoss;
                return;
            case VoxelSlice4ObservePhase.SupportLoss:
                if (!_session.AdvanceSimulation(30).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not coast after split.");
                if (_session.Damage(StraightRailFixture.SupportLossCell, 100).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Slice 4 observe could not remove the support cell.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitSupportLoss;
                return;
            case VoxelSlice4ObservePhase.AwaitSupportLoss:
                CaptureIssue74("issue74-support-loss", "support loss snaps display to committed halt");
                _slice4ObservePhase = VoxelSlice4ObservePhase.GapStop;
                return;
            case VoxelSlice4ObservePhase.GapStop:
                ResetSlice4AfterMotion();
                if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
                    !_session.AdvanceSimulation(300).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not rebuild for the gap stop.");
                if (_session.Damage(new WorldVoxelCoord(110, 1, 15), 100).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Slice 4 observe could not open the forward gap.");
                if (!_session.AdvanceSimulation(2000).Succeeded)
                    throw new InvalidOperationException("Slice 4 observe could not halt at the gap.");
                ApplyRailPresentation();
                _observeHoldFrames = 8;
                _slice4ObservePhase = VoxelSlice4ObservePhase.AwaitGapStop;
                return;
            case VoxelSlice4ObservePhase.AwaitGapStop:
                CaptureIssue74("issue74-gap-stop", "forward-gap halt, no start until repair");
                GD.Print("VOXEL_OBSERVE_COMPLETE");
                _slice4ObservePhase = VoxelSlice4ObservePhase.Done;
                GetTree().Quit();
                return;
        }
    }

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
                RequireLiveReload().WriteFerrousOre(maxDurability: 150);
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
                RequireLiveReload().WriteFerrousOre(maxDurability: 150, name: "热重载亚铁矿");
                RequireLiveReload().WriteAsymmetricCubePngs();
                RequireCommittedReload("observe display");
                _observeHoldFrames = 8;
                _observePhase = VoxelObservePhase.AwaitReloadDisplay;
                return;
            case VoxelObservePhase.AwaitReloadDisplay:
                if (!_cell.Text.Contains("热重载亚铁矿", StringComparison.Ordinal))
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
                RequireLiveReload().WriteFerrousOre(maxDurability: 60, name: "热重载亚铁矿");
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
                    !_cell.Text.Contains("热重载亚铁矿", StringComparison.Ordinal) ||
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

    private void CaptureObserve(string fileName, string note, string? relativeDirectory = null)
    {
        ValidateGeneratedMesh();
        var directory = ProjectSettings.GlobalizePath(
            string.IsNullOrEmpty(relativeDirectory) ? "res://.godot" : "res://.godot/" + relativeDirectory);
        DirAccess.MakeDirRecursiveAbsolute(directory);
        var path = Path.Combine(directory, fileName + ".png");
        try
        {
            RenderingServer.ForceDraw();
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

        var collide = _session.QueryCollision(_selectedPosition);
        GD.Print($"VOXEL_OBSERVE_CAPTURE {fileName} voxels={_session.World.VoxelCount} faces={_meshes.FaceCount} vehicles={_composites.VehicleCount} composite_faces={_composites.FaceCount} collide={collide.Status} hud={_cell.Text} result={_result.Text} {note}");
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
                RequireLiveReload().WriteFerrousOre(maxDurability: 150);
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
                _slice2ObservePhase = VoxelSlice2ObservePhase.LocalIdentity;
                return;
            case VoxelSlice2ObservePhase.LocalIdentity:
                if (_session.Add(ObserveLedgeCell, LedgeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 observe could not place the local-box example.");
                _selectedPosition = ObserveLedgeCell;
                MarkDirty();
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitLocalIdentity;
                return;
            case VoxelSlice2ObservePhase.AwaitLocalIdentity:
                if (_session.QueryCollision(ObserveLedgeCell).Hits.FirstOrDefault()?.Kind != VoxelCollisionKind.LocalBox)
                    throw new InvalidOperationException("Observe local-box identity did not hit local_box.");
                CaptureObserve("issue52-local-identity", "eccentric local_box at (16,15,8) identity, debug box smaller than the cell");
                _slice2ObservePhase = VoxelSlice2ObservePhase.LocalRotated;
                return;
            case VoxelSlice2ObservePhase.LocalRotated:
                if (_session.Remove(ObserveLedgeCell).Status != WorldCommandStatus.Removed ||
                    _session.Add(ObserveLedgeCell, LedgeId, VoxelOrientation.Yaw90).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 observe could not rotate the local-box example.");
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitLocalRotated;
                return;
            case VoxelSlice2ObservePhase.AwaitLocalRotated:
                if (_session.Query(ObserveLedgeCell).Voxel?.Orientation != VoxelOrientation.Yaw90)
                    throw new InvalidOperationException("Observe local-box yaw did not keep pz_py.");
                CaptureObserve("issue52-local-rotated", "same local_box after pz_py, debug box moved with orientation");
                _slice2ObservePhase = VoxelSlice2ObservePhase.LocalRemoved;
                return;
            case VoxelSlice2ObservePhase.LocalRemoved:
                if (_session.Remove(ObserveLedgeCell).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Slice 2 observe could not remove the local-box example.");
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitLocalRemoved;
                return;
            case VoxelSlice2ObservePhase.AwaitLocalRemoved:
                if (_session.QueryCollision(ObserveLedgeCell).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Observe still had domain collision after local-box remove.");
                CaptureObserve("issue52-local-removed", "removed local_box leaves no debug collider in the cell");
                _slice2ObservePhase = VoxelSlice2ObservePhase.LocalReloaded;
                return;
            case VoxelSlice2ObservePhase.LocalReloaded:
                if (_session.Add(ObserveLedgeCell, LedgeId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Slice 2 observe could not restore the local-box example.");
                // The previous reject left an unloadable probe on disk; occupied probe
                // instances still require that definition, so restore it before ledge reload.
                RequireLiveReload().WriteProbeMod();
                RequireLiveReload().WriteLedge(0.5, 0.5, 0.5, 0.375, 0.375, 0.375);
                RequireCommittedReload("observe local box geometry");
                if (_session.Query(ObserveLedgeCell).IsOccupied != true ||
                    _session.Query(ObserveLedgeCell).Voxel?.Orientation != VoxelOrientation.Identity)
                    throw new InvalidOperationException("Observe local-box geometry reload did not keep occupancy and orientation.");
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitLocalReloaded;
                return;
            case VoxelSlice2ObservePhase.AwaitLocalReloaded:
                if (_session.QueryCollision(ObserveLedgeCell).Hits.FirstOrDefault()?.Kind != VoxelCollisionKind.LocalBox)
                    throw new InvalidOperationException("Observe reloaded local-box did not keep local_box collision.");
                CaptureObserve("issue52-local-reloaded", "hot-reloaded local_box occupies the far corner instead of the original ledge");
                _slice2ObservePhase = VoxelSlice2ObservePhase.LocalRejected;
                return;
            case VoxelSlice2ObservePhase.LocalRejected:
                var generation = GetNode<Main>("/root/Main").Reloader?.Catalog.Current?.Generation ?? 0;
                var snapshot = GetNode<Main>("/root/Main").Reloader?.Catalog.Current;
                RequireLiveReload().WriteInvalidLedge();
                var rejectedBox = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejectedBox.Status != ContentReloadStatus.Rejected ||
                    rejectedBox.Changed ||
                    rejectedBox.CatalogVersion != generation ||
                    !ReferenceEquals(snapshot, GetNode<Main>("/root/Main").Reloader?.Catalog.Current) ||
                    !_session.Query(ObserveLedgeCell).IsOccupied)
                    throw new InvalidOperationException("Observe illegal local-box reload did not reject atomically.");
                RequireLiveReload().WriteLedge();
                _observeHoldFrames = 8;
                _slice2ObservePhase = VoxelSlice2ObservePhase.AwaitLocalRejected;
                return;
            case VoxelSlice2ObservePhase.AwaitLocalRejected:
                if (_session.QueryCollision(ObserveLedgeCell).Hits.FirstOrDefault()?.Kind != VoxelCollisionKind.LocalBox)
                    throw new InvalidOperationException("Observe rejected local-box reload lost the previous published collision.");
                CaptureObserve("issue52-local-rejected", "illegal overflowing local_box rejected; corner hit region stays");
                GD.Print("VOXEL_OBSERVE_COMPLETE");
                _slice2ObservePhase = VoxelSlice2ObservePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void AdvanceSlice3Observe()
    {
        if (_slice3ObservePhase == VoxelSlice3ObservePhase.None)
            return;
        if (_observeHoldFrames > 0)
        {
            _observeHoldFrames--;
            return;
        }

        try
        {
            AdvanceSlice3ObserveStep();
        }
        catch (Exception exception)
        {
            GD.Print($"VOXEL_OBSERVE_ERROR {_slice3ObservePhase}: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private void AdvanceSlice3ObserveStep()
    {
        switch (_slice3ObservePhase)
        {
            case VoxelSlice3ObservePhase.CaptureEmpty:
                CaptureObserve("issue58-empty", "empty slice 3 world before manufacture");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitEmpty;
                return;
            case VoxelSlice3ObservePhase.AwaitEmpty:
                _slice3ObservePhase = VoxelSlice3ObservePhase.Manufacture;
                return;
            case VoxelSlice3ObservePhase.Manufacture:
                var manufactured = _session.Manufacture(HollowShellFixture.DefinitionId);
                if (!manufactured.Succeeded)
                    throw new InvalidOperationException($"Slice 3 observe manufacture failed: {manufactured.Status}.");
                SetCompositeResult(manufactured);
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitManufacture;
                return;
            case VoxelSlice3ObservePhase.AwaitManufacture:
                if (_composites.VehicleCount != 1 || _composites.FaceCount == 0)
                    return;
                ValidateCompositeVisual();
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureExterior;
                return;
            case VoxelSlice3ObservePhase.CaptureExterior:
                CaptureObserve("issue58-exterior", "parked hollow shell from outside, world mesh empty");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitExterior;
                return;
            case VoxelSlice3ObservePhase.AwaitExterior:
                _slice3ObservePhase = VoxelSlice3ObservePhase.AimInterior;
                return;
            case VoxelSlice3ObservePhase.AimInterior:
                var camera = GetChildren().OfType<Camera3D>().Single();
                camera.Position = new Vector3(11.5f, 10.5f, 9f);
                camera.LookAt(new Vector3(8.6f, 8.6f, 7.6f), Vector3.Up);
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitInterior;
                return;
            case VoxelSlice3ObservePhase.AwaitInterior:
                CaptureObserve("issue58-interior", "camera inside hollow cavity looking at inner -X/-Y/-Z walls");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AimRefit;
                return;
            case VoxelSlice3ObservePhase.AimRefit:
                AimRefitCamera(HollowShellRefit.FirstAdd);
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitAimRefit;
                return;
            case VoxelSlice3ObservePhase.AwaitAimRefit:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitAdd;
                return;
            case VoxelSlice3ObservePhase.RefitAdd:
                _refitVehicle = _session.QueryComposites().Single().Id;
                var added = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.FirstAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "observe add P");
                _refitP = RequireSingleAllocated(added, "observe add P");
                var damaged = _session.Damage(_refitVehicle, HollowShellRefit.FirstAdd, 25);
                if (!damaged.Succeeded)
                    throw new InvalidOperationException($"Slice 3 observe refit damage failed: {damaged.Status}.");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitAdd;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitAdd:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureAdded;
                return;
            case VoxelSlice3ObservePhase.CaptureAdded:
                CaptureObserve("issue60-added", "added visible-orientation marker P at (2,1,0), durability 75");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitAdded;
                return;
            case VoxelSlice3ObservePhase.AwaitAdded:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitMove;
                return;
            case VoxelSlice3ObservePhase.RefitMove:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Move(_refitP, HollowShellRefit.Moved)), "observe move P");
                AimRefitCamera(HollowShellRefit.Moved);
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitMove;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitMove:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureMoved;
                return;
            case VoxelSlice3ObservePhase.CaptureMoved:
                CaptureObserve("issue60-moved", "same P moved to (3,1,0) with durability 75");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitMoved;
                return;
            case VoxelSlice3ObservePhase.AwaitMoved:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitRotate;
                return;
            case VoxelSlice3ObservePhase.RefitRotate:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Rotate(_refitP, HollowShellRefit.MarkerOrientation)), "observe rotate P");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitRotate;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitRotate:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureRotated;
                return;
            case VoxelSlice3ObservePhase.CaptureRotated:
                CaptureObserve("issue60-rotated", "P at (3,1,0) rotated to nz_py; top +X mark turns toward world -Z");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRotated;
                return;
            case VoxelSlice3ObservePhase.AwaitRotated:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitSwap;
                return;
            case VoxelSlice3ObservePhase.RefitSwap:
                var addedQ = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Add(HollowShellRefit.SecondAdd, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "observe add Q");
                _refitQ = RequireSingleAllocated(addedQ, "observe add Q");
                RequireCommittedRefit(
                    _session.Refit(_refitVehicle, new[]
                    {
                        CompositeRefitOperation.Move(_refitP, HollowShellRefit.SecondAdd),
                        CompositeRefitOperation.Move(_refitQ, HollowShellRefit.Moved)
                    }),
                    "observe swap");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitSwap;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitSwap:
                if (!AwaitCommittedVehicleVisual(1, 170))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureSwapped;
                return;
            case VoxelSlice3ObservePhase.CaptureSwapped:
                CaptureObserve("issue60-swapped", "atomic swap: P at (4,1,0) still nz_py/75, Q at (3,1,0)");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitSwapped;
                return;
            case VoxelSlice3ObservePhase.AwaitSwapped:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitReplace;
                return;
            case VoxelSlice3ObservePhase.RefitReplace:
                var replaced = RequireCommittedRefit(
                    _session.Refit(_refitVehicle, CompositeRefitOperation.Replace(HollowShellRefit.Moved, HollowShellRefit.ReplacementId, VoxelOrientation.Identity)),
                    "observe replace");
                _refitR = RequireSingleAllocated(replaced, "observe replace");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitReplace;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitReplace:
                if (!AwaitCommittedVehicleVisual(1, 170))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReplaced;
                return;
            case VoxelSlice3ObservePhase.CaptureReplaced:
                CaptureObserve("issue60-replaced", "Q replaced by new R (max 150) at (3,1,0); P unchanged");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReplaced;
                return;
            case VoxelSlice3ObservePhase.AwaitReplaced:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitRemove;
                return;
            case VoxelSlice3ObservePhase.RefitRemove:
                RequireCommittedRefit(_session.Refit(_refitVehicle, CompositeRefitOperation.Remove(_refitR)), "observe remove R");
                _refitSuccess = _session.CaptureAuthority();
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitRemove;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitRemove:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureRemoved;
                return;
            case VoxelSlice3ObservePhase.CaptureRemoved:
                CaptureObserve("issue60-removed", "R removed; P remains at (4,1,0) with durability 75 and nz_py");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRemoved;
                return;
            case VoxelSlice3ObservePhase.AwaitRemoved:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitReject;
                return;
            case VoxelSlice3ObservePhase.RefitReject:
                RunOfficialRefitFailures();
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureRejected;
                return;
            case VoxelSlice3ObservePhase.CaptureRejected:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
                CaptureObserve("issue60-rejected", "failed batches keep the committed success layout; no drop into world");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRejected;
                return;
            case VoxelSlice3ObservePhase.AwaitRejected:
                _slice3ObservePhase = VoxelSlice3ObservePhase.Reset;
                return;
            case VoxelSlice3ObservePhase.Reset:
                _session.Reset();
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
                _result.Text = "最近命令：已重置正式切片 3 静置车辆世界。";
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReset;
                return;
            case VoxelSlice3ObservePhase.AwaitReset:
                if (_session.QueryComposites().Count != 0 ||
                    _composites.VehicleCount != 0 ||
                    !_cell.Text.Contains("车辆 0", StringComparison.Ordinal))
                    return;
                CaptureObserve("issue58-reset", "reset clears manufactured vehicle and derived visual");
                _slice3ObservePhase = VoxelSlice3ObservePhase.Bridge;
                return;
            case VoxelSlice3ObservePhase.Bridge:
                var bridged = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!bridged.Succeeded)
                    throw new InvalidOperationException($"Slice 3 observe bridge manufacture failed: {bridged.Status}.");
                SetCompositeResult(bridged);
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitBridge;
                return;
            case VoxelSlice3ObservePhase.AwaitBridge:
                if (_composites.VehicleCount != 1 || _composites.FaceCount == 0)
                    return;
                ValidateCompositeVisual();
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureBridge;
                return;
            case VoxelSlice3ObservePhase.CaptureBridge:
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
                CaptureObserve("issue59-bridge", "parked bridge fixture before damage");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitBridgeView;
                return;
            case VoxelSlice3ObservePhase.AwaitBridgeView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.Fracture;
                return;
            case VoxelSlice3ObservePhase.Fracture:
                var bridgeVehicle = _session.QueryComposites().Single();
                var fractured = _session.Damage(bridgeVehicle.Id, BridgeFixture.BridgeCell, 100);
                if (!fractured.Succeeded || _session.QueryComposites().Count != 2)
                    throw new InvalidOperationException($"Slice 3 observe fracture failed: {fractured.Status}.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：桥接伤害已结算断裂。";
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitFracture;
                return;
            case VoxelSlice3ObservePhase.AwaitFracture:
                if (_composites.VehicleCount != 2 || _composites.MeshInstanceCount != 2)
                    return;
                ValidateCompositeVisual();
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureFracture;
                return;
            case VoxelSlice3ObservePhase.CaptureFracture:
                CaptureObserve("issue59-fracture", "two surviving composites after bridge break, no world duplicate");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitFractureView;
                return;
            case VoxelSlice3ObservePhase.AwaitFractureView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.Partial;
                return;
            case VoxelSlice3ObservePhase.Partial:
                _session.Reset();
                var partialVehicle = _session.Manufacture(BridgeFixture.DefinitionId);
                var partial = _session.Damage(new[]
                {
                    new CompositeDamageTarget(partialVehicle.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(partialVehicle.VehicleId.Value, BridgeFixture.LeftCoreCell, 100)
                });
                if (!partial.Succeeded || _session.QueryComposites().Count != 1 || _session.World.VoxelCount != 26)
                    throw new InvalidOperationException($"Slice 3 observe partial drop failed: {partial.Status}.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：桥和左核心伤害已结算。";
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitPartial;
                return;
            case VoxelSlice3ObservePhase.AwaitPartial:
                if (_composites.VehicleCount != 1 || _session.World.VoxelCount != 26 || _meshes.FaceCount == 0)
                    return;
                ValidateCompositeVisual();
                _slice3ObservePhase = VoxelSlice3ObservePhase.CapturePartial;
                return;
            case VoxelSlice3ObservePhase.CapturePartial:
                CaptureObserve("issue59-partial", "right composite remains; left fragment appears once in the world mesh");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitPartialView;
                return;
            case VoxelSlice3ObservePhase.AwaitPartialView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.Drop;
                return;
            case VoxelSlice3ObservePhase.Drop:
                _session.Reset();
                var dropVehicle = _session.Manufacture(BridgeFixture.DefinitionId);
                var dropped = _session.Damage(new[]
                {
                    new CompositeDamageTarget(dropVehicle.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(dropVehicle.VehicleId.Value, BridgeFixture.LeftCoreCell, 100),
                    new CompositeDamageTarget(dropVehicle.VehicleId.Value, BridgeFixture.RightCoreCell, 100)
                });
                if (dropped.Status != CompositeDamageStatus.Removed || _session.World.VoxelCount != 52)
                    throw new InvalidOperationException($"Slice 3 observe drop failed: {dropped.Status}.");
                AssertUniqueOwnership();
                _result.Text = "最近命令：双核心伤害已整体脱落。";
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitDrop;
                return;
            case VoxelSlice3ObservePhase.AwaitDrop:
                if (_composites.VehicleCount != 0 || _meshes.FaceCount == 0)
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureDrop;
                return;
            case VoxelSlice3ObservePhase.CaptureDrop:
                CaptureObserve("issue59-drop", "identity-less fragments appear once in the world mesh");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitDropView;
                return;
            case VoxelSlice3ObservePhase.AwaitDropView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.RefitFracture;
                return;
            case VoxelSlice3ObservePhase.RefitFracture:
                _session.Reset();
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
                var refitBridge = _session.Manufacture(BridgeFixture.DefinitionId);
                if (!refitBridge.Succeeded)
                    throw new InvalidOperationException($"Slice 3 observe refit-fracture manufacture failed: {refitBridge.Status}.");
                RequireCommittedRefit(
                    _session.Refit(
                        refitBridge.VehicleId!.Value,
                        CompositeRefitOperation.Add(BridgeFixture.SecondBridgeCell, HollowShellRefit.MarkerId, VoxelOrientation.Identity)),
                    "observe second bridge");
                var staleRevision = _session.Vehicles.Single().SaveLocalRevision
                    ?? throw new InvalidOperationException("Slice 3 observe refit-fracture missing save-local revision.");
                var refitCut = _session.Damage(new[]
                {
                    new CompositeDamageTarget(refitBridge.VehicleId.Value, BridgeFixture.BridgeCell, 100),
                    new CompositeDamageTarget(refitBridge.VehicleId.Value, BridgeFixture.SecondBridgeCell, 100)
                });
                if (!refitCut.Succeeded || _session.Vehicles.Count != 2 || _session.World.VoxelCount != 0)
                    throw new InvalidOperationException($"Slice 3 observe refit-fracture failed: {refitCut.Status}.");
                RequireSuccessorSaveLocal(staleRevision);
                AssertUniqueOwnership();
                _result.Text = "最近命令：改装后双桥同事件断裂已结算。";
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitFracture;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitFracture:
                if (!AwaitCommittedVehicleVisual(2, 27))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureRefitFracture;
                return;
            case VoxelSlice3ObservePhase.CaptureRefitFracture:
                CaptureObserve("issue60-refit-fracture", "refitted dual-bridge cut leaves two 27-part successors, world empty");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitRefitFractureView;
                return;
            case VoxelSlice3ObservePhase.AwaitRefitFractureView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadSetup;
                return;
            case VoxelSlice3ObservePhase.ReloadSetup:
                RunSlice3ReloadSetup();
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadSetup;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadSetup:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                RequireLiveReload().WriteRefitMarkerMax(150);
                RequireCommittedReload("observe-marker-max-150");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 75, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                RequireDamagedMax(_refitP, 75, 150);
                AimRefitCamera(HollowShellRefit.SecondAdd);
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReload150;
                return;
            case VoxelSlice3ObservePhase.CaptureReload150:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                AimRefitCamera(HollowShellRefit.SecondAdd);
                CaptureObserve("issue61-reload-150", "refit marker max 150, P durability still 75");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReload150;
                return;
            case VoxelSlice3ObservePhase.AwaitReload150:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadMax60;
                return;
            case VoxelSlice3ObservePhase.ReloadMax60:
                RequireLiveReload().WriteRefitMarkerMax(60);
                RequireCommittedReload("observe-marker-max-60");
                RequirePart(_refitVehicle, HollowShellRefit.SecondAdd, _refitP, 60, HollowShellRefit.MarkerOrientation, HollowShellRefit.MarkerId);
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadMax60;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadMax60:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReload60;
                return;
            case VoxelSlice3ObservePhase.CaptureReload60:
                CaptureObserve("issue61-reload-60", "refit marker max 60 clamped P durability to 60");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReload60;
                return;
            case VoxelSlice3ObservePhase.AwaitReload60:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadDisplay;
                return;
            case VoxelSlice3ObservePhase.ReloadDisplay:
                _reloadFingerprint = CurrentCompositeFingerprint();
                RequireLiveReload().WriteRefitMarkerName("显示改装标记");
                RequireLiveReload().WriteAsymmetricMarkerTop();
                RequireCommittedReload("observe-marker-display");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadDisplay;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadDisplay:
                if (!AwaitCommittedVehicleVisual(1, 169))
                    return;
                if (string.Equals(_reloadFingerprint, CurrentCompositeFingerprint(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Observe display reload did not refresh existing vehicle textures.");
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReloadDisplay;
                return;
            case VoxelSlice3ObservePhase.CaptureReloadDisplay:
                CaptureObserve("issue61-display", "published marker name/asset update refreshes the existing vehicle");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadDisplayView;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadDisplayView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadTemplate;
                return;
            case VoxelSlice3ObservePhase.ReloadTemplate:
                RequireLiveReload().AddHollowShellExtraCell();
                RequireCommittedReload("observe-hollow-template");
                if (_session.QueryComposite(_refitVehicle)!.Parts.Count != 169)
                    throw new InvalidOperationException("Observe template reload rebuilt the existing vehicle.");
                var fresh = _session.Manufacture(HollowShellFixture.DefinitionId);
                if (!fresh.Succeeded || fresh.Parts?.Count != 169)
                    throw new InvalidOperationException("Observe new manufacture did not use the updated template.");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadTemplate;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadTemplate:
                if (!AwaitCommittedVehicleVisual(2, 169))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReloadTemplate;
                return;
            case VoxelSlice3ObservePhase.CaptureReloadTemplate:
                CaptureObserve("issue61-template", "existing 169-part refit kept; new manufacture uses extra interior cell");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadTemplateView;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadTemplateView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadCollision;
                return;
            case VoxelSlice3ObservePhase.ReloadCollision:
                if (_session.Add(_reloadWorldCell, HollowShellFixture.ShellId, VoxelOrientation.Identity).Status != WorldCommandStatus.Added)
                    throw new InvalidOperationException("Observe collision setup failed to place a world shell.");
                RequireLiveReload().WriteVehicleShellCollision("none");
                RequireCommittedReload("observe-shell-collision-none");
                if (_session.QueryCollision(_reloadWorldCell).Status != VoxelCollisionQueryStatus.Miss)
                    throw new InvalidOperationException("Observe collision reload did not miss the world shell.");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadCollision;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadCollision:
                if (!CollisionPhysicsReady)
                    return;
                RequireDomainAndPublishedCollision(_reloadWorldCell, expectHit: false);
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReloadCollision;
                return;
            case VoxelSlice3ObservePhase.CaptureReloadCollision:
                CaptureObserve("issue61-collision", "supported world collision misses after vehicle-shell kind none");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadCollisionView;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadCollisionView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadReject;
                return;
            case VoxelSlice3ObservePhase.ReloadReject:
                _reloadFingerprint = CurrentCompositeFingerprint();
                _reloadAuthority = _session.CaptureAuthority();
                RequireLiveReload().RemoveIdentityCore();
                var rejected = VoxelLiveReloadDriver.Reload(GetNode<Main>("/root/Main"));
                if (rejected.Status != ContentReloadStatus.Rejected || _reloadAuthority != _session.CaptureAuthority())
                    throw new InvalidOperationException("Observe missing-core reload did not reject atomically.");
                if (!string.Equals(_reloadFingerprint, CurrentCompositeFingerprint(), StringComparison.Ordinal))
                    throw new InvalidOperationException("Observe rejected reload changed existing vehicle display.");
                RequireContinueAfterRejectedReload();
                RequireLiveReload().RestoreIdentityCore();
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadReject;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadReject:
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReloadRejected;
                return;
            case VoxelSlice3ObservePhase.CaptureReloadRejected:
                CaptureObserve("issue61-rejected", "rejected missing identity-core keeps vehicles, world, and collision");
                _observeHoldFrames = 8;
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadRejectedView;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadRejectedView:
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadValue;
                return;
            case VoxelSlice3ObservePhase.ReloadValue:
                _session.Reset();
                PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
                RequireLiveReload().WriteIdentityCoreHeavyValue(10);
                RequireCommittedReload("observe-heavy-core-10");
                var rightHeavy = _session.Manufacture(BridgeFixture.RightHeavyDefinitionId);
                if (!rightHeavy.Succeeded)
                    throw new InvalidOperationException($"Observe right-heavy manufacture failed: {rightHeavy.Status}.");
                _refitVehicle = rightHeavy.VehicleId!.Value;
                RequireLiveReload().WriteIdentityCoreHeavyValue(20);
                RequireCommittedReload("observe-heavy-core-20");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadValue;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadValue:
                if (!AwaitCommittedVehicleVisual(1, 55))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.ReloadFracture;
                return;
            case VoxelSlice3ObservePhase.ReloadFracture:
                if (!_session.Damage(_refitVehicle, BridgeFixture.BridgeCell, 100).Succeeded)
                    throw new InvalidOperationException("Observe value-fracture damage failed.");
                var successors = _session.QueryComposites();
                if (successors.Count != 2 ||
                    successors[0].Id != _refitVehicle ||
                    successors[0].Parts.Any(part => !BridgeFixture.IsRightCell(part.Cell)))
                    throw new InvalidOperationException("Observe reloaded right-core value did not become the primary successor.");
                _slice3ObservePhase = VoxelSlice3ObservePhase.AwaitReloadFracture;
                return;
            case VoxelSlice3ObservePhase.AwaitReloadFracture:
                if (!AwaitCommittedVehicleVisual(2, 27))
                    return;
                _slice3ObservePhase = VoxelSlice3ObservePhase.CaptureReloadFracture;
                return;
            case VoxelSlice3ObservePhase.CaptureReloadFracture:
                CaptureObserve("issue61-value-fracture", "after Value=20 reload, right fragment inherits the original vehicle id");
                GD.Print("VOXEL_OBSERVE_COMPLETE");
                _slice3ObservePhase = VoxelSlice3ObservePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private GameConsole? FindConsole() =>
        GetNode<Main>("/root/Main").GetChildren().OfType<GameConsole>().SingleOrDefault();

    private void AdvanceSlice4ResumeSmoke()
    {
        switch (_slice4ResumeSmokePhase)
        {
            case VoxelSlice4ResumeSmokePhase.Load:
            {
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Slice 4 persist smoke is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Slice 4 persist smoke could not load {saveId:D}: {loaded.Status}.");
                var restored = RequireSlice4CoastAssemblies(98, 88.875, 4, 3.5, frontRunning: true);
                if (_session.SimulationStep != 330)
                    throw new InvalidOperationException($"Slice 4 resume loaded step={_session.SimulationStep}.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_RESUME_LOADED id={saveId:D} step={_session.SimulationStep} loco={restored.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={restored.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} loco_speed={restored.Loco.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage_speed={restored.Carriage.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} hash={WorldSaveAuthority.Fingerprint(_session)} name={loaded.DisplayName}");
                _slice4ResumeSmokePhase = VoxelSlice4ResumeSmokePhase.AwaitLoad;
                return;
            }
            case VoxelSlice4ResumeSmokePhase.AwaitLoad:
            {
                if (_dirty || _tracks.SolidCount == 0 || _composites.VehicleCount != 2)
                    return;
                ValidateGeneratedMesh();
                ValidateCompositeVisual();
                _slice4TrainMeshIds = _composites.MeshInstanceIds.ToArray();
                RunSlice4SupportLossSmoke("VOXEL_SMOKE_SLICE4_SUPPORT_LOSS_RESUMED");
                var supportSave = SaveSlice4Detached("Slice 4 Support Loss");
                var supportPath = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(supportSave);
                if (!File.Exists(supportPath))
                    throw new InvalidOperationException($"Slice 4 support-loss save was not created at {supportPath}.");
                GD.Print($"VOXEL_SMOKE_SLICE4_SUPPORT_LOSS_SAVE_WRITTEN id={supportSave:D} bytes={new FileInfo(supportPath).Length} path={supportPath}");
                var resumeIdText = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(resumeIdText, out var resumeId))
                    throw new InvalidOperationException("Slice 4 persist smoke is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var reload = _session.Load(resumeId);
                if (reload.Status != WorldSaveStatus.Loaded || reload.SaveId != resumeId)
                    throw new InvalidOperationException($"Slice 4 smoke could not restore the 330 save after support loss: {reload.Status}.");
                RequireSlice4CoastAssemblies(98, 88.875, 4, 3.5, frontRunning: true);
                _slice4ResumeSmokePhase = VoxelSlice4ResumeSmokePhase.ContinueMotion;
                return;
            }
            case VoxelSlice4ResumeSmokePhase.ContinueMotion:
                if (!_session.AdvanceSimulation(30).Succeeded || _session.SimulationStep != 360)
                    throw new InvalidOperationException("Slice 4 smoke could not continue coasting after resume.");
                var continued = RequireSlice4CoastAssemblies();
                if (Math.Abs(continued.Loco.Vehicles[0].FrontContact.X - 100) > 1e-8 ||
                    Math.Abs(continued.Carriage.Vehicles[0].FrontContact.X - 90.5) > 1e-8)
                    throw new InvalidOperationException(
                        $"Slice 4 resumed coast is {continued.Loco.Vehicles[0].FrontContact.X},{continued.Carriage.Vehicles[0].FrontContact.X}.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_COAST_RESUMED loco={continued.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={continued.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
                _slice4ResumeSmokePhase = VoxelSlice4ResumeSmokePhase.AwaitContinueVisual;
                return;
            case VoxelSlice4ResumeSmokePhase.AwaitContinueVisual:
                if (_dirty || _composites.VehicleCount != 2)
                    return;
                ValidateCompositeVisual();
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_RESUME_TRANSFORM_ONLY rebuilt={(_composites.LastSync == CompositeVisualSyncKind.TransformOnly ? 0 : 1)} meshes={_composites.MeshInstanceCount}");
                ResetSlice4AfterMotion();
                if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
                    !_session.AdvanceSimulation(900).Succeeded ||
                    _session.SimulationStep != 900)
                    throw new InvalidOperationException("Slice 4 smoke could not rebuild and reach step 900 after coast resume.");
                var step900 = RequireSlice4Assembly();
                if (Math.Abs(step900.Vehicles[0].FrontContact.X - 136) > 1e-8 ||
                    Math.Abs(step900.Speed - 4) > 1e-8 ||
                    !step900.Running)
                    throw new InvalidOperationException(
                        $"Slice 4 step 900 is {step900.Vehicles[0].FrontContact.X},{step900.Speed},{step900.Running}.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_STEP900 loco={step900.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={step900.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} hash={WorldSaveAuthority.Fingerprint(_session)}");
                ContinueSlice4MotionToStop();
                RunSlice4EndStopSmoke();
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitResetAfterMotion;
                _slice4ResumeSmokePhase = VoxelSlice4ResumeSmokePhase.Done;
                return;
        }
    }

    private void AdvanceSlice4Smoke()
    {
        switch (_slice4SmokePhase)
        {
            case VoxelSlice4SmokePhase.SaveEmpty:
                if (_session.World.VoxelCount != 0 || _session.HasRailway)
                    throw new InvalidOperationException("Slice 4 smoke must start from an empty world.");
                var emptySave = _session.Save("Slice 4 Empty");
                if (emptySave.Status != WorldSaveStatus.Saved || !emptySave.SaveId.HasValue)
                    throw new InvalidOperationException($"Slice 4 empty save failed: {emptySave.Status}.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.Construct;
                return;
            case VoxelSlice4SmokePhase.Construct:
                if (!_session.ConstructRails().Succeeded || _session.World.VoxelCount != 1032)
                    throw new InvalidOperationException("Slice 4 smoke could not construct the fixture railway.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_CONSTRUCTED voxels={_session.World.VoxelCount} sleepers={_session.QueryRails().SleeperCount} segments={_session.Rails.Segments.Count} solids={_tracks.SolidCount}");
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitConstruct;
                return;
            case VoxelSlice4SmokePhase.AwaitConstruct:
                if (_tracks.SolidCount == 0 || _dirty)
                    return;
                ValidateGeneratedMesh();
                _slice4SmokePhase = VoxelSlice4SmokePhase.Inspect;
                return;
            case VoxelSlice4SmokePhase.Inspect:
                RequireSlice4Occupancy(StraightRailFixture.EdgeCell, "VOXEL_SMOKE_SLICE4_EDGE_CELL");
                RequireSlice4Occupancy(StraightRailFixture.LeftRailSample, "VOXEL_SMOKE_SLICE4_RAILS");
                RequireSlice4Occupancy(StraightRailFixture.RightRailSample, "VOXEL_SMOKE_SLICE4_RAILS");
                if (_session.QueryRails().SleeperCount != 257)
                    throw new InvalidOperationException("Slice 4 sleeper count is not 257.");
                GD.Print($"VOXEL_SMOKE_SLICE4_SLEEPERS count={_session.QueryRails().SleeperCount}");
                if (!_session.Rails.AreConnected(new EntityId(1), new EntityId(2)))
                    throw new InvalidOperationException("Slice 4 technical segments are not connected.");
                GD.Print("VOXEL_SMOKE_SLICE4_CONNECTION connected=1");
                CaptureSlice4UnrelatedTrackMeshes();
                _slice4SmokePhase = VoxelSlice4SmokePhase.Assemble;
                return;
            case VoxelSlice4SmokePhase.Assemble:
                if (!_session.AssembleStaticTrain().Succeeded)
                    throw new InvalidOperationException("Slice 4 smoke could not assemble the formal locomotive and carriage.");
                RequireSlice4TrainMarkers();
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitAssemble;
                return;
            case VoxelSlice4SmokePhase.AwaitAssemble:
                if (_dirty)
                    return;
                if (_composites.VehicleCount != 2 || _composites.FaceCount == 0 || _composites.MeshInstanceCount != 2)
                    return;
                if (_session.World.VoxelCount != 1032)
                    throw new InvalidOperationException("Slice 4 train assembly published vehicle voxels into the world.");
                ValidateCompositeVisual();
                GD.Print($"VOXEL_SMOKE_SLICE4_TRAIN_VISUAL vehicles={_composites.VehicleCount} meshes={_composites.MeshInstanceCount} faces={_composites.FaceCount}");
                _slice4TrainMeshIds = _composites.MeshInstanceIds.ToArray();
                _slice4SmokePhase = VoxelSlice4SmokePhase.LegacyAdd;
                return;
            case VoxelSlice4SmokePhase.LegacyAdd:
                _selectedPosition = new WorldVoxelCoord(4, 4, 4);
                if (_session.Query(_selectedPosition).IsOccupied)
                    throw new InvalidOperationException("Slice 4 shortcut probe must start empty.");
                PressKey(Key.Insert);
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitLegacyAdd;
                return;
            case VoxelSlice4SmokePhase.AwaitLegacyAdd:
                if (_session.Query(_selectedPosition).Voxel is null || _session.UndoCount != 1)
                    throw new InvalidOperationException($"Slice 4 Insert shortcut did not add a voxel with one undo record; undo={_session.UndoCount}.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.LegacyStart;
                return;
            case VoxelSlice4SmokePhase.LegacyStart:
                PressKey(Key.T);
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitLegacyStart;
                return;
            case VoxelSlice4SmokePhase.AwaitLegacyStart:
                if (!_session.QueryTrainAssemblies().Single().Running || _session.HasEditHistory)
                    throw new InvalidOperationException("Slice 4 T shortcut started the train without ending non-empty history.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.LegacyRemove;
                return;
            case VoxelSlice4SmokePhase.LegacyRemove:
                PressKey(Key.Delete);
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitLegacyRemove;
                return;
            case VoxelSlice4SmokePhase.AwaitLegacyRemove:
                if (_session.Query(_selectedPosition).Voxel is not null || _session.UndoCount != 1)
                    throw new InvalidOperationException($"Slice 4 Delete shortcut did not remove the voxel with one undo record; undo={_session.UndoCount}.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.LegacyStop;
                return;
            case VoxelSlice4SmokePhase.LegacyStop:
                PressKey(Key.Y);
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitLegacyStop;
                return;
            case VoxelSlice4SmokePhase.AwaitLegacyStop:
                if (_session.QueryTrainAssemblies().Single().Running || _session.HasEditHistory || _session.World.VoxelCount != 1032)
                    throw new InvalidOperationException("Slice 4 Y shortcut did not stop after ending non-empty history and restoring the world.");
                GD.Print("VOXEL_SMOKE_SLICE4_LEGACY_SHORTCUT keys=T,Y history=nonempty started=true stopped=true");
                _selectedPosition = SelectedCell(_session.Identity);
                _slice4SmokePhase = VoxelSlice4SmokePhase.MotionStep300;
                return;
            case VoxelSlice4SmokePhase.MotionStep300:
                RunSlice4Step300Smoke();
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitMotionVisual;
                return;
            case VoxelSlice4SmokePhase.AwaitMotionVisual:
                if (_dirty)
                    return;
                if (_composites.LastSync != CompositeVisualSyncKind.TransformOnly)
                    throw new InvalidOperationException("Slice 4 motion rebuilt composite meshes instead of updating transforms.");
                if (!_slice4TrainMeshIds.SequenceEqual(_composites.MeshInstanceIds))
                    throw new InvalidOperationException("Slice 4 motion replaced composite mesh instances.");
                GD.Print("VOXEL_SMOKE_SLICE4_TRANSFORM_ONLY rebuilt=0");
                RunSlice4CouplerCoastSmoke();
                var saved = _session.Save("Slice 4 Step 330");
                if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
                    throw new InvalidOperationException($"Slice 4 smoke could not save at step 330: {saved.Status}.");
                var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
                if (!File.Exists(path))
                    throw new InvalidOperationException($"Slice 4 smoke save file was not created at {path}.");
                GD.Print($"VOXEL_SMOKE_SLICE4_SAVE_WRITTEN id={saved.SaveId:D} bytes={new FileInfo(path).Length} path={path} step={_session.SimulationStep}");
                RunSlice4SupportLossSmoke("VOXEL_SMOKE_SLICE4_SUPPORT_LOSS");
                ResetSlice4AfterMotion();
                RunSlice4CoastStopHudSmoke();
                ResetSlice4AfterMotion();
                RunSlice4GapStopSmoke("VOXEL_SMOKE_SLICE4_GAP_STOP");
                ResetSlice4AfterMotion();
                RunSlice4FullDestroySmoke();
                _slice4SmokePhase = VoxelSlice4SmokePhase.OpenConsole;
                return;
            case VoxelSlice4SmokePhase.AwaitResetAfterMotion:
                if (_dirty || _tracks.SolidCount == 0 || _composites.VehicleCount != 2)
                    return;
                CaptureSlice4UnrelatedTrackMeshes();
                _slice4SmokePhase = VoxelSlice4SmokePhase.ContactDamage;
                return;
            case VoxelSlice4SmokePhase.ContactDamage:
            {
                var assembled = _session.QueryTrainAssemblies();
                if (assembled.Count != 1)
                    throw new InvalidOperationException("Slice 4 smoke lost the assembled train before contact damage.");
                var locoId = assembled[0].VehicleIds[0];
                var carriageId = assembled[0].VehicleIds[1];
                if (_session.Damage(locoId, HollowShellFixture.RearBoundCell, 100).Status != CompositeDamageStatus.Damaged)
                    throw new InvalidOperationException("Slice 4 smoke could not destroy the locomotive rear contact.");
                if (_session.Damage(carriageId, HollowShellFixture.RearBoundCell, 100).Status != CompositeDamageStatus.Damaged)
                    throw new InvalidOperationException("Slice 4 smoke could not destroy the carriage rear contact.");
                var remaining = _session.QueryTrainAssemblies();
                var loco = _session.QueryComposite(locoId);
                var carriage = _session.QueryComposite(carriageId);
                if (remaining.Count != 0)
                    throw new InvalidOperationException("Slice 4 smoke still reports an assembly after required contact loss.");
                if (loco is null || loco.OperationalCondition != OperationalCondition.Disabled)
                    throw new InvalidOperationException("Slice 4 smoke contact loss did not disable the locomotive.");
                if (carriage is null || carriage.OperationalCondition != OperationalCondition.Disabled)
                    throw new InvalidOperationException("Slice 4 smoke contact loss did not disable the carriage.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_TRAIN_CONTACT_LOST assemblies={remaining.Count} loco={loco.OperationalCondition} carriage={carriage.OperationalCondition}");
                _slice4SmokePhase = VoxelSlice4SmokePhase.EdgeDamage;
                return;
            }
            case VoxelSlice4SmokePhase.EdgeDamage:
                if (_session.Damage(new WorldVoxelCoord(110, 1, 14), 100).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Slice 4 smoke could not destroy the sleeper-edge cell.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitEdgeDamage;
                return;
            case VoxelSlice4SmokePhase.AwaitEdgeDamage:
                if (_dirty)
                    Refresh();
                var edge = new WorldVoxelCoord(110, 1, 14);
                var intact = new WorldVoxelCoord(110, 1, 17);
                var farChunk = new ChunkCoord(1, 0, 0);
                var zChunk = new ChunkCoord(6, 0, 1);
                if (!_tracks.HasGapNear(edge))
                    throw new InvalidOperationException("Slice 4 edge damage still covers the destroyed sleeper cell.");
                if (!_tracks.Model.Sections.SelectMany(section => section.Solids).Any(solid =>
                        solid.Kind == TrackSolidKind.Sleeper && solid.Volume.OverlapsCell(intact)))
                    throw new InvalidOperationException("Slice 4 edge damage deleted the intact sleeper half.");
                if (_tracks.LastRebuiltChunks.Contains(farChunk) || _tracks.LastRebuiltChunks.Contains(zChunk))
                    throw new InvalidOperationException(
                        $"Slice 4 edge damage rebuilt unrelated sections: {string.Join(',', _tracks.LastRebuiltChunks)}");
                if (_tracks.MeshFor(farChunk)?.GetInstanceId() != _slice4FarMeshId ||
                    _tracks.MeshFor(zChunk)?.GetInstanceId() != _slice4ZNeighborMeshId)
                    throw new InvalidOperationException("Slice 4 local refresh replaced an unrelated section mesh.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_RENDER_LOCAL rebuilt={_tracks.LastRebuiltChunks.Count} retained={_tracks.LastRetainedChunks.Count} far_kept=1 z_kept=1");
                GD.Print("VOXEL_SMOKE_SLICE4_EDGE_CLIP intact_sleeper=1");
                _slice4SmokePhase = VoxelSlice4SmokePhase.RepairEdge;
                return;
            case VoxelSlice4SmokePhase.RepairEdge:
                if (!_session.RepairRails(new[] { new WorldVoxelCoord(110, 1, 14) }).Succeeded)
                    throw new InvalidOperationException("Slice 4 smoke could not repair the sleeper-edge cell.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitRepairEdge;
                return;
            case VoxelSlice4SmokePhase.AwaitRepairEdge:
                if (_dirty || _tracks.HasGapNear(new WorldVoxelCoord(110, 1, 14)))
                    return;
                _slice4SmokePhase = VoxelSlice4SmokePhase.Damage;
                return;
            case VoxelSlice4SmokePhase.Damage:
                if (_session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status != WorldCommandStatus.Removed)
                    throw new InvalidOperationException("Slice 4 smoke could not destroy the fixture support cell.");
                GD.Print("VOXEL_SMOKE_SLICE4_DAMAGED");
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitDamage;
                return;
            case VoxelSlice4SmokePhase.AwaitDamage:
                if (_dirty)
                    Refresh();
                var first = _session.Rails.TraversableWorldX(new EntityId(1), _session.World);
                if (first.Any(interval => interval.Start <= 110 && interval.End >= 111))
                    throw new InvalidOperationException(
                        $"Slice 4 damage did not open x=110..111: {string.Join(',', first.Select(item => $"{item.Start}..{item.End}"))}");
                if (!_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
                    _tracks.Refresh(this, _session);
                if (!_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
                    throw new InvalidOperationException("Slice 4 track render still covers the destroyed support cell.");
                GD.Print($"VOXEL_SMOKE_SLICE4_GAP intervals={string.Join(',', first.Select(item => $"{item.Start}..{item.End}"))}");
                GD.Print($"VOXEL_SMOKE_SLICE4_RENDER_GAP solids={_tracks.SolidCount}");
                _slice4SmokePhase = VoxelSlice4SmokePhase.Repair;
                return;
            case VoxelSlice4SmokePhase.Repair:
                if (!_session.RepairRails(new[] { StraightRailFixture.FirstSegmentDamageCell }).Succeeded)
                    throw new InvalidOperationException("Slice 4 smoke could not repair the fixture support cell.");
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitRepair;
                return;
            case VoxelSlice4SmokePhase.AwaitRepair:
                if (_dirty || !_session.Query(StraightRailFixture.FirstSegmentDamageCell).IsOccupied)
                    return;
                if (_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
                    return;
                GD.Print("VOXEL_SMOKE_SLICE4_REPAIRED");
                ResetSlice4AfterMotion();
                RunSlice4GapStopSmoke("VOXEL_SMOKE_SLICE4_GAP_STOP");
                _slice4SmokePhase = VoxelSlice4SmokePhase.SaveAt900;
                return;
            case VoxelSlice4SmokePhase.SaveAt900:
                var resumedSave = SaveSlice4Detached("Slice 4 Gap Halt");
                GD.Print($"VOXEL_SMOKE_SLICE4_RESUME_SAVED id={resumedSave:D} step={_session.SimulationStep}");
                _slice4SmokePhase = VoxelSlice4SmokePhase.Done;
                GetTree().Quit();
                return;
            case VoxelSlice4SmokePhase.OpenConsole:
                PressKey(Key.Quoteleft, '`');
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitConsoleOpen;
                return;
            case VoxelSlice4SmokePhase.AwaitConsoleOpen:
                if (FindConsole()?.IsOpen != true)
                    return;
                _slice4SmokePhase = VoxelSlice4SmokePhase.CloseConsole;
                return;
            case VoxelSlice4SmokePhase.CloseConsole:
                PressKey(Key.Escape);
                _slice4SmokePhase = VoxelSlice4SmokePhase.AwaitConsoleClosed;
                return;
            case VoxelSlice4SmokePhase.AwaitConsoleClosed:
                if (FindConsole()?.IsOpen != false)
                    return;
                GD.Print("VOXEL_SMOKE_CONSOLE_CLOSED");
                _slice4SmokePhase = VoxelSlice4SmokePhase.LeaveScene;
                return;
            case VoxelSlice4SmokePhase.LeaveScene:
                System.Environment.SetEnvironmentVariable(VoxelSmokeReenterEnvironmentVariable, "1");
                LeaveSmokeScene();
                _slice4SmokePhase = VoxelSlice4SmokePhase.Done;
                return;
        }
    }

    private void AdvanceHostSimulation(double delta)
    {
        if (_session.Identity.Slice != VoxelDevelopmentSlice.Slice4)
            return;
        if (IsAutomatedHarness())
            return;
        var steps = _session.CollectHostSteps(delta);
        if (steps > 0)
            _session.AdvanceSimulation(steps);
    }

    private void ApplyRailPresentation()
    {
        if (_session.Identity.Slice != VoxelDevelopmentSlice.Slice4)
            return;
        _composites.ApplyDisplayPoses(_session.QueryDisplayPoses());
        ApplyFollowCamera();
        if (_title is not null)
            _title.Text = FollowTitle();
        if (_halt is not null)
            _halt.Text = HaltHud();
    }

    private string FollowTitle()
    {
        var followed = _session.FollowedVehicleId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        return $"切片 4 · 跟随 {followed} · 侧向观察车体/车钩 · {HaltHud()}";
    }

    private string HaltHud()
    {
        var assemblies = _session.QueryTrainAssemblies();
        if (assemblies.Count == 0)
            return "停因 无编组";
        var parts = assemblies.Select(item =>
            $"编组{item.Id.Value} {AlongRailDisplay.MotionStatusText(item, _session.SimulationPaused)}");
        return "停因 " + string.Join(" · ", parts);
    }

    private void ApplyFollowCamera()
    {
        var follow = _session.QueryFollowedCamera();
        if (follow is null)
            return;
        var camera = GetChildren().OfType<Camera3D>().FirstOrDefault();
        if (camera is null || !IsInsideTree() || !camera.IsInsideTree())
            return;
        var definition = _session.QueryComposite(follow.VehicleId)?.DefinitionId
                         ?? StaticTrainFixture.CarriageId;
        var frame = AlongRailDisplay.FrameFollowCamera(follow, definition);
        var look = new Vector3((float)frame.LookX, (float)frame.LookY, (float)frame.LookZ);
        camera.Position = new Vector3((float)frame.PositionX, (float)frame.PositionY, (float)frame.PositionZ);
        if ((camera.Position - look).LengthSquared() > 1e-8f)
            camera.LookAt(look, Vector3.Up);
    }

    private void CycleFollowedVehicle()
    {
        var ids = _session.Vehicles.OrderBy(item => item.Id.Value).Select(item => item.Id).ToArray();
        if (ids.Length == 0)
        {
            _result.Text = "最近命令：没有可跟随的存活车辆。";
            return;
        }

        var index = _session.FollowedVehicleId is { } current
            ? Array.FindIndex(ids, item => item == current)
            : -1;
        var next = ids[(index + 1 + ids.Length) % ids.Length];
        if (!_session.TryFollowVehicle(next))
        {
            _result.Text = "最近命令：切换跟随失败。";
            return;
        }

        ApplyRailPresentation();
        _result.Text = $"最近命令：相机跟随车辆 {next.Value}。";
    }

    private void PrintSlice4DisplayMarkers(string marker)
    {
        var zero = _session.QueryDisplayPoses(0);
        var mid = _session.QueryDisplayPoses(0.5);
        var one = _session.QueryDisplayPoses(1);
        var follow = _session.QueryFollowedCamera(0.5);
        var meshIds = string.Join(',', _composites.MeshInstanceIds);
        GD.Print(
            $"{marker} step={_session.SimulationStep} alpha={_session.DisplayInterpolationAlpha.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} ended={mid.IntervalEnded.ToString().ToLowerInvariant()} vehicles={mid.Vehicles.Count} follow={_session.FollowedVehicleId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} from={zero.Vehicles.FirstOrDefault()?.FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} mid={mid.Vehicles.FirstOrDefault()?.FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} to={one.Vehicles.FirstOrDefault()?.FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} heading={follow?.HeadingX.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) ?? "none"} meshes={meshIds}");
    }

    private Camera3D RequireFollowCameraNode()
    {
        var camera = GetChildren().OfType<Camera3D>().FirstOrDefault();
        if (camera is null || !IsInsideTree() || !camera.IsInsideTree())
            throw new InvalidOperationException("Slice 4 camera node is not in the tree.");
        return camera;
    }

    private (Vector3 Position, Vector3 Forward, double Offset) RequireSlice4CameraFrame()
    {
        ApplyFollowCamera();
        var camera = RequireFollowCameraNode();
        var follow = _session.QueryFollowedCamera()
                     ?? throw new InvalidOperationException("Slice 4 follow camera query is missing.");
        var definition = _session.QueryComposite(follow.VehicleId)?.DefinitionId
                         ?? StaticTrainFixture.CarriageId;
        var offset = AlongRailDisplay.LookOffsetAlongHeading(definition);
        return (camera.GlobalPosition, -camera.GlobalTransform.Basis.Z, offset);
    }

    private void PrintSlice4CameraMarker(string marker, (Vector3 Position, Vector3 Forward, double Offset) frame)
    {
        var follow = _session.FollowedVehicleId?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
        GD.Print(
            $"{marker} step={_session.SimulationStep} follow={follow} cam={frame.Position.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)},{frame.Position.Y.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)},{frame.Position.Z.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} forward={frame.Forward.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)},{frame.Forward.Y.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)},{frame.Forward.Z.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} offset={frame.Offset.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static void RequireSlice4CameraContinuous(
        (Vector3 Position, Vector3 Forward, double Offset) before,
        (Vector3 Position, Vector3 Forward, double Offset) after)
    {
        if (Math.Abs(after.Offset - before.Offset) > 1e-8)
            throw new InvalidOperationException(
                $"Slice 4 follow offset jumped from {before.Offset} to {after.Offset}.");
        if ((after.Position - before.Position).Length() > 1f || (after.Forward - before.Forward).Length() > 0.2f)
            throw new InvalidOperationException(
                $"Slice 4 Camera3D jumped from {before.Position}/{before.Forward} to {after.Position}/{after.Forward}.");
    }

    private bool IsAutomatedHarness() =>
        string.Equals(System.Environment.GetEnvironmentVariable(VoxelSmokeEnvironmentVariable), "1", StringComparison.Ordinal) ||
        string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveEnvironmentVariable), "1", StringComparison.Ordinal) ||
        string.Equals(System.Environment.GetEnvironmentVariable(VoxelObserveResumeEnvironmentVariable), "1", StringComparison.Ordinal) ||
        string.Equals(System.Environment.GetEnvironmentVariable("TRAIN_VOXEL_BASELINE"), "1", StringComparison.Ordinal);

    private void RunSlice4Step300Smoke()
    {
        var reverse = _session.StartTrain(RailHeading.AgainstAlignment);
        if (reverse.Status != TrainMotionStatus.ReverseUnsupported)
            throw new InvalidOperationException($"Slice 4 reverse start should be rejected, got {reverse.Status}.");
        var parked = RequireSlice4Assembly();
        if (Math.Abs(parked.Vehicles[0].FrontContact.X - 80) > 1e-8 || parked.Running)
            throw new InvalidOperationException("Slice 4 reverse rejection moved the parked train.");
        GD.Print("VOXEL_SMOKE_SLICE4_REVERSE_REJECTED");

        var started = _session.StartTrain(RailHeading.AlongAlignment);
        if (!started.Succeeded)
            throw new InvalidOperationException($"Slice 4 smoke could not start the train: {started.Status}.");
        var advanced = _session.AdvanceSimulation(300);
        if (!advanced.Succeeded)
            throw new InvalidOperationException($"Slice 4 smoke could not advance 300 steps: {advanced.Status}.");
        var step300 = RequireSlice4Assembly();
        if (Math.Abs(step300.Vehicles[0].FrontContact.X - 96) > 1e-8 ||
            Math.Abs(step300.Vehicles[1].FrontContact.X - 87) > 1e-8 ||
            Math.Abs(step300.Speed - 4) > 1e-8)
            throw new InvalidOperationException(
                $"Slice 4 step 300 is {step300.Vehicles[0].FrontContact.X},{step300.Vehicles[1].FrontContact.X},{step300.Speed}.");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_STEP300 loco={step300.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={step300.Vehicles[1].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={step300.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
        var mid = _session.QueryDisplayPoses(0.5);
        if (mid.Vehicles.Count != 2 ||
            Math.Abs(mid.Vehicles[0].FrontContact.X - mid.Vehicles[1].FrontContact.X - 9) > 1e-8 ||
            mid.IntervalEnded)
            throw new InvalidOperationException("Slice 4 display interpolation lost rigid coupler spacing.");
        if (_session.FollowedVehicleId != step300.VehicleIds[0])
            throw new InvalidOperationException("Slice 4 default follow is not the locomotive.");
        var camera = _session.QueryFollowedCamera(0.5);
        if (camera is null || camera.VehicleId != step300.VehicleIds[0] || Math.Abs(camera.HeadingX - 1) > 1e-8)
            throw new InvalidOperationException("Slice 4 follow camera is not using the displayed heading.");
        PrintSlice4DisplayMarkers("VOXEL_SMOKE_SLICE4_DISPLAY");
        GD.Print($"VOXEL_SMOKE_SLICE4_FOLLOW id={_session.FollowedVehicleId!.Value} heading={camera.HeadingX.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private void AdvanceSlice4GapResumeSmoke()
    {
        switch (_slice4GapResumeSmokePhase)
        {
            case VoxelSlice4GapResumeSmokePhase.Load:
            {
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Slice 4 gap resume smoke is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Slice 4 gap resume could not load {saveId:D}: {loaded.Status}.");
                var stopped = RequireSlice4Assembly();
                if (stopped.Vehicles[0].FrontContact.X > 110 + 1e-8 ||
                    stopped.Speed != 0 ||
                    stopped.Running ||
                    stopped.HaltReason != TrainHaltReason.ForwardGap)
                    throw new InvalidOperationException(
                        $"Slice 4 gap resume is {stopped.Vehicles[0].FrontContact.X} speed {stopped.Speed} halt {stopped.HaltReason}.");
                if (stopped.Vehicles.Any(vehicle =>
                        vehicle.FrontContact.X > 110 + 1e-8 && vehicle.FrontContact.X < 111 - 1e-8 ||
                        vehicle.RearContact.X > 110 + 1e-8 && vehicle.RearContact.X < 111 - 1e-8))
                    throw new InvalidOperationException("Slice 4 gap resume placed a contact inside the gap.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_GAP_RESUME_LOADED id={saveId:D} front={stopped.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={stopped.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={stopped.HaltReason} hash={WorldSaveAuthority.Fingerprint(_session)} name={loaded.DisplayName}");
                _slice4GapResumeSmokePhase = VoxelSlice4GapResumeSmokePhase.AwaitLoad;
                return;
            }
            case VoxelSlice4GapResumeSmokePhase.AwaitLoad:
                if (_dirty || _tracks.SolidCount == 0)
                    return;
                if (!_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
                    _tracks.Refresh(this, _session);
                if (!_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
                    throw new InvalidOperationException("Slice 4 gap resume render still covers the destroyed support cell.");
                GD.Print($"VOXEL_SMOKE_SLICE4_GAP_RESUME_RENDER solids={_tracks.SolidCount}");
                _slice4GapResumeSmokePhase = VoxelSlice4GapResumeSmokePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void AdvanceSlice4SupportResumeSmoke()
    {
        switch (_slice4SupportResumeSmokePhase)
        {
            case VoxelSlice4SupportResumeSmokePhase.Load:
            {
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Slice 4 support-loss resume is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Slice 4 support-loss resume could not load {saveId:D}: {loaded.Status}.");
                var restored = RequireSlice4CoastAssemblies(98, 88.875, 4, 0, frontRunning: true);
                if (restored.Carriage.HaltReason != TrainHaltReason.SupportLost ||
                    restored.Loco.HaltReason != TrainHaltReason.None ||
                    !restored.Loco.Running)
                    throw new InvalidOperationException(
                        $"Slice 4 support-loss resume halt is {restored.Carriage.HaltReason}/{restored.Loco.HaltReason}.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_SUPPORT_LOSS_RESUME_LOADED id={saveId:D} loco={restored.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={restored.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} loco_speed={restored.Loco.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage_speed={restored.Carriage.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={restored.Carriage.HaltReason} hash={WorldSaveAuthority.Fingerprint(_session)} name={loaded.DisplayName}");
                _slice4SupportResumeSmokePhase = VoxelSlice4SupportResumeSmokePhase.AwaitLoad;
                return;
            }
            case VoxelSlice4SupportResumeSmokePhase.AwaitLoad:
                if (_dirty || _tracks.SolidCount == 0 || _composites.VehicleCount != 2)
                    return;
                ValidateGeneratedMesh();
                ValidateCompositeVisual();
                _tracks.Refresh(this, _session);
                if (!_tracks.HasGapNear(StraightRailFixture.SupportLossCell))
                    throw new InvalidOperationException("Slice 4 support-loss resume render still covers the destroyed cell.");
                GD.Print($"VOXEL_SMOKE_SLICE4_SUPPORT_LOSS_RESUME_RENDER solids={_tracks.SolidCount}");
                _slice4SupportResumeSmokePhase = VoxelSlice4SupportResumeSmokePhase.Done;
                GetTree().Quit();
                return;
        }
    }

    private void AdvanceSlice4DestroyResumeSmoke()
    {
        switch (_slice4DestroyResumeSmokePhase)
        {
            case VoxelSlice4DestroyResumeSmokePhase.Load:
            {
                var rawId = System.Environment.GetEnvironmentVariable(VoxelSmokeSaveIdEnvironmentVariable);
                if (!Guid.TryParse(rawId, out var saveId))
                    throw new InvalidOperationException("Slice 4 full-destroy resume is missing TRAIN_VOXEL_SMOKE_SAVE_ID.");
                var loaded = _session.Load(saveId);
                if (loaded.Status != WorldSaveStatus.Loaded || loaded.SaveId != saveId)
                    throw new InvalidOperationException($"Slice 4 full-destroy resume could not load {saveId:D}: {loaded.Status}.");
                var firstId = _session.Rails.Segments[0].Id;
                var restored = RequireSlice4Assembly();
                if (restored.Reference.SegmentId != firstId ||
                    restored.HaltReason != TrainHaltReason.SupportLost ||
                    restored.Speed != 0 ||
                    restored.Running ||
                    Math.Abs(restored.Vehicles[0].FrontContact.X - 80) > 1e-8 ||
                    _session.Rails.TraversableWorldX(firstId, _session.World).Count != 0)
                    throw new InvalidOperationException(
                        $"Slice 4 full-destroy resume is {restored.Reference.SegmentId} halt {restored.HaltReason} front {restored.Vehicles[0].FrontContact.X}.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_FULL_DESTROY_RESUME_LOADED id={saveId:D} segment={firstId.Value} front={restored.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={restored.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={restored.HaltReason} hash={WorldSaveAuthority.Fingerprint(_session)} name={loaded.DisplayName}");
                _slice4DestroyResumeSmokePhase = VoxelSlice4DestroyResumeSmokePhase.AwaitLoad;
                return;
            }
            case VoxelSlice4DestroyResumeSmokePhase.AwaitLoad:
            {
                if (_dirty || _composites.VehicleCount != 2)
                    return;
                ValidateGeneratedMesh();
                ValidateCompositeVisual();
                var restoredId = _session.Rails.Segments[0].Id;
                var cells = Slice4RegisteredCells(restoredId);
                if (!_session.RepairRails(cells).Succeeded)
                    throw new InvalidOperationException("Slice 4 full-destroy resume could not repair the original segment.");
                if (_session.QueryTrainAssemblies().Single().HaltReason != TrainHaltReason.None)
                    throw new InvalidOperationException("Slice 4 full-destroy resume did not become startable after repair.");
                if (_session.StartTrain(RailHeading.AlongAlignment).Status != TrainMotionStatus.Started)
                    throw new InvalidOperationException("Slice 4 full-destroy resume could not start after repairing the original identity.");
                if (!_session.AdvanceSimulation(1).Succeeded)
                    throw new InvalidOperationException("Slice 4 full-destroy resume could not advance after the explicit start.");
                var moving = RequireSlice4Assembly();
                if (!moving.Running || moving.Speed <= 0 || moving.Vehicles[0].FrontContact.X <= 80)
                    throw new InvalidOperationException("Slice 4 full-destroy resume did not leave the parked pose after start.");
                GD.Print(
                    $"VOXEL_SMOKE_SLICE4_FULL_DESTROY_RESUME_STARTED front={moving.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={moving.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
                _slice4DestroyResumeSmokePhase = VoxelSlice4DestroyResumeSmokePhase.Done;
                GetTree().Quit();
                return;
            }
        }
    }

    private void RunSlice4FullDestroySmoke()
    {
        var parked = RequireSlice4Assembly();
        var firstId = _session.Rails.Segments[0].Id;
        if (parked.Reference.SegmentId != firstId || Math.Abs(parked.Vehicles[0].FrontContact.X - 80) > 1e-8)
            throw new InvalidOperationException("Slice 4 full-destroy smoke must start from the parked original identity.");
        var cells = Slice4RegisteredCells(firstId)
            .Where(cell => _session.Query(cell).IsOccupied)
            .ToArray();
        if (cells.Length == 0 ||
            !_session.Damage(cells.Select(cell => new VoxelDamageTarget(cell, 100)).ToArray()).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not fully destroy the referenced first segment.");
        var destroyed = RequireSlice4Assembly();
        if (destroyed.Reference.SegmentId != firstId ||
            destroyed.HaltReason != TrainHaltReason.SupportLost ||
            destroyed.Speed != 0 ||
            _session.Rails.TraversableWorldX(firstId, _session.World).Count != 0)
            throw new InvalidOperationException("Slice 4 full-destroy smoke lost the referenced identity or locating geometry.");
        var hash = WorldSaveAuthority.Fingerprint(_session);
        var saveId = SaveSlice4Detached("Slice 4 Full Destroy");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saveId);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Slice 4 full-destroy save was not created at {path}.");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_FULL_DESTROY_SAVE_WRITTEN id={saveId:D} bytes={new FileInfo(path).Length} path={path} segment={firstId.Value} front={destroyed.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={destroyed.HaltReason} hash={hash}");
    }

    private IReadOnlyList<WorldVoxelCoord> Slice4RegisteredCells(EntityId segmentId) =>
        _session.QueryRails().Occupancy
            .Where(item => item.Supports.Any(support => support.SegmentId == segmentId))
            .Select(item => item.Cell)
            .OrderBy(item => item.X)
            .ThenBy(item => item.Y)
            .ThenBy(item => item.Z)
            .ToArray();

    private Guid SaveSlice4Detached(string displayName)
    {
        var saved = _session.SaveAs(displayName);
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Slice 4 detached save '{displayName}' failed: {saved.Status}.");
        return saved.SaveId.Value;
    }

    private void RunSlice4SupportLossSmoke(string marker)
    {
        var before = RequireSlice4CoastAssemblies(98, 88.875, 4, 3.5, frontRunning: true);
        var graph = _session.AssemblyGraphGeneration;
        if (_session.Damage(StraightRailFixture.SupportLossCell, 100).Status != WorldCommandStatus.Removed)
            throw new InvalidOperationException("Slice 4 smoke could not destroy the support-loss cell.");
        var after = RequireSlice4CoastAssemblies(98, 88.875, 4, 0, frontRunning: true);
        if (after.Carriage.HaltReason != TrainHaltReason.SupportLost)
            throw new InvalidOperationException($"Slice 4 support-loss halt is {after.Carriage.HaltReason}.");
        if (after.Loco.HaltReason != TrainHaltReason.None || !after.Loco.Running)
            throw new InvalidOperationException("Slice 4 support-loss froze the unaffected locomotive.");
        if (after.Carriage.Vehicles[0].Transform != before.Carriage.Vehicles[0].Transform)
            throw new InvalidOperationException("Slice 4 support-loss moved the coasting carriage.");
        if (_session.AssemblyGraphGeneration <= graph)
            throw new InvalidOperationException("Slice 4 support-loss did not commit a new assembly graph generation.");
        _tracks.Refresh(this, _session);
        if (!_tracks.HasGapNear(StraightRailFixture.SupportLossCell))
            throw new InvalidOperationException("Slice 4 support-loss render still covers the destroyed cell.");
        GD.Print(
            $"{marker} loco={after.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={after.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} loco_speed={after.Loco.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage_speed={after.Carriage.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={after.Carriage.HaltReason} graph={_session.AssemblyGraphGeneration}");
    }

    private void RunSlice4GapStopSmoke(string marker)
    {
        if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
            !_session.AdvanceSimulation(300).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not reach step 300 before the gap stop.");
        if (_session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status != WorldCommandStatus.Removed)
            throw new InvalidOperationException("Slice 4 smoke could not open the fixture gap.");
        if (!_session.AdvanceSimulation(2000).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not advance to the gap boundary.");
        var stopped = RequireSlice4Assembly();
        if (stopped.Vehicles[0].FrontContact.X > 110 + 1e-8 ||
            stopped.Speed != 0 ||
            stopped.Running ||
            stopped.HaltReason != TrainHaltReason.ForwardGap)
            throw new InvalidOperationException(
                $"Slice 4 gap stop is {stopped.Vehicles[0].FrontContact.X} speed {stopped.Speed} halt {stopped.HaltReason}.");
        if (stopped.Vehicles.Any(vehicle =>
                vehicle.FrontContact.X > 110 + 1e-8 && vehicle.FrontContact.X < 111 - 1e-8 ||
                vehicle.RearContact.X > 110 + 1e-8 && vehicle.RearContact.X < 111 - 1e-8))
            throw new InvalidOperationException("Slice 4 gap stop placed a contact inside the gap.");
        _tracks.Refresh(this, _session);
        if (!_tracks.HasGapNear(StraightRailFixture.FirstSegmentDamageCell))
            throw new InvalidOperationException("Slice 4 gap stop render still covers the destroyed support cell.");
        GD.Print(
            $"{marker} front={stopped.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={stopped.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} halt={stopped.HaltReason} graph={_session.AssemblyGraphGeneration}");
    }

    private void RunSlice4CouplerCoastSmoke()
    {
        var assembled = RequireSlice4Assembly();
        var carriageId = assembled.VehicleIds[1];
        if (!_session.TryFollowVehicle(carriageId))
            throw new InvalidOperationException("Slice 4 smoke could not follow the carriage before coupler split.");
        ApplyRailPresentation();
        var beforeCamera = RequireSlice4CameraFrame();
        PrintSlice4CameraMarker("VOXEL_SMOKE_SLICE4_CAMERA_BEFORE_SPLIT", beforeCamera);
        var coupler = assembled.Connections[0];
        var damaged = _session.Damage(assembled.ActiveLocomotiveId!.Value, coupler.FromCell, 100);
        if (!damaged.Succeeded || damaged.Targets is null || damaged.Targets[0].PartId != coupler.FromPartId)
            throw new InvalidOperationException($"Slice 4 smoke could not destroy the locomotive rear coupler: {damaged.Status}.");
        var split = RequireSlice4CoastAssemblies(96, 87, 4, 4, frontRunning: true);
        var afterSplit = _session.QueryDisplayPoses(0.5);
        if (!afterSplit.IntervalEnded)
            throw new InvalidOperationException("Slice 4 coupler split kept the old display interval.");
        if (_session.FollowedVehicleId != carriageId)
            throw new InvalidOperationException("Slice 4 follow jumped after coupler split.");
        ApplyRailPresentation();
        var afterCamera = RequireSlice4CameraFrame();
        PrintSlice4CameraMarker("VOXEL_SMOKE_SLICE4_CAMERA_AFTER_SPLIT", afterCamera);
        RequireSlice4CameraContinuous(beforeCamera, afterCamera);
        var splitHud = HaltHud();
        if (!splitHud.Contains("滑行", StringComparison.Ordinal) ||
            !AlongRailDisplay.MotionStatusText(split.Carriage, paused: false).Equals("滑行", StringComparison.Ordinal))
            throw new InvalidOperationException($"Slice 4 split HUD is '{splitHud}'.");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_COAST_SPLIT assemblies={_session.QueryTrainAssemblies().Count} graph={_session.AssemblyGraphGeneration} loco={split.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={split.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} hud={splitHud}");
        PrintSlice4DisplayMarkers("VOXEL_SMOKE_SLICE4_DISPLAY_ENDED");
        if (!_session.AdvanceSimulation(30).Succeeded || _session.SimulationStep != 330)
            throw new InvalidOperationException("Slice 4 smoke could not coast 30 steps after coupler damage.");
        var coasted = RequireSlice4CoastAssemblies(98, 88.875, 4, 3.5, frontRunning: true);
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_COAST_STEP330 loco={coasted.Loco.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage={coasted.Carriage.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} loco_speed={coasted.Loco.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} carriage_speed={coasted.Carriage.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} hud={HaltHud()}");
    }

    private void RunSlice4CoastStopHudSmoke()
    {
        if (!_session.StartTrain(RailHeading.AlongAlignment).Succeeded ||
            !_session.AdvanceSimulation(300).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not rebuild for coast-to-stop HUD.");
        var carriageId = _session.QueryTrainAssemblies().Single().VehicleIds[1];
        BreakSlice4Coupler();
        var coasting = _session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriageId));
        if (!AlongRailDisplay.MotionStatusText(coasting, paused: false).Equals("滑行", StringComparison.Ordinal))
            throw new InvalidOperationException($"Slice 4 coast HUD is {AlongRailDisplay.MotionStatusText(coasting, paused: false)}.");
        if (!_session.AdvanceSimulation(240).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not coast to zero.");
        var stopped = _session.QueryTrainAssemblies().Single(item => item.VehicleIds.Contains(carriageId));
        if (Math.Abs(stopped.Speed) > 1e-8 || stopped.Running)
            throw new InvalidOperationException($"Slice 4 coast stop is speed {stopped.Speed} running {stopped.Running}.");
        var stopText = AlongRailDisplay.MotionStatusText(stopped, paused: false);
        if (!stopText.Equals("停止", StringComparison.Ordinal))
            throw new InvalidOperationException($"Slice 4 zero-speed HUD is {stopText}.");
        GD.Print($"VOXEL_SMOKE_SLICE4_COAST_STOP step={_session.SimulationStep} speed={stopped.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} hud={HaltHud()}");
    }

    private void BreakSlice4Coupler()
    {
        var assembled = RequireSlice4Assembly();
        var coupler = assembled.Connections[0];
        if (!_session.Damage(assembled.ActiveLocomotiveId!.Value, coupler.FromCell, 100).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not destroy the coupler for HUD coast-stop.");
    }

    private (TrainAssemblyQuery Loco, TrainAssemblyQuery Carriage) RequireSlice4CoastAssemblies(
        double? locoFront = null,
        double? carriageFront = null,
        double? locoSpeed = null,
        double? carriageSpeed = null,
        bool? frontRunning = null)
    {
        var assemblies = _session.QueryTrainAssemblies();
        if (assemblies.Count != 2)
            throw new InvalidOperationException($"Slice 4 smoke expected two assemblies, got {assemblies.Count}.");
        var loco = assemblies.Single(item => item.ActiveLocomotiveId is not null);
        var carriage = assemblies.Single(item => item.ActiveLocomotiveId is null);
        if (locoFront is { } expectedLoco && Math.Abs(loco.Vehicles[0].FrontContact.X - expectedLoco) > 1e-8)
            throw new InvalidOperationException($"Slice 4 loco front is {loco.Vehicles[0].FrontContact.X}.");
        if (carriageFront is { } expectedCarriage && Math.Abs(carriage.Vehicles[0].FrontContact.X - expectedCarriage) > 1e-8)
            throw new InvalidOperationException($"Slice 4 carriage front is {carriage.Vehicles[0].FrontContact.X}.");
        if (locoSpeed is { } expectedLocoSpeed && Math.Abs(loco.Speed - expectedLocoSpeed) > 1e-8)
            throw new InvalidOperationException($"Slice 4 loco speed is {loco.Speed}.");
        if (carriageSpeed is { } expectedCarriageSpeed && Math.Abs(carriage.Speed - expectedCarriageSpeed) > 1e-8)
            throw new InvalidOperationException($"Slice 4 carriage speed is {carriage.Speed}.");
        if (frontRunning is { } expectedRunning && loco.Running != expectedRunning)
            throw new InvalidOperationException($"Slice 4 loco running is {loco.Running}.");
        if (carriage.Running)
            throw new InvalidOperationException("Slice 4 coasting carriage is still running.");
        return (loco, carriage);
    }

    private void ContinueSlice4MotionToStop()
    {
        var remaining = 1800UL - _session.SimulationStep;
        if (remaining > 0 && !_session.AdvanceSimulation(remaining).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not continue to step 1800.");
        if (_session.SimulationStep != 1800)
            throw new InvalidOperationException($"Slice 4 smoke expected step 1800, got {_session.SimulationStep}.");
        var step1800 = RequireSlice4Assembly();
        if (Math.Abs(step1800.Vehicles[0].FrontContact.X - 196) > 1e-8)
            throw new InvalidOperationException($"Slice 4 step 1800 front is {step1800.Vehicles[0].FrontContact.X}.");
        if (step1800.Reference.SegmentId != _session.Rails.Segments[1].Id)
            throw new InvalidOperationException("Slice 4 step 1800 did not update the owning segment after the seam.");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_CROSS_SEAM segment={step1800.Reference.SegmentId.Value} distance={step1800.Reference.Distance.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} front={step1800.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
        GD.Print($"VOXEL_SMOKE_SLICE4_STEP1800 front={step1800.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");

        if (!_session.StopTrain().Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not stop after 1800 steps.");
        var stopped = RequireSlice4Assembly();
        if (stopped.Running || stopped.Speed != 0)
            throw new InvalidOperationException("Slice 4 stop did not zero speed.");
        GD.Print("VOXEL_SMOKE_SLICE4_STOPPED");

        var beforePause = _session.SimulationStep;
        if (!_session.PauseSimulation().Succeeded || _session.CollectHostSteps(1.0 / 30.0) != 0 || _session.SimulationStep != beforePause)
            throw new InvalidOperationException("Slice 4 pause still collected host steps.");
        if (!_session.ResumeSimulation().Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not resume.");
        GD.Print($"VOXEL_SMOKE_SLICE4_PAUSED step={beforePause}");
    }

    private void RunSlice4EndStopSmoke()
    {
        _session.Reset();
        if (!EnterSlice4Fixture() || !_session.StartTrain(RailHeading.AlongAlignment).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not rebuild the fixture for the end stop.");
        if (!_session.AdvanceSimulation(4000).Succeeded)
            throw new InvalidOperationException("Slice 4 smoke could not advance to the line end.");
        var ended = RequireSlice4Assembly();
        if (Math.Abs(ended.Vehicles[0].FrontContact.X - 272) > 1e-8 || ended.Vehicles[0].FrontContact.X > 272 + 1e-8 || ended.Speed != 0)
            throw new InvalidOperationException($"Slice 4 end stop is {ended.Vehicles[0].FrontContact.X} speed {ended.Speed}.");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_END_STOP front={ended.Vehicles[0].FrontContact.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)} speed={ended.Speed.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)}");
        ResetSlice4AfterMotion();
    }

    private void ResetSlice4AfterMotion()
    {
        _session.Reset();
        if (!EnterSlice4Fixture())
            throw new InvalidOperationException("Slice 4 smoke could not restore the static assembly after motion.");
    }

    private TrainAssemblyQuery RequireSlice4Assembly()
    {
        var assemblies = _session.QueryTrainAssemblies();
        if (assemblies.Count != 1)
            throw new InvalidOperationException($"Slice 4 smoke expected one assembly, got {assemblies.Count}.");
        return assemblies[0];
    }

    private bool EnterSlice4Fixture() =>
        _session.ConstructRails().Succeeded && _session.AssembleStaticTrain().Succeeded;

    private void CaptureSlice4UnrelatedTrackMeshes()
    {
        _slice4FarMeshId = _tracks.MeshFor(new ChunkCoord(1, 0, 0))?.GetInstanceId() ?? 0;
        _slice4ZNeighborMeshId = _tracks.MeshFor(new ChunkCoord(6, 0, 1))?.GetInstanceId() ?? 0;
        if (_slice4FarMeshId == 0 || _slice4ZNeighborMeshId == 0)
            throw new InvalidOperationException("Slice 4 smoke is missing the far or z-boundary track mesh.");
    }

    private void RequireSlice4TrainMarkers()
    {
        var assemblies = _session.QueryTrainAssemblies();
        if (assemblies.Count != 1)
            throw new InvalidOperationException($"Slice 4 smoke expected one train assembly, got {assemblies.Count}.");
        var assembly = assemblies[0];
        var loco = _session.QueryComposite(assembly.VehicleIds[0]);
        var carriage = _session.QueryComposite(assembly.VehicleIds[1]);
        if (loco is null || carriage is null)
            throw new InvalidOperationException("Slice 4 smoke assembly is missing a vehicle query.");
        if (loco.DefinitionId != StaticTrainFixture.LocomotiveId ||
            carriage.DefinitionId != StaticTrainFixture.CarriageId)
            throw new InvalidOperationException("Slice 4 smoke did not manufacture the formal locomotive and carriage.");
        if (loco.OperationalCondition != OperationalCondition.Operational ||
            carriage.OperationalCondition != OperationalCondition.Operational)
            throw new InvalidOperationException("Slice 4 smoke assembly is not operational.");
        if (assembly.Connections.Count != 1 ||
            assembly.Connections[0].FromPartId == default ||
            assembly.Connections[0].ToPartId == default)
            throw new InvalidOperationException("Slice 4 smoke assembly is missing a live coupler connection.");
        if (Math.Abs(assembly.Vehicles[0].FrontContact.X - 80) > 1e-8 ||
            Math.Abs(assembly.Vehicles[1].FrontContact.X - 71) > 1e-8)
            throw new InvalidOperationException("Slice 4 smoke contact positions are not the formal static poses.");
        GD.Print($"VOXEL_SMOKE_SLICE4_TRAIN_ASSEMBLED id={assembly.Id} vehicles=2");
        GD.Print($"VOXEL_SMOKE_SLICE4_TRAIN_LOCO id={loco.Id} front=80");
        GD.Print($"VOXEL_SMOKE_SLICE4_TRAIN_CARRIAGE id={carriage.Id} front=71");
        GD.Print(
            $"VOXEL_SMOKE_SLICE4_TRAIN_COUPLER from={assembly.Connections[0].FromPartId} to={assembly.Connections[0].ToPartId} cell={assembly.Connections[0].FromCell.X},{assembly.Connections[0].FromCell.Y},{assembly.Connections[0].FromCell.Z}");
        GD.Print($"VOXEL_SMOKE_SLICE4_TRAIN_QUALIFIED loco={loco.OperationalCondition} carriage={carriage.OperationalCondition}");
    }

    private void RequireSlice4Occupancy(WorldVoxelCoord cell, string marker)
    {
        if (!_session.Rails.Occupies(cell) || !_session.Query(cell).IsOccupied)
            throw new InvalidOperationException($"Slice 4 missing occupancy at {cell}.");
        GD.Print($"{marker} cell={cell.X},{cell.Y},{cell.Z}");
    }

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
        if (!_baselineEnabled)
            return;
        if (_slice4Baseline)
        {
            AdvanceSlice4Baseline();
            return;
        }

        if (_mutationStopwatch is null)
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
        if (_baselineStage == 2)
        {
            GD.Print(
                $"VOXEL_BASELINE_SLICE3_RUN run={_baselineRun + 1} voxels={_slice3Voxels} parts={_slice3Parts} mutation_ms={_slice3MutationMs:F3} structure_ms={_slice3StructureMs:F3} refresh_ms={_slice3RefreshMs:F3} save_ms={_slice3SaveMs:F3} load_ms={_slice3LoadMs:F3} file_bytes={_slice3Bytes} stable_seconds={_stableSeconds:F3} frame_avg_ms={average:F3} frame_max_ms={maximum:F3} memory_bytes={memory:F0}");
            _baselineRun++;
            if (_baselineRun == 3)
            {
                GD.Print("VOXEL_BASELINE_COMPLETE");
                _baselineEnabled = false;
                GetTree().Quit();
                return;
            }

            StartSlice3BaselineRun();
            return;
        }

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
            RecordSaveLoadBaseline();
            ReplaceSession(VoxelWorldIdentity.Slice3);
            PlaceCamera(GetChildren().OfType<Camera3D>().Single(), VoxelWorldIdentity.Slice3);
            Refresh();
            MeasureSlice3PersistBaseline(warmup: true);
            _baselineStage = 2;
            _baselineRun = 0;
            StartSlice3BaselineRun();
            return;
        }

        StartBaselineRun();
    }

    private void StampSlice4BaselineFrame()
    {
        var now = Time.GetTicksUsec();
        if (_lastFrameTicks != 0)
        {
            var frameMs = (now - _lastFrameTicks) / 1000.0;
            _frameMilliseconds.Add(frameMs);
            if (_slice4BaselinePhase == VoxelSlice4BaselinePhase.Running &&
                (_slice4BaselineMaxStep == 0 || frameMs >= _frameMilliseconds.Max()))
                _slice4BaselineMaxStep = _session.SimulationStep;
        }

        _lastFrameTicks = now;
    }

    private void AdvanceSlice4Baseline()
    {
        switch (_slice4BaselinePhase)
        {
            case VoxelSlice4BaselinePhase.Running:
            {
                var remaining = 1800UL - _session.SimulationStep;
                if (remaining > 0)
                {
                    var chunk = remaining > 30 ? 30UL : remaining;
                    var sim = Stopwatch.StartNew();
                    if (!_session.AdvanceSimulation(chunk).Succeeded)
                        throw new InvalidOperationException("Slice 4 baseline could not advance a running chunk.");
                    sim.Stop();
                    _slice4SimMs += sim.Elapsed.TotalMilliseconds;
                    ApplyRailPresentation();
                    return;
                }

                PrintSlice4BaselineKind("running");
                _slice4BaselinePhase = VoxelSlice4BaselinePhase.Damage;
                _frameMilliseconds.Clear();
                _lastFrameTicks = 0;
                return;
            }
            case VoxelSlice4BaselinePhase.Damage:
                MeasureSlice4BaselineDamage();
                _slice4BaselinePhase = VoxelSlice4BaselinePhase.DamageAwait;
                return;
            case VoxelSlice4BaselinePhase.DamageAwait:
                PrintSlice4BaselineKind("damage");
                _slice4BaselinePhase = VoxelSlice4BaselinePhase.SaveLoad;
                _frameMilliseconds.Clear();
                _lastFrameTicks = 0;
                return;
            case VoxelSlice4BaselinePhase.SaveLoad:
                MeasureSlice4BaselineSaveLoad();
                _slice4BaselinePhase = VoxelSlice4BaselinePhase.SaveLoadAwait;
                return;
            case VoxelSlice4BaselinePhase.SaveLoadAwait:
                PrintSlice4BaselineKind("saveload");
                if (!_slice4BaselineRecording)
                {
                    _slice4BaselineRecording = true;
                    _baselineRun = 0;
                    BeginSlice4BaselineCycle();
                    return;
                }

                _baselineRun++;
                if (_baselineRun == 3)
                {
                    GD.Print("VOXEL_BASELINE_COMPLETE");
                    _baselineEnabled = false;
                    GetTree().Quit();
                    return;
                }

                BeginSlice4BaselineCycle();
                return;
        }
    }

    private void BeginSlice4BaselineCycle()
    {
        _session.Reset();
        if (!EnterSlice4Fixture() || !_session.StartTrain(RailHeading.AlongAlignment).Succeeded)
            throw new InvalidOperationException("Slice 4 baseline could not assemble and start the fixture.");
        _slice4BaselinePhase = VoxelSlice4BaselinePhase.Running;
        _slice4SimMs = 0;
        _slice4BaselineMaxStep = 0;
        _frameMilliseconds.Clear();
        _lastFrameTicks = 0;
        ApplyRailPresentation();
    }

    private void MeasureSlice4BaselineDamage()
    {
        var mutation = Stopwatch.StartNew();
        if (_session.Damage(StraightRailFixture.FirstSegmentDamageCell, 100).Status != WorldCommandStatus.Removed)
            throw new InvalidOperationException("Slice 4 baseline could not apply the single gap damage.");
        mutation.Stop();
        var refresh = Stopwatch.StartNew();
        Refresh();
        ApplyRailPresentation();
        refresh.Stop();
        _slice4MutationMs = mutation.Elapsed.TotalMilliseconds;
        _slice4RefreshMs = refresh.Elapsed.TotalMilliseconds;
        _slice4EventWorkMs = _slice4MutationMs + _slice4RefreshMs;
        _slice4Voxels = _session.World.VoxelCount;
    }

    private void MeasureSlice4BaselineSaveLoad()
    {
        var eventClock = Stopwatch.StartNew();
        var saveClock = Stopwatch.StartNew();
        var saved = _session.Save(_slice4BaselineRecording ? "Slice 4 Baseline" : "Slice 4 Baseline Warmup");
        saveClock.Stop();
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Slice 4 baseline save failed: {saved.Status}.");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        _slice4Bytes = new FileInfo(path).Length;
        _session.Reset();
        if (!EnterSlice4Fixture())
            throw new InvalidOperationException("Slice 4 baseline could not rebuild before load.");
        var loadClock = Stopwatch.StartNew();
        var loaded = _session.Load(saved.SaveId.Value);
        loadClock.Stop();
        if (loaded.Status != WorldSaveStatus.Loaded)
            throw new InvalidOperationException($"Slice 4 baseline load failed: {loaded.Status}.");
        Refresh();
        ApplyRailPresentation();
        eventClock.Stop();
        _slice4SaveMs = saveClock.Elapsed.TotalMilliseconds;
        _slice4LoadMs = loadClock.Elapsed.TotalMilliseconds;
        _slice4EventWorkMs = eventClock.Elapsed.TotalMilliseconds;
        _slice4Voxels = _session.World.VoxelCount;
    }

    private void PrintSlice4BaselineKind(string kind)
    {
        if (!_slice4BaselineRecording)
            return;
        var memory = Performance.GetMonitor(Performance.Monitor.MemoryStatic);
        var run = _baselineRun + 1;
        switch (kind)
        {
            case "running":
            {
                var average = _frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Average();
                var maximum = _frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Max();
                GD.Print(
                    $"VOXEL_BASELINE_SLICE4_RUN kind=running run={run} voxels={_session.World.VoxelCount} sim_ms={_slice4SimMs:F3} frames={_frameMilliseconds.Count} frame_avg_ms={average:F3} frame_max_ms={maximum:F3} frame_max_step={_slice4BaselineMaxStep} memory_bytes={memory:F0}");
                return;
            }
            case "damage":
                GD.Print(
                    $"VOXEL_BASELINE_SLICE4_RUN kind=damage run={run} voxels={_slice4Voxels} mutation_ms={_slice4MutationMs:F3} refresh_ms={_slice4RefreshMs:F3} work_ms={_slice4EventWorkMs:F3} frame_interval_ms={(_frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Last()):F3} memory_bytes={memory:F0}");
                return;
            default:
                GD.Print(
                    $"VOXEL_BASELINE_SLICE4_RUN kind=saveload run={run} voxels={_slice4Voxels} save_ms={_slice4SaveMs:F3} load_ms={_slice4LoadMs:F3} file_bytes={_slice4Bytes} work_ms={_slice4EventWorkMs:F3} frame_interval_ms={(_frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Last()):F3} memory_bytes={memory:F0}");
                return;
        }
    }

    private void StartSlice3BaselineRun()
    {
        MeasureSlice3PersistBaseline(warmup: false);
        _mutationStopwatch = Stopwatch.StartNew();
        _frameMilliseconds.Clear();
        _stableSeconds = 0;
        _lastFrameTicks = Time.GetTicksUsec();
    }

    private void MeasureSlice3PersistBaseline(bool warmup)
    {
        _session.Reset();
        var mutation = Stopwatch.StartNew();
        var hollow = _session.Manufacture(HollowShellFixture.DefinitionId);
        if (!hollow.Succeeded)
            throw new InvalidOperationException($"Slice 3 baseline manufacture failed: {hollow.Status}.");
        var trace = HollowShellRefit.ApplySuccess(_session, hollow.VehicleId!.Value);
        HollowShellRefit.RunMoveReturnPairs(_session, hollow.VehicleId.Value, trace.P, 100);
        var bridge = _session.Manufacture(BridgeFixture.DefinitionId, PersistDropBridgePose);
        if (!bridge.Succeeded)
            throw new InvalidOperationException($"Slice 3 baseline bridge manufacture failed: {bridge.Status}.");
        mutation.Stop();
        var structure = Stopwatch.StartNew();
        if (!_session.Damage(new[]
            {
                new CompositeDamageTarget(bridge.VehicleId!.Value, BridgeFixture.BridgeCell, 100),
                new CompositeDamageTarget(bridge.VehicleId.Value, BridgeFixture.LeftCoreCell, 100)
            }).Succeeded)
            throw new InvalidOperationException("Slice 3 baseline bridge damage failed.");
        structure.Stop();
        var refresh = Stopwatch.StartNew();
        Refresh();
        refresh.Stop();
        var saveWatch = Stopwatch.StartNew();
        var saved = _session.Save("Slice 3 Baseline");
        saveWatch.Stop();
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Slice 3 baseline could not save: {saved.Status}.");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        var bytes = new FileInfo(path).Length;
        var beforeLoad = _session.CaptureAuthority();
        _session.Reset();
        var loadWatch = Stopwatch.StartNew();
        var loaded = _session.Load(saved.SaveId.Value);
        loadWatch.Stop();
        if (loaded.Status != WorldSaveStatus.Loaded || _session.CaptureAuthority() != beforeLoad)
            throw new InvalidOperationException($"Slice 3 baseline could not restore authority: {loaded.Status}.");
        if (warmup)
            return;
        _slice3MutationMs = mutation.Elapsed.TotalMilliseconds;
        _slice3StructureMs = structure.Elapsed.TotalMilliseconds;
        _slice3RefreshMs = refresh.Elapsed.TotalMilliseconds;
        _slice3SaveMs = saveWatch.Elapsed.TotalMilliseconds;
        _slice3LoadMs = loadWatch.Elapsed.TotalMilliseconds;
        _slice3Bytes = bytes;
        _slice3Voxels = _session.World.VoxelCount;
        _slice3Parts = _session.QueryComposites().Sum(item => item.Parts.Count);
    }

    private void RecordSaveLoadBaseline()
    {
        MeasureSaveLoad(warmup: true);
        for (var run = 1; run <= 3; run++)
            MeasureSaveLoad(warmup: false, run);
    }

    private void MeasureSaveLoad(bool warmup, int run = 0)
    {
        _session.Reset();
        WorldSaveFixture.ApplyFixedSequence(_session, includeLedge: true);
        var saveWatch = Stopwatch.StartNew();
        var saved = _session.Save("Baseline World");
        saveWatch.Stop();
        if (saved.Status != WorldSaveStatus.Saved || !saved.SaveId.HasValue)
            throw new InvalidOperationException($"Voxel baseline could not save: {saved.Status}.");
        var path = CreateSaveStore(GetNode<Main>("/root/Main")).PathFor(saved.SaveId.Value);
        var bytes = new FileInfo(path).Length;
        _session.Reset();
        var loadWatch = Stopwatch.StartNew();
        var loaded = _session.Load(saved.SaveId.Value);
        loadWatch.Stop();
        if (loaded.Status != WorldSaveStatus.Loaded || _session.World.VoxelCount != 1089)
            throw new InvalidOperationException($"Voxel baseline could not load: {loaded.Status}.");
        if (warmup)
            return;
        var memory = Performance.GetMonitor(Performance.Monitor.MemoryStatic);
        GD.Print(
            $"VOXEL_BASELINE_SAVE_RUN run={run} voxels={_session.World.VoxelCount} save_ms={saveWatch.Elapsed.TotalMilliseconds:F3} load_ms={loadWatch.Elapsed.TotalMilliseconds:F3} file_bytes={bytes} memory_bytes={memory:F0}");
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
