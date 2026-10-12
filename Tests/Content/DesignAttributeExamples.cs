
using System.Collections.Frozen;
using Train.Content.Behaviors;
using Train.Content.Voxels;

namespace Train.Content.Tests;

internal static class DesignAttributeExamples
{
    public static ICoreVoxelDefinition Structure { get; } = Create("structure", 1, 0.01);

    public static ICoreVoxelDefinition Propulsion { get; } = Create(
        "propulsion", 250, 4, new PropulsionBehavior(12.5, 20));

    public static ICoreVoxelDefinition Control { get; } = Create(
        "control", 474, 0.74, new ControlBehavior());

    public static ICoreVoxelDefinition Cargo { get; } = Create(
        "cargo", 237, 2.37, new CargoBehavior(4));

    public static ICoreVoxelDefinition Combined { get; } = Create(
        "combined", 300, 5, new PropulsionBehavior(5, 10), new ControlBehavior(), new CargoBehavior(1));

    public static IReadOnlyList<ICoreVoxelDefinition> Empty { get; } = Array.Empty<ICoreVoxelDefinition>();

    public static IReadOnlyList<ICoreVoxelDefinition> StructureOnly { get; } = Array.AsReadOnly(
        new[] { Structure });

    public static IReadOnlyList<ICoreVoxelDefinition> Mixed { get; } = Array.AsReadOnly(
        new[] { Structure, Propulsion, Control, Cargo });

    private static ICoreVoxelDefinition Create(string localId, long value, double mass, params VoxelBehavior[] behaviors) =>
        new CoreDefinition(new VoxelDefinitionId("core", "example-" + localId), value, mass, behaviors);

    private sealed class CoreDefinition : ICoreVoxelDefinition
    {
        public VoxelDefinitionId Id { get; }
        public long Value { get; }
        public double Mass { get; }
        public int MaxDurability => 100;
        public IReadOnlySet<string> Tags { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
        public IReadOnlyList<VoxelBehavior> Behaviors { get; }

        public CoreDefinition(VoxelDefinitionId id, long value, double mass, VoxelBehavior[] behaviors)
        {
            Id = id;
            Value = value;
            Mass = mass;
            Behaviors = Array.AsReadOnly(behaviors.ToArray());
        }
    }
}
