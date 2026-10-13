using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Railway;

namespace Train.Core.Tests.Railway;

[TestClass]
public sealed class TrainMotionTests
{
    [TestMethod]
    public void Long_line_accelerates_cruises_and_brakes_to_the_exact_endpoint()
    {
        var distance = 0.0;
        var speed = 0.0;
        var arrived = false;
        for (var step = 1; step <= 362 && !arrived; step++)
        {
            var result = TrainMotion.Advance(8 - distance, 8 - distance, speed, 1, 1, 2);
            distance += result.DistanceMeters;
            speed = result.SpeedMetersPerSecond;
            arrived = result.Arrived;
            Assert.IsTrue(distance <= 8);
            Assert.IsTrue(speed is >= 0 and <= 2);
            if (step == 60) { Assert.AreEqual(0.5, distance, 1e-12); Assert.AreEqual(1.0, speed, 1e-12); }
            if (step == 180) { Assert.AreEqual(4.0, distance, 1e-11); Assert.AreEqual(2.0, speed, 1e-12); }
            if (step == 300) { Assert.AreEqual(7.5, distance, 1e-11); Assert.AreEqual(1.0, speed, 1e-11); }
        }
        Assert.IsTrue(arrived);
        Assert.AreEqual(8.0, distance);
        Assert.AreEqual(0.0, speed);
    }

    [TestMethod]
    public void Short_line_has_a_triangular_curve_and_arrives_in_finite_steps()
    {
        var distance = 0.0;
        var speed = 0.0;
        var arrived = false;
        for (var step = 1; step <= 122 && !arrived; step++)
        {
            var result = TrainMotion.Advance(1 - distance, 1 - distance, speed, 1, 1, 2);
            distance += result.DistanceMeters;
            speed = result.SpeedMetersPerSecond;
            arrived = result.Arrived;
            Assert.IsTrue(distance <= 1);
            Assert.IsTrue(speed <= 1 + 1e-12);
            if (step == 60) { Assert.AreEqual(0.5, distance, 1e-12); Assert.AreEqual(1.0, speed, 1e-12); }
        }
        Assert.IsTrue(arrived);
        Assert.AreEqual(1.0, distance);
        Assert.AreEqual(0.0, speed);
    }

    [TestMethod]
    public void One_step_can_finish_acceleration_and_continue_cruising()
    {
        var result = TrainMotion.Advance(10, null, 0, 120, 1, 1);
        Assert.AreEqual(0.0125, result.DistanceMeters, 1e-14);
        Assert.AreEqual(1.0, result.SpeedMetersPerSecond);
        Assert.IsFalse(result.Arrived);
    }

    [TestMethod]
    public void One_step_can_finish_cruising_and_begin_braking()
    {
        // 以 1m/s 巡航 1/120 秒，随后按 1m/s² 制动 1/120 秒。
        var result = TrainMotion.Advance(0.5 + 1.0 / 120, 0.5 + 1.0 / 120, 1, 1, 1, 1);
        Assert.AreEqual(1.0 / 60 - 1.0 / 28800, result.DistanceMeters, 1e-14);
        Assert.AreEqual(119.0 / 120, result.SpeedMetersPerSecond, 1e-14);
        Assert.IsFalse(result.Arrived);
    }

    [TestMethod]
    public void Critical_braking_distance_begins_braking_immediately()
    {
        var result = TrainMotion.Advance(0.5, 0.5, 1, 3, 1, 2);
        Assert.AreEqual(119.0 / 7200, result.DistanceMeters, 1e-14);
        Assert.AreEqual(59.0 / 60, result.SpeedMetersPerSecond, 1e-14);
        Assert.IsFalse(result.Arrived);
    }

    [TestMethod]
    public void Waypoint_arrival_keeps_event_speed_and_discards_unused_step_time()
    {
        var result = TrainMotion.Advance(0.005, null, 1, 1, 2, 1);
        Assert.AreEqual(0.005, result.DistanceMeters);
        Assert.AreEqual(1.0, result.SpeedMetersPerSecond);
        Assert.IsTrue(result.Arrived);
        var next = TrainMotion.Advance(1, null, result.SpeedMetersPerSecond, 1, 2, 1);
        Assert.AreEqual(1.0 / 60, next.DistanceMeters, 1e-14);
    }

