using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Train.Core.Contracts;
using Train.Core.Sessions;

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
            ICoreSession session;
            if (mode == "resume")
            {
                var factory = CoreFactory.Create();
                var saved = Success(factory.DecodeState(await File.ReadAllTextAsync(payloadPath)));
                using var candidate = Success(factory.PrepareRestore(saved, Content('B'), new(runtime, false, 0)));
                session = candidate.Activate();
            }
            else
                session = new CoreSession(new CoreState(new(7, 2), 10, new(20), Content('A')));

            using (session)
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
                    Sample();
                }

                Sample();
                if (mode != "resume")
                {
                    Publish('B');
                    Advance(1);
                    Publish('A');
                    Publish('B');
                }
                if (mode == "save")
                    await File.WriteAllTextAsync(payloadPath, samples[^1].Payload, new UTF8Encoding(false));
                else
                {
                    Publish('C');
                    Publish('A');
                    Advance(2);
                }
                var result = new CoreProcessResult(Environment.ProcessId, runtime, samples.ToArray(), receipts.ToArray());
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result), new UTF8Encoding(false));
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static T Success<T>(CoreResult<T> result) where T : notnull => result is CoreResult<T>.Success success
        ? success.Value : throw new InvalidOperationException($"核心调用失败：{result}");

    private static CoreContentProjection Content(char identity) =>
        new(new(new string(identity, 64)), [], [], [], [], [], [], [], [], [], [], [], [], [], []);

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
