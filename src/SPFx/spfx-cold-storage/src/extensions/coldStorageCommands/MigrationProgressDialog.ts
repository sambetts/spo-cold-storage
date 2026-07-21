/**
 * Controller for the cold-storage migrate/restore progress modal.
 *
 * Behaviour is unchanged from the original plain-DOM implementation, but the
 * ~600 lines of manual `document.createElement` rendering are gone: this class
 * now owns only the imperative concerns (lifecycle, the polling state machine,
 * API calls, focus/keyboard handling) and hands an immutable state snapshot to
 * a declarative React view (MigrationProgressDialogView) on every change. The
 * public API is identical so ColdStorageCommandSet is untouched.
 *
 * Design guarantees preserved:
 *   1. Opens immediately on click (before any API call).
 *   2. Preflight + submit errors surface in the dialog, never as unhandled
 *      async rejections.
 *   3. Polls /api/jobs/{jobId} (immediate first poll; 3s → 30s backoff on
 *      failure) and re-renders the per-item, folder-grouped table.
 *   4. Stops on terminal status, on auth errors, and after a 15-minute cap.
 *   5. Closing never cancels the server-side job (footnote makes that explicit).
 */
import * as React from 'react';
import * as ReactDOM from 'react-dom';
import {
  ColdStorageApiClient,
  ColdStorageApiError,
  DialogMode,
  IAcceptedJobResponse,
  IJobItemStatus,
  IJobStatusResponse,
  IWorkerHealth,
} from '../../common/ColdStorageApiClient';
import { formatNumber, isTerminal } from '../../common/statusFormat';
import {
  DialogPhase,
  IConfirmRequest,
  IDialogViewHandlers,
  IDialogViewState,
  ITrackedJob,
  MigrationProgressDialogView,
} from './MigrationProgressDialogView';

const POLL_INTERVAL_MS = 3000;
const MAX_POLL_INTERVAL_MS = 30000;
const POLL_HARD_CAP_MS = 15 * 60 * 1000;

export class MigrationProgressDialog {
  private readonly client: ColdStorageApiClient;
  private readonly operation: DialogMode;

  private container?: HTMLDivElement;
  private previouslyFocused?: HTMLElement;
  private maximised = false;
  // Folder keys (namespaced by jobId) the user has expanded. Persisted across
  // the 3s re-renders so a poll never collapses what the user opened.
  private readonly expandedFolders = new Set<string>();

  private phase: DialogPhase = 'submitting';
  private statusMessage = '';
  private errorMessage?: string;
  private retryHandler?: () => void;
  private confirmRequest?: IConfirmRequest;
  private confirmHandler?: () => void;
  private refreshLibraryOnClose = false;
  private jobs: ITrackedJob[] = [];
  private workerHealth?: IWorkerHealth;
  private startedAt = Date.now();
  private pollTimer?: number;
  private currentPollDelay = POLL_INTERVAL_MS;
  private closed = false;
  private escListener?: (e: KeyboardEvent) => void;
  private keydownTrap?: (e: KeyboardEvent) => void;

  public constructor(client: ColdStorageApiClient, operation: DialogMode) {
    this.client = client;
    this.operation = operation;
  }

  // ----- Public API (unchanged) -----

  public open(initialMessage: string): void {
    if (this.container) return; // already open
    this.statusMessage = initialMessage;
    this.previouslyFocused = (document.activeElement as HTMLElement | null) ?? undefined;
    this.container = document.createElement('div');
    document.body.appendChild(this.container);
    this.attachKeyListeners();
    this.render();
    window.setTimeout(() => { if (!this.closed) this.focusFirst(); }, 0);
  }

  public setStatusMessage(message: string): void {
    if (this.closed) return;
    this.statusMessage = message;
    this.render();
  }

  /**
   * Show a pre-submit confirmation screen listing exactly what will be
   * migrated/restored. The job is only submitted when the user clicks the
   * confirm button, which invokes {@link onConfirm}. Cancelling closes the
   * dialog without touching the server.
   */
  public confirm(request: IConfirmRequest, onConfirm: () => void): void {
    if (this.closed) return;
    this.confirmRequest = request;
    this.confirmHandler = onConfirm;
    this.errorMessage = undefined;
    this.phase = 'confirm';
    this.render();
    window.setTimeout(() => { if (!this.closed) this.focusFirst(); }, 0);
  }

  public addAcceptedJob(jobId: string, acceptResponse: IAcceptedJobResponse, label?: string): void {
    if (this.closed) return;
    if (this.jobs.some(j => j.jobId === jobId)) return;
    this.jobs.push({ jobId, label: label ?? `Job ${this.jobs.length + 1}`, acceptResponse, pollFailures: 0 });
    this.phase = 'polling';
    this.currentPollDelay = POLL_INTERVAL_MS;
    this.render();
    this.scheduleNextPoll(0); // poll immediately - accept response has no item detail
  }

