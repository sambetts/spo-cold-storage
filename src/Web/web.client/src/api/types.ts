/**
 * TypeScript mirrors of the Web.Server cold-storage DTOs (Web.Models.Api).
 * Kept in one place so pages share a single contract. JSON is camelCase and
 * enums are serialised as their string names (see Program.cs JSON options).
 *
 * TODO (shared-types phase): generate these from the Web.Server OpenAPI/Swagger
 * document instead of hand-maintaining them.
 */

export type MigrationOperationKind = "Migrate" | "Restore";

export type MigrationLifecycleStatus =
  | "Queued"
  | "Validating"
  | "ValidationFailed"
  | "MigrationInProgress"
  | "CopiedToColdStorage"
  | "CopyToColdStorageFailed"
  | "PostCopyValidation"
  | "DeletePending"
  | "DeleteFailed"
  | "PlaceholderCreating"
  | "PlaceholderFailed"
  | "ColdStorageMigrationCompleted"
  | "RestoreInProgress"
  | "RestoredToSharePoint"
  | "RestoreFailed"
  | "PostRestoreValidation"
  | "PlaceholderRemoving"
  | "PlaceholderRemoveFailed"
  | "RestoreCompleted"
  | "CompletedWithWarning"
  | "RetryScheduled"
  | "Cancelled"
  | "Skipped";

/** GET /api/jobs/recent — one row per transfer (job) with item counts. */
export interface JobSummary {
  jobId: string;
  operation: MigrationOperationKind;
  status: MigrationLifecycleStatus;
  summary?: string | null;
  siteUrl: string;
  requestedByUpn: string;
  containerName?: string | null;
  itemCount: number;
  completedCount: number;
  failedCount: number;
  inProgressCount: number;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
}

export interface JobItemStatus {
  itemId: string;
  spServerRelativeUrl: string;
  placeholderServerRelativeUrl?: string | null;
  itemKind: string;
  status: MigrationLifecycleStatus;
  attempts: number;
  lastError?: string | null;
  lastErrorDetail?: string | null;
  createdAt: string;
  updatedAt: string;
  validatedAt?: string | null;
  copiedAt?: string | null;
  sourceDeletedAt?: string | null;
  placeholderCreatedAt?: string | null;
  restoredAt?: string | null;
  completedAt?: string | null;
}

/** GET /api/jobs/{id} — full transfer status with items. */
export interface JobStatus {
  jobId: string;
  operation: MigrationOperationKind;
  status: MigrationLifecycleStatus;
  summary?: string | null;
  siteUrl: string;
  requestedByUpn: string;
  containerName?: string | null;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
  items: JobItemStatus[];
  warnings: string[];
  errors: string[];
}

/** GET /api/jobs/{id}/logs — one row per lifecycle log line. */
export interface JobLogEntry {
  itemId?: string | null;
  timestamp: string;
  status: MigrationLifecycleStatus;
  /** Microsoft.Extensions.Logging.LogLevel: 0 Trace, 1 Debug, 2 Info, 3 Warn, 4 Error, 5 Critical. */
  level: number;
  message: string;
  exception?: string | null;
}

/** GET /api/worker/health — worker liveness beacon. */
export interface WorkerHealth {
  isOnline: boolean;
  lastSeenUtc?: string | null;
  secondsSinceLastSeen?: number | null;
  onlineWindowSeconds: number;
  workerId?: string | null;
  machineName?: string | null;
  workerVersion?: string | null;
  startedAtUtc?: string | null;
  workerCount: number;
}

export interface StorageEntry {
  name: string;
  size: number;
  lastModified?: string | null;
}

/** GET /api/storage/blobs — hierarchical container listing. */
export interface StorageListing {
  container: string;
  prefix: string;
  folders: string[];
  files: StorageEntry[];
}

/** GET /api/reports/savings — cost & savings KPIs. */
export interface SavingsReport {
  from?: string | null;
  to?: string | null;
  archivedItemCount: number;
  reclaimedBytes: number;
  reclaimedGb: number;
  azurePricePerGbMonth: number;
  spoPricePerGbMonth: number;
  estimatedAzureCostPerMonth: number;
  estimatedSpoValuePerMonth: number;
  estimatedNetSavingsPerMonth: number;
  currency: string;
}

/** GET /api/placeholders/download/{itemId} — short-lived download URL. */
export interface DownloadUrl {
  url: string;
  expiresAt: string;
  fileName?: string | null;
  contentLength: number;
}

export type ExtensionRuleMode = "Exclude" | "Include";

/** GET /api/exclusions/extensions — a runtime file-type archiving rule. */
export interface ExtensionRule {
  id: number;
  extension: string;
  mode: ExtensionRuleMode;
  description?: string | null;
  enabled: boolean;
  createdBy?: string | null;
  createdAt: string;
}

/** GET /api/exclusions — a runtime site/folder archiving exclusion scope. */
export interface ExclusionScope {
  id: number;
  siteUrl?: string | null;
  serverRelativePrefix?: string | null;
  description?: string | null;
  enabled: boolean;
  createdBy?: string | null;
  createdAt: string;
}
