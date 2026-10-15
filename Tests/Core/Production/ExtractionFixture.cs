using System.Collections.Immutable;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.Tests.World;

namespace Train.Core.Tests.Production;

internal static class ExtractionFixture
{
    internal static readonly FacilityKey Facility = new(new(2), CapabilityKind.Extraction);
    internal static readonly ProcessRef Process = new ProcessRef.Production(new("fixture:extraction"));

    internal static CoreContentProjection Content(long batch = 2, long work = 1, long capacity = 100,
        bool dual = false, double powerPerWork = 0, double laborPerWork = 0)
    {
        var content = ProductionFixture.Content(stock: powerPerWork > 0 ? [new(ProductionFixture.Fuel, 4)] : [],
            work: work, capacity: capacity,
            inputs: [], outputs: [new(ProductionFixture.Product, 3)],
            powerPerWork: powerPerWork, laborPerWork: laborPerWork);
        return content with
        {
            Designs = content.Designs.Select(d => d.Design != WorldFixture.FactoryBuilding ? d : d with
            {
                Kind = new DesignKind.Building(PlotSize.Small, BuildingKind.Production,
                    [FacilityTag.Agriculture, FacilityTag.Extraction]),
                Parts = d.Parts.Select(p => p with { Capabilities =
                    [new PartCapability.Production(60), new PartCapability.Extraction(60)] }).ToImmutableArray()
            }).ToImmutableArray(),
            Processes = [..content.Processes, new(new ProcessDetails.Extraction(new("fixture:extraction"),
                new(ProductionFixture.Input, batch)), WorldFixture.Revision, work, powerPerWork,
                laborPerWork, FacilityTag.Extraction, [WorldFixture.Plain])],
            SettlementTemplates = [content.SettlementTemplates[0] with { ProductionPlans = dual
                ? [content.SettlementTemplates[0].ProductionPlans[0], new(2, CapabilityKind.Extraction, Process, true, 0)]
                : [new(2, CapabilityKind.Extraction, Process, true, 0)] }]
        };
    }

    internal static ICoreSessionCandidate Prepare(CoreContentProjection? content = null, long reserve = 6, bool renewable = false)
        => WorldFixture.Prepare(WorldFixture.Input with { Configuration = WorldFixture.Input.Configuration with
        { Resources = [new(WorldFixture.Material, [WorldFixture.Plain], 1, reserve, reserve, renewable, 0, 1000, 0)] } },
            content ?? Content());

    internal static FacilityView Read(ICoreSession session, FacilityKey? facility = null)
        => ((Lookup<FacilityView>.Found)session.Read().FindFacility(facility ?? Facility)).Value;

    internal static ImmutableArray<NodeResourceView> Resources(ICoreSession session)
        => session.Read().World.Nodes.Single(n => n.Occupant is NodeOccupant.Settlement).Resources;

    internal static void AssertConserved(ICoreSession session, long initial)
    {
        var facilities = new[] { Facility, new FacilityKey(new(3), CapabilityKind.Extraction) }
            .Select(key => session.Read().FindFacility(key)).OfType<Lookup<FacilityView>.Found>().Select(x => x.Value);
        var accounted = ProductionFixture.Stock(session, ProductionFixture.Input) + facilities.Sum(f =>
            f.OutputBuffer.Where(g => g.Good == ProductionFixture.Input).Sum(g => g.Quantity)
            + (f.Batch?.FrozenOutputs.Where(g => g.Good == ProductionFixture.Input).Sum(g => g.Quantity) ?? 0));
        Assert.AreEqual(initial - Resources(session).Sum(r => r.Quantity), accounted);
        Assert.IsTrue(Resources(session).All(r => r.Quantity >= 0));
    }
}
