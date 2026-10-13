using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Tests.World;
using static Train.Core.Tests.World.WorldFixture;

namespace Train.Core.Tests.Sessions;

internal static class CoreProcessDriver
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var mode = args[0];
            var output = args[1];
            var payloadPath = args[2];
            var runtime = Identity();
            if (mode == "replay")
                return await Replay(output, payloadPath, runtime);
            ICoreSession session;
            if (mode == "resume")
            {
                var factory = CoreFactory.Create();
                var saved = Success(factory.DecodeState(await File.ReadAllTextAsync(payloadPath)));
                using var candidate = Success(factory.PrepareRestore(saved, Content('E'), new(runtime, false, 1)));
                session = candidate.Activate();
            }
            else
            {
                using var candidate = Success(CoreFactory.Create().PrepareNew(WorldFixture.MultiInput,
                    Content('A'), new(runtime, false, 1)));
                session = candidate.Activate();
            }

            try
            {
                var samples = new List<CoreProcessSample>();
                var receipts = new List<CoreProcessReceipt>();
                void Sample()
                {
                    var capture = Success(session.Capture(CapturePurpose.Save));
                    samples.Add(new(capture.Position, capture.Revision, capture.InputIdentity.Value,
                        capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256));
                }
                void Publish(char identity)
                {
                    if (session.Submit(new CoreCommand.PublishContent(Content(identity)), CommandSource.AutomationTest)
                        is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted } completed)
                        throw new InvalidOperationException("内容发布未成功。");
                    receipts.Add(new(completed.Receipt.Number, completed.Receipt.Before, completed.Receipt.After));
                    Sample();
                }
                void Advance(uint steps)
                {
                    var result = session.Advance(steps);
                    if (result.CompletedSteps != steps || result.Fault is not null)
                        throw new InvalidOperationException("推进未成功。");
                    if (mode == "every-step")
                    {
                        var capture = Success(session.Capture(CapturePurpose.Save));
                        File.WriteAllText(payloadPath, capture.Encode(), new UTF8Encoding(false));
                        var factory = CoreFactory.Create();
                        var saved = Success(factory.DecodeState(File.ReadAllText(payloadPath)));
                        using var candidate = Success(factory.PrepareRestore(saved,
                            Content(capture.InputIdentity.Value[0]), new(runtime, false, 1)));
                        session.Dispose();
                        session = candidate.Activate();
                    }
                    Sample();
                }

                Sample();
                if (mode != "resume")
                {
                    Publish('B');
                    Advance(1);
                    Publish('A');
                    Publish('B');
                    Publish('E');
                }
                if (mode == "save")
                    await File.WriteAllTextAsync(payloadPath, samples[^1].Payload, new UTF8Encoding(false));
                else
                {
                    Publish('C');
                    Publish('A');
                    if (mode is "record" or "every-step" or "stage-continuous")
                    {
                        Advance(1);
                        Advance(1);
                    }
                    else Advance(2);
                }
                if (mode == "record")
                {
                    var window = session.ReadRecording();
                    var recording = new CoreDiskRecording(runtime, window.Starts[0].State.Encode(),
                        window.Commands.Select(command => new CoreDiskCommand(command.Source, command.CanonicalPayload,
                            command.Result.Number, command.Result.Before, command.Result.After,
                            (CommandDecision.Accepted)command.Result.Decision, command.InputBefore, command.InputAfter)).ToArray(),
                        window.Checkpoints.ToArray(), window.EndPosition, samples.Select(sample => sample.Position).ToArray());
                    await File.WriteAllTextAsync(payloadPath, JsonSerializer.Serialize(recording), new UTF8Encoding(false));
                }
                var result = new CoreProcessResult(Environment.ProcessId, runtime, samples.ToArray(), receipts.ToArray());
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result), new UTF8Encoding(false));
            }
            finally
            {
                session.Dispose();
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<int> Replay(string output, string payloadPath, RuntimeIdentity runtime)
    {
        var recording = JsonSerializer.Deserialize<CoreDiskRecording>(await File.ReadAllTextAsync(payloadPath))!;
        var input = new CoreReplayInput(1, recording.Runtime, recording.InitialState,
            recording.Commands.Select(command => new RecordedCommand(command.Source, command.Payload,
                new(command.Number, command.Before, command.After, command.Decision),
                command.InputBefore, command.InputAfter)).ToImmutableArray(),
            recording.Checkpoints.ToImmutableArray(), recording.EndPosition, null);
        using var replay = Success(CoreFactory.Create().PrepareReplay(input, runtime,
            new[] { 'A', 'B', 'C', 'E' }.Select(Content).ToImmutableArray()));
        var samples = new List<CoreProcessSample>();
        foreach (var position in recording.Positions)
        {
            var result = replay.Advance(uint.MaxValue, position);
            if (result.Stop != ReplayStop.RequestedPosition || result.Difference is not null || result.Fault is not null)
                throw new InvalidOperationException($"回放失败：{result}");
            var capture = replay.CaptureBoundary();
            samples.Add(new(capture.Position, capture.Revision, capture.InputIdentity.Value,
                capture.Encode(), capture.Checkpoint.CanonicalText, capture.Checkpoint.Sha256));
        }
        var end = replay.Advance(1);
        if (end.Stop != ReplayStop.EndOfRecording || end.Difference is not null || end.Fault is not null)
            throw new InvalidOperationException($"回放未到录制末尾：{end}");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new CoreProcessResult(
            Environment.ProcessId, runtime, samples.ToArray(), [])), new UTF8Encoding(false));
        return 0;
    }

    private static CoreContentProjection Content(char identity)
    {
        var content = WorldFixture.MultiContent with { Identity = new(new string(identity, 64)) };
        return identity != 'E' ? content : content with
        {
            Designs = content.Designs.Select(d => d with
            {
                MaxStructureDurability = 40, MassTons = 2,
                Parts = d.Parts.Select(p => p with { MaxDurability = 30,
                    Capabilities = d.Design == Station ? [] : p.Capabilities }).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    internal static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static RuntimeIdentity Identity()
    {
        var directory = Path.GetDirectoryName(typeof(CoreProcessDriver).Assembly.Location)!;
        var manifest = new StringBuilder();
        foreach (var name in new[] { "Train.Core.Contracts.dll", "Train.Core.dll", "Train.Core.Tests.dll" })
            manifest.Append(name).Append(':').Append(Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(directory, name))))).Append('\n');
        return new(Sha256(manifest.ToString()), "core-session-v1", RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription);
    }
}

internal sealed record CoreProcessSample(SimulationPosition Position, CoreRevision Revision,
    string InputIdentity, string Payload, string CanonicalText, string Sha256);
internal sealed record CoreProcessReceipt(ulong Number, SimulationPosition Before, SimulationPosition After);
internal sealed record CoreProcessResult(int ProcessId, RuntimeIdentity Identity,
    CoreProcessSample[] Samples, CoreProcessReceipt[] Receipts);

internal sealed record CoreDiskCommand(CommandSource Source, string Payload, ulong Number,
    SimulationPosition Before, SimulationPosition After, CommandDecision.Accepted Decision,
    CoreInputId InputBefore, CoreInputId InputAfter);
internal sealed record CoreDiskRecording(RuntimeIdentity Runtime, string InitialState,
    CoreDiskCommand[] Commands, CoreCheckpoint[] Checkpoints, SimulationPosition EndPosition,
    SimulationPosition[] Positions);
