using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed partial class SettlementVehiclesPage
{
    private ConfirmationDialog _buyDialog = null!, _sellDialog = null!, _dismantleDialog = null!;
    private ImmutableArray<QuotedVehicle> _pending = [];

    private void BuildConfirmations(Node parent)
    {
        _buyDialog = Dialog("BuyVehiclesConfirmation", "购买现车", "购买并编组");
        var buyOnly = _buyDialog.AddButton("只购买"); buyOnly.Name = "BuyOnly";
        buyOnly.Pressed += () => { _buyDialog.Hide(); Execute(_pending, false); };
        _buyDialog.Confirmed += () => Execute(_pending, true);
        _sellDialog = Dialog("SellVehiclesConfirmation", "出售车辆", "出售");
        _sellDialog.Confirmed += () => Execute(_pending, false);
        _dismantleDialog = Dialog("DismantleVehiclesConfirmation", "拆解车辆", "拆解");
        _dismantleDialog.Confirmed += () => Execute(_pending, false);
        ConfirmationDialog Dialog(string name, string title, string confirm)
        {
            var dialog = new ConfirmationDialog { Name = name, Title = title, OkButtonText = confirm,
                CancelButtonText = "取消", Exclusive = true };
            parent.AddChild(dialog); dialog.Canceled += () => _pending = [];
            return dialog;
        }
    }

    private void Confirm(ImmutableArray<QuotedVehicle> batch)
    {
        if (!canAct() || batch.IsEmpty) return;
        _pending = batch;
        var command = batch[0].Command;
        var dialog = command is CoreCommand.BuyVehicle ? _buyDialog : command is CoreCommand.SellVehicle ? _sellDialog : _dismantleDialog;
        dialog.Title = $"{Verb(command)} {batch.Length} 辆车？";
        var amount = batch.Sum(item => item.Amount);
        dialog.DialogText = string.Join("、", batch.Select(item => item.Name)) + (command switch
        {
            CoreCommand.BuyVehicle => $"\n合计：{amount}\n交易后资金：{_view.Economy.Wallet.Value - amount}\n买下的车辆会停在这里。",
            CoreCommand.SellVehicle => $"\n收入：{amount}\n出售后车辆归本聚居地所有，成为在售现车。",
            _ => "\n拆解不能撤销。回收材料进入本聚居地库存。"
        });
        dialog.PopupCentered(new(480, 320));
    }

    private void Execute(ImmutableArray<QuotedVehicle> batch, bool form)
    {
        if (!canAct() || batch.IsEmpty) return;
        _pending = [];
        var completed = 0;
        foreach (var item in batch)
        {
            var result = submit(_target, item.Command);
            if (result is not SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted })
            {
                var reason = result switch
                {
                    SubmissionResult.NotAccepted rejected => Describe([rejected.Reason]),
                    SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejected } => Describe(rejected.Problems),
                    SubmissionResult.Completed { Receipt.Decision: CommandDecision.Faulted fault } => CommandDiagnostics.Describe(fault.Fault),
                    _ => "操作未完成。"
                };
                _result.Text = $"已{Verb(item.Command)} {completed} 辆；第 {completed + 1} 辆（{item.Name}）未完成：{reason}";
                _quotedRevision = null; RefreshVehicles();
                return;
            }
            completed++;
            if (item.Command is CoreCommand.BuyVehicle)
            { _buySelection.Remove(item.Vehicle); if (!_parkedSelection.Contains(item.Vehicle)) _parkedSelection.Add(item.Vehicle); }
            else _parkedSelection.Remove(item.Vehicle);
        }
        _result.Text = $"已{Verb(batch[0].Command)} {completed} 辆。";
        _quotedRevision = null; RefreshVehicles();
        if (form) OpenFormation([.. batch.Select(item => item.Vehicle)]);
    }

    private static string Verb(CoreCommand command) => command switch
    {
        CoreCommand.BuyVehicle => "买入", CoreCommand.SellVehicle => "卖出",
        CoreCommand.StartOwnedJob { Request: JobRequest.Dismantle } => "开始拆解", _ => "开始修理"
    };
}
