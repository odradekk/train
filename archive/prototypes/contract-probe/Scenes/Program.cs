using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Content.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSamples;

// 仅用于 #292 的有限固定情景。接口的未知成员/顺序显式拒绝，不生成真实场景或业务规则。
static class Program
{
    static void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("ASSERT: " + name); Console.WriteLine("PASS " + name); }
    static void Unknown(Action call, string name) { try { call(); } catch (InvalidOperationException e) when (e.Message.StartsWith("UNCONFIGURED:")) { Check(true, name); return; } throw new InvalidOperationException("ASSERT: " + name); }
    static readonly SceneRay Ray = new(-Vector3.UnitZ, Vector3.UnitZ, 10);
    static readonly EditViewOptions Options = new(new(GridAxis.Y, 0), null, SceneDebugLayer.None);
    static SceneWorldSnapshot Snapshot()
    {
        var core = ScriptedInput.Create<ICoreReadView>();
        ((ScriptedInput)(object)core).Answers["get_Position"] = _ => new SimulationPosition(1, 0);
        ((ScriptedInput)(object)core).Answers["get_InputIdentity"] = _ => new CoreInputId("fixed-probe-input");
        return new(core, Content());
    }
    static IContentView Content()
    {
        var content = ScriptedInput.Create<IContentView>();
        ((ScriptedInput)(object)content).Answers["get_Generation"] = _ => new ContentGeneration(1);
        ((ScriptedInput)(object)content).Answers["get_Core"] = _ => new CoreContentProjection(new("fixed-probe-input"), [], [], [], [], [], [], [], [], [], [], [], [], [], []);
        return content;
    }
    static DesignDraft Draft(string text) => new(new(text, "fixed"), new DesignSpecification.SceneModel(null),
        new([new(new("probe:voxel"),VoxelOrientation.PxPy,null,null)], [new(new(0,0,0), 0)], [new NestedDesignItem.AssemblyCopy(new(1,0,0), VoxelOrientation.PxPy,
            new(new("nested", ""), new DesignSpecification.Assembly(null), new([], [], [])))]));
    // 固定单格与一个空嵌套副本，不推导占格、编号、哈希或合法性。
    static DesignEvaluation Evaluation(DesignDraft draft) => new DesignEvaluation.Complete(new("probe-design"), new("probe-composition"), draft, null,
        new(new("probe-design"), new(new(0,0,0),new(0,0,0)), null, true, null, null,
            [new(new(0,0,0),new("probe:voxel"),VoxelOrientation.PxPy,null,null,new(1),new DesignElementPath.Cell([],0),[],new(0,0,0),VoxelOrientation.PxPy,false)],
            [new(new(1),new DesignElementPath.Cell([],0),new ComputedValue<ImmutableArray<GridPosition>>.Known([new(0,0,0)]),false,false)],
            [new([0],new(1,0,0),VoxelOrientation.PxPy,new NestedObjectDetails.Assembly(null))], []), false,
            [new("probe_fixture_invalid",ContentDiagnosticSeverity.Error,"fixture",null,null,"layout.nestedItems[0]",null,"固定空嵌套副本仅用于不合法草稿交接，不是生产规则诊断。")]);
    static void Show(WorldConsumer consumer) => consumer.Update(Snapshot(), [], 0, Vector3.Zero, SceneDebugLayer.None);
    static void World()
    {
        var session = new WorldScript(); var factory = new ScenesFactoryScript { World = session };
        Check(WorldConsumer.Open(factory,session.Binding,Snapshot().Core,Snapshot().Content) is SceneResult<IWorldSceneSession>.Success, "world.consumer.opens-via-contract-factory");
        var display = new Display(); var consumer = new WorldConsumer(session, display);
        var first = session.Frame(1); session.Enqueue(first, null);
        ImmutableArray<EventEnvelope> committedEvents = [
            new(new(1,0),0,new CoreEvent.TrainArrived(new(1),new(1))),
            new(new(1,0),1,new CoreEvent.TrainActivityChanged(new(1),TrainActivity.Running,TrainActivity.Stopped,new TrainPosition.Node(new(1)),[]))];
        session.ConfigureEvents(committedEvents);
        consumer.Update(Snapshot(),committedEvents,0,Vector3.Zero,SceneDebugLayer.None);
        Check(session.ObservedEvents == committedEvents && ReferenceEquals(session.ObservedEvents[0],committedEvents[0])
            && ReferenceEquals(session.ObservedEvents[1],committedEvents[1]) && session.ObservedEvents[0].Position==new SimulationPosition(1,0)
            && session.ObservedEvents[0].Ordinal==0 && session.ObservedEvents[1].Ordinal==1 && display.Current==first,
            "world.consumer.nonempty-committed-events-forwarded-with-identity-position-order-before-frame");
        Check(consumer.PickCurrent(Ray, _=>{}) is SceneResult<PickSubject>.Success && session.Resolved == first.Update.Frame, "world.consumer.pick-uses-displayed-frame");
        var wrong = session.Frame(2, session.Frame(99).Update.Frame); var replacement = session.Frame(3);
        session.Enqueue(wrong, first); session.Enqueue(replacement, null); Show(consumer);
        Check(display.Current == replacement && session.FullReplacementRequests == 2, "world.consumer.base-mismatch-requests-full-replacement");
        display.FailNext = true; var notInstalled = session.Frame(4, replacement.Update.Frame); session.Enqueue(notInstalled, replacement); Show(consumer);
        consumer.PickCurrent(Ray, _=>{});
        Check(display.Current == replacement && session.Resolved == replacement.Update.Frame, "world.consumer.install-failure-keeps-interaction-frame");
        var failed = session.Frame(5, replacement.Update.Frame, SceneReadiness.Failed); session.Enqueue(failed, replacement); Show(consumer); consumer.PickCurrent(Ray, _=>{});
        Check(display.Current == replacement && session.Resolved == replacement.Update.Frame, "world.consumer.success-with-failed-readiness-is-not-published");
        var oldJson = JsonSerializer.Serialize(first.Update); var oldHit = (ScenePick.Hit)first.Pick(Ray);
        session.RequestView(new WorldSceneTarget.Location(new WorldLocation.Node(new NodeId(2))));
        Check(session.Resolve(oldHit.Value) is SceneResult<PickSubject>.Failure { Problems: var problems } && problems[0].Code == "stale_request", "world.producer.old-request-hit-rejected");
        consumer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Check(JsonSerializer.Serialize(first.Update) == oldJson && first.Update.Upserts[0].Visuals[0] is SceneVisual.Geometry { Mesh.Vertices.Length: 3 }, "world.producer.retained-nested-frame-survives-close");
        Unknown(()=>new WorldScript().Present(new(0,null,SceneDebugLayer.None),null), "world.fake.rejects-unconfigured-present");
    }
    static void Edit()
    {
        var edit = new EditScript(Draft("one")); var opening = new ScenesFactoryScript { Editor = edit };
        Check(EditorConsumer.Open(opening,Content(),Evaluation(Draft("one"))) is SceneResult<IDesignEditSession>.Success, "edit.consumer.opens-via-contract-factory");
        var consumer = new EditorConsumer(edit, new Display());
        var snapshot = edit.Capture(); var operation = new EditOperation.RemoveSelection();
        edit.ConfigurePreview(operation, canApply:true, locked:false, Draft("two"));
        Check(consumer.Edit(operation, _=>true, _=>{}) is SceneResult<EditState>.Success && edit.Read().Snapshot.Stamp.Revision.Value == 2 && edit.Read().IsDirty, "edit.consumer.success-commits-one-configured-revision");
        var changed = edit.Read(); edit.ConfigurePreview(operation, canApply:false, locked:true, null);
        Check(consumer.Edit(operation, _=>true, _=>{}) is null && JsonSerializer.Serialize(edit.Read()) == JsonSerializer.Serialize(changed), "edit.consumer.locked-preview-does-not-apply-or-change-history");
        Check(edit.Apply(edit.LastPreview!,true) is SceneResult<EditState>.Failure { Problems: var locked } && locked[0].Code == "edit_locked", "edit.producer.direct-locked-apply-rejected");
        Check(!edit.AcceptEvaluation(snapshot.Stamp, Evaluation(snapshot.Draft)) && edit.Read().CurrentEvaluation is null, "edit.producer.late-evaluation-does-not-cover-current-revision");
        var current = edit.Capture(); var clone = Draft("two");
        Check(clone != current.Draft && edit.AcceptEvaluation(current.Stamp, Evaluation(clone)), "edit.producer.equal-deep-collections-accepted-despite-record-inequality");
        var accepted = edit.Read().CurrentEvaluation;
        var nestedChanged = clone with { Layout = clone.Layout with { NestedItems = [((NestedDesignItem.AssemblyCopy)clone.Layout.NestedItems[0]) with { Design = ((NestedDesignItem.AssemblyCopy)clone.Layout.NestedItems[0]).Design with { Display = new("different child", "") } }] } };
        Check(!edit.AcceptEvaluation(current.Stamp, Evaluation(nestedChanged)) && edit.Read().CurrentEvaluation == accepted, "edit.producer.nested-draft-mismatch-is-not-hidden-by-equal-root-fields");
        Check(!edit.AcceptEvaluation(current.Stamp, Evaluation(Draft("other"))) && edit.Read().CurrentEvaluation == accepted, "edit.producer.same-stamp-wrong-draft-does-not-change-diagnostics");
        Check(edit.MarkSaved(snapshot) && edit.Read().IsDirty, "edit.producer.old-save-completion-keeps-new-edits-dirty");
        Check(edit.MarkSaved(current) && !edit.Read().IsDirty, "edit.producer.matching-save-clears-only-captured-draft");
        var wrongSession = current with { Stamp = current.Stamp with { Session = new(Guid.NewGuid()) } };
        Check(!edit.MarkSaved(wrongSession), "edit.producer.foreign-session-save-rejected");
        var content = Content(); var factory = ScriptedInput.Create<IContentFactory>();
        var script = (ScriptedInput)(object)factory;
        script.Answers["EvaluateDesign"] = args => { if(!ReferenceEquals(args![0],content) || !ReferenceEquals(args[1],edit.Capture().Draft)) throw new InvalidOperationException("UNCONFIGURED: evaluate input"); var captured = (DesignDraft)args[1]!; edit.ChangeTo(Draft("three")); return new ContentResult<DesignEvaluation>.Success(Evaluation(captured)); };
        Check(!consumer.Evaluate(factory,content,_=>{}) && edit.Read().CurrentEvaluation is null, "edit.consumer.evaluate-retains-original-stamp-on-late-return");
        var saveContent = Content(); var saveFactory = ScriptedInput.Create<IContentFactory>();
        var saveScript = (ScriptedInput)(object)saveFactory; var disposedCandidate = false;
        var draftId = new DraftId(Guid.Parse("00000000-0000-0000-0000-000000000050"));
        saveScript.Answers["PrepareEdit"] = args => {
            if(!ReferenceEquals(args![0],saveContent) || args[1] is not ContentEdit.SaveLocalDraft { Draft.Id: var id } request || id!=draftId)
                throw new InvalidOperationException("UNCONFIGURED: draft save input");
            if(request.Draft.Revision!=edit.Capture().Stamp.Revision || request.Draft.Payload is not DraftPayload.Design)
                throw new InvalidOperationException("UNCONFIGURED: draft save revision/payload");
            var candidate = ScriptedInput.Create<IContentCandidate>(); var candidateScript=(ScriptedInput)(object)candidate;
            candidateScript.Answers["get_Report"]=_=>new ContentPreparationReport([],[],[],[]);
            candidateScript.Answers["Dispose"]=_=>{disposedCandidate=true;return null;};
            return new ContentResult<IContentCandidate>.Success(candidate);
        };
        Check(!consumer.SaveDraft(saveFactory,saveContent,draftId,new DraftTarget.LocalBlueprint(null),new CommitScript(()=>false),_=>{}) && disposedCandidate && edit.Read().IsDirty,
            "edit.consumer.failed-save-does-not-mark-saved-and-releases-candidate");
        disposedCandidate=false;
        Check(consumer.SaveDraft(saveFactory,saveContent,draftId,new DraftTarget.LocalBlueprint(null),new CommitScript(()=>{edit.ChangeTo(Draft("four"));return true;}),_=>{}) && disposedCandidate && edit.Read().IsDirty,
            "edit.consumer.save-completion-keeps-captured-stamp-and-subsequent-edits-dirty");
        Unknown(()=>saveFactory.EncodeDraft(new(draftId,new(1),new DraftTarget.LocalBlueprint(null),new DraftPayload.Design(Draft("unknown")))), "content.fake.rejects-unconfigured-member");
        edit.Dispose();
        Check(!edit.AcceptEvaluation(edit.CaptureForEvidence.Stamp,Evaluation(edit.CaptureForEvidence.Draft)) && !edit.MarkSaved(edit.CaptureForEvidence), "edit.producer.closed-session-late-completions-rejected");
        Check(JsonSerializer.Serialize(snapshot.Draft) == JsonSerializer.Serialize(Draft("one")) && ((NestedDesignItem.AssemblyCopy)snapshot.Draft.Layout.NestedItems[0]).Design.Display.Name == "nested", "edit.producer.retained-recursive-draft-unchanged");
        Unknown(()=>new EditScript(Draft("unused")).Preview(operation), "edit.fake.rejects-unconfigured-preview");
    }
    static void Preview()
    {
        var content = Content(); var input = (ScriptedInput)(object)content; var design = Evaluation(Draft("icon"));
        var good = new GoodId.Voxel(new("probe:voxel"));
        input.Answers["FindGood"] = args => Equals(args![0],good) ? new ContentLookup<GoodPresentation>.Found(new(good, new(new("icon",""), null, null), new("probe-design"),false)) : throw new InvalidOperationException("UNCONFIGURED: good lookup");
        input.Answers["FindDesign"] = args => args![0] is DesignHash { Value: "probe-design" } ? new ContentLookup<DesignEvaluation>.Found(design) : throw new InvalidOperationException("UNCONFIGURED: design lookup");
        Unknown(()=>content.FindGood(new GoodId.Voxel(new("unconfigured:voxel"))), "content.fake.rejects-unconfigured-lookup-input");
        var factory = new ScenesFactoryScript(); var display = new Display();
        DesignIconConsumer.Present(factory,content,good,display,_=>{});
        Check(factory.PreviewCalls == 1 && factory.EditorCalls == 0 && factory.Preview!.Closed && display.Current != null, "preview.consumer.read-only-icon-never-enters-editor-and-disposes");
    }
    static void LateCompletion(bool broken)
    {
        // 相同持久 UUID 与不同绑定：只模拟宿主的候选接入资格，不伪称真实 Godot 宿主已测试。
        var uuid = Guid.Parse("00000000-0000-0000-0000-000000000292");
        var a = new Pending(uuid,new(Guid.Parse("00000000-0000-0000-0000-000000000001")));
        var b = new Pending(uuid,new(Guid.Parse("00000000-0000-0000-0000-000000000002")));
        a.Cancelled = true; Pending active = b; var dirty = true;
        void Complete(Pending candidate) { if (broken || (!candidate.Cancelled && candidate.Binding == active.Binding)) { active=candidate; dirty=false; } else candidate.Dispose(); }
        Complete(a);
        Check(active.Binding == b.Binding && active.SaveUuid == b.SaveUuid && dirty && a.Released && !b.Released, "async.coordinator.cancelled-A-late-return-cannot-publish-or-clear-B-and-releases-A");
    }
    public static int Main(string[] args)
    {
        try { World(); Edit(); Preview(); LateCompletion(args.Contains("--allow-stale")); foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name!.StartsWith("Train.") || a.GetName().Name=="ScenesProbe")) Console.WriteLine("ASSEMBLY " + assembly.GetName().FullName + " | " + assembly.Location); Console.WriteLine("SCENES_PROBE_OK"); return 0; }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

