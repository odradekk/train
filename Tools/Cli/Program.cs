using System.Text;
using System.Text.Json;
using Train.Cli;

Console.OutputEncoding = Encoding.UTF8;

if (!(args.Length == 4 && (args[0] is "run" or "replay")
    || args.Length == 3 && args[0] == "compare"))
{
    Console.Error.WriteLine("用法：Train.Cli run|replay <repository> <recipe.json> <output.json>");
    Console.Error.WriteLine("      Train.Cli compare <expected.json> <actual.json>");
    return 2;
}
try
{
    var runner = Composition.Create();
    if (args[0] == "compare")
    {
        var expected = JsonSerializer.Deserialize<RunOutput>(File.ReadAllText(args[1]), Recipe.Json)
            ?? throw new InvalidDataException("预期结果为空。");
        var actual = JsonSerializer.Deserialize<RunOutput>(File.ReadAllText(args[2]), Recipe.Json)
            ?? throw new InvalidDataException("实际结果为空。");
        runner.Compare(expected, actual);
        Console.WriteLine("检查点一致。");
    }
    else
    {
        var result = runner.Run(args[1], Recipe.Read(args[2]), args[0] == "replay");
        File.WriteAllText(args[3], JsonSerializer.Serialize(result, Recipe.Json), new UTF8Encoding(false));
    }
    return 0;
}
catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
