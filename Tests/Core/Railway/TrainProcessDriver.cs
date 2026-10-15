using System.Text;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Tests.Sessions;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

internal static class TrainProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var boundary = args[1];
            var output = args[2];
            var statePath = args[3];
            var runtime = CoreProcessDriver.Identity();
            ICoreSession session;
            if (mode == "resume")
            {
                var factory = CoreFactory.Create();
                using var candidate = Success(factory.PrepareRestore(Success(factory.DecodeState(await File.ReadAllTextAsync(statePath))),
                    TrainRecoveryFixture.ContentFor(boundary), new(runtime, false, 32)));
                session = candidate.Activate();
            }
            else session = TrainRecoveryFixture.New(runtime, boundary);
            using (session)
            {
                var events = new List<string>();
                if (mode != "resume")
                {
                    TrainRecoveryFixture.Configure(session, events);
                    TrainRecoveryFixture.Boundary(session, boundary, events);
                }
                var samples = new List<TrainProcessSample>();
                void Sample()
                {
                    var capture = Success(session.Capture(CapturePurpose.Save));
                    samples.Add(new(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText,
                        capture.Checkpoint.Sha256, TrainRecoveryFixture.Query(session)));
                }
                Sample();
                var prefixEvents = events.ToArray();
                events.Clear();
                if (mode == "save") await File.WriteAllTextAsync(statePath, samples[0].Payload, new UTF8Encoding(false));
                else
                {
                    if (boundary == "waiting")
                        TrainRecoveryFixture.Submit(session, new CoreCommand.PublishContent(TrainRecoveryFixture.Content), events);
                    // 所有分支从同一个保存边界执行同一后缀，含停车后恢复运行。
                    if (boundary != "pending-stop")
                        TrainRecoveryFixture.Submit(session, new CoreCommand.StopTrain(TrainRecoveryFixture.Train(session).Id), events);
                    Sample();
                    for (var step = 0; TrainRecoveryFixture.Train(session).Activity != TrainActivity.Stopped; step++)
                    {
                        if (step == 1000) throw new InvalidOperationException("列车未完成停车。");
                        TrainRecoveryFixture.Advance(session, events); Sample();
                    }
                    TrainRecoveryFixture.Submit(session, new CoreCommand.StartTrain(TrainRecoveryFixture.Train(session).Id), events);
                    Sample();
                    for (var step = 0; TrainRecoveryFixture.Train(session).NextStopIndex is not null; step++)
                    {
                        if (step == 2000) throw new InvalidOperationException("列车未完成多站计划。");
                        TrainRecoveryFixture.Advance(session, events); Sample();
                    }
                }
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new TrainProcessResult(Environment.ProcessId,
                    runtime, samples.ToArray(), prefixEvents, events.ToArray())), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
}

internal sealed record TrainProcessSample(SimulationPosition Position, string Payload, string CanonicalText,
    string Sha256, string Query);
internal sealed record TrainProcessResult(int ProcessId, RuntimeIdentity Runtime, TrainProcessSample[] Samples,
    string[] PrefixEvents, string[] Events);
