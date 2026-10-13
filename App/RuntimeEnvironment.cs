using System.Runtime.InteropServices;
using Train.Core.Contracts;

namespace Train.App;

internal static class RuntimeEnvironment
{
    internal static RuntimeIdentity Identity(string gameBuild) => new(gameBuild, "core-session-v1",
        OperatingSystem.IsWindows() ? "Windows" : RuntimeInformation.OSDescription,
        RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription);
}
