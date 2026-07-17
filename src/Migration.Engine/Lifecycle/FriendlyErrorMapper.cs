using Models.ColdStorage;
using System.Text.RegularExpressions;

namespace Migration.Engine.Lifecycle;

/// <summary>
/// Maps raw exception text / failure messages to short, actionable, user-facing
/// summaries for the job-status "Last error" column (issue #5). The raw detail
/// is kept separately (item.LastErrorDetail + the job log) for support staff.
/// </summary>
public static partial class FriendlyErrorMapper
{
    public static string ToFriendly(Exception exception, MigrationLifecycleStatus status)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ToFriendly(exception.Message, status);
    }

    public static string ToFriendly(string? rawMessage, MigrationLifecycleStatus status)
    {
        var msg = rawMessage ?? string.Empty;

        // Skip reasons are already written for humans — keep them verbatim.
        if (status == MigrationLifecycleStatus.Skipped)
        {
            return string.IsNullOrWhiteSpace(msg) ? "Skipped." : Sanitize(msg);
        }

        var restoreContext = status is MigrationLifecycleStatus.RestoreFailed
            or MigrationLifecycleStatus.RestoredToSharePoint
            or MigrationLifecycleStatus.PostRestoreValidation
            or MigrationLifecycleStatus.PlaceholderRemoving
            or MigrationLifecycleStatus.PlaceholderRemoveFailed
            or MigrationLifecycleStatus.RestoreInProgress
            or MigrationLifecycleStatus.RestoreCompleted;

        // Keyword matches, most specific first.
        if (ContainsAny(msg, "checked out", "checkout", "CheckOutType"))
        {
            return "The file is checked out in SharePoint, so it couldn't be removed. Ask whoever has it checked out to check it in, then retry.";
        }
        if (ContainsAny(msg, "429", "throttl", "Too Many Requests"))
        {
            return status.IsTerminal()
                ? "SharePoint or Azure throttled the request too many times, so it was stopped after several automatic retries. Re-queue to try again."
                : "SharePoint or Azure is busy and throttled the request. It is waiting and will be retried automatically.";
        }
        if (ContainsAny(msg, "timeout", "timed out", "TaskCanceled", "operation was canceled", "operation was cancelled"))
        {
            return status.IsTerminal()
                ? "The operation kept timing out and was stopped after several automatic retries. Re-queue to try again."
                : "The operation timed out. It is waiting and will be retried automatically.";
        }
        if (ContainsAny(msg, "401", "403", "access denied", "accessdenied", "unauthorized", "forbidden", "AADSTS"))
        {
            return "Access was denied. The app may be missing permission to this site, the file, or the cold-storage container.";
        }
        if (ContainsAny(msg, "MD5", "hash", "does not match", "mismatch", "integrity", "length"))
        {
            return "The copy in cold storage didn't match the original (integrity check failed). The original was left untouched.";
        }
        if (ContainsAny(msg, "404", "not found", "filenotfound", "blobnotfound", "could not be found", "does not exist"))
        {
            return restoreContext
                ? "The archived copy couldn't be found in cold storage."
                : "The file couldn't be found in SharePoint — it may have been moved or deleted.";
        }
        if (ContainsAny(msg, "service bus", "servicebus", "publish to service bus", "messaging"))
        {
            return "The request couldn't be queued (messaging service problem). Please try again.";
        }
        if (ContainsAny(msg, "blob", "storage account", "container"))
        {
            return "There was a problem talking to Azure cold storage. Please try again.";
        }

        return ByStatus(status, restoreContext) ?? Sanitize(msg);
    }

    private static string? ByStatus(MigrationLifecycleStatus status, bool restoreContext) => status switch
    {
        MigrationLifecycleStatus.CopyToColdStorageFailed => "Couldn't copy the file to cold storage. The original is safe in SharePoint.",
        MigrationLifecycleStatus.DeleteFailed => "The file was archived but couldn't be removed from SharePoint. The archived copy is safe.",
        MigrationLifecycleStatus.PlaceholderFailed => "The file was archived but its placeholder link couldn't be created.",
        MigrationLifecycleStatus.ValidationFailed => "The item couldn't be validated for archiving.",
        MigrationLifecycleStatus.RestoreFailed => "The file couldn't be restored from cold storage. The archived copy is intact.",
        MigrationLifecycleStatus.PlaceholderRemoveFailed => "The file was restored but its placeholder couldn't be removed.",
        _ => restoreContext ? "The restore didn't complete. Please try again." : null,
    };

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Last-resort cleanup of an otherwise-unmapped message: take the first line,
    /// strip GUIDs and long hex blobs, and cap the length so the column never
    /// shows a bare GUID or a stack trace.
    /// </summary>
    private static string Sanitize(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg))
        {
            return "The operation failed. See the job log for details.";
        }
        var firstLine = msg.Replace("\r", string.Empty).Split('\n')[0].Trim();
        firstLine = GuidRegex().Replace(firstLine, "…");
        firstLine = LongHexRegex().Replace(firstLine, "…");
        firstLine = firstLine.Trim();
        if (firstLine.Length > 200)
        {
            firstLine = firstLine[..200].TrimEnd() + "…";
        }
        return string.IsNullOrWhiteSpace(firstLine)
            ? "The operation failed. See the job log for details."
            : firstLine;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b(?:0x)?[0-9a-fA-F]{12,}\b")]
    private static partial Regex LongHexRegex();
}
