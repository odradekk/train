using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Saves;

internal sealed record SaveDetails(WorldSummary World, long Funds, int OwnedSettlements,
    ImmutableArray<PackageMetadata> Packages, ImmutableArray<string> Warnings);

internal sealed partial class SaveService
{
    private PendingDetails? _details;
    internal event Action? DetailsChanged;
    internal SaveDetails? Details { get; private set; }
    internal string? DetailsMessage { get; private set; }

    internal void Inspect(SaveEntry entry)
    {
        CancelDetails();
        if (_host.SessionOptions is not { } options)
        {
            DetailsMessage = "当前无法读取存档详情，请等待内容加载完成。";
            DetailsChanged?.Invoke();
            return;
        }
        var cancellation = new CancellationTokenSource();
        _details = new(cancellation, _host.BackgroundWork.Enqueue(() =>
        {
            var document = _store.Read(entry.Path);
            cancellation.Token.ThrowIfCancellationRequested();
            using var prepared = _host.PrepareRestore(document.CorePayload, document.ContentPayload, options);
            cancellation.Token.ThrowIfCancellationRequested();
            if (PreparationFailure(prepared) is { } failure)
                throw new SaveFormatException(failure.Code, "存档内容无法恢复，请检查所需内容包。");
            var view = prepared.CoreCandidate!.View;
            return new SaveDetails(view.World, view.Economy.Wallet.Value,
                view.World.Nodes.Count(node => node.Occupant is NodeOccupant.Settlement { Relation: SettlementRelation.PlayerOwned }),
                prepared.SavedPackages, SaveContentWarnings.Describe(prepared.ContentCandidate!.Report,
                    prepared.CoreCandidate.MissingReferences, prepared.SavedPackages));
        }, cancellation.Token));
        DetailsMessage = "正在读取详情…";
        DetailsChanged?.Invoke();
    }

    internal void CancelDetails()
    {
        if (_details is { } pending)
        {
            _details = null;
            pending.Cancellation.Cancel();
            _ = Observe(pending.Work, pending.Cancellation);
        }
        Details = null;
        DetailsMessage = null;
        DetailsChanged?.Invoke();
    }

    private void PumpDetails()
    {
        if (_details is not { } pending || !pending.Work.IsCompleted) return;
        _details = null;
        try { Details = pending.Work.GetAwaiter().GetResult(); DetailsMessage = null; }
        catch (SaveFormatException error) { DetailsMessage = $"无法读取详情：{error.Message}"; }
        catch (Exception) { DetailsMessage = "无法读取详情，请检查存档文件和所需内容包。"; }
        finally { pending.Cancellation.Dispose(); }
        DetailsChanged?.Invoke();
    }

    private sealed record PendingDetails(CancellationTokenSource Cancellation, Task<SaveDetails> Work);
}
