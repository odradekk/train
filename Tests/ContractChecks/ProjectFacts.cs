using Microsoft.Build.Framework;
using System.Text.Json;

namespace Train.ContractChecks;

public sealed class ProjectFacts : Microsoft.Build.Utilities.Task
{
    [Required] public string OutputFile { get; set; } = "";
    [Required] public string TargetPath { get; set; } = "";
    public ITaskItem[]? ProjectReference { get; set; }
    public ITaskItem[]? ReferencePath { get; set; }
    public ITaskItem[]? Compile { get; set; }
    public ITaskItem[]? InternalsVisibleTo { get; set; }

    public override bool Execute()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile)!);
        File.WriteAllText(OutputFile, JsonSerializer.Serialize(new
        {
            Properties = new { TargetPath, ProcessId = Environment.ProcessId },
            Items = new
            {
                ProjectReference = Items(ProjectReference), ReferencePath = Items(ReferencePath),
                Compile = Items(Compile), InternalsVisibleTo = Items(InternalsVisibleTo)
            }
        }));
        return true;
    }

    private static object[] Items(ITaskItem[]? items) => (items ?? []).Select(item => (object)new
    {
        Identity = item.ItemSpec, FullPath = item.GetMetadata("FullPath"), Filename = item.GetMetadata("Filename")
    }).ToArray();
}
