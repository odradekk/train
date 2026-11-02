using Train.Ui;

namespace Train.App.Console;

internal static class ConsoleCommandRegistration
{
    internal static void RegisterDevelopment(ConsoleCommandDispatcher dispatcher, Action reload,
        Action<IReadOnlyList<string>> simulation, Action<IReadOnlyList<string>> train,
        Action<IReadOnlyList<string>> lighting, Action<IReadOnlyList<string>> development)
    {
        dispatcher.Register(new("reload", "重新载入全部内容包", "用法：reload；修正内容文件后输入 reload 重新载入。", [], _ => reload()));
        dispatcher.Register(new("sim", "查看和控制模拟", SimulationConsoleCommands.Usage,
            [new("status", "查看模拟时间、步号和速度"), new("pause", "暂停模拟"), new("resume", "继续模拟"),
             new("speed", "设置模拟倍速"), new("step", "暂停时推进指定步数"), new("checkpoint", "查看检查点摘要")], simulation));
        dispatcher.Register(new("train", "编组、解编、启停列车或替换运行计划", TrainConsoleCommands.Usage,
            [new("form", "用停放的车辆编组"), new("disband", "解编列车"), new("start", "按运行计划发车"),
             new("stop", "停止列车运行"), new("plan", "替换列车的运行计划")], train));
        dispatcher.Register(new("lighting", "查看或切换检查光照", LightingConsoleCommands.Usage,
            [new("daylight", "晴天光照"), new("overcast", "阴天光照"), new("night", "夜间光照"), new("restore", "恢复游戏光照")], lighting));
        dispatcher.Register(new("dev", "打开或关闭开发调试窗口", DeveloperInspector.Usage,
            [new("open", "打开开发调试窗口"), new("close", "关闭开发调试窗口")], development));
    }
}
