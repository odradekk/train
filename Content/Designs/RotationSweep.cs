using System.Collections.Immutable;
using C = Train.Content.Contracts;

namespace Train.Content.Designs;

internal static class RotationSweep
{
    private const double ContactTolerance = 1e-10;

    internal static ImmutableArray<C.GridPosition> Calculate(IEnumerable<C.GridPosition> cells,
        C.RotationAppearance rotation)
    {
        var result = cells.ToHashSet();
        var pivot = Plane(rotation.Pivot, rotation.Axis);
        var minimum = rotation.MinimumDegrees * Math.PI / 180;
        var maximum = rotation.MaximumDegrees * Math.PI / 180;
        foreach (var cell in result.ToArray())
        {
            var source = Plane(cell, rotation.Axis);
            double u = (double)source.U - pivot.U, v = (double)source.V - pivot.V;
            var radius = Math.Sqrt(Math.Pow(Math.Abs(u) + .5, 2) + Math.Pow(Math.Abs(v) + .5, 2));
            var bound = (int)Math.Ceiling(radius + .5);
            for (var x = -bound; x <= bound; x++)
            for (var y = -bound; y <= bound; y++)
            {
                if (!Intersects(u, v, x, y, minimum, maximum)) continue;
                result.Add(rotation.Axis switch
                {
                    C.RotationAxis.X => new(cell.X, pivot.U + x, pivot.V + y),
                    C.RotationAxis.Y => new(pivot.V + y, cell.Y, pivot.U + x),
                    _ => new(pivot.U + x, pivot.V + y, cell.Z)
                });
            }
        }
        return result.OrderBy(p => p.X).ThenBy(p => p.Y).ThenBy(p => p.Z).ToImmutableArray();
    }

    private static (int U, int V) Plane(C.GridPosition p, C.RotationAxis axis) => axis switch
    {
        C.RotationAxis.X => (p.Y, p.Z),
        C.RotationAxis.Y => (p.Z, p.X),
        _ => (p.X, p.Y)
    };

    private static bool Intersects(double u, double v, double x, double y, double minimum, double maximum)
    {
        // In each quadrant the four SAT axes have boundaries A*cos(t)+B*sin(t)=D.
        // Only boundary events can change overlap, so one witness per open interval is sufficient.
        for (var quadrant = -2; quadrant < 2; quadrant++)
        {
            var start = Math.Max(minimum, quadrant * Math.PI / 2);
            var end = Math.Min(maximum, (quadrant + 1) * Math.PI / 2);
            if (end <= start) continue;
            var middle = (start + end) / 2;
            var cosineSign = Math.Sign(Math.Cos(middle));
            var sineSign = Math.Sign(Math.Sin(middle));
            var events = new List<double> { start, end };
            foreach (var sign in new[] { -1, 1 })
            {
                AddRoots(sign * u - .5 * cosineSign, -sign * v - .5 * sineSign, .5 + sign * x);
                AddRoots(sign * v - .5 * cosineSign, sign * u - .5 * sineSign, .5 + sign * y);
                AddRoots(-sign * x - .5 * cosineSign, -sign * y - .5 * sineSign, .5 - sign * u);
                AddRoots(-sign * y - .5 * cosineSign, sign * x - .5 * sineSign, .5 - sign * v);
            }
            events.Sort();
            for (var index = 1; index < events.Count; index++)
            {
                var angle = (events[index - 1] + events[index]) / 2;
                var cosine = Math.Cos(angle);
                var sine = Math.Sin(angle);
                var support = .5 * (1 + Math.Abs(cosine) + Math.Abs(sine)) - ContactTolerance;
                if (Math.Abs(u * cosine - v * sine - x) < support
                    && Math.Abs(u * sine + v * cosine - y) < support
                    && Math.Abs(u - x * cosine - y * sine) < support
                    && Math.Abs(v + x * sine - y * cosine) < support)
                    return true;
            }

            void AddRoots(double a, double b, double d)
            {
                var length = Math.Sqrt(a * a + b * b);
                if (length == 0 || Math.Abs(d) > length) return;
                var phase = Math.Atan2(b, a);
                var offset = Math.Acos(Math.Clamp(d / length, -1, 1));
                foreach (var root in new[] { phase - offset, phase + offset })
                for (var turn = -1; turn <= 1; turn++)
                {
                    var angle = root + turn * 2 * Math.PI;
                    if (angle > start && angle < end) events.Add(angle);
                }
            }
        }
        return false;
    }
}
