// Train.Core.Tests.Simulation 对照实际宿主驱动在不同时间安排下的权威结果。
// 同一资源领域和命令脚本经过暂停、倍速与预算入口，在相同模拟位置比较检查点。

using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Simulation;

namespace Train.Core.Tests.Simulation;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:simulation")]
public sealed class SimulationDriverDeterminismTests
{
    [TestMethod]
    public void Frame_cadence_speed_and_budget_do_not_change_same_position_checkpoints()
    {
        var profiles = new[]
        {
            (Speed: SimulationSpeed.One, Delta: 1.0 / 60, Budget: 0.005, Cost: 0.0),
            (Speed: SimulationSpeed.Two, Delta: 1.0 / 30, Budget: 0.020, Cost: 0.003),
            (Speed: SimulationSpeed.Four, Delta: 1.0 / 60, Budget: 0.005, Cost: 0.003),
            (Speed: SimulationSpeed.Eight, Delta: 1.0 / 120, Budget: 0.005, Cost: 0.006)
        };
        var reference = RunScript(profiles[0]);
        foreach (var profile in profiles.Skip(1))
        {
            var actual = RunScript(profile);
            CollectionAssert.AreEqual(reference.Checkpoints, actual.Checkpoints, $"档位={profile.Speed}");
            CollectionAssert.AreEqual(reference.CommandChain, actual.CommandChain, $"档位={profile.Speed}");
        }
    }

    /// <summary>执行固定位置的命令脚本；可控时间让每帧分别完成一、四、二、一步，并覆盖整步额度放弃。</summary>
    private static (AuthoritativeCheckpoint[] Checkpoints, string[] CommandChain) RunScript(
        (SimulationSpeed Speed, double Delta, double Budget, double Cost) profile)
    {
        var realSeconds = 0.0;
        var domain = new ResourceCalculations();
        var calculations = new ScriptedCalculations
        {
            Step = (before, context) =>
            {
                realSeconds += profile.Cost;
                return domain.ComputeStep(before, context);
            },
            Command = domain.ComputeCommand
        };
        var initial = new ResourceState
        {
            Stock = 20,
            EntityIds = ImmutableSortedSet.Create(10UL, 20UL),
            NextEntityId = 21,
            Demands = [new ResourceDemand(10, 1, 0, 3), new ResourceDemand(20, 2, 0, 4)]
        };
        Assert.IsNull(domain.CheckInitialState(initial));
        var session = new SimulationSession(initial, calculations, new ResourceCommandCodec());
        var driver = new SimulationDriver(session, new ResourceStateCodec(), () => realSeconds);
        var checkpoints = new List<AuthoritativeCheckpoint>();
        driver.SetSpeed(profile.Speed);
        driver.SetPaused(true);
        Submit(new CreateEntityCommand());
        Submit(new NoChangeCommand());
        Capture(new SimulationPosition(0, 2));

        foreach (var target in new[] { 4UL, 8UL, 12UL })
        {
            driver.SetPaused(false);
            for (var frame = 0; driver.GetStatus().State.CompletedSteps < target && frame < 100; frame++)
            {
                realSeconds += profile.Delta;
                var result = driver.AdvanceFrame(profile.Delta, profile.Budget);
                Assert.IsNull(result.StateAfter.Fault);
            }
            Assert.AreEqual(target, driver.GetStatus().State.CompletedSteps);
            driver.SetPaused(true);
            var pauseSeconds = 0.5 * (int)profile.Speed;
            realSeconds += pauseSeconds;
            Assert.AreEqual(0UL, driver.AdvanceFrame(pauseSeconds, profile.Budget).StepsAdvanced);
            if (target == 4)
            {
                Submit(new DeleteEntityCommand(21));
                Submit(new DeleteEntityCommand(21), CommandOutcome.Rejected);
                Submit(new CreateEntityCommand());
                Capture(new SimulationPosition(4, 3));
            }
            else if (target == 8)
            {
                Submit(new NoChangeCommand());
                Capture(new SimulationPosition(8, 1));
            }
            else
            {
                Capture(new SimulationPosition(12, 0));
            }
        }

        // 三步消耗全部 20 单位库存；六条命令中拒绝也占用身份，已销毁的 21 不再分配。
        var final = (ResourceState)driver.GetStatus().State;
        Assert.AreEqual(0UL, final.Stock);
        Assert.AreEqual(9UL, final.Holdings[10]);
        Assert.AreEqual(11UL, final.Holdings[20]);
        Assert.AreEqual(18UL, final.TransactionCount);
        Assert.AreEqual(7UL, final.NextCommandId);
        Assert.AreEqual(23UL, final.NextEntityId);
        CollectionAssert.AreEqual(new[] { 10UL, 20UL, 22UL }, final.EntityIds.ToArray());
        return (checkpoints.ToArray(), session.GetCommandLog().Entries.Select(item => item.ChainSha256).ToArray());

        void Submit(CoreCommand command, CommandOutcome outcome = CommandOutcome.Executed)
        {
            var result = (SubmissionResult.Completed)driver.SubmitCommand(command, CommandSource.Console);
            Assert.AreEqual(outcome, result.Execution.Outcome);
        }

        void Capture(SimulationPosition position)
        {
            var checkpoint = driver.CreateCheckpoint();
            Assert.AreEqual(position, checkpoint.Position);
            checkpoints.Add(checkpoint);
        }
    }
}
