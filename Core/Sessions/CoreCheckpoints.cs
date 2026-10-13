using System.Text.Json;
using Train.Core.Contracts;

namespace Train.Core.Sessions;

internal static class CoreCheckpoints
{
    internal static CoreResult<CheckpointComparison> Compare(CoreCheckpoint expected, CoreCheckpoint actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var problem = CoreCapture.ValidateCheckpoint(expected) ?? CoreCapture.ValidateCheckpoint(actual);
        if (problem is not null)
            return new CoreResult<CheckpointComparison>.Failure([problem]);
        var difference = Difference(expected.Position, expected.CanonicalText, actual.CanonicalText);
        return new CoreResult<CheckpointComparison>.Success(difference is null
            ? new CheckpointComparison.Equal() : new CheckpointComparison.Different(difference));
    }

    internal static CheckpointDifference? Difference(SimulationPosition position, string expected, string actual)
    {
        using var left = JsonDocument.Parse(expected);
        using var right = JsonDocument.Parse(actual);
        return CompareFields(position, "", left.RootElement, right.RootElement);
    }

    private static CheckpointDifference? CompareFields(SimulationPosition position, string field,
        JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            var names = expected.EnumerateObject().Select(property => property.Name)
                .Concat(actual.EnumerateObject().Select(property => property.Name))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var name in names)
            {
                expected.TryGetProperty(name, out var left);
                actual.TryGetProperty(name, out var right);
                var difference = CompareFields(position, field + "/" + name.Replace("~", "~0").Replace("/", "~1"), left, right);
                if (difference is not null) return difference;
            }
            return null;
        }
        if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            for (var index = 0; index < Math.Max(expected.GetArrayLength(), actual.GetArrayLength()); index++)
            {
                var left = index < expected.GetArrayLength() ? expected[index] : default;
                var right = index < actual.GetArrayLength() ? actual[index] : default;
                var difference = CompareFields(position, field + "/" + index, left, right);
                if (difference is not null) return difference;
            }
            return null;
        }
        var leftText = Text(expected);
        var rightText = Text(actual);
        return expected.ValueKind == actual.ValueKind && StringComparer.Ordinal.Equals(leftText, rightText)
            ? null : new(position, field, leftText, rightText);
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined => "<missing>",
        JsonValueKind.String => value.GetString()!,
        _ => value.GetRawText()
    };

    // 不能用 record.Equals 比较回执中的 ImmutableArray；编码完整决策再逐字段比较。
    internal static string ReceiptText(CommandReceipt receipt) => JsonSerializer.Serialize(new
    {
        receipt.Number, receipt.Before, receipt.After,
        Decision = receipt.Decision switch
        {
            CommandDecision.Accepted accepted => (object)new
            {
                Kind = "accepted", Created = accepted.Created.Select(Subject), accepted.SettlementCopies
            },
            CommandDecision.Rejected rejected => new
            {
                Kind = "rejected", Problems = rejected.Problems.Select(problem => new
                {
                    problem.Code, problem.Field, Subject = Subject(problem.Subject), problem.RelatedIdentities
                })
            },
            CommandDecision.Faulted faulted => new
            {
                Kind = "faulted", Fault = new
                {
                    faulted.Fault.Code, faulted.Fault.Position, faulted.Fault.Operation,
                    faulted.Fault.Field, Subject = Subject(faulted.Fault.Subject)
                }
            },
            _ => throw new ArgumentException("命令决策无效。", nameof(receipt))
        }
    });

    private static object? Subject(WorldObjectId? subject) => subject is null ? null : new
    {
        Kind = subject.GetType().Name,
        Value = JsonSerializer.SerializeToElement(subject, subject.GetType())
    };
}
