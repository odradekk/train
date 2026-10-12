
namespace Train.Content.Behaviors;

public abstract record VoxelBehavior;

public sealed record PropulsionBehavior(double Traction, double RatedSpeed) : VoxelBehavior;

public sealed record ControlBehavior : VoxelBehavior;

public sealed record CargoBehavior(int Capacity) : VoxelBehavior;
