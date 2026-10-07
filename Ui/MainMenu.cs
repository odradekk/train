// Train.Ui 提供当前应用的主菜单。
// 只显示已实现的退出入口，动作交给宿主处理。

using Godot;

namespace Train.Ui;

/// <summary>显示退出按钮，并把用户退出请求通知给应用宿主。</summary>
/// <example>点击“退出”触发 QuitRequested，应用随后关闭并释放日志会话。</example>
public partial class MainMenu : Control
{
    /// <summary>退出按钮被按下时通知宿主；界面不持有应用或世界会话。</summary>
    public event Action? QuitRequested;

    /// <summary>连接场景中的退出按钮，并为键盘操作提供初始焦点。</summary>
    public override void _Ready()
    {
        var quit = GetNode<Button>("%QuitGame");
        quit.Pressed += () => QuitRequested?.Invoke();
        quit.GrabFocus();
    }
}