  public showError(message: string, retry?: () => void): void {
    if (this.closed) return;
    this.cancelPollTimer();
    this.phase = 'error';
    this.errorMessage = message;
    this.retryHandler = retry;
    this.render();
  }

  public showJobList(jobs: IJobStatusResponse[]): void {
    if (this.closed) return;
    this.cancelPollTimer();
    this.jobs = jobs.map((j, idx) => ({
      jobId: j.jobId,
      label: this.labelForListedJob(j, idx, jobs.length),
      lastResponse: j,
      pollFailures: 0,
    }));
    this.phase = 'browse';
    this.statusMessage = jobs.length === 0
      ? 'No migration or restore jobs have been submitted from this site yet.'
      : `Showing ${formatNumber(jobs.length)} most recent job${jobs.length === 1 ? '' : 's'} for this site.`;
    this.render();
    void this.refreshWorkerHealthAndRender();
  }

  public close(): void {
    if (this.closed) return;
    this.closed = true;
    this.cancelPollTimer();
    this.detachKeyListeners();
    if (this.container) {
      ReactDOM.unmountComponentAtNode(this.container);
      this.container.parentElement?.removeChild(this.container);
      this.container = undefined;
    }
    try { this.previouslyFocused?.focus(); } catch { /* element may have been detached */ }
  }

  public get isOpen(): boolean { return !this.closed && !!this.container; }

  // ----- Rendering (delegated to React) -----

  private render(): void {
    if (!this.container || this.closed) return;
    const state: IDialogViewState = {
      operation: this.operation,
      phase: this.phase,
      statusMessage: this.statusMessage,
      errorMessage: this.errorMessage,
      confirm: this.confirmRequest,
      jobs: this.jobs,
      workerHealth: this.workerHealth,
      maximised: this.maximised,
      expandedFolders: this.expandedFolders,
      hasRetry: !!this.retryHandler,
      refreshOnClose: this.refreshLibraryOnClose,
    };
    const handlers: IDialogViewHandlers = {
      onRefresh: () => this.handleRefreshClick(),
      onToggleMaximise: () => { this.maximised = !this.maximised; this.render(); },
      onClose: () => this.userClose(),
      onRetry: () => this.handleRetry(),
      onConfirm: () => this.handleConfirm(),
      onCancel: () => this.close(),
      onToggleFolder: (nsKey) => this.toggleFolder(nsKey),
      onToggleAllFolders: (nsKeys, expand) => this.toggleAllFolders(nsKeys, expand),
    };
    ReactDOM.render(React.createElement(MigrationProgressDialogView, { state, handlers }), this.container);
  }

  private toggleFolder(nsKey: string): void {
    if (this.expandedFolders.has(nsKey)) { this.expandedFolders.delete(nsKey); }
    else { this.expandedFolders.add(nsKey); }
    this.render();
  }

  private toggleAllFolders(nsKeys: string[], expand: boolean): void {
    for (const k of nsKeys) {
      if (expand) { this.expandedFolders.add(k); } else { this.expandedFolders.delete(k); }
    }
    this.render();
  }

  private handleRetry(): void {
    const retry = this.retryHandler;
    this.errorMessage = undefined;
    this.retryHandler = undefined;
    this.phase = this.jobs.length > 0 ? 'polling' : 'submitting';
    this.render();
    retry?.();
  }

  private handleConfirm(): void {
    const submit = this.confirmHandler;
    this.confirmHandler = undefined;
    this.confirmRequest = undefined;
    this.phase = 'submitting';
    this.statusMessage = this.operation === 'Restore' ? 'Submitting restore…' : 'Submitting migration…';
    this.render();
    submit?.();
  }

  /**
   * User-initiated close (✕ / Esc). When a migrate/restore job has finished,
   * reload the page so the document library reflects the new placeholders /
   * restored files. Programmatic {@link close} never reloads.
   */
  private userClose(): void {
    if (this.refreshLibraryOnClose && !this.closed) {
      this.close();
      try { window.location.reload(); } catch { /* ignore */ }
      return;
    }
    this.close();
  }

  // ----- Keyboard / focus -----

