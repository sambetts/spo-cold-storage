namespace Migration.Engine.Lifecycle;

/// <summary>
/// Pure, unit-testable estimator for when a migration/restore job will finish. Combines a
/// throughput estimate (how fast items have been completing) with a throttle floor (a job can
/// never finish before the latest scheduled throttle retry fires), so an ETA that throttling
/// pushes further out is reflected honestly. Kept free of EF/HTTP so it can be tested and reused
/// by every endpoint that needs an ETA.
/// </summary>
public static class JobEtaCalculator
{
    /// <summary>
    /// Estimates the UTC completion time, or null when it can't be estimated yet (nothing
    /// remaining, or no throughput and no scheduled retry to anchor to).
    /// </summary>
    /// <param name="completedItems">Items that finished successfully (drives the throughput rate).</param>
    /// <param name="remainingItems">Non-terminal items still to process.</param>
    /// <param name="jobStartUtc">When the job started (created).</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="latestNextRetryUtc">The furthest-out scheduled retry among waiting items, if any.</param>
    public static DateTime? EstimateCompletion(
        int completedItems,
        int remainingItems,
        DateTime jobStartUtc,
        DateTime nowUtc,
        DateTime? latestNextRetryUtc)
    {
        if (remainingItems <= 0)
        {
            return null;
        }

        DateTime? throughputEta = null;
        var elapsedSeconds = (nowUtc - jobStartUtc).TotalSeconds;
        if (completedItems > 0 && elapsedSeconds > 1)
        {
            var ratePerSecond = completedItems / elapsedSeconds;
            if (ratePerSecond > 0)
            {
                throughputEta = nowUtc.AddSeconds(remainingItems / ratePerSecond);
            }
        }

        // A job can't finish before its latest scheduled throttle retry fires.
        var floor = latestNextRetryUtc;

        if (throughputEta is null)
        {
            // No completions yet to measure a rate — the best anchor we have is when the
            // furthest-out retry is due (null if nothing is scheduled → unknown).
            return floor;
        }
        if (floor is DateTime f && f > throughputEta.Value)
        {
            return f;
        }
        return throughputEta;
    }
}
