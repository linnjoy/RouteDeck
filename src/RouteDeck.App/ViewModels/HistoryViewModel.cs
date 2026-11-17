using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RouteDeck.Core.Journal;
using RouteDeck.Core.Models;
using StreamJsonRpc;

namespace RouteDeck.App.ViewModels;

public sealed partial class OperationRow(OperationRecord record, HistoryViewModel owner) : ObservableObject
{
    public OperationRecord Record { get; } = record;

    public string Time => Record.Time.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public string Id => $"#{Record.Id}";

    public string Description => Record.ChangeSet.Description;

    public string Steps => string.Join("\n", Record.ChangeSet.Operations.Select(o => o.Describe()));

    public string Origin => Record.ChangeSet.Origin switch
    {
        ChangeSet.ManualOrigin => "手动",
        "recover" => "自动恢复",
        string o when o.StartsWith("fix:") => "诊断修复",
        string o => o,
    };

    public string Status => Record.Status switch
    {
        OperationStatus.Running => "执行中",
        OperationStatus.PendingConfirm => "等待确认",
        OperationStatus.Applied => Record.UndoneBy is null ? "已生效" : $"已撤销（#{Record.UndoneBy}）",
        OperationStatus.Failed => "失败，已回滚",
        OperationStatus.RollbackFailed => "失败，回滚不完整",
        OperationStatus.Reverted => "已自动恢复",
        OperationStatus.Undone => $"已撤销（#{Record.UndoneBy}）",
        _ => Record.Status.ToString(),
    };

    public string? Error => Record.Error;

    public bool CanUndo => Record.Status is OperationStatus.Applied or OperationStatus.PendingConfirm && Record.UndoneBy is null;

    [RelayCommand]
    private Task UndoAsync() => owner.Main.UndoAsync(Record.Id, Record.ChangeSet.Description);
}

public sealed partial class HistoryViewModel(MainViewModel main) : ObservableObject
{
    public MainViewModel Main { get; } = main;

    [ObservableProperty]
    private IReadOnlyList<OperationRow> _operations = [];

    [ObservableProperty]
    private string? _note;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Main.Connection.Service is not { } service)
        {
            Operations = [];
            Note = "操作历史保存在服务中，服务未运行时无法查看。";
            return;
        }
        try
        {
            Operations = (await service.GetRecentOperationsAsync(100)).Select(r => new OperationRow(r, this)).ToList();
            Note = Operations.Count == 0 ? "还没有任何操作。RouteDeck 做的每一次修改都会记录在这里，并可以安全撤销。" : null;
        }
        catch (Exception ex) when (ex is RemoteInvocationException or ConnectionLostException)
        {
            Note = ex.Message;
        }
    }
}
