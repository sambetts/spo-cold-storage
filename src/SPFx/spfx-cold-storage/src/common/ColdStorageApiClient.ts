import { AadHttpClient, HttpClientResponse, IHttpClientOptions } from '@microsoft/sp-http';

/**
 * Lifecycle states. Mirrors the C# `MigrationLifecycleStatus` enum exactly so
 * the SPFx UI can reason about each state without ambiguity.
 */
export enum MigrationLifecycleStatus {
  Queued = 'Queued',
  Validating = 'Validating',
  ValidationFailed = 'ValidationFailed',
  MigrationInProgress = 'MigrationInProgress',
  CopiedToColdStorage = 'CopiedToColdStorage',
  CopyToColdStorageFailed = 'CopyToColdStorageFailed',
  PostCopyValidation = 'PostCopyValidation',
  DeletePending = 'DeletePending',
  DeleteFailed = 'DeleteFailed',
  PlaceholderCreating = 'PlaceholderCreating',
  PlaceholderFailed = 'PlaceholderFailed',
  ColdStorageMigrationCompleted = 'ColdStorageMigrationCompleted',
  RestoreInProgress = 'RestoreInProgress',
  RestoredToSharePoint = 'RestoredToSharePoint',
  RestoreFailed = 'RestoreFailed',
  PostRestoreValidation = 'PostRestoreValidation',
  PlaceholderRemoving = 'PlaceholderRemoving',
  PlaceholderRemoveFailed = 'PlaceholderRemoveFailed',
  RestoreCompleted = 'RestoreCompleted',
  CompletedWithWarning = 'CompletedWithWarning',
  RetryScheduled = 'RetryScheduled',
  Cancelled = 'Cancelled',
  Skipped = 'Skipped',
}

export type ColdStorageItemKind = 'File' | 'Folder';
export type ConflictBehavior = 'Fail' | 'Overwrite' | 'Rename';
export type OperationKind = 'Migrate' | 'Restore';
export type DialogMode = OperationKind | 'Status';

export interface IContainerResponse {
  name: string;
  displayName: string;
  blobContainerName: string;
  storageAccountUri: string;
  canMigrate: boolean;
  canRestore: boolean;
}

export interface IStartMigrationItem {
  serverRelativeUrl: string;
  itemKind: ColdStorageItemKind;
  fileSize?: number;
  lastModified?: string;
}

export interface IStartMigrationRequest {
  siteUrl: string;
  webUrl?: string;
  containerName: string;
  recursive: boolean;
  items: IStartMigrationItem[];
}

export interface IStartRestoreRequest {
  siteUrl: string;
  webUrl?: string;
  placeholderServerRelativeUrl: string;
  originalServerRelativeUrl?: string;
  conflictBehavior: ConflictBehavior;
}

export interface IAcceptedJobResponse {
  jobId: string;
  status: MigrationLifecycleStatus;
  warnings: string[];
}

export interface IJobItemStatus {
  itemId: string;
  spServerRelativeUrl: string;
  placeholderServerRelativeUrl?: string;
  itemKind: ColdStorageItemKind;
  status: MigrationLifecycleStatus;
  attempts: number;
  lastError?: string;
  lastErrorDetail?: string;
  copiedAt?: string;
  sourceDeletedAt?: string;
  placeholderCreatedAt?: string;
  restoredAt?: string;
  completedAt?: string;
}

export interface IJobStatusResponse {
  jobId: string;
  operation: OperationKind;
  status: MigrationLifecycleStatus;
  summary?: string;
  siteUrl: string;
  requestedByUpn: string;
  containerName?: string;
  items: IJobItemStatus[];
  warnings: string[];
  errors: string[];
}

export interface IPlaceholderMetadata {
  isResolved: boolean;
  isEligibleForRestore: boolean;
  unavailableReason?: string;
  originalServerRelativeUrl?: string;
  originalFileName?: string;
  originalFileSize?: number;
  migratedAt?: string;
  jobId?: string;
  currentStatus?: MigrationLifecycleStatus;
  containerName?: string;
}

