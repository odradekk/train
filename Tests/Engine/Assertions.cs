namespace Train.Engine.Tests;

internal static class Assertions
{
    internal static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
