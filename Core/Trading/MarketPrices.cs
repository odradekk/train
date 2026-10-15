using System.Numerics;
using Train.Core.Contracts;
using Train.Core.World;

namespace Train.Core.Trading;

internal static class MarketPrices
{
    internal static Money Good(Money value, GoodsPriceParameters parameters, long target, BigInteger twiceStock, bool buy)
    {
        BigInteger numerator;
        BigInteger denominator;
        if (target == 0)
        {
            numerator = (BigInteger)value.Value * parameters.MinimumMultiplier.Value * (10000 + parameters.Spread.Value);
            denominator = 100_000_000;
        }
        else
        {
            var twiceTarget = (BigInteger)target * 2;
            var multiplier = twiceTarget * 10000 + parameters.Sensitivity.Value * (twiceTarget - twiceStock);
            multiplier = BigInteger.Clamp(multiplier, twiceTarget * parameters.MinimumMultiplier.Value,
                twiceTarget * parameters.MaximumMultiplier.Value);
            numerator = value.Value * multiplier * (buy ? 10000 + parameters.Spread.Value : 10000 - parameters.Spread.Value);
            denominator = twiceTarget * 100_000_000;
        }
        return new(Math.Max(1, checked((long)Round(numerator, denominator, buy))));
    }

    internal static Money Vehicle(InstanceState instance, DesignProjection design, BasisPoints multiplier, bool ceiling)
    {
        BigInteger numerator = 0, denominator = 1;
        Add(design.StructuralBaseValue.Value, Math.Min(instance.StructureDurability, design.MaxStructureDurability), design.MaxStructureDurability);
        foreach (var part in design.Parts)
            Add(part.BaseValue.Value, Math.Min(instance.Parts.Single(p => p.Id == part.Id).Current, part.MaxDurability), part.MaxDurability);
        return new(checked((long)Round(numerator * multiplier.Value, denominator * 10000, ceiling)));

        void Add(long value, long current, long maximum)
        {
            numerator = numerator * maximum + (BigInteger)value * current * denominator;
            denominator *= maximum;
            var divisor = BigInteger.GreatestCommonDivisor(numerator, denominator);
            numerator /= divisor;
            denominator /= divisor;
        }
    }

    private static BigInteger Round(BigInteger numerator, BigInteger denominator, bool ceiling)
        => BigInteger.DivRem(numerator, denominator, out var remainder) + (ceiling && remainder != 0 ? 1 : 0);
}
