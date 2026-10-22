using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Sessions;
using Train.Core.Tests.Sessions;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Railway;

internal static class TrainDispatchProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var state = TrainDispatchRecoveryScenario.State(args[1]);
            var factory = CoreFactory.Create();
            ICoreSession session;
            if (args[0] == "resume")
            {
                using var candidate = Success(factory.PrepareRestore(Success(factory.DecodeState(await File.ReadAllTextAsync(args[3]))),
                    state.Content, new(CoreProcessDriver.Identity(), false, 1)));
                session = candidate.Activate();
            }
            else session = new CoreSession(state);
            using (session)
            {
                if (args[0] == "save")
                {
                    var capture = Success(session.Capture(CapturePurpose.Save));
                    await File.WriteAllTextAsync(args[3], capture.Encode());
                    var sample = new TrainProcessSample(capture.Position, capture.Encode(), capture.Checkpoint.CanonicalText,
                        capture.Checkpoint.Sha256, TrainDispatchRecoveryScenario.Query(session));
                    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new TrainProcessResult(Environment.ProcessId,
                        CoreProcessDriver.Identity(), [sample], [], [])));
                }
                else
                {
                    var suffix = TrainDispatchRecoveryScenario.Suffix(session, args[1]);
                    await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new TrainProcessResult(Environment.ProcessId,
                        CoreProcessDriver.Identity(), suffix.Samples, [], suffix.Events)));
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
