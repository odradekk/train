using Train.Core.Contracts;

namespace Train.Core.Railway;

internal static class TrainMotion
{
    internal static (double DistanceMeters, double SpeedMetersPerSecond, bool Arrived) Advance(
        double remainingDistance, double? stopDistanceAfterEndpoint, double speed, double acceleration,
        double brakingDeceleration, double maximumSpeed)
    {
        var stopAtEndpoint = stopDistanceAfterEndpoint is 0;
        if (remainingDistance == 0)
            return (0, stopAtEndpoint ? 0 : speed, true);
        var time = SimulationTime.StepSeconds;
        var distance = 0.0;

        if (speed > maximumSpeed && Move((speed - maximumSpeed) / brakingDeceleration,
                -brakingDeceleration, maximumSpeed))
            return (distance, speed, true);

        if (stopDistanceAfterEndpoint is not { } distanceAfterEndpoint)
        {
            if (acceleration > 0 && Move(Math.Max(0, (maximumSpeed - speed) / acceleration), acceleration, maximumSpeed))
                return (distance, speed, true);
            var arrived = Move(time, 0, speed);
            return (distance, speed, arrived);
        }

        var stoppingDistance = remainingDistance - distance + distanceAfterEndpoint;
        if (stoppingDistance <= speed * speed / (2 * brakingDeceleration))
        {
            var arrived = Move(speed / brakingDeceleration, -brakingDeceleration, 0);
            return (distance, speed, arrived);
        }
        if (acceleration == 0 && speed == 0)
            return (0, 0, false);

        var peak = acceleration == 0 ? speed : Math.Sqrt(
            (2 * acceleration * brakingDeceleration * stoppingDistance + brakingDeceleration * speed * speed)
            / (acceleration + brakingDeceleration));
        var cruiseSpeed = Math.Min(maximumSpeed, peak);
        var acceleratingTime = acceleration == 0 ? 0 : Math.Max(0, (cruiseSpeed - speed) / acceleration);
        var acceleratingDistance = acceleration == 0 ? 0
            : (cruiseSpeed * cruiseSpeed - speed * speed) / (2 * acceleration);
        var cruiseDistance = Math.Max(0, stoppingDistance - acceleratingDistance
            - cruiseSpeed * cruiseSpeed / (2 * brakingDeceleration));
        var cruiseTime = cruiseDistance / cruiseSpeed;
        var brakingTime = cruiseSpeed / brakingDeceleration;
        if (Move(acceleratingTime, acceleration, cruiseSpeed)
            || Move(cruiseTime, 0, cruiseSpeed)
            || Move(brakingTime, -brakingDeceleration, 0))
            return (distance, speed, true);
        return (distance, speed, false);

        bool Move(double duration, double rate, double endSpeed)
        {
            var elapsed = Math.Min(time, Math.Max(0, duration));
            if (elapsed == 0)
                return false;
            var travel = speed * elapsed + rate * elapsed * elapsed / 2;
            var remaining = remainingDistance - distance;
            if (travel >= remaining || (rate < 0 && stopAtEndpoint && elapsed == duration))
            {
                var discriminant = speed * speed + 2 * rate * remaining;
                // 节点抽象允许来不及制动或制动舍入误差时归零，不在途中修正数值。
                if (stopAtEndpoint || (rate < 0 && discriminant <= 0))
                    speed = 0;
                else
                {
                    var arrivalTime = 2 * remaining / (speed + Math.Sqrt(discriminant));
                    speed += rate * arrivalTime;
                }
                distance = remainingDistance;
                return true;
            }
            distance += travel;
            speed = elapsed == duration ? endSpeed : speed + rate * elapsed;
            time -= elapsed;
            return false;
        }
    }
}