/**
 * Thin AAD-authenticated client for the SPO Cold Storage web API.
 * Configured by the SPFx extension via the `apiBaseUrl` / `apiAppIdUri`
 * properties so each tenant can point at its own API deployment.
 */
export class ColdStorageApiClient {
  public constructor(
    private readonly aadClient: AadHttpClient,
    private readonly baseUrl: string,
  ) {}

  public async listContainers(): Promise<IContainerResponse[]> {
    return this.getJson<IContainerResponse[]>('/api/containers');
  }

  public async startMigration(request: IStartMigrationRequest): Promise<IAcceptedJobResponse> {
    return this.postJson<IAcceptedJobResponse>('/api/migrations/start', request);
  }

  public async startRestore(request: IStartRestoreRequest): Promise<IAcceptedJobResponse> {
    return this.postJson<IAcceptedJobResponse>('/api/restores/start', request);
  }

  public async getJob(jobId: string): Promise<IJobStatusResponse> {
    return this.getJson<IJobStatusResponse>(`/api/jobs/${jobId}`);
  }

  public async listRecentJobs(siteUrl: string, take: number = 20): Promise<IJobStatusResponse[]> {
    const qs = `siteUrl=${encodeURIComponent(siteUrl)}&take=${encodeURIComponent(String(take))}`;
    return this.getJson<IJobStatusResponse[]>(`/api/jobs?${qs}`);
  }

  public async resolvePlaceholder(serverRelativeUrl: string): Promise<IPlaceholderMetadata> {
    const qs = `placeholderServerRelativeUrl=${encodeURIComponent(serverRelativeUrl)}`;
    return this.getJson<IPlaceholderMetadata>(`/api/placeholders/resolve?${qs}`);
  }

  private async getJson<T>(path: string): Promise<T> {
    let response: HttpClientResponse;
    try {
      response = await this.aadClient.get(this.url(path), AadHttpClient.configurations.v1);
    } catch (err) {
      throw ColdStorageApiError.fromTransport(err);
    }
    return this.parse<T>(response);
  }

  private async postJson<T>(path: string, body: unknown): Promise<T> {
    const options: IHttpClientOptions = {
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    };
    let response: HttpClientResponse;
    try {
      response = await this.aadClient.post(this.url(path), AadHttpClient.configurations.v1, options);
    } catch (err) {
      throw ColdStorageApiError.fromTransport(err);
    }
    return this.parse<T>(response);
  }

  private url(path: string): string {
    return `${this.baseUrl.replace(/\/$/, '')}${path}`;
  }

  private async parse<T>(response: HttpClientResponse): Promise<T> {
    const text = await response.text();
    if (!response.ok) {
      throw new ColdStorageApiError(response.status, response.statusText, text);
    }
    return text ? (JSON.parse(text) as T) : ({} as T);
  }
}

/**
 * Error raised by ColdStorageApiClient for any non-2xx HTTP response or
 * transport failure. Carries the HTTP status so callers (e.g. the progress
 * dialog) can react differently to auth issues vs throttling vs server errors.
 *
 * status === 0 indicates a transport / CORS / network failure where no HTTP
 * response was received.
 */
export class ColdStorageApiError extends Error {
  public readonly status: number;
  public readonly statusText: string;
  public readonly bodyText: string;

  public constructor(status: number, statusText: string, bodyText: string, message?: string) {
    super(message ?? `API ${status}: ${bodyText || statusText}`);
    this.name = 'ColdStorageApiError';
    this.status = status;
    this.statusText = statusText;
    this.bodyText = bodyText;
    // Restore prototype chain (ES5 target loses it when extending built-ins).
    Object.setPrototypeOf(this, ColdStorageApiError.prototype);
  }

  public get isUnauthorized(): boolean { return this.status === 401 || this.status === 403; }
  public get isThrottled(): boolean    { return this.status === 429; }
  public get isServerError(): boolean  { return this.status >= 500 && this.status <= 599; }
  public get isTransport(): boolean    { return this.status === 0; }

  public static fromTransport(err: unknown): ColdStorageApiError {
    const message = err instanceof Error ? err.message : String(err);
    return new ColdStorageApiError(0, 'Network error', '', `Network error: ${message}`);
  }
}
