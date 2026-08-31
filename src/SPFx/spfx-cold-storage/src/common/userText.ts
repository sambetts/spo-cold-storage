/**
 * Plain-English wording for the SharePoint (end-user) surface — issue #70.
 *
 * The SPFx extension is what ordinary site users see. They did not ask for a
 * lifecycle state machine: they asked for their files to be tidied away, or
 * brought back. So this module is the single place that translates the
 * product's internal vocabulary into something a non-technical person can act
 * on, and it deliberately hides:
 *
 *   - lifecycle status names ("PostCopyValidation", "PlaceholderCreating"),
 *   - raw server error strings and stack detail,
 *   - job GUIDs, blob paths, container names and other plumbing,
 *   - the activity/log timeline.
 *
 * Two rules drive the wording:
 *   1. **Reassure about the file.** This product deletes people's files, so
 *      every failure message says explicitly where their content is. "Your
 *      file is safe and unchanged" is the most important sentence in the UI.
 *   2. **Only surface what the user can act on.** A transient throttle that
 *      retries by itself is not an error and must not be shown as one; a
 *      genuine dead end is, and only then do we offer the technical detail
 *      (collapsed) so they can pass it to whoever supports them.
 *
 * NOTE ON PARITY: `statusFormat.ts` is shared with the SPA and stays
 * technical on purpose — the SPA is the admin/accountability console, where
 * exact statuses, raw errors and the audit timeline are the point. This file
 * is intentionally SPFx-only and must not be "kept in sync" with the SPA.
 */
import { MigrationLifecycleStatus } from './ColdStorageApiClient';
import { normalizeStatus, StatusLike } from './statusFormat';

export type OperationKind = 'Migrate' | 'Restore';

/**
 * Short label for a file's current state, e.g. "Moving to archive".
 * Falls back to a neutral "Working on it" rather than leaking a status name.
 */
export function friendlyStatusLabel(value: StatusLike, operation: OperationKind = 'Migrate'): string {
  const status = normalizeStatus(value);
  const restoring = operation === 'Restore';
  switch (status) {
    case MigrationLifecycleStatus.Queued:
      return 'Waiting to start';
    case MigrationLifecycleStatus.Validating:
      return 'Getting ready';
    case MigrationLifecycleStatus.MigrationInProgress:
      return 'Moving to archive';
    case MigrationLifecycleStatus.CopiedToColdStorage:
    case MigrationLifecycleStatus.PostCopyValidation:
      return 'Checking the copy';
    case MigrationLifecycleStatus.DeletePending:
      return 'Tidying up';
    case MigrationLifecycleStatus.PlaceholderCreating:
      return 'Adding the shortcut';
    case MigrationLifecycleStatus.ColdStorageMigrationCompleted:
      return 'Archived';
    case MigrationLifecycleStatus.RestoreInProgress:
      return 'Bringing it back';
    case MigrationLifecycleStatus.RestoredToSharePoint:
    case MigrationLifecycleStatus.PostRestoreValidation:
      return 'Checking the file';
    case MigrationLifecycleStatus.PlaceholderRemoving:
      return 'Tidying up';
    case MigrationLifecycleStatus.RestoreCompleted:
      return 'Back in place';
    case MigrationLifecycleStatus.RetryScheduled:
      return 'Busy — retrying';
    case MigrationLifecycleStatus.Skipped:
      return 'Nothing to do';
    case MigrationLifecycleStatus.Cancelled:
      return 'Cancelled';
    case MigrationLifecycleStatus.CompletedWithWarning:
      return 'Needs a look';
    case MigrationLifecycleStatus.ValidationFailed:
    case MigrationLifecycleStatus.CopyToColdStorageFailed:
      return restoring ? 'Couldn’t bring back' : 'Couldn’t archive';
    case MigrationLifecycleStatus.DeleteFailed:
      return 'Archived, needs a look';
    case MigrationLifecycleStatus.PlaceholderFailed:
      return 'Archived, no shortcut';
    case MigrationLifecycleStatus.RestoreFailed:
      return 'Couldn’t bring back';
    case MigrationLifecycleStatus.PlaceholderRemoveFailed:
      return 'Back, shortcut left over';
    default:
      return 'Working on it';
  }
}

/**
 * One reassuring sentence explaining what happened to the user's file, and —
 * where it matters — exactly where their content is right now.
 */
