using System.Text;
using System.Text.Json;
using Train.Cli;
using Train.App.Saves;
using System.Globalization;

Console.OutputEncoding = Encoding.UTF8;

if (!(args.Length == 4 && (args[0] is "run" or "replay" or "make" or "check")
    || args.Length == 5 && (args[0] is "save" or "resume")
    || args.Length == 3 && args[0] == "compare"))
{
    Console.Error.WriteLine("用法：Train.Cli run|replay <repository> <recipe.json> <output.json>");
    Console.Error.WriteLine("      Train.Cli compare <expected.json> <actual.json>");
    Console.Error.WriteLine("      Train.Cli make|check <repository> <recipe.json> <save.json>");
    Console.Error.WriteLine("      Train.Cli save <repository> <recipe.json> <save.json> <until-step>");
    Console.Error.WriteLine("      Train.Cli resume <repository> <recipe.json> <save.json> <output.json>");
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
    else if (args[0] is "make" or "save")
    {
        runner.Save(args[1], Recipe.Read(args[2]), args[3],
            args[0] == "save" ? ulong.Parse(args[4], CultureInfo.InvariantCulture) : null);
        Console.WriteLine("存档已制作。");
    }
    else if (args[0] == "check")
    {
        runner.CheckExample(args[1], Recipe.Read(args[2]), args[3]);
        Console.WriteLine("示例与配方的检查点一致。");
    }
    else
    {
        var recipe = Recipe.Read(args[2]);
        var result = args[0] == "resume" ? runner.Resume(args[1], recipe, args[3])
            : runner.Run(args[1], recipe, args[0] == "replay");
        File.WriteAllText(args[0] == "resume" ? args[4] : args[3],
            JsonSerializer.Serialize(result, Recipe.Json), new UTF8Encoding(false));
    }
    return 0;
}
catch (Exception error) when (error is SaveFormatException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or OverflowException or FormatException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
