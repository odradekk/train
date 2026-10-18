using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal partial class EconomyPanels
{
    private readonly Dictionary<JobId, Button> _servicePauseButtons = [];
    internal void Observe(HostBinding binding, ICoreReadView view, ImmutableArray<EventEnvelope> events)
    {
        if (_serviceObservedBinding != binding)
        { _serviceObservedBinding = binding; _serviceCompletion = null; }
        if (_services is null || _settlement is null || _settlementTarget?.Binding != binding) return;
        foreach (var envelope in events)
        {
            if (envelope.Event is CoreEvent.InstanceDesignChanged { Instance: InstanceId.Vehicle vehicle } changed &&
                vehicle.Id == _serviceSelectedVehicle && _serviceCompletion is null)
                _serviceResult.Text = view.FindInstance(vehicle) is Lookup<InstanceView>.Found found
                    ? $"核心已确认车辆 {vehicle.Id.Value} 设计变化 · 当前设计 {found.Value.Design.Value}" : $"已不存在：车辆 {vehicle.Id.Value}";
            if (envelope.Event is not CoreEvent.ConstructionCompleted completed) continue;
            var createdHere = completed.CreatedInstance is { } instance && view.FindInstance(instance) is
                Lookup<InstanceView>.Found { Value.Location: WorldLocation.Node node } && node.Id == _settlement.Node;
            if (createdHere || _settlement.CommissionJobs.Contains(completed.Job) || _settlement.OwnedJobs.Contains(completed.Job))
                _serviceCompletion = completed;
        }
    }

    private void RefreshServiceJobs()
    {
        var ids = _settlement!.CommissionJobs.Concat(_settlement.OwnedJobs).Distinct().OrderBy(id => id.Value).ToArray();
        foreach (var id in _serviceJobRows.Keys.Except(ids).ToArray()) { RemoveControl(_serviceJobRows[id]); _serviceJobRows.Remove(id); }
        foreach (var id in _servicePauseButtons.Keys.Except(ids).ToArray()) { RemoveControl(_servicePauseButtons[id]); _servicePauseButtons.Remove(id); }
        foreach (var id in ids)
        {
            if (!_serviceJobRows.TryGetValue(id, out var label)) _serviceJobRows.Add(id, label = Text(_serviceJobs, $"ServiceJob_{id.Value}", ""));
            label.Text = $"{(_settlement.OwnedJobs.Contains(id) ? "自有任务" : "NPC 委托")} {id.Value}：";
            if (_view.FindJob(id) is Lookup<JobView>.Found { Value: JobView.Construction job })
            {
                var value = job.Value;
                label.Text += $"建筑 {value.Facility.Building.Value} · {ServiceKind(value.Kind)} · {ServiceState(value.State)}\n工作 {value.ProgressWork:0.###}/{value.FrozenTotalWork} · {value.Progress:P1} · 实际 {value.ActualWorkPerSecond:0.###} 工作/秒\n限制：{string.Join("、", value.Limits.Select(ServiceLimit))}\n投入：{ServiceGoods(value.InvestedGoods)}\n" + Problems(value.Blockers);
                if (_settlement.OwnedJobs.Contains(id) && value.Kind == ConstructionKind.Deconstruction)
                {
                    if (!_servicePauseButtons.TryGetValue(id, out var button))
                    {
                        button = UiStyle.Button($"PauseServiceJob_{id.Value}", "", () => ToggleServicePause(id));
                        _serviceJobs.AddChild(button); _servicePauseButtons.Add(id, button);
                    }
                    button.Text = value.State == ConstructionState.Paused ? "恢复拆解" : "暂停拆解";
                    button.Disabled = !CanAct;
                }
            }
            else label.Text += "已不在当前任务查询中；未收到完工事件。";
        }
        if (_serviceCompletion is { } completed)
        {
            _serviceResult.Text = $"核心已确认任务 {completed.Job.Value} 完工 · {ServiceKind(completed.Kind)}";
            if (completed.CreatedInstance is InstanceId.Vehicle vehicle)
                _serviceResult.Text += _view.FindInstance(vehicle) is Lookup<InstanceView>.Found
                    ? $" · 新车 {vehicle.Id.Value}，可编组" : $" · 新车 {vehicle.Id.Value} 已不存在";
            else if (completed.Target is ConstructionTarget.Existing { Instance: InstanceId.Vehicle original })
                _serviceResult.Text += _view.FindInstance(original) is Lookup<InstanceView>.Found found
                    ? $"\n原车辆 {original.Id.Value} · 当前设计 {found.Value.Design.Value}" : $"\n原车辆 {original.Id.Value} 已不存在";
            _serviceResult.Text += $"\n返还：{ServiceGoods(completed.ReturnedGoods)}";
        }
    }

    private void ToggleServicePause(JobId id)
    {
        if (!ReadCurrent() || !CanAct || _view.FindSettlement(_settlementId) is not Lookup<SettlementView>.Found settlement ||
            !settlement.Value.OwnedJobs.Contains(id) || _view.FindJob(id) is not Lookup<JobView>.Found
            { Value: JobView.Construction { Value.Kind: ConstructionKind.Deconstruction } job }) return;
        var accepted = _submit(_settlementTarget, new CoreCommand.SetDeconstructionPaused(id, job.Value.State != ConstructionState.Paused));
        _serviceCompletion = null;
        _serviceResult.Text = accepted is null ? "暂停／恢复未受理或被拒绝，请查看错误。" : "拆解暂停状态已更新。";
        ResetQuote(ref _serviceQuote, _serviceDescription, "任务状态已变化，请重新获取报价。");
        if (ReadCurrent()) { _settlementRevision = null; Refresh(_binding, _view, _content); }
    }

}
