using System.Text.Json;
using Train.ContractChecks;

if (args.Length != 4 || args[0] is not ("structure" or "verify" or "snapshot" or "isolate") ||
    args[2] is not ("Debug" or "Release"))
{
    Console.Error.WriteLine("usage: Train.ContractChecks structure|isolate <repository> <Debug|Release> <output-directory>");
    Console.Error.WriteLine("       Train.ContractChecks verify|snapshot <repository> <Debug|Release> <baseline>");
    return 2;
}

try
{
    var root = Path.GetFullPath(args[1]);
    var destination = Path.GetFullPath(args[3]);
    var rules = JsonSerializer.Deserialize<BoundaryRules>(
        File.ReadAllText(Path.Combine(root, "scripts", "checks", "project-boundaries.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    return args[0] switch
    {
        "structure" => await ProjectAnalysis.CheckWorkspace(root, args[2], destination, rules.Projects),
        "isolate" => await Isolation.Run(root, args[2], destination, rules.Projects, rules.IsolationTests),
        _ => ApiSurface.Run(args[0], root, args[2], destination, rules.Projects)
    };
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
