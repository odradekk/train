using System.Globalization;
using Train.Core.Contracts;

namespace Train.Core.World;

internal sealed class GenerationFailure(string category, string field) : Exception
{
    internal string Category { get; } = category;
    internal string Field { get; } = field;
}

internal sealed class GenerationBudgetCounter(GenerationBudget limits)
{
    private string _stage = "configuration";
    internal ulong WorldRetry { get; set; }
    internal string Stage
    {
        get => _stage;
        set { _stage = value; Candidate = ""; }
    }
    internal string Candidate { get; set; } = "";
    internal long UsedCandidates { get; private set; }
    internal long UsedSearchWork { get; private set; }

    internal void CandidateCount()
    {
        if (UsedCandidates == limits.MaximumCandidates)
            throw new GenerationFailure("budget_exhausted", "Configuration.Budget.MaximumCandidates");
        UsedCandidates++;
    }

    internal void Work(long units = 1)
    {
        if (units > limits.MaximumSearchWork - UsedSearchWork)
            throw new GenerationFailure("budget_exhausted", "Configuration.Budget.MaximumSearchWork");
        UsedSearchWork += units;
    }

    internal void Sort(int count) => Work((long)count * count);

    internal CoreProblem Problem(GenerationFailure failure) => new("generation_failed", failure.Field, null,
        [failure.Category, Stage, Candidate, WorldRetry.ToString(CultureInfo.InvariantCulture),
            UsedCandidates.ToString(CultureInfo.InvariantCulture), UsedSearchWork.ToString(CultureInfo.InvariantCulture)]);
}
