using Train.Content.Designs;
using static Train.Content.Loading.DefinitionJson;
using K = Train.Core.Contracts;

namespace Train.Content.Loading;

internal static class RuleSemantics
{
    internal static K.SemanticRevision Revision(K.PopulationRuleProjection rule) => Semantic("population-rule", rule.Id.Value, new
    {
        needs = rule.Needs.OrderBy(need => DesignEvaluator.Wire(need.Need), StringComparer.Ordinal).Select(need => new
        {
            need = DesignEvaluator.Wire(need.Need), @class = DesignEvaluator.Wire(need.Class),
            amount_per_person_second = need.AmountPerPersonSecond, weight = need.Weight
        }).ToArray(),
        consumption_goods = rule.ConsumptionGoods.OrderBy(good => ((K.GoodId.Voxel)good.Good).Definition.Value, StringComparer.Ordinal).Select(good => new
        {
            good = ((K.GoodId.Voxel)good.Good).Definition.Value,
            satisfies = good.Satisfies.OrderBy(conversion => DesignEvaluator.Wire(conversion.Need), StringComparer.Ordinal).Select(conversion => new
            {
                need = DesignEvaluator.Wire(conversion.Need), amount_per_good_unit = conversion.AmountPerGoodUnit
            }).ToArray(),
            selection_order = good.SelectionOrder
        }).ToArray(),
        labor_ratio = rule.LaborRatio, consumption_interval_ticks = rule.ConsumptionIntervalTicks,
        statistics_period_ticks = rule.StatisticsPeriodTicks, base_growth_per_second = rule.BaseGrowthPerSecond,
        improvement_growth_per_second = rule.ImprovementGrowthPerSecond, maximum_loss_per_second = rule.MaximumLossPerSecond
    });

    internal static K.SemanticRevision Revision(K.MarketRuleProjection rule) => Semantic("market-rule", rule.Id.Value, new
    {
        basic_reserve_hours = rule.BasicReserveHours, production_reserve_hours = rule.ProductionReserveHours,
        improvement_reserve_hours = rule.ImprovementReserveHours, default_goods_prices = Prices(rule.DefaultGoodsPrices),
        good_prices = rule.GoodPrices.OrderBy(price => ((K.GoodId.Voxel)price.Good).Definition.Value, StringComparer.Ordinal).Select(price => new
        {
            good = ((K.GoodId.Voxel)price.Good).Definition.Value, parameters = Prices(price.Parameters)
        }).ToArray(),
        vehicle_buy_multiplier_basis_points = rule.VehicleBuyMultiplier.Value,
        vehicle_sell_multiplier_basis_points = rule.VehicleSellMultiplier.Value,
        vehicle_purchase_limits = rule.VehiclePurchaseLimits.OrderBy(limit => DesignEvaluator.Wire(limit.Size), StringComparer.Ordinal).Select(limit => new
        {
            size = DesignEvaluator.Wire(limit.Size), maximum_for_sale = limit.MaximumForSale
        }).ToArray(),
        automatic_evaluation_interval_ticks = rule.AutomaticEvaluationIntervalTicks, recent_report_limit = rule.RecentReportLimit,
        commission_money_per_work = rule.CommissionMoneyPerWork.Value, rent_money_per_capacity_hour = rule.RentMoneyPerCapacityHour.Value,
        rentable_capacity_ratio_basis_points = rule.RentableCapacityRatio.Value, lease_duration_ticks = rule.LeaseDurationTicks
    });

    private static object Prices(K.GoodsPriceParameters price) => new
    {
        sensitivity_basis_points = price.Sensitivity.Value, minimum_multiplier_basis_points = price.MinimumMultiplier.Value,
        maximum_multiplier_basis_points = price.MaximumMultiplier.Value, spread_basis_points = price.Spread.Value
    };

    internal static K.SemanticRevision Revision(K.ConstructionRuleProjection rule) => Semantic("construction-rule", rule.Id.Value, new
    {
        founding_duration_ticks = rule.FoundingDurationTicks, construction_work_per_item = rule.ConstructionWorkPerItem,
        refit_work_per_changed_item = rule.RefitWorkPerChangedItem, deconstruction_work_per_item = rule.DeconstructionWorkPerItem,
        repair_work_per_damaged_part = rule.RepairWorkPerDamagedPart, power_per_work = rule.PowerPerWork, labor_per_work = rule.LaborPerWork
    });

    internal static K.SemanticRevision Revision(K.RailwayRuleProjection rule) => Semantic("railway-rule", rule.Id.Value, new
    {
        braking_deceleration = rule.BrakingDeceleration, minimum_acceleration = rule.MinimumAcceleration,
        minimum_gap_meters = rule.MinimumGapMeters, rail_good = rule.RailGood.Value, rail_units_per_meter = rule.RailUnitsPerMeter,
        build_seconds_per_meter = rule.BuildSecondsPerMeter, repair_rail_units_per_break = rule.RepairRailUnitsPerBreak,
        repair_seconds_per_break = rule.RepairSecondsPerBreak,
        approach_length_meters = rule.ApproachLengthMeters, departure_length_meters = rule.DepartureLengthMeters,
        node_speed_limit_meters_per_second = rule.NodeSpeedLimitMetersPerSecond
    });

    internal static K.SemanticRevision Revision(K.CombatRuleProjection rule) => Semantic("combat-rule", rule.Id.Value, new
    {
        retreat_seconds = rule.RetreatSeconds, morale_damage_coefficient = rule.MoraleDamageCoefficient,
        weapon_destroyed_morale_loss = rule.WeaponDestroyedMoraleLoss, morale_recovery_per_second = rule.MoraleRecoveryPerSecond,
        surrender_threshold = rule.SurrenderThreshold, new_lair_interval_seconds = rule.NewLairIntervalSeconds
    });
}
