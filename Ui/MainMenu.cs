
using Godot;
using Train.App.Saves;
using Train.App.Recording;

namespace Train.Ui;

public partial class MainMenu : Control
{
    private SavePanel? _saves;
    public event Action? QuitRequested;

    internal void ShowSaveFailure(SaveFailure? failure) => _saves?.ShowFailure(failure);

    internal void ConfigureSaves(SaveService service)
    {
        _saves = new SavePanel();
        AddChild(_saves);
        _saves.Configure(service);
        var button = GetNode<Button>("%Saves");
        button.Visible = true;
        button.Pressed += _saves.Open;
    }

    internal void ConfigureRecording(RecordingService service)
    {
        var report = new IssueReportPanel();
        AddChild(report);
        report.Configure(service);
        var button = GetNode<Button>("%ReportIssue");
        button.Visible = true;
        button.Pressed += report.Open;
    }

    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        quit.GrabFocus();
    }
}
