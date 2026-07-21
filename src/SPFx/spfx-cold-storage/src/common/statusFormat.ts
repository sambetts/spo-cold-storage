/**
 * Shared formatting / lifecycle utilities used by both the status field
 * customizer and the migration progress dialog. Kept defensive about its
 * input type so it works regardless of whether the backend serializes
 * MigrationLifecycleStatus as strings (the intent, after Web.Server registers
 * JsonStringEnumConverter) or as raw integers (default System.Text.Json
 * behaviour, the historical contract). Treat any input as opaque and map it
 * to a known status name.
 */
import { MigrationLifecycleStatus } from './ColdStorageApiClient';

// Mirror of MigrationLifecycleStatus integer values from the C# enum in
// src/Models/ColdStorage/MigrationLifecycleStatus.cs. Used to translate
// integer responses into the canonical enum names so the rest of the UI
// stays string-driven.
const NUMERIC_TO_NAME: Record<number, MigrationLifecycleStatus> = {
  0:  MigrationLifecycleStatus.Queued,
  10: MigrationLifecycleStatus.Validating,
  11: MigrationLifecycleStatus.ValidationFailed,
  20: MigrationLifecycleStatus.MigrationInProgress,
  21: MigrationLifecycleStatus.CopiedToColdStorage,
  22: MigrationLifecycleStatus.CopyToColdStorageFailed,
  23: MigrationLifecycleStatus.PostCopyValidation,
  24: MigrationLifecycleStatus.DeletePending,
  25: MigrationLifecycleStatus.DeleteFailed,
  26: MigrationLifecycleStatus.PlaceholderCreating,
  27: MigrationLifecycleStatus.PlaceholderFailed,
  30: MigrationLifecycleStatus.ColdStorageMigrationCompleted,
  40: MigrationLifecycleStatus.RestoreInProgress,
  41: MigrationLifecycleStatus.RestoredToSharePoint,
  42: MigrationLifecycleStatus.RestoreFailed,
  43: MigrationLifecycleStatus.PostRestoreValidation,
  44: MigrationLifecycleStatus.PlaceholderRemoving,
  45: MigrationLifecycleStatus.PlaceholderRemoveFailed,
  50: MigrationLifecycleStatus.RestoreCompleted,
  60: MigrationLifecycleStatus.CompletedWithWarning,
  70: MigrationLifecycleStatus.RetryScheduled,
  80: MigrationLifecycleStatus.Cancelled,
  81: MigrationLifecycleStatus.Skipped,
};

const TERMINAL: ReadonlySet<MigrationLifecycleStatus> = new Set([
  MigrationLifecycleStatus.ColdStorageMigrationCompleted,
  MigrationLifecycleStatus.RestoreCompleted,
  MigrationLifecycleStatus.ValidationFailed,
  MigrationLifecycleStatus.CopyToColdStorageFailed,
  MigrationLifecycleStatus.DeleteFailed,
  MigrationLifecycleStatus.PlaceholderFailed,
  MigrationLifecycleStatus.RestoreFailed,
  MigrationLifecycleStatus.PlaceholderRemoveFailed,
  MigrationLifecycleStatus.CompletedWithWarning,
  MigrationLifecycleStatus.Cancelled,
  MigrationLifecycleStatus.Skipped,
]);

const FAILED_SUFFIX_REGEX = /Failed$/;

export type StatusLike = MigrationLifecycleStatus | string | number | null | undefined;

/**
 * Normalize any status representation we might receive (enum, string, integer)
 * into the canonical MigrationLifecycleStatus name. Returns undefined when the
 * value can't be recognized so callers can render a neutral fallback.
 */
export function normalizeStatus(value: StatusLike): MigrationLifecycleStatus | undefined {
  if (value === null || value === undefined) {
    return undefined;
  }
  if (typeof value === 'number') {
    return NUMERIC_TO_NAME[value];
  }
  // string or enum member (enum members are strings at runtime)
  const known = (Object.values(MigrationLifecycleStatus) as string[]).indexOf(value as string);
  return known >= 0 ? (value as MigrationLifecycleStatus) : undefined;
}

/**
 * Background colour for a status badge. Picks a sensible default for
 * unrecognized values so we never render empty / invisible chips.
 */
export function colorFor(value: StatusLike): string {
  const status = normalizeStatus(value);
  if (!status) return '#888';
  if (FAILED_SUFFIX_REGEX.test(status) || status === MigrationLifecycleStatus.Cancelled) {
    return '#a4262c';
  }
  if (status === MigrationLifecycleStatus.RetryScheduled) {
    return '#ca5010'; // amber — actively waiting out a backoff before an automatic retry (e.g. throttled)
  }
  if (status === MigrationLifecycleStatus.CompletedWithWarning ||
      status === MigrationLifecycleStatus.Skipped ||
      status === MigrationLifecycleStatus.PlaceholderRemoveFailed) {
    return '#797775';
  }
  if (status === MigrationLifecycleStatus.ColdStorageMigrationCompleted ||
      status === MigrationLifecycleStatus.RestoreCompleted) {
    return '#107c10';
  }
  return '#0078d4';
}

/**
 * Human-friendly label — splits PascalCase into spaced words. Falls back to
 * the raw value (or "Unknown") when the input is unrecognised so we never
 * silently render nothing.
 */
