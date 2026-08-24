namespace TrainMvp.Domain;

public static class ApprovedMvpCatalog
{
    public static ScenarioData Create()
    {
        return new ScenarioData(
            MvpContent.Goods,
            MvpContent.Stations,
            MvpContent.Edges,
            MvpContent.CreateInitialMarkets(),
            MvpContent.StartingCash,
            MvpContent.CargoCapacity,
            MvpContent.StartingStationId,
            MvpContent.CreatePresetPlan());
    }
}