  private attachKeyListeners(): void {
    this.escListener = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); this.userClose(); }
    };
    window.addEventListener('keydown', this.escListener, true);

    // Focus trap: keep Tab within the dialog card.
    this.keydownTrap = (e: KeyboardEvent) => {
      if (e.key !== 'Tab' || !this.container) return;
      const card = this.container.querySelector<HTMLElement>('[role="dialog"]');
      if (!card) return;
      const focusables = Array.from(
        card.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'),
      ).filter(el => !el.hasAttribute('disabled'));
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    };
    // Listen on the container so it survives React re-renders of the card.
    this.container?.addEventListener('keydown', this.keydownTrap);
  }

  private detachKeyListeners(): void {
    if (this.escListener) {
      window.removeEventListener('keydown', this.escListener, true);
      this.escListener = undefined;
    }
    if (this.keydownTrap) {
      this.container?.removeEventListener('keydown', this.keydownTrap);
      this.keydownTrap = undefined;
    }
  }

  private focusFirst(): void {
    const btn = this.container?.querySelector<HTMLElement>('[role="dialog"] button');
    btn?.focus();
  }

  // ----- Refresh -----

  private handleRefreshClick(): void {
    if (this.closed || this.jobs.length === 0) return;
    if (this.phase === 'browse') { void this.refreshBrowseJobs(); return; }
    this.startedAt = Date.now();
    this.phase = 'polling';
    this.cancelPollTimer();
    void this.pollOnce();
  }

  private async refreshBrowseJobs(): Promise<void> {
    await Promise.all(this.jobs.map(async job => {
      try {
        job.lastResponse = await this.client.getJob(job.jobId);
        try { job.logs = await this.client.getJobLogs(job.jobId); } catch { /* keep previous */ }
      } catch { /* keep previous snapshot */ }
    }));
    // Render the refreshed job data unconditionally; the worker-health banner
    // updates independently and must not gate the job re-render (a failing
    // /api/worker/health call would otherwise make Refresh look broken).
    this.render();
    await this.refreshWorkerHealthAndRender();
  }

  private refreshWorkerHealthAndRender(): Promise<void> {
    return this.client.getWorkerHealth()
      .then(h => { if (!this.closed) { this.workerHealth = h; this.render(); } })
      .catch(() => { /* best-effort; leave banner off */ });
  }

  private labelForListedJob(job: IJobStatusResponse, idx: number, total: number): string {
    const created = job.items.length > 0
      ? job.items.map(i => i.spServerRelativeUrl.split('/').pop()).filter(Boolean).slice(0, 2).join(', ')
      : '';
    const summary = created ? ` — ${created}${job.items.length > 2 ? `, +${formatNumber(job.items.length - 2)} more` : ''}` : '';
    return `${total - idx}. ${job.operation}${summary}`;
  }

  // ----- Polling state machine (unchanged) -----

  private scheduleNextPoll(delayMs: number): void {
    if (this.closed) return;
    this.cancelPollTimer();
    this.pollTimer = window.setTimeout(() => { void this.pollOnce(); }, delayMs);
  }

  private cancelPollTimer(): void {
    if (this.pollTimer !== undefined) {
      window.clearTimeout(this.pollTimer);
      this.pollTimer = undefined;
    }
  }

  private async pollOnce(): Promise<void> {
    if (this.closed) return;
    if (Date.now() - this.startedAt > POLL_HARD_CAP_MS) {
      this.phase = 'expired';
      this.render();
      return;
    }

    let unauthorized = false;
    let anyFailure = false;

    const healthPromise = this.client.getWorkerHealth()
      .then(h => { if (!this.closed) this.workerHealth = h; })
      .catch(() => { /* keep previous health */ });

    const jobPromises = this.jobs.map(async job => {
      try {
        const resp = await this.client.getJob(job.jobId);
        if (this.closed) return;
        job.lastResponse = resp;
        job.lastPollError = undefined;
        job.pollFailures = 0;
        try { job.logs = await this.client.getJobLogs(job.jobId); } catch { /* keep previous logs */ }
      } catch (err) {
        if (this.closed) return;
        const apiErr = err instanceof ColdStorageApiError ? err : ColdStorageApiError.fromTransport(err);
        job.lastPollError = apiErr;
        job.pollFailures++;
        anyFailure = true;
        if (apiErr.isUnauthorized) unauthorized = true;
      }
    });

    await Promise.all([healthPromise, ...jobPromises]);

    if (this.closed) return;

    if (unauthorized) {
      this.showError('Your sign-in has expired or you no longer have permission to view this job. Please refresh the page and sign in again.');
      return;
    }

    if (anyFailure) {
      this.currentPollDelay = Math.min(this.currentPollDelay * 2, MAX_POLL_INTERVAL_MS);
    } else {
      this.currentPollDelay = POLL_INTERVAL_MS;
    }

    const allTerminal = this.jobs.every(j => {
      const items = j.lastResponse?.items ?? [];
      if (items.length === 0) {
        const s = j.lastResponse?.status;
        return s !== undefined && isTerminal(s);
      }
      return items.every((i: IJobItemStatus) => isTerminal(i.status));
    });

    if (allTerminal) {
      this.phase = 'terminal';
      // A finished migrate/restore has changed this library (new .url placeholders
      // or restored files). Reload the page when the user closes the dialog so the
      // list view reflects the change. Status/browse mode never sets this.
      if (this.operation === 'Migrate' || this.operation === 'Restore') {
        this.refreshLibraryOnClose = true;
      }
      this.render();
      return; // stop polling
    }

    this.phase = 'polling';
    this.statusMessage = `Refreshing every ${Math.round(this.currentPollDelay / 1000)}s — last update ${new Date().toLocaleTimeString()}`;
    this.render();
    this.scheduleNextPoll(this.currentPollDelay);
  }
}
