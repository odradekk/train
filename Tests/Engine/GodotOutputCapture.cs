using Godot;

namespace Train.Engine.Tests;

internal partial class GodotOutputCapture : Logger
{
    private readonly object _gate = new();
    private readonly List<string> _messages = [];

    public override void _LogMessage(string message, bool error)
    {
        lock (_gate) _messages.Add(message);
    }

    public override void _LogError(string function, string file, int line, string code, string rationale,
        bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        => _LogMessage(code + "\n" + rationale, true);

    internal bool Contains(string text)
    {
        lock (_gate) return _messages.Any(message => message.Contains(text, StringComparison.Ordinal));
    }
}