// 固定成员的公开接口代理，不读取任何生产源码/内部字段；没有通配/默认成功。
public class ScriptedInput : DispatchProxy
{
    public Dictionary<string,Func<object?[]?,object?>> Answers { get; } = [];
    public static T Create<T>() where T: class => DispatchProxy.Create<T,ScriptedInput>();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method != null && Answers.TryGetValue(method.Name,out var answer)
        ? answer(args) : throw new InvalidOperationException("UNCONFIGURED: public input " + method?.Name);
}
sealed class Pending(Guid saveUuid, SceneBinding binding): IDisposable
{
    public Guid SaveUuid {get;}=saveUuid; public SceneBinding Binding {get;}=binding; public bool Cancelled; public bool Released; public void Dispose()=>Released=true;
}
sealed class Display : ISceneDisplay
{
    public ISceneFrame? Current; public bool FailNext;
    public bool TryDisplay(ISceneFrame frame) { if(FailNext) { FailNext=false; return false; } if(frame.Update.Base != null && frame.Update.Base != Current?.Update.Frame) return false; Current=frame; return true; }
}
sealed class FixedFrame(SceneFrameStamp stamp, SceneFrameStamp? basis=null, SceneReadiness readiness=SceneReadiness.Ready): ISceneFrame
{
    public ScenePoint Origin => new(0,0,0); public ScenePanBounds? NodePanBounds => null; public SceneReadiness Readiness=>readiness; public double SimulationSeconds=>0;
    public ImmutableArray<SceneProblem> Diagnostics=>[]; public ImmutableArray<SceneDebugLine> DebugLines=>[];
    public SceneUpdate Update {get;}=new(basis,stamp,Vector3.Zero,
        [new(new(1),null,new(Vector3.Zero,Quaternion.Identity),null,
            [new SceneVisual.Geometry(new(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
                [new(Vector3.Zero,Vector3.UnitZ,Vector2.Zero,Vector2.Zero,ExposedFaceEdges.None,0,0),
                 new(Vector3.UnitX,Vector3.UnitZ,Vector2.Zero,Vector2.Zero,ExposedFaceEdges.None,0,0),
                 new(Vector3.UnitY,Vector3.UnitZ,Vector2.Zero,Vector2.Zero,ExposedFaceEdges.None,0,0)],[0,1,2]),new(false,1,0,false))],[],[])],[],new([],[]),[]);
    public ScenePick Pick(SceneRay ray)=>ray == new SceneRay(-Vector3.UnitZ,Vector3.UnitZ,10) ? new ScenePick.Hit(new(stamp,new(1),new PickSubject.WorldObject(new WorldObjectId.Node(new(1)),null,null),new(0,0,0),GridFace.NegativeZ,Vector3.Zero,-Vector3.UnitZ,1,[0])) : throw new InvalidOperationException("UNCONFIGURED: frame ray");
    public Lookup<ScenePose> FindPose(WorldObjectId subject)=>throw new InvalidOperationException("UNCONFIGURED: frame pose");
    public Lookup<WorldLocation> Locate(Vector3 point)=>throw new InvalidOperationException("UNCONFIGURED: frame locate");
    public Lookup<double> TerrainHeight(double x,double z)=>throw new InvalidOperationException("UNCONFIGURED: frame terrain");
}
sealed class WorldScript: IWorldSceneSession
{
    readonly Queue<(FixedFrame Frame,ISceneFrame? Basis)> frames=[]; bool closed; SceneRequestId request=new(1);
    public SceneSessionId Id {get;}=new(Guid.Parse("00000000-0000-0000-0000-000000000010")); public SceneBinding Binding {get;}=new(Guid.Parse("00000000-0000-0000-0000-000000000020"));
    public SceneFrameStamp? Resolved; public int FullReplacementRequests;
    ImmutableArray<EventEnvelope>? expectedEvents; public ImmutableArray<EventEnvelope> ObservedEvents {get;private set;}=[];
    public void ConfigureEvents(ImmutableArray<EventEnvelope> events){if(expectedEvents is not null)throw new InvalidOperationException("UNCONFIGURED: pending event sequence"); expectedEvents=events;}
    public FixedFrame Frame(ulong revision,SceneFrameStamp? basis=null,SceneReadiness readiness=SceneReadiness.Ready)=>new(new(Id,request,new(1),revision),basis,readiness);
    public void Enqueue(FixedFrame frame,ISceneFrame? basis)=>frames.Enqueue((frame,basis));
    public SceneResult<SceneRequestId> RequestView(WorldSceneTarget target) { ObjectDisposedException.ThrowIf(closed,this); if(target is not WorldSceneTarget.Location { Value: WorldLocation.Node { Id.Value:2 } }) throw new InvalidOperationException("UNCONFIGURED: world target"); request=new(request.Value+1);return new SceneResult<SceneRequestId>.Success(request); }
    public SceneResult<SimulationPosition> Observe(SceneWorldSnapshot snapshot,ImmutableArray<EventEnvelope> events) { ObjectDisposedException.ThrowIf(closed,this); if((expectedEvents is { } configured ? events!=configured : events.Length!=0) || snapshot.Core.Position!=new SimulationPosition(1,0) || snapshot.Content.Generation != new ContentGeneration(1) || snapshot.Core.InputIdentity != snapshot.Content.Core.Identity) throw new InvalidOperationException("UNCONFIGURED: world observation"); ObservedEvents=events;expectedEvents=null; return new SceneResult<SimulationPosition>.Success(snapshot.Core.Position); }
    public SceneResult<ISceneFrame> Present(SceneFrameRequest request,ISceneFrame? displayedFrame) { if(frames.Count==0)throw new InvalidOperationException("UNCONFIGURED: world present"); ObjectDisposedException.ThrowIf(closed,this); var configured=frames.Dequeue(); if(!ReferenceEquals(configured.Basis,displayedFrame))throw new InvalidOperationException("UNCONFIGURED: present basis"); if(displayedFrame==null)FullReplacementRequests++; return new SceneResult<ISceneFrame>.Success(configured.Frame); }
    public SceneResult<PickSubject> Resolve(SceneHit hit) { if(closed||hit.Frame.Session!=Id||hit.Frame.Request!=request) return new SceneResult<PickSubject>.Failure([Problems.At("stale_request")]); Resolved=hit.Frame;return new SceneResult<PickSubject>.Success(hit.Subject); }
    public ValueTask DisposeAsync(){closed=true;return ValueTask.CompletedTask;}
}
static class Problems { public static SceneProblem At(string code)=>new(code,SceneStage.Edit,null,null,null,null,"fixed probe scenario"); }
sealed record Preview(EditStamp Basis,bool CanApply,bool Locked): IEditPreview
{
    public int CoveredCells=>1;public int AffectedCells=>CanApply?1:0;public int SkippedCells=>CanApply?0:1;public bool OccupancyComplete=>true;public bool RequiresConfirmation=>false;
    public ImmutableArray<EditConflict> Conflicts=>Locked?[new(null,new(1),Problems.At("edit_locked"))]:[]; public ImmutableArray<GridPosition> HighlightCells=>[new(0,0,0)];
}
sealed class EditScript(DesignDraft initial): IDesignEditSession
{
    EditSnapshot current=new(new(new(Guid.Parse("00000000-0000-0000-0000-000000000030")),new(1),new(1)),initial); bool closed; bool dirty; DesignEvaluation? evaluation;
    EditOperation? configuredOperation; DesignDraft? nextDraft; public Preview? LastPreview;
    public EditSessionId Id=>current.Stamp.Session;public ContentGeneration Content=>current.Stamp.Content;public EditSnapshot CaptureForEvidence=>current;
    public EditState Read(){ObjectDisposedException.ThrowIf(closed,this);return new(current,dirty,current.Stamp.Revision.Value>1,false,null,[],[],evaluation,[]);}
    public EditSnapshot Capture(){ObjectDisposedException.ThrowIf(closed,this);return current;}
    public void ConfigurePreview(EditOperation operation,bool canApply,bool locked,DesignDraft? next){configuredOperation=operation;LastPreview=new(current.Stamp,canApply,locked);nextDraft=next;}
    public void ChangeTo(DesignDraft draft){current=new(current.Stamp with{Revision=new(current.Stamp.Revision.Value+1)},draft);dirty=true;evaluation=null;}
    public SceneResult<IEditPreview> Preview(EditOperation operation){ObjectDisposedException.ThrowIf(closed,this);if(!ReferenceEquals(configuredOperation,operation))throw new InvalidOperationException("UNCONFIGURED: edit preview");return new SceneResult<IEditPreview>.Success(LastPreview!);}
    public SceneResult<EditState> Apply(IEditPreview preview,bool confirmed){ObjectDisposedException.ThrowIf(closed,this);if(!ReferenceEquals(preview,LastPreview))throw new InvalidOperationException("UNCONFIGURED: edit apply");if(preview.Basis!=current.Stamp)return new SceneResult<EditState>.Failure([Problems.At("stale_preview")]);if(LastPreview!.Locked)return new SceneResult<EditState>.Failure([Problems.At("edit_locked")]);if(!preview.CanApply||nextDraft==null)throw new InvalidOperationException("UNCONFIGURED: unappliable edit");ChangeTo(nextDraft);return new SceneResult<EditState>.Success(Read());}
    // 只比较本原型支持的 SceneModel/Assembly 固定草稿，不重算业务数据。
    static bool Same(DesignDraft a,DesignDraft b)
    {
        if(a.Specification is not (DesignSpecification.SceneModel or DesignSpecification.Assembly) || b.Specification is not (DesignSpecification.SceneModel or DesignSpecification.Assembly))
            throw new InvalidOperationException("UNCONFIGURED: draft specification comparison");
        return a.Display==b.Display && a.Specification==b.Specification && a.Layout.Palette.SequenceEqual(b.Layout.Palette)
            && a.Layout.Cells.SequenceEqual(b.Layout.Cells) && a.Layout.NestedItems.Length==b.Layout.NestedItems.Length
            && a.Layout.NestedItems.Zip(b.Layout.NestedItems).All(pair=>SameNested(pair.First,pair.Second));
    }
    static bool SameNested(NestedDesignItem a,NestedDesignItem b) => (a,b) switch
    {
        (NestedDesignItem.AssemblyCopy x,NestedDesignItem.AssemblyCopy y)=>x.At==y.At && x.Orientation==y.Orientation && Same(x.Design,y.Design),
        (NestedDesignItem.SceneModelCopy x,NestedDesignItem.SceneModelCopy y)=>x.At==y.At && x.Orientation==y.Orientation && Same(x.Design,y.Design),
        (NestedDesignItem.FunctionalCompositeReference x,NestedDesignItem.FunctionalCompositeReference y)=>x==y,
        _=>false
    };
    public bool AcceptEvaluation(EditStamp requested,DesignEvaluation result){if(closed||requested!=current.Stamp)return false;var draft=result switch{DesignEvaluation.Complete c=>c.Definition,DesignEvaluation.Partial p=>p.Definition,_=>null};if(draft==null||!Same(draft,current.Draft))return false;evaluation=result;return true;}
    public bool MarkSaved(EditSnapshot saved){if(closed||saved.Stamp.Session!=Id||saved.Stamp.Content!=Content||saved.Stamp.Revision.Value>current.Stamp.Revision.Value)return false;dirty=!Same(saved.Draft,current.Draft);return true;}
    public SceneResult<DesignDraft> CaptureObject(EditElementId e)=>throw new InvalidOperationException("UNCONFIGURED: capture object"); public SceneResult<EditState> Select(EditSelection s)=>throw new InvalidOperationException("UNCONFIGURED: select"); public SceneResult<EditState> Enter(EditElementId? e)=>throw new InvalidOperationException("UNCONFIGURED: enter");public SceneResult<EditState> Undo()=>throw new InvalidOperationException("UNCONFIGURED: undo");public SceneResult<EditState> Redo()=>throw new InvalidOperationException("UNCONFIGURED: redo");public SceneResult<ISceneFrame> Present(EditViewOptions o,ISceneFrame? f)=>throw new InvalidOperationException("UNCONFIGURED: edit frame");public SceneResult<EditElementId> Locate(EditStamp s,DesignElementPath p)=>throw new InvalidOperationException("UNCONFIGURED: locate"); public void Dispose()=>closed=true;
}
sealed class CommitScript(Func<bool> result): IContentCommit { public bool TryCommit(IContentCandidate candidate)=>result(); }
sealed class ReadOnlyPreview: IDesignPreview
{
    public bool Closed; public ContentGeneration Content=>new(1);
    public SceneResult<ISceneFrame> Present(){ObjectDisposedException.ThrowIf(Closed,this);return new SceneResult<ISceneFrame>.Success(new FixedFrame(new(new(Guid.Parse("00000000-0000-0000-0000-000000000040")),new(1),new(1),1)));}
    public void Dispose()=>Closed=true;
}
sealed class ScenesFactoryScript: IScenesFactory
{
    public int PreviewCalls;public int EditorCalls;public ReadOnlyPreview? Preview;public WorldScript? World;public EditScript? Editor;
    public SceneResult<IDesignPreview> PreviewDesign(IContentView content,DesignEvaluation initial){if(content.Generation!=new ContentGeneration(1)||initial is not DesignEvaluation.Complete { Definition.Display.Name: "icon" })throw new InvalidOperationException("UNCONFIGURED: preview design");PreviewCalls++;Preview=new();return new SceneResult<IDesignPreview>.Success(Preview);}
    public SceneResult<IDesignEditSession> OpenEditor(IContentView content,DesignEvaluation initial){EditorCalls++;if(Editor!=null && content.Generation==Editor.Content && initial is DesignEvaluation.Complete { Definition.Display.Name: "one" }) return new SceneResult<IDesignEditSession>.Success(Editor);throw new InvalidOperationException("UNCONFIGURED: open editor");}
    public SceneResult<IWorldSceneSession> OpenWorld(SceneBinding b,SceneWorldSnapshot s){if(World!=null && b==World.Binding && s.Core.InputIdentity==new CoreInputId("fixed-probe-input") && s.Core.InputIdentity==s.Content.Core.Identity && s.Content.Generation==new ContentGeneration(1))return new SceneResult<IWorldSceneSession>.Success(World);throw new InvalidOperationException("UNCONFIGURED: open world");}public SceneResult<ITemplatePreview> PreviewTemplate(IContentView c,SettlementTemplateDraft d,EditRevision r)=>throw new InvalidOperationException("UNCONFIGURED: preview template");
}
