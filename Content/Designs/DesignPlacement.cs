using C = Train.Content.Contracts;

namespace Train.Content.Designs;

internal readonly record struct DesignPlacement(C.GridPosition At, C.VoxelOrientation Orientation)
{
    internal C.GridPosition Map(C.GridPosition local)
    {
        var (right, up, forward) = Axes(Orientation);
        return new(checked(At.X + right.X * local.X + up.X * local.Y + forward.X * local.Z),
            checked(At.Y + right.Y * local.X + up.Y * local.Y + forward.Y * local.Z),
            checked(At.Z + right.Z * local.X + up.Z * local.Y + forward.Z * local.Z));
    }

    internal C.VoxelOrientation Rotate(C.VoxelOrientation local)
    {
        var (right, up, _) = Axes(local);
        var rotation = this with { At = new(0, 0, 0) };
        return Enum.Parse<C.VoxelOrientation>(Name(rotation.Map(right)) + Name(rotation.Map(up)));
    }

    private static (C.GridPosition Right, C.GridPosition Up, C.GridPosition Forward) Axes(C.VoxelOrientation orientation)
    {
        var name = orientation.ToString();
        var right = Axis(name[..2]); var up = Axis(name[2..]);
        return (right, up, new(right.Y * up.Z - right.Z * up.Y, right.Z * up.X - right.X * up.Z, right.X * up.Y - right.Y * up.X));
    }

    private static string Name(C.GridPosition axis) => axis switch
    { (1, 0, 0) => "Px", (-1, 0, 0) => "Nx", (0, 1, 0) => "Py", (0, -1, 0) => "Ny", (0, 0, 1) => "Pz", (0, 0, -1) => "Nz", _ => throw new ArgumentException("不是正交轴。") };
    private static C.GridPosition Axis(string axis) => axis switch
    { "Px" => new(1, 0, 0), "Nx" => new(-1, 0, 0), "Py" => new(0, 1, 0), "Ny" => new(0, -1, 0), "Pz" => new(0, 0, 1), "Nz" => new(0, 0, -1), _ => throw new ArgumentException("非法朝向。") };
}
