import { MigrationLifecycleStatus, MigrationOperationKind } from "./types";

/** Maps to Fluent UI Badge `color` values. */
export type StatusTone = "success" | "danger" | "warning" | "informative" | "subtle";

export interface StatusDescriptor {
  label: string;
  tone: StatusTone;
}

const STATUS_MAP: Record<MigrationLifecycleStatus, StatusDescriptor> = {
  Queued: { label: "Queued", tone: "informative" },
  Validating: { label: "Validating", tone: "informative" },
  ValidationFailed: { label: "Validation failed", tone: "danger" },
  MigrationInProgress: { label: "Copying to cold storage", tone: "informative" },
  CopiedToColdStorage: { label: "Copied", tone: "informative" },
  CopyToColdStorageFailed: { label: "Copy failed", tone: "danger" },
  PostCopyValidation: { label: "Verifying copy", tone: "informative" },
  DeletePending: { label: "Removing source", tone: "informative" },
  DeleteFailed: { label: "Source delete failed", tone: "danger" },
  PlaceholderCreating: { label: "Writing placeholder", tone: "informative" },
  PlaceholderFailed: { label: "Placeholder failed", tone: "danger" },
  ColdStorageMigrationCompleted: { label: "Archived", tone: "success" },
  RestoreInProgress: { label: "Restoring", tone: "informative" },
  RestoredToSharePoint: { label: "Restored", tone: "informative" },
  RestoreFailed: { label: "Restore failed", tone: "danger" },
  PostRestoreValidation: { label: "Verifying restore", tone: "informative" },
  PlaceholderRemoving: { label: "Removing placeholder", tone: "informative" },
  PlaceholderRemoveFailed: { label: "Placeholder cleanup failed", tone: "warning" },
  RestoreCompleted: { label: "Restored", tone: "success" },
  CompletedWithWarning: { label: "Completed with warning", tone: "warning" },
  RetryScheduled: { label: "Retry scheduled", tone: "warning" },
  Cancelled: { label: "Cancelled", tone: "subtle" },
  Skipped: { label: "Skipped (not eligible)", tone: "subtle" },
};

export function describeStatus(status: MigrationLifecycleStatus): StatusDescriptor {
  return STATUS_MAP[status] ?? { label: status, tone: "subtle" };
}

export function describeOperation(operation: MigrationOperationKind): string {
  return operation === "Migrate" ? "Archive" : "Restore";
}

const FAILED_STATUSES: MigrationLifecycleStatus[] = [
  "ValidationFailed",
  "CopyToColdStorageFailed",
  "DeleteFailed",
  "PlaceholderFailed",
  "RestoreFailed",
  "PlaceholderRemoveFailed",
];

export function isFailedStatus(status: MigrationLifecycleStatus): boolean {
  return FAILED_STATUSES.includes(status);
}

/** Coarse bucket used for progress bars, filtering and folder rollups. */
export type StatusCategory = "completed" | "failed" | "skipped" | "inprogress";

const COMPLETED_STATUSES: MigrationLifecycleStatus[] = ["ColdStorageMigrationCompleted", "RestoreCompleted"];
const SKIPPED_STATUSES: MigrationLifecycleStatus[] = ["Skipped", "Cancelled"];

export function statusCategory(status: MigrationLifecycleStatus): StatusCategory {
  if (COMPLETED_STATUSES.includes(status) || status === "CompletedWithWarning") return "completed";
  if (isFailedStatus(status)) return "failed";
  if (SKIPPED_STATUSES.includes(status)) return "skipped";
  return "inprogress";
}

export function isInProgressStatus(status: MigrationLifecycleStatus): boolean {
  return statusCategory(status) === "inprogress";
}

const LOG_LEVEL_NAMES: Record<number, string> = {
  0: "Trace",
  1: "Debug",
  2: "Info",
  3: "Warning",
  4: "Error",
  5: "Critical",
};

export function describeLogLevel(level: number): string {
  return LOG_LEVEL_NAMES[level] ?? `L${level}`;
}

export function isErrorLevel(level: number): boolean {
  return level >= 4;
}

export function isWarnLevel(level: number): boolean {
  return level === 3;
}
