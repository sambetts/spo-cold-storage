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
  if (status === MigrationLifecycleStatus.CompletedWithWarning ||
      status === MigrationLifecycleStatus.RetryScheduled ||
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