export function formatLabel(value: StatusLike): string {
  const status = normalizeStatus(value);
  if (status) {
    return status.replace(/([A-Z])/g, ' $1').replace(/^./, c => c.toUpperCase()).trim();
  }
  if (value === null || value === undefined || value === '') return 'Unknown';
  return String(value);
}

/**
 * True when the lifecycle will not transition further without an explicit
 * retry or re-queue. Mirrors MigrationLifecycleStatusExtensions.IsTerminal on
 * the backend.
 */
export function isTerminal(value: StatusLike): boolean {
  const status = normalizeStatus(value);
  return status ? TERMINAL.has(status) : false;
}

/**
 * Compact "time from now" for a future instant, e.g. "in 45s", "in 12m", "in 3h",
 * or "now" when due/overdue. Returns "" for missing/invalid values.
 */
export function formatCountdown(value: string | null | undefined): string {
  if (!value) return '';
  const target = new Date(value).getTime();
  if (isNaN(target)) return '';
  const seconds = Math.round((target - Date.now()) / 1000);
  if (seconds <= 0) return 'now';
  if (seconds < 60) return `in ${seconds}s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `in ${minutes}m`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `in ${hours}h`;
  return `in ${Math.round(hours / 24)}d`;
}

/** ETA label combining clock time and countdown, e.g. "~14:32 (in 12m)". */
export function formatEta(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  if (isNaN(date.getTime())) return '';
  const clock = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' }).format(date);
  return `~${clock} (${formatCountdown(value)})`;
}

/**
 * Culture-aware number formatting with grouping separators, using the user's locale:
 * 1000 -> "1,000" (en-US/GB), "1.000" (es-ES). Values under 1000 are unchanged.
 * Null/undefined/NaN -> "0".
 */
export function formatNumber(value: number | null | undefined, options?: Intl.NumberFormatOptions): string {
  if (value === null || value === undefined || isNaN(value)) return '0';
  return value.toLocaleString(undefined, options);
}

const DESCRIPTIONS: Partial<Record<MigrationLifecycleStatus, string>> = {
  [MigrationLifecycleStatus.Queued]: 'Waiting for a background worker to pick this up.',
  [MigrationLifecycleStatus.Validating]: 'Checking the item is eligible and reading its details.',
  [MigrationLifecycleStatus.ValidationFailed]: 'The item couldn\u2019t be validated for archiving.',
  [MigrationLifecycleStatus.MigrationInProgress]: 'Copying the file from SharePoint to cold storage.',
  [MigrationLifecycleStatus.CopiedToColdStorage]: 'Copied to cold storage; verifying the copy.',
  [MigrationLifecycleStatus.CopyToColdStorageFailed]: 'Couldn\u2019t copy to cold storage. The original is safe in SharePoint.',
  [MigrationLifecycleStatus.PostCopyValidation]: 'Verifying the copied file (size + checksum).',
  [MigrationLifecycleStatus.DeletePending]: 'Copy verified \u2014 removing the original from SharePoint.',
  [MigrationLifecycleStatus.DeleteFailed]: 'Archived, but the original couldn\u2019t be removed from SharePoint.',
  [MigrationLifecycleStatus.PlaceholderCreating]: 'Creating the .url placeholder in SharePoint.',
  [MigrationLifecycleStatus.PlaceholderFailed]: 'Archived, but the placeholder link couldn\u2019t be created.',
  [MigrationLifecycleStatus.ColdStorageMigrationCompleted]: 'Migration complete \u2014 file archived and placeholder created.',
  [MigrationLifecycleStatus.RestoreInProgress]: 'Downloading from cold storage and uploading back to SharePoint.',
  [MigrationLifecycleStatus.RestoredToSharePoint]: 'Content restored to SharePoint; verifying.',
  [MigrationLifecycleStatus.RestoreFailed]: 'The file couldn\u2019t be restored. The archived copy is intact.',
  [MigrationLifecycleStatus.PostRestoreValidation]: 'Verifying the restored file (size + checksum).',
  [MigrationLifecycleStatus.PlaceholderRemoving]: 'Removing the .url placeholder now the file is back.',
  [MigrationLifecycleStatus.PlaceholderRemoveFailed]: 'Restored, but the placeholder couldn\u2019t be removed.',
  [MigrationLifecycleStatus.RestoreCompleted]: 'Restore complete \u2014 file is back in SharePoint.',
  [MigrationLifecycleStatus.CompletedWithWarning]: 'Finished, but one or more items need attention.',
  [MigrationLifecycleStatus.RetryScheduled]: 'Throttled or hit a transient error \u2014 waiting, then retrying automatically.',
  [MigrationLifecycleStatus.Cancelled]: 'The operation was cancelled.',
  [MigrationLifecycleStatus.Skipped]: 'Deliberately not archived (ineligible or excluded); the original is untouched.',
};

/**
 * A short, human-friendly sentence explaining what a status means / what is
 * happening right now. Falls back to the spaced label when unrecognised so the
 * UI always shows something meaningful.
 */
export function describeStatus(value: StatusLike): string {
  const status = normalizeStatus(value);
  if (status && DESCRIPTIONS[status]) {
    return DESCRIPTIONS[status] as string;
  }
  return formatLabel(value);
}
