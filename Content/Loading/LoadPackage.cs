using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal sealed class LoadPackage(C.ContentRoot root, string prefix, bool builtIn)
{
    internal C.ContentRoot Root { get; } = root;
    internal string Prefix { get; } = prefix;
    internal bool BuiltIn { get; } = builtIn;
    internal C.PackageMetadata? Metadata { get; set; }
    internal bool ImplicitCoreDependency { get; set; }
    internal C.PackageState State { get; set; } = C.PackageState.Available;
    internal List<C.ContentDiagnostic> Diagnostics { get; } = [];
    internal string Resources => Prefix + "resources";
    internal C.PackageStatus Status => new(Metadata, Root, State, Diagnostics.ToImmutableArray());

    internal void Reject(string code, string path, string field, string message, C.ContentReference? subject = null,
        C.DesignElementPath? element = null)
    {
        State = C.PackageState.Invalid;
        Diagnostics.Add(new(code, C.ContentDiagnosticSeverity.Error, "load", subject,
            Metadata is null ? null : new(Metadata.Id, path), field, element,
            Metadata is null && path.Length > 0 ? path + ": " + message : message));
    }
}
