namespace Gaska.Payments.Domain.Matching;

/// <summary>One answer to "which documents add up to the amount received".</summary>
public sealed record SubsetSolution(IReadOnlyList<int> Indices, long Sum, long Difference);

/// <summary>
/// Finds a subset of amounts summing to a given value - the heart of handling bulk payments,
/// where a customer settles a dozen invoices with a single transfer.
/// </summary>
/// <remarks>
/// Amounts are kept in minor units (long) to avoid rounding errors. Values may be negative
/// (corrections). The search is a depth-first walk pruned by suffix sums, with a hard cap on
/// visited nodes so that a large portfolio of receivables cannot stall the service.
/// </remarks>
public static class SubsetSumSolver
{
    public static IReadOnlyList<SubsetSolution> Solve(
        IReadOnlyList<long> values,
        long target,
        int maxItems,
        long tolerance = 0,
        int maxSolutions = 20,
        int nodeBudget = 400_000)
    {
        var solutions = new List<SubsetSolution>();
        if (values.Count == 0) return solutions;

        // Descending by absolute value - that prunes branches soonest.
        var order = Enumerable.Range(0, values.Count)
            .OrderByDescending(i => Math.Abs(values[i]))
            .ToArray();

        var sorted = order.Select(i => values[i]).ToArray();
        var n = sorted.Length;

        // suffixMax[i] is the largest sum reachable from element i onwards (taking only the
        // positive ones), suffixMin[i] the smallest (taking only the negative ones).
        var suffixMax = new long[n + 1];
        var suffixMin = new long[n + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            suffixMax[i] = suffixMax[i + 1] + Math.Max(0, sorted[i]);
            suffixMin[i] = suffixMin[i + 1] + Math.Min(0, sorted[i]);
        }

        var chosen = new List<int>(Math.Min(maxItems, n));
        var nodes = 0;

        void Search(int index, long sum)
        {
            if (solutions.Count >= maxSolutions) return;
            if (++nodes > nodeBudget) return;

            var difference = Math.Abs(sum - target);
            if (chosen.Count > 0 && difference <= tolerance)
            {
                solutions.Add(new SubsetSolution([.. chosen.Select(i => order[i])], sum, sum - target));
                // Any superset of this solution would sum to something else, so we only descend
                // further if zero-valued elements remain, which in practice they do not.
                return;
            }

            if (index >= n || chosen.Count >= maxItems) return;

            // Prune: even taking every positive (or every negative) item we cannot reach the target.
            if (sum + suffixMax[index] < target - tolerance) return;
            if (sum + suffixMin[index] > target + tolerance) return;

            chosen.Add(index);
            Search(index + 1, sum + sorted[index]);
            chosen.RemoveAt(chosen.Count - 1);

            Search(index + 1, sum);
        }

        Search(0, 0);
        return solutions;
    }

    public static long ToCents(decimal value) => (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
}