export function friendlyStatusExplanation(value: StatusLike, operation: OperationKind = 'Migrate'): string {
  const status = normalizeStatus(value);
  switch (status) {
    case MigrationLifecycleStatus.Queued:
      return 'Waiting its turn. You can close this window — it carries on in the background.';
    case MigrationLifecycleStatus.Validating:
      return 'Checking this file can be archived.';
    case MigrationLifecycleStatus.MigrationInProgress:
      return 'Copying this file to long-term storage.';
    case MigrationLifecycleStatus.CopiedToColdStorage:
    case MigrationLifecycleStatus.PostCopyValidation:
      return 'Making sure the copy is complete before anything is changed here.';
    case MigrationLifecycleStatus.DeletePending:
      return 'The copy is confirmed good, so the original is being replaced with a shortcut.';
    case MigrationLifecycleStatus.PlaceholderCreating:
      return 'Adding the shortcut that opens the archived file.';
    case MigrationLifecycleStatus.ColdStorageMigrationCompleted:
      return 'Archived. A shortcut is in its place — open it any time to get the file back.';
    case MigrationLifecycleStatus.RestoreInProgress:
      return 'Copying this file back from long-term storage.';
    case MigrationLifecycleStatus.RestoredToSharePoint:
    case MigrationLifecycleStatus.PostRestoreValidation:
      return 'Making sure the file came back complete.';
    case MigrationLifecycleStatus.PlaceholderRemoving:
      return 'Removing the shortcut now the file itself is back.';
    case MigrationLifecycleStatus.RestoreCompleted:
      return 'Back where it was, ready to use.';
    case MigrationLifecycleStatus.RetryScheduled:
      return 'SharePoint is busy right now, so this will try again automatically. Nothing for you to do.';
    case MigrationLifecycleStatus.Skipped:
      return 'Already how you wanted it, so nothing needed doing.';
    case MigrationLifecycleStatus.Cancelled:
      return 'This was cancelled. Your file has not been changed.';
    case MigrationLifecycleStatus.ValidationFailed:
    case MigrationLifecycleStatus.CopyToColdStorageFailed:
      return operation === 'Restore'
        ? 'This one couldn’t be brought back. The archived copy is safe — please try again.'
        : 'This one couldn’t be archived. Your file is safe and unchanged — please try again.';
    case MigrationLifecycleStatus.DeleteFailed:
      return 'It was archived safely, but the original couldn’t be removed. There are two copies for now — nothing has been lost.';
    case MigrationLifecycleStatus.PlaceholderFailed:
      return 'It was archived safely, but the shortcut couldn’t be added. Ask your site owner to bring it back if you need it.';
    case MigrationLifecycleStatus.RestoreFailed:
      return 'This one couldn’t be brought back. The archived copy is safe — please try again.';
    case MigrationLifecycleStatus.PlaceholderRemoveFailed:
      return 'The file is back, but its old shortcut is still here. You can delete the shortcut.';
    case MigrationLifecycleStatus.CompletedWithWarning:
      return 'Finished, but some files need a look.';
    default:
      return '';
  }
}

/**
 * True when the user needs to know about this and possibly act — a genuine dead
 * end. A throttle that retries by itself, a skip, or a cancel are NOT failures
 * and must not be shown as errors.
 */
export function isUserFacingFailure(value: StatusLike): boolean {
  const status = normalizeStatus(value);
  switch (status) {
    case MigrationLifecycleStatus.ValidationFailed:
    case MigrationLifecycleStatus.CopyToColdStorageFailed:
    case MigrationLifecycleStatus.DeleteFailed:
    case MigrationLifecycleStatus.PlaceholderFailed:
    case MigrationLifecycleStatus.RestoreFailed:
    case MigrationLifecycleStatus.PlaceholderRemoveFailed:
      return true;
    default:
      return false;
  }
}

/**
 * Headline for a finished job — what actually happened, in the user's terms.
 */
export function friendlyJobOutcome(
  counts: { total: number; completed: number; failed: number; skipped: number },
  operation: OperationKind,
): { headline: string; tone: 'good' | 'attention' } {
  const verbed = operation === 'Restore' ? 'brought back' : 'archived';
  if (counts.total === 0) {
    return { headline: 'Nothing needed doing.', tone: 'good' };
  }
  if (counts.failed === 0 && counts.skipped === 0) {
    return {
      headline: counts.total === 1 ? `Your file has been ${verbed}.` : `All ${counts.total} files have been ${verbed}.`,
      tone: 'good',
    };
  }
  if (counts.failed === 0) {
    return {
      headline: `${counts.completed} ${verbed}. ${counts.skipped} didn’t need doing.`,
      tone: 'good',
    };
  }
  return {
    headline: counts.completed > 0
      ? `${counts.completed} ${verbed}. ${counts.failed} couldn’t be — those files are unchanged.`
      : `Nothing could be ${verbed}. Your files are unchanged.`,
    tone: 'attention',
  };
}

/** Friendly progress line while a job is running. */
export function friendlyProgress(
  counts: { total: number; completed: number; failed: number; skipped: number },
  operation: OperationKind,
): string {
  const done = counts.completed + counts.failed + counts.skipped;
  const verb = operation === 'Restore' ? 'Bringing back' : 'Archiving';
  if (counts.total <= 0) {
    return `${verb} your files…`;
  }
  return `${verb} your files — ${done} of ${counts.total} done`;
}

/**
 * Turns any thrown error into something a non-technical user can act on.
 * Deliberately does NOT surface status codes or server text: those go in the
 * collapsed technical detail for support instead.
 */
export function friendlyErrorMessage(status: number | undefined, fallback: string): string {
  if (status === 401 || status === 403) {
    return 'You don’t have permission to do this here. Ask your site owner if you need access.';
  }
  if (status === 0) {
    return 'We couldn’t reach the archive service. Check your connection and try again.';
  }
  if (status === 404) {
    return 'We couldn’t find that item. It may have been moved or already dealt with.';
  }
  if (status !== undefined && status >= 500) {
    return 'The archive service is having a problem right now. Please try again in a few minutes.';
  }
  return fallback;
}
