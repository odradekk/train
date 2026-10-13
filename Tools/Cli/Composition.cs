using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Train.Core.Contracts;

namespace Train.Cli;

internal static class Composition
{
    internal static CliRunner Create() => new(Train.Core.CoreFactory.Create(),
        Train.Content.ContentFactory.Create(), Identity());

    private static RuntimeIdentity Identity()
    {
        var directory = AppContext.BaseDirectory;
        var manifest = new StringBuilder();
        foreach (var name in new[] { "Train.Core.Contracts", "Train.Content.Contracts", "Train.Core", "Train.Content", "Train.Cli" })
            manifest.Append(name).Append(':').Append(Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(directory, name + ".dll"))))).Append('\n');
        return new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))),
            "core-session-v1", RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.FrameworkDescription);
    }
}