    [TestMethod]
    public void Braking_for_a_station_begins_before_the_previous_waypoint()
    {
        var first = TrainMotion.Advance(0.01, 0.5, 1, 1, 1, 2);
        Assert.IsTrue(first.Arrived);
        Assert.AreEqual(0.01, first.DistanceMeters);
        Assert.AreEqual(Math.Sqrt(0.98), first.SpeedMetersPerSecond, 1e-14);
        var remaining = 0.49;
        var speed = first.SpeedMetersPerSecond;
        var arrived = false;
        for (var step = 0; step < 62 && !arrived; step++)
        {
            var result = TrainMotion.Advance(remaining, remaining, speed, 1, 1, 2);
            remaining -= result.DistanceMeters;
            speed = result.SpeedMetersPerSecond;
            arrived = result.Arrived;
            Assert.IsTrue(remaining >= 0);
        }
        Assert.IsTrue(arrived);
        Assert.AreEqual(0.0, remaining);
        Assert.AreEqual(0.0, speed);
    }

    [TestMethod]
    public void Late_stop_uses_braking_until_the_node_then_forces_zero_speed()
    {
        var remaining = 1.0;
        var speed = 2.0;
        var first = TrainMotion.Advance(remaining, remaining, speed, 1, 1, 2);
        Assert.AreEqual(239.0 / 7200, first.DistanceMeters, 1e-14);
        Assert.AreEqual(119.0 / 60, first.SpeedMetersPerSecond, 1e-14);
        var arrived = false;
        for (var step = 0; step < 36 && !arrived; step++)
        {
            var result = TrainMotion.Advance(remaining, remaining, speed, 1, 1, 2);
            remaining -= result.DistanceMeters;
            speed = result.SpeedMetersPerSecond;
            arrived = result.Arrived;
            Assert.IsTrue(remaining >= 0);
        }
        Assert.IsTrue(arrived);
        Assert.AreEqual(0.0, remaining);
        Assert.AreEqual(0.0, speed);
    }

    [TestMethod]
    public void A_stop_shorter_than_one_step_is_committed_at_the_exact_endpoint()
    {
        var result = TrainMotion.Advance(0.000001, 0.000001, 0, 1, 1, 2);
        Assert.IsTrue(result.Arrived);
        Assert.AreEqual(0.000001, result.DistanceMeters);
        Assert.AreEqual(0.0, result.SpeedMetersPerSecond);
    }

    [TestMethod]
    public void Finishing_critical_braking_commits_arrival_despite_floating_point_tail_distance()
    {
        // 0.05m/s 按 3m/s² 制动正好一个模拟步，制动距离为 v²/(2b)。
        var distance = 0.05 * 0.05 / 6;
        var result = TrainMotion.Advance(distance, distance, 0.05, 1, 3, 2);
        Assert.IsTrue(result.Arrived);
        Assert.AreEqual(distance, result.DistanceMeters);
        Assert.AreEqual(0.0, result.SpeedMetersPerSecond);
    }

    [TestMethod]
    public void One_step_can_accelerate_then_brake_on_a_short_line_without_reaching_the_speed_limit()
    {
        // 峰值 1.5m/s：加速 1/80 秒，再制动 1/240 秒，位移 7/480 米。
        var result = TrainMotion.Advance(3.0 / 160, 3.0 / 160, 0, 120, 120, 2);
        Assert.AreEqual(7.0 / 480, result.DistanceMeters, 1e-14);
        Assert.AreEqual(1.0, result.SpeedMetersPerSecond, 1e-14);
        Assert.IsFalse(result.Arrived);
    }

    [TestMethod]
    public void Arrival_during_acceleration_preserves_endpoint_event_speed()
    {
        var result = TrainMotion.Advance(0.00005, null, 0, 1, 1, 2);
        Assert.IsTrue(result.Arrived);
        Assert.AreEqual(0.00005, result.DistanceMeters);
        Assert.AreEqual(0.01, result.SpeedMetersPerSecond, 1e-14);
    }

    [TestMethod]
    public void Losing_traction_while_moving_can_still_cruise_then_brake()
    {
        var result = TrainMotion.Advance(2, 2, 1, 0, 1, 2);
        Assert.AreEqual(1.0 / 60, result.DistanceMeters, 1e-14);
        Assert.AreEqual(1.0, result.SpeedMetersPerSecond);
        Assert.IsFalse(result.Arrived);
    }
}
