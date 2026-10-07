// Train.App 将不依赖引擎的诊断事件接入 Godot 运行环境。
// 此适配器使用共享的可读格式，并选择相应的引擎输出通道。

using Godot;
using Train.Core.Logging;

namespace Train.App;

/// <summary>通过 Godot 的普通、警告和错误输出通道发布 Core 诊断事件。</summary>
/// <remarks>调用方必须在 Godot 主线程调用此适配器；它不会切换调用线程。</remarks>
/// <example>日志运行时冒烟检查会在 Godot 输出中观察应用的 Info 级就绪事件。</example>
public sealed class GodotLogOutput : IGodotLogOutput
{
    /// <summary>使用共享的可读格式渲染事件，再按日志级别写入相应的引擎通道。</summary>
    /// <param name="logEvent">要输出的结构化诊断事件；Warning 和 Error 分别使用引擎的警告和错误通道。</param>
    /// <exception cref="ArgumentNullException"><paramref name="logEvent"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">事件包含未定义的 LogLevel 值。</exception>
    public void Write(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var line = LogLevelText.FormatBounded(logEvent);
        switch (logEvent.Level)
        {
            case LogLevel.Warning:
                GD.PushWarning(line);
                break;
            case LogLevel.Error:
                GD.PushError(line);
                break;
            default:
                GD.Print(line);
                break;
        }
    }
}
