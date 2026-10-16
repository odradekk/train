using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;

namespace Train.Scenes.Tests;

[TestClass]
public sealed class WorldSceneReadThreadTests
{
    [TestMethod]
    public void Initial_world_objects_read_instances_on_the_presenting_thread()
    {
        var caller = Environment.CurrentManagedThreadId;
        var backgroundReads = 0;
        var instance = new InstanceId.Vehicle(new(1));
        var location = new WorldLocation.Node(new(1));
        var content = SceneFixtures.Content(SceneFixtures.Evaluation(
            SceneFixtures.Projection([SceneFixtures.Cell(new(0, 0, 0))])));
        var core = new CountingCoreReadView(new FixedCoreReadView
        {
            Position = new(0, 0),
            InputIdentity = content.Core.Identity,
            Combats = new Dictionary<CombatId, Lookup<CombatView>>
            { [new(1)] = new Lookup<CombatView>.Found(new(new(1), location, 0, [])) },
            Instances = new Dictionary<InstanceId, Lookup<InstanceView>>
            { [instance] = new Lookup<InstanceView>.Found(new(instance, SceneFixtures.Design, new("composition"),
                location, SettlementRelation.PlayerOwned, new InstanceDetails.Vehicle(SizeClass.Small, null),
                InstanceLock.None, new(10, 10), [], [])) }
        })
        {
            InstanceRead = () =>
            {
                if (Environment.CurrentManagedThreadId != caller) Interlocked.Increment(ref backgroundReads);
            }
        };
        var session = new WorldSceneSession(new(Guid.NewGuid()), new(core, content),
            (_, _, _) => new([], [new(instance, SceneFixtures.Identity)], [], [], []), new(), new());
        try
        {
            Assert.IsInstanceOfType<SceneResult<SceneRequestId>.Success>(
                session.RequestView(new WorldSceneTarget.Combat(new(1))));
            session.PendingBuild!.GetAwaiter().GetResult();
            var frame = Present();
            while (session.PendingObjectBuilds.Length > 0)
            {
                Task.WhenAll(session.PendingObjectBuilds).GetAwaiter().GetResult();
                frame = Present();
            }
            Assert.AreEqual(SceneReadiness.Ready, frame.Readiness);
            Assert.IsTrue(frame.Update.Upserts.Any(item => item.Subject == new WorldObjectId.Instance(instance)));
            Assert.IsTrue(core.InstanceQueries.ContainsKey(instance), "必须实际读取初始世界对象。");
            Assert.AreEqual(0, backgroundReads, "初始对象必须在协调线程取得实例，再交给后台构建。");
        }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        ISceneFrame Present()
        {
            var result = session.Present(new(0, null, SceneDebugLayer.None), null);
            Assert.IsInstanceOfType<SceneResult<ISceneFrame>.Success>(result,
                (result as SceneResult<ISceneFrame>.Faulted)?.Problem.Detail);
            return ((SceneResult<ISceneFrame>.Success)result).Value;
        }
    }
}
