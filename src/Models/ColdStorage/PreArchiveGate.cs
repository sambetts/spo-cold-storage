namespace Models.ColdStorage;

/// <summary>
/// Lifecycle of a pre-archive notice (issue #17).
/// </summary>
public enum PreArchiveNoticeStatus
{
    /// <summary>Notice sent; within the grace window.</summary>
    Pending = 0,
    /// <summary>Grace window elapsed; archiving may proceed.</summary>
    Proceeded = 1,
    /// <summary>Notice withdrawn (e.g. the file was edited again).</summary>
    Cancelled = 2,
}

/// <summary>
/// What the auto-archive trigger should do for a candidate, given whether a
/// pre-archive notice has been sent and the grace period has elapsed.
/// </summary>
public enum PreArchiveDecision
{
    /// <summary>No notice yet — send one and start the grace window.</summary>
    SendNotice,
    /// <summary>Notice sent but the grace window hasn't elapsed — don't archive yet.</summary>
    Waiting,
    /// <summary>Grace elapsed (or the feature is disabled) — archiving may proceed.</summary>
    Proceed,
}

/// <summary>
/// Pure decision for the pre-archive notification grace period (issue #17). Lets
/// users be warned before an auto-archive moves their file, with a configurable
/// window. When the grace period is disabled (0 or less) archiving proceeds with
/// no notice — matching today's user-initiated flow.
/// </summary>
public static class PreArchiveGate
{
    public static PreArchiveDecision Decide(DateTime? graceUntilUtc, DateTime nowUtc, int graceHours)
    {
        if (graceHours <= 0)
        {
            return PreArchiveDecision.Proceed;
        }
        if (graceUntilUtc is null)
        {
            return PreArchiveDecision.SendNotice;
        }
        return nowUtc >= graceUntilUtc.Value ? PreArchiveDecision.Proceed : PreArchiveDecision.Waiting;
    }
}
