namespace Train.Content.Behaviors;

public static class BehaviorTypes
{
    public const string Propulsion = "propulsion";
    public const string Control = "control";
    public const string Cargo = "cargo";
}

public static class BehaviorRegistry
{
    internal static string TypeOf(VoxelBehavior behavior) => behavior switch
    {
        PropulsionBehavior => BehaviorTypes.Propulsion,
        ControlBehavior => BehaviorTypes.Control,
        CargoBehavior => BehaviorTypes.Cargo,
        _ => throw new InvalidOperationException($"Unsupported behavior type {behavior.GetType().Name}.")
    };

    public static bool IsRegistered(string type) =>
        type is BehaviorTypes.Propulsion or BehaviorTypes.Control or BehaviorTypes.Cargo;
}
