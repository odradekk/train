using System.Collections.Immutable;
using Train.Core.Contracts;

namespace Train.Cli;

// 配方期望和样本共用的计数，全部来自公开查询；指定节点时只统计该节点上的对象。
internal static class WorldCounts
{
    internal static readonly ImmutableArray<string> Keys = ["largest_train_meters", "largest_train_vehicles", "trains_running"];

    internal static ImmutableSortedDictionary<string, double> Read(ICoreReadView view, NodeId? node = null)
    {
        var trains = view.World.Trains.Select(train => Found(view.FindTrain(train.Id)))
            .Where(train => node is null || train.Position is TrainPosition.Node at && at.Id == node).ToArray();
        return new Dictionary<string, double>
        {
            ["largest_train_meters"] = trains.Select(train => train.LengthMeters).DefaultIfEmpty().Max(),
            ["largest_train_vehicles"] = trains.Select(train => train.Vehicles.Length).DefaultIfEmpty().Max(),
            ["trains_running"] = trains.Count(train => train.Activity == TrainActivity.Running)
        }.ToImmutableSortedDictionary(StringComparer.Ordinal);
    }

    private static T Found<T>(Lookup<T> lookup) where T : notnull => lookup is Lookup<T>.Found found
        ? found.Value : throw new InvalidDataException($"计数对象不存在：{lookup}。");
}
