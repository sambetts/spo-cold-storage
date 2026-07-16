/**
 * Plain-DOM modal that gives the user immediate, continuous feedback while a
 * cold-storage migration or restore is being submitted and processed by the
 * backend. Designed to never silently fail:
 *
 *   1. Opens IMMEDIATELY on click, before any API call, so the user always
 *      sees that the click was registered.
 *   2. Renders preflight work (listContainers / resolvePlaceholder / submit)
 *      so any error surfaces in the dialog rather than disappearing into an
 *      unhandled-async-rejection.
 *   3. After acceptance, polls /api/jobs/{jobId} every 3 s and re-renders the
 *      per-item table. First poll runs immediately because the accepted
 *      response carries no item-level detail.
 *   4. Stops polling on terminal status, on auth errors (with a helpful
 *      message) and after a 15-minute hard cap (with a "still running" hint).
 *   5. Closing the dialog never cancels the server-side job - a footnote
 *      makes that explicit.
 *
 * No React / Fluent dependency; matches the existing plain-DOM style used by
 * ColdStorageStatusFieldCustomizer.ts. All dynamic text uses textContent to
 * avoid XSS via user-controlled file paths or backend error messages.
 */
import { ColdStorageApiClient, ColdStorageApiError, DialogMode, IAcceptedJobResponse, IJobItemStatus, IJobLogEntry, IJobStatusResponse, IWorkerHealth, MigrationLifecycleStatus } from '../../common/ColdStorageApiClient';
import { colorFor, describeStatus, formatLabel, isTerminal } from '../../common/statusFormat';

const POLL_INTERVAL_MS = 3000;
const MAX_POLL_INTERVAL_MS = 30000;
const POLL_HARD_CAP_MS = 15 * 60 * 1000;

type DialogPhase = 'submitting' | 'polling' | 'terminal' | 'expired' | 'error' | 'browse';

interface ITrackedJob {
  jobId: string;
  label: string;
  lastResponse?: IJobStatusResponse;
  acceptResponse?: IAcceptedJobResponse;
  logs?: IJobLogEntry[];
  pollFailures: number;
  lastPollError?: ColdStorageApiError;
}

export class MigrationProgressDialog {
  private readonly client: ColdStorageApiClient;
  private readonly operation: DialogMode;

  private backdrop?: HTMLDivElement;
  private card?: HTMLDivElement;
  private bodyEl?: HTMLDivElement;
  private closeButton?: HTMLButtonElement;
  private previouslyFocused?: HTMLElement;
  private maximised = false;
  private maxButton?: HTMLButtonElement;
  // Folder keys (namespaced by jobId) the user has expanded. Persisted across
  // the 3s re-renders so a poll never collapses what the user opened. Folders
  // are collapsed by default so a job with thousands of files renders only a
  // handful of folder headers (fast) instead of one giant row-per-file table.
  private readonly expandedFolders = new Set<string>();

  private phase: DialogPhase = 'submitting';
  private statusMessage = '';
  private errorMessage?: string;
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

  // ----- Public API -----

  public open(initialMessage: string): void {
    if (this.backdrop) {
      return; // already open
    }
    this.statusMessage = initialMessage;
    this.previouslyFocused = (document.activeElement as HTMLElement | null) ?? undefined;
    this.buildDom();
    this.render();
    // Defer focus so the browser has applied the layout first.
    window.setTimeout(() => { if (!this.closed) this.closeButton?.focus(); }, 0);
  }

  /**
   * Update the "submitting" status message (e.g. "Checking permissions…",
   * "Resolving placeholders…", "Submitting 5 restore jobs…").
   */
  public setStatusMessage(message: string): void {
    if (this.closed) return;
    this.statusMessage = message;
    this.render();
  }

  /**
   * Record a job that the backend accepted. Triggers the first poll
   * immediately. May be called multiple times to add additional jobs (e.g.
   * for a multi-item restore where each row produces its own jobId).
   */
  public addAcceptedJob(jobId: string, acceptResponse: IAcceptedJobResponse, label?: string): void {
    if (this.closed) return;
    if (this.jobs.some(j => j.jobId === jobId)) return;
    this.jobs.push({
      jobId,
      label: label ?? `Job ${this.jobs.length + 1}`,
      acceptResponse,
      pollFailures: 0,
    });
    this.phase = 'polling';
    this.currentPollDelay = POLL_INTERVAL_MS;
    this.render();
    this.scheduleNextPoll(0); // poll immediately - accept response has no item detail
  }

  /**
   * Switch the dialog into an error state. Stops polling but leaves the
   * dialog open so the user sees the message. Pass `retry` to add a
   * "Try again" button that re-invokes the supplied callback.
   */
  public showError(message: string, retry?: () => void): void {
    if (this.closed) return;
    this.cancelPollTimer();
    this.phase = 'error';
    this.errorMessage = message;
    this.retryHandler = retry;
    this.render();
  }

  /**
   * Populate the dialog with a pre-fetched list of jobs (used by the
   * COLDSTORAGE_STATUS toolbar command - no polling, just a read-only view of
   * recent activity for the current site). Renders an "Empty" hint when the
   * server returns no rows.
   */
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
      : `Showing ${jobs.length} most recent job${jobs.length === 1 ? '' : 's'} for this site.`;
    this.render();
    // One-shot worker liveness so a "Queued" row here is explained (offline
    // worker) rather than looking like a silent hang. Best-effort.
    void this.refreshWorkerHealthAndRender();
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
    const summary = created ? ` — ${created}${job.items.length > 2 ? `, +${job.items.length - 2} more` : ''}` : '';
    return `${total - idx}. ${job.operation}${summary}`;
  }

  public close(): void {
    if (this.closed) return;
    this.closed = true;
    this.cancelPollTimer();
    if (this.escListener) {
      window.removeEventListener('keydown', this.escListener);
      this.escListener = undefined;
    }
    if (this.keydownTrap) {
      this.backdrop?.removeEventListener('keydown', this.keydownTrap);
      this.keydownTrap = undefined;
    }
    this.backdrop?.parentElement?.removeChild(this.backdrop);
    this.backdrop = undefined;
    this.card = undefined;
    this.bodyEl = undefined;
    this.closeButton = undefined;
    try { this.previouslyFocused?.focus(); } catch { /* element may have been detached */ }
  }

  public get isOpen(): boolean { return !this.closed && !!this.backdrop; }

  // ----- DOM build (run once) -----

  private buildDom(): void {
    const backdrop = document.createElement('div');
    backdrop.setAttribute('role', 'presentation');
    Object.assign(backdrop.style, {
      position: 'fixed', inset: '0', background: 'rgba(0,0,0,0.5)',
      zIndex: '2147483600', display: 'flex',
      alignItems: 'center', justifyContent: 'center',
      fontFamily: '"Segoe UI", "Segoe UI Web (West European)", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif',
    } as CSSStyleDeclaration);

    const card = document.createElement('div');
    card.setAttribute('role', 'dialog');
    card.setAttribute('aria-modal', 'true');
    card.setAttribute('aria-labelledby', 'cold-storage-dialog-title');
    Object.assign(card.style, {
      background: '#fff', color: '#201f1e', borderRadius: '4px',
      boxShadow: '0 8px 32px rgba(0,0,0,0.32)',
      display: 'flex', flexDirection: 'column', overflow: 'hidden',
      outline: 'none',
    } as CSSStyleDeclaration);
    card.tabIndex = -1;

    // Header
    const header = document.createElement('div');
    Object.assign(header.style, {
      display: 'flex', alignItems: 'center', justifyContent: 'space-between',
      padding: '16px 20px', borderBottom: '1px solid #edebe9',
    } as CSSStyleDeclaration);
    const title = document.createElement('h2');
    title.id = 'cold-storage-dialog-title';
    title.textContent = this.operation === 'Migrate' ? 'Migrate to cold storage'
                       : this.operation === 'Restore' ? 'Restore from cold storage'
                       : 'Cold storage status';
    Object.assign(title.style, { margin: '0', fontSize: '18px', fontWeight: '600' } as CSSStyleDeclaration);

    // Right-side header actions: Refresh · Maximise · Close.
    const actions = document.createElement('div');
    Object.assign(actions.style, { display: 'flex', alignItems: 'center', gap: '4px' } as CSSStyleDeclaration);

    const refresh = this.makeIconButton('\u21BB', 'Refresh now', () => this.handleRefreshClick());

    const maximise = this.makeIconButton('\u2922', 'Maximise', () => this.toggleMaximise());
    this.maxButton = maximise;

    const close = this.makeIconButton('\u2715', 'Close', () => this.close());
    close.style.fontSize = '16px';

    actions.appendChild(refresh);
    actions.appendChild(maximise);
    actions.appendChild(close);
    header.appendChild(title);
    header.appendChild(actions);

    // Body (re-rendered per state)
    const body = document.createElement('div');
    Object.assign(body.style, {
      padding: '16px 20px', overflow: 'auto', flex: '1 1 auto',
    } as CSSStyleDeclaration);

    // Footer note - constant text
    const footer = document.createElement('div');
    Object.assign(footer.style, {
      padding: '10px 20px 14px', borderTop: '1px solid #edebe9',
      background: '#faf9f8',
    } as CSSStyleDeclaration);
    const note = document.createElement('p');
    note.textContent = 'Closing this dialog does not cancel the job — the server will keep working in the background and the cold-storage status column will update.';
    Object.assign(note.style, {
      margin: '0', fontSize: '12px', color: '#605e5c', lineHeight: '1.4',
    } as CSSStyleDeclaration);
    footer.appendChild(note);

    card.appendChild(header);
    card.appendChild(body);
    card.appendChild(footer);
    backdrop.appendChild(card);
    document.body.appendChild(backdrop);

    // Escape to close
    this.escListener = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        this.close();
      }
    };
    window.addEventListener('keydown', this.escListener, true);

    // Focus trap: when Tab leaves the card, wrap to the first/last focusable.
    this.keydownTrap = (e: KeyboardEvent) => {
      if (e.key !== 'Tab' || !this.card) return;
      const focusables = Array.from(
        this.card.querySelectorAll<HTMLElement>('button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])'),
      ).filter(el => !el.hasAttribute('disabled'));
      if (focusables.length === 0) return;
      const first = focusables[0];
      const last = focusables[focusables.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    card.addEventListener('keydown', this.keydownTrap);

    // Don't dismiss on backdrop click — too easy to lose progress accidentally.

    this.backdrop = backdrop;
    this.card = card;
    this.bodyEl = body;
    this.closeButton = close;
    this.applyCardSize();
  }

  private makeIconButton(glyph: string, label: string, onClick: () => void): HTMLButtonElement {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.setAttribute('aria-label', label);
    btn.title = label;
    btn.textContent = glyph;
    Object.assign(btn.style, {
      background: 'transparent', border: 'none', cursor: 'pointer',
      fontSize: '15px', lineHeight: '1', padding: '6px 8px', color: '#605e5c', borderRadius: '2px',
    } as CSSStyleDeclaration);
    btn.onmouseenter = () => { btn.style.background = '#f3f2f1'; };
    btn.onmouseleave = () => { btn.style.background = 'transparent'; };
    btn.onclick = onClick;
    return btn;
  }

  /** Apply the normal or maximised card dimensions and update the toggle glyph. */
  private applyCardSize(): void {
    if (!this.card) return;
    if (this.maximised) {
      Object.assign(this.card.style, { width: '98vw', height: '96vh', maxHeight: '96vh' } as CSSStyleDeclaration);
    } else {
      Object.assign(this.card.style, { width: 'min(720px, 92vw)', height: 'auto', maxHeight: '86vh' } as CSSStyleDeclaration);
    }
    if (this.maxButton) {
      this.maxButton.textContent = this.maximised ? '\u2921' : '\u2922';
      const lbl = this.maximised ? 'Restore size' : 'Maximise';
      this.maxButton.title = lbl;
      this.maxButton.setAttribute('aria-label', lbl);
    }
  }

  private toggleMaximise(): void {
    this.maximised = !this.maximised;
    this.applyCardSize();
  }

  /**
   * Manual refresh. In the read-only "browse" list it re-fetches each listed
   * job; while tracking a job it resets the 15-minute cap and polls immediately.
   */
  private handleRefreshClick(): void {
    if (this.closed || this.jobs.length === 0) return;
    if (this.phase === 'browse') {
      void this.refreshBrowseJobs();
      return;
    }
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
    await this.refreshWorkerHealthAndRender();
  }

  // ----- Body re-render per state -----

  private retryHandler?: () => void;

  private render(): void {
    if (!this.bodyEl) return;
    // Preserve scroll so the 3s auto-refresh doesn't yank the user back to the
    // top while they're reading the activity log further down.
    const prevScroll = this.bodyEl.scrollTop;
    this.bodyEl.innerHTML = ''; // static markup ahead - safe to clear
    this.renderBody();
    this.bodyEl.scrollTop = prevScroll;
  }

  private renderBody(): void {
    if (!this.bodyEl) return;

    if (this.phase === 'submitting' && this.jobs.length === 0) {
      this.bodyEl.appendChild(this.makeSpinnerBlock(this.statusMessage || 'Working…'));
      return;
    }

    if (this.phase === 'error') {
      this.bodyEl.appendChild(this.makeErrorBlock(this.errorMessage ?? 'Unknown error.', this.retryHandler));
      return;
    }

    // Worker-liveness banner: explains a long "Queued" wait (worker offline /
    // still warming up) so the user isn't left staring at a bare "Queued".
    const workerBanner = this.renderWorkerBanner();
    if (workerBanner) {
      this.bodyEl.appendChild(workerBanner);
    }

    if (this.phase === 'browse') {
      // Browse mode = COLDSTORAGE_STATUS toolbar command. No polling, just a
      // read-only snapshot of recent jobs for the current site.
      const banner = document.createElement('div');
      Object.assign(banner.style, {
        fontSize: '12px', color: '#605e5c', marginBottom: '8px',
      } as CSSStyleDeclaration);
      banner.textContent = this.statusMessage;
      this.bodyEl.appendChild(banner);
      if (this.jobs.length === 0) {
        return; // banner already explains the empty state
      }
      for (const job of this.jobs) {
        this.bodyEl.appendChild(this.renderJobBlock(job));
      }
      return;
    }

    // Polling / terminal / expired all share the same job-tracking layout.
    if (this.phase === 'expired') {
      const banner = document.createElement('div');
      Object.assign(banner.style, {
        background: '#fff4ce', border: '1px solid #f0c419',
        padding: '8px 12px', borderRadius: '2px', marginBottom: '12px', fontSize: '13px',
      } as CSSStyleDeclaration);
      banner.textContent = 'Still working after 15 minutes — stopping live refresh. You can close this dialog; the cold-storage status column will keep updating in the background.';
      this.bodyEl.appendChild(banner);
    } else if (this.phase === 'polling' && this.statusMessage) {
      const banner = document.createElement('div');
      Object.assign(banner.style, {
        fontSize: '12px', color: '#605e5c', marginBottom: '8px',
      } as CSSStyleDeclaration);
      banner.textContent = this.statusMessage;
      this.bodyEl.appendChild(banner);
    }

    if (this.phase === 'terminal') {
      const ok = document.createElement('div');
      Object.assign(ok.style, {
        background: '#dff6dd', border: '1px solid #107c10',
        color: '#107c10', padding: '8px 12px', borderRadius: '2px',
        marginBottom: '12px', fontSize: '13px', fontWeight: '600',
      } as CSSStyleDeclaration);
      ok.textContent = this.allJobsSucceeded()
        ? 'All items reached a final state.'
        : 'All items have finished — some did not complete successfully (see details below).';
      this.bodyEl.appendChild(ok);
    }

    for (const job of this.jobs) {
      this.bodyEl.appendChild(this.renderJobBlock(job));
    }
  }

  /**
   * Merge accept-time warnings (returned synchronously by /api/migrations/start
   * or /api/restores/start) with any warnings the server later attaches to the
   * job. The accept-time list is the only place where "no eligible items"-style
   * preflight reasons live, so we have to keep showing them after polling
   * starts or the user is left staring at "Job has no items yet" with no idea
   * why.
   */
  private mergedWarnings(job: ITrackedJob): string[] {
    const out: string[] = [];
    const seen = new Set<string>();
    const push = (m: string | undefined): void => {
      if (!m) return;
      if (seen.has(m)) return;
      seen.add(m);
      out.push(m);
    };
    for (const w of job.acceptResponse?.warnings ?? []) push(w);
    for (const w of job.lastResponse?.warnings ?? []) push(w);
    return out;
  }

  private allJobsSucceeded(): boolean {
    for (const job of this.jobs) {
      const items = job.lastResponse?.items ?? [];
      for (const item of items) {
        if (item.status !== MigrationLifecycleStatus.ColdStorageMigrationCompleted &&
            item.status !== MigrationLifecycleStatus.RestoreCompleted) {
          return false;
        }
      }
    }
    return true;
  }

  private renderJobBlock(job: ITrackedJob): HTMLElement {
    const wrap = document.createElement('section');
    Object.assign(wrap.style, {
      border: '1px solid #edebe9', borderRadius: '2px',
      marginBottom: '12px',
    } as CSSStyleDeclaration);

    const head = document.createElement('div');
    Object.assign(head.style, {
      display: 'flex', flexWrap: 'wrap', gap: '12px', alignItems: 'center',
      padding: '10px 12px', background: '#faf9f8',
      borderBottom: '1px solid #edebe9',
    } as CSSStyleDeclaration);

    const titleSpan = document.createElement('span');
    titleSpan.style.fontWeight = '600';
    titleSpan.textContent = job.label;
    head.appendChild(titleSpan);

    const overallStatus = job.lastResponse?.status ?? job.acceptResponse?.status;
    head.appendChild(this.makeBadge(overallStatus));

    const idSpan = document.createElement('span');
    Object.assign(idSpan.style, { fontSize: '12px', color: '#605e5c', fontFamily: 'Consolas, "Courier New", monospace' } as CSSStyleDeclaration);
    idSpan.textContent = job.jobId;
    head.appendChild(idSpan);

    if (job.lastPollError) {
      const warn = document.createElement('span');
      Object.assign(warn.style, { fontSize: '12px', color: '#a4262c' } as CSSStyleDeclaration);
      warn.textContent = ` Refresh failing (${job.lastPollError.status === 0 ? 'network' : job.lastPollError.status}) — retrying…`;
      head.appendChild(warn);
    }

    wrap.appendChild(head);

    // Sub-line: what the job is doing right now + how long it's been running.
    const meta = this.renderJobMeta(job, overallStatus);
    if (meta) {
      wrap.appendChild(meta);
    }

    const items = job.lastResponse?.items ?? [];
    if (items.length === 0) {
      const empty = document.createElement('p');
      Object.assign(empty.style, { margin: '12px', color: '#605e5c', fontSize: '13px' } as CSSStyleDeclaration);
      if (job.lastResponse && isTerminal(job.lastResponse.status)) {
        // Server already finished the job without queuing anything (e.g.
        // "No eligible items"). The warnings rendered below explain why.
        empty.textContent = 'No items were queued for this job — see the warnings below for the reason.';
      } else if (job.lastResponse) {
        empty.textContent = 'Job has no items yet.';
      } else {
        empty.textContent = 'Waiting for first status update…';
      }
      wrap.appendChild(empty);
    } else {
      wrap.appendChild(this.renderItemsByFolder(job, items));
    }

    const warnings = this.mergedWarnings(job);
    if (warnings.length > 0) {
      wrap.appendChild(this.renderMessageList('Warnings', warnings, '#797775'));
    }
    if (job.lastResponse) {
      if (job.lastResponse.errors.length > 0) {
        wrap.appendChild(this.renderMessageList('Errors', job.lastResponse.errors, '#a4262c'));
      }
      if (job.lastResponse.summary) {
        const sum = document.createElement('p');
        Object.assign(sum.style, { margin: '8px 12px', fontSize: '12px', color: '#605e5c' } as CSSStyleDeclaration);
        sum.textContent = job.lastResponse.summary;
        wrap.appendChild(sum);
      }
    }

    // Live activity log so the user can watch each step happen.
    const timeline = this.renderTimeline(job);
    if (timeline) {
      wrap.appendChild(timeline);
    }
    return wrap;
  }

  /**
   * "What is happening + for how long" sub-line under a job header. For a
   * finished job it shows the total elapsed time instead.
   */
  private renderJobMeta(job: ITrackedJob, overallStatus?: MigrationLifecycleStatus | string): HTMLElement | undefined {
    const desc = overallStatus !== undefined ? describeStatus(overallStatus) : '';
    const timing: string[] = [];
    const created = job.lastResponse?.createdAt;
    const terminal = job.lastResponse ? isTerminal(job.lastResponse.status) : false;
    if (created) {
      const startedMs = MigrationProgressDialog.parseServerDate(created);
      if (!isNaN(startedMs)) {
        if (terminal && job.lastResponse?.completedAt) {
          const endMs = MigrationProgressDialog.parseServerDate(job.lastResponse.completedAt);
          if (!isNaN(endMs)) {
            timing.push(`finished in ${MigrationProgressDialog.formatDuration(endMs - startedMs)}`);
          }
        } else {
          timing.push(`running for ${MigrationProgressDialog.formatDuration(Date.now() - startedMs)}`);
        }
      }
    }
    const text = [desc, timing.join(' · ')].filter(Boolean).join(' — ');
    if (!text) return undefined;
    const el = document.createElement('div');
    Object.assign(el.style, { padding: '8px 12px 4px', fontSize: '12px', color: '#605e5c' } as CSSStyleDeclaration);
    el.textContent = text;
    return el;
  }

  /**
   * Groups a job's items by parent folder and renders each folder as a
   * collapsible section (collapsed by default). Collapsed folders render only a
   * header with a status rollup, so a job with thousands of files across many
   * folders draws a handful of rows per refresh instead of thousands — fixing
   * the slow refresh. Expand state is namespaced by jobId and persists across
   * the 3s re-renders.
   */
  private renderItemsByFolder(job: ITrackedJob, items: IJobItemStatus[]): HTMLElement {
    const wrap = document.createElement('div');

    const groups = new Map<string, IJobItemStatus[]>();
    for (const item of items) {
      const key = this.folderOf(item.spServerRelativeUrl);
      const arr = groups.get(key);
      if (arr) { arr.push(item); } else { groups.set(key, [item]); }
    }
    const folderKeys = Array.from(groups.keys()).sort((a, b) => a.localeCompare(b));
    const nsKeys = folderKeys.map(k => `${job.jobId}::${k}`);
    const allExpanded = nsKeys.every(k => this.expandedFolders.has(k));

    // Summary + Expand/Collapse-all control.
    const controlBar = document.createElement('div');
    Object.assign(controlBar.style, {
      display: 'flex', justifyContent: 'space-between', alignItems: 'center',
      padding: '6px 12px', fontSize: '12px', color: '#605e5c', borderBottom: '1px solid #f3f2f1',
    } as CSSStyleDeclaration);
    const countSpan = document.createElement('span');
    countSpan.textContent = `${items.length} file${items.length === 1 ? '' : 's'} in ${folderKeys.length} folder${folderKeys.length === 1 ? '' : 's'}`;
    const toggleAll = document.createElement('button');
    toggleAll.type = 'button';
    Object.assign(toggleAll.style, {
      background: 'transparent', border: 'none', color: '#0078d4', cursor: 'pointer', fontSize: '12px', padding: '2px 4px',
    } as CSSStyleDeclaration);
    toggleAll.textContent = allExpanded ? 'Collapse all' : 'Expand all';
    toggleAll.onclick = () => {
      if (allExpanded) { for (const k of nsKeys) this.expandedFolders.delete(k); }
      else { for (const k of nsKeys) this.expandedFolders.add(k); }
      this.render();
    };
    controlBar.appendChild(countSpan);
    controlBar.appendChild(toggleAll);
    wrap.appendChild(controlBar);

    for (const key of folderKeys) {
      const nsKey = `${job.jobId}::${key}`;
      wrap.appendChild(this.renderFolderSection(nsKey, key, groups.get(key)!, this.expandedFolders.has(nsKey)));
    }
    return wrap;
  }

  private renderFolderSection(nsKey: string, folderPath: string, items: IJobItemStatus[], expanded: boolean): HTMLElement {
    const section = document.createElement('div');
    Object.assign(section.style, { borderBottom: '1px solid #f3f2f1' } as CSSStyleDeclaration);

    const header = document.createElement('div');
    header.setAttribute('role', 'button');
    header.tabIndex = 0;
    header.setAttribute('aria-expanded', expanded ? 'true' : 'false');
    Object.assign(header.style, {
      display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer',
      padding: '8px 12px', background: '#faf9f8',
    } as CSSStyleDeclaration);

    const caret = document.createElement('span');
    caret.textContent = expanded ? '\u25BE' : '\u25B8';
    Object.assign(caret.style, { color: '#605e5c', flex: '0 0 auto' } as CSSStyleDeclaration);

    const name = document.createElement('span');
    name.textContent = this.folderDisplayName(folderPath);
    name.title = folderPath;
    Object.assign(name.style, { fontWeight: '600', fontSize: '13px', flex: '1 1 auto', wordBreak: 'break-all' } as CSSStyleDeclaration);

    const count = document.createElement('span');
    count.textContent = String(items.length);
    Object.assign(count.style, { fontSize: '12px', color: '#605e5c', background: '#f3f2f1', borderRadius: '10px', padding: '1px 8px', flex: '0 0 auto' } as CSSStyleDeclaration);

    const rollup = this.folderRollup(items);
    const rollupBadge = document.createElement('span');
    rollupBadge.textContent = rollup.label;
    Object.assign(rollupBadge.style, {
      fontSize: '11px', color: '#fff', background: rollup.color, borderRadius: '10px',
      padding: '2px 8px', whiteSpace: 'nowrap', flex: '0 0 auto',
    } as CSSStyleDeclaration);

    header.appendChild(caret);
    header.appendChild(name);
    header.appendChild(count);
    header.appendChild(rollupBadge);

    const toggle = (): void => {
      if (this.expandedFolders.has(nsKey)) { this.expandedFolders.delete(nsKey); }
      else { this.expandedFolders.add(nsKey); }
      this.render();
    };
    header.onclick = toggle;
    header.onkeydown = (e: KeyboardEvent) => {
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); }
    };

    section.appendChild(header);
    if (expanded) {
      section.appendChild(this.renderItemsTable(items));
    }
    return section;
  }

  /** Parent folder server-relative URL of a file (the grouping key). */
  private folderOf(serverRelativeUrl: string): string {
    if (!serverRelativeUrl) return '/';
    const idx = serverRelativeUrl.lastIndexOf('/');
    return idx > 0 ? serverRelativeUrl.substring(0, idx) : '/';
  }

  /** Compact, readable folder label (last two segments); full path on hover. */
  private folderDisplayName(folderPath: string): string {
    const segs = folderPath.split('/').filter(Boolean);
    if (segs.length === 0) return '/ (site root)';
    if (segs.length <= 2) return '/' + segs.join('/');
    return '…/' + segs.slice(-2).join('/');
  }

  /** Per-folder status rollup shown on the (collapsed) folder header. */
  private folderRollup(items: IJobItemStatus[]): { label: string; color: string } {
    let done = 0, failed = 0, inProgress = 0;
    for (const it of items) {
      const completed = it.status === MigrationLifecycleStatus.ColdStorageMigrationCompleted
        || it.status === MigrationLifecycleStatus.RestoreCompleted;
      if (completed) { done++; }
      else if (isTerminal(it.status)) { failed++; }
      else { inProgress++; }
    }
    const total = items.length;
    if (inProgress > 0) return { label: `${done}/${total} done`, color: '#0078d4' };
    if (failed > 0) return { label: `${done}/${total} done · ${failed} issue${failed === 1 ? '' : 's'}`, color: '#a4262c' };
    return { label: `All ${total} done`, color: '#107c10' };
  }

  private renderItemsTable(items: IJobItemStatus[]): HTMLElement {
    const table = document.createElement('table');
    Object.assign(table.style, {
      width: '100%', borderCollapse: 'collapse', fontSize: '13px',
    } as CSSStyleDeclaration);
    const thead = document.createElement('thead');
    const headRow = document.createElement('tr');
    for (const h of ['File', 'Kind', 'Status', 'Attempts', 'Last error']) {
      const th = document.createElement('th');
      th.textContent = h;
      Object.assign(th.style, {
        textAlign: 'left', padding: '6px 10px', borderBottom: '1px solid #edebe9',
        fontWeight: '600', color: '#605e5c', fontSize: '12px',
      } as CSSStyleDeclaration);
      headRow.appendChild(th);
    }
    thead.appendChild(headRow);
    table.appendChild(thead);
    const tbody = document.createElement('tbody');
    for (const item of items) {
      const tr = document.createElement('tr');
      Object.assign(tr.style, { borderBottom: '1px solid #f3f2f1' } as CSSStyleDeclaration);
      tr.appendChild(this.cell(this.basename(item.spServerRelativeUrl), { wordBreak: 'break-all' }));
      tr.appendChild(this.cell(item.itemKind));
      const statusTd = document.createElement('td');
      Object.assign(statusTd.style, { padding: '6px 10px' } as CSSStyleDeclaration);
      statusTd.appendChild(this.makeBadge(item.status));
      const step = this.renderItemStep(item);
      if (step) {
        statusTd.appendChild(step);
      }
      tr.appendChild(statusTd);
      tr.appendChild(this.cell(String(item.attempts)));
      const errCell = this.cell(item.lastError ?? '', { color: '#a4262c', fontSize: '12px' });
      if (item.lastErrorDetail) {
        // Friendly summary in the column; raw technical detail on hover (issue #5).
        errCell.title = item.lastErrorDetail;
      }
      tr.appendChild(errCell);
      tbody.appendChild(tr);
    }
    table.appendChild(tbody);
    return table;
  }

  private renderMessageList(title: string, messages: string[], color: string): HTMLElement {
    const wrap = document.createElement('div');
    Object.assign(wrap.style, { margin: '8px 12px' } as CSSStyleDeclaration);
    const h = document.createElement('div');
    Object.assign(h.style, { fontSize: '12px', fontWeight: '600', color, marginBottom: '4px' } as CSSStyleDeclaration);
    h.textContent = title;
    wrap.appendChild(h);
    const ul = document.createElement('ul');
    Object.assign(ul.style, { margin: '0', paddingLeft: '18px', fontSize: '12px', color: '#323130' } as CSSStyleDeclaration);
    for (const m of messages) {
      const li = document.createElement('li');
      li.textContent = m;
      ul.appendChild(li);
    }
    wrap.appendChild(ul);
    return wrap;
  }

  /**
   * Short "current step + how long in it" line shown under an item's status
   * badge. For a Queued item this surfaces exactly how long it's been waiting —
   * the whole point of the exercise.
   */
  private renderItemStep(item: IJobItemStatus): HTMLElement | undefined {
    const desc = describeStatus(item.status);
    let duration = '';
    if (!isTerminal(item.status) && item.updatedAt) {
      const ms = MigrationProgressDialog.parseServerDate(item.updatedAt);
      if (!isNaN(ms)) {
        const d = MigrationProgressDialog.formatDuration(Date.now() - ms);
        duration = item.status === MigrationLifecycleStatus.Queued
          ? ` · queued ${d}`
          : ` · ${d} in this step`;
      }
    }
    if (!desc && !duration) return undefined;
    const el = document.createElement('div');
    Object.assign(el.style, { marginTop: '4px', fontSize: '11px', color: '#605e5c', maxWidth: '280px' } as CSSStyleDeclaration);
    el.textContent = `${desc}${duration}`;
    return el;
  }

  /**
   * Live activity log (most-recent dozen entries) so the user can watch each
   * lifecycle step happen with timestamps instead of a single static badge.
   */
  private renderTimeline(job: ITrackedJob): HTMLElement | undefined {
    const logs = job.logs ?? [];
    if (logs.length === 0) return undefined;
    const recent = logs.slice(-12); // logs are oldest→newest; show the last dozen
    const wrap = document.createElement('div');
    Object.assign(wrap.style, { margin: '4px 12px 12px' } as CSSStyleDeclaration);
    const h = document.createElement('div');
    Object.assign(h.style, { fontSize: '12px', fontWeight: '600', color: '#605e5c', marginBottom: '4px' } as CSSStyleDeclaration);
    h.textContent = 'Activity';
    wrap.appendChild(h);
    const ul = document.createElement('ul');
    Object.assign(ul.style, { margin: '0', padding: '0', listStyle: 'none' } as CSSStyleDeclaration);
    for (const log of recent) {
      const li = document.createElement('li');
      Object.assign(li.style, { display: 'flex', gap: '8px', fontSize: '12px', padding: '2px 0', alignItems: 'baseline' } as CSSStyleDeclaration);
      const time = document.createElement('span');
      Object.assign(time.style, { color: '#a19f9d', whiteSpace: 'nowrap', fontSize: '11px', minWidth: '58px' } as CSSStyleDeclaration);
      time.textContent = MigrationProgressDialog.relativeTime(log.timestamp);
      const dot = document.createElement('span');
      Object.assign(dot.style, { color: MigrationProgressDialog.logLevelColor(log.level), fontWeight: '700', whiteSpace: 'nowrap' } as CSSStyleDeclaration);
      dot.textContent = '\u2022';
      const msg = document.createElement('span');
      Object.assign(msg.style, { color: '#323130', wordBreak: 'break-word' } as CSSStyleDeclaration);
      msg.textContent = log.message;
      li.appendChild(time);
      li.appendChild(dot);
      li.appendChild(msg);
      ul.appendChild(li);
    }
    wrap.appendChild(ul);
    return wrap;
  }

  /**
   * Banner explaining a stalled "Queued" state: worker offline (nothing draining
   * the queue) vs. worker online (actively working / warming up). Only shown when
   * there's actually pending work waiting on the worker.
   */
  private renderWorkerBanner(): HTMLElement | undefined {
    const health = this.workerHealth;
    if (!health || !this.hasPendingWork()) return undefined;

    if (!health.isOnline) {
      const seen = health.lastSeenUtc
        ? `last seen ${MigrationProgressDialog.relativeTime(health.lastSeenUtc)}`
        : 'no worker has checked in yet';
      return this.makeBanner(
        `\u26A0 The background worker appears to be offline (${seen}). Queued items won\u2019t start until it\u2019s running \u2014 on an idle app the worker may need to be started.`,
        'warn');
    }
    return this.makeBanner(
      'The background worker is online and processing \u2014 queued items will start shortly.',
      'info');
  }

  /** True when a tracked job still has work that hasn't reached a final state. */
  private hasPendingWork(): boolean {
    for (const job of this.jobs) {
      const items = job.lastResponse?.items ?? [];
      if (items.length === 0) {
        const s = job.lastResponse?.status ?? job.acceptResponse?.status;
        if (s === undefined || !isTerminal(s)) return true;
      }
      for (const it of items) {
        if (!isTerminal(it.status)) return true;
      }
    }
    return false;
  }

  private makeBanner(text: string, kind: 'info' | 'warn' | 'error' | 'ok'): HTMLElement {
    const palette = {
      info:  { bg: '#eff6fc', border: '#0078d4', color: '#005a9e' },
      warn:  { bg: '#fff4ce', border: '#f0c419', color: '#8a6d00' },
      error: { bg: '#fde7e9', border: '#a4262c', color: '#a4262c' },
      ok:    { bg: '#dff6dd', border: '#107c10', color: '#107c10' },
    }[kind];
    const el = document.createElement('div');
    Object.assign(el.style, {
      background: palette.bg, border: `1px solid ${palette.border}`, color: palette.color,
      padding: '8px 12px', borderRadius: '2px', marginBottom: '12px', fontSize: '13px', lineHeight: '1.4',
    } as CSSStyleDeclaration);
    el.textContent = text;
    return el;
  }

  private static logLevelColor(level: number): string {
    // Microsoft.Extensions.Logging.LogLevel: 2=Info, 3=Warning, 4=Error, 5=Critical.
    if (level >= 4) return '#a4262c';
    if (level === 3) return '#8a6d00';
    return '#0078d4';
  }

  /**
   * Parse a server timestamp as UTC. EF may serialize DateTimes without a
   * timezone designator (Kind=Unspecified); a bare timestamp must be treated as
   * UTC or elapsed calculations would be off by the viewer's local offset.
   */
  private static parseServerDate(iso?: string): number {
    if (!iso) return NaN;
    const hasTz = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(iso);
    return Date.parse(hasTz ? iso : iso + 'Z');
  }

  private static formatDuration(ms: number): string {
    let s = Math.floor((isFinite(ms) && ms > 0 ? ms : 0) / 1000);
    if (s < 60) return `${s}s`;
    const m = Math.floor(s / 60);
    s = s % 60;
    if (m < 60) return s ? `${m}m ${s}s` : `${m}m`;
    const h = Math.floor(m / 60);
    const mm = m % 60;
    return mm ? `${h}h ${mm}m` : `${h}h`;
  }

  private static relativeTime(iso?: string): string {
    const t = MigrationProgressDialog.parseServerDate(iso);
    if (isNaN(t)) return '';
    const diff = Date.now() - t;
    if (diff < 5000) return 'just now';
    const s = Math.floor(diff / 1000);
    if (s < 60) return `${s}s ago`;
    const m = Math.floor(s / 60);
    if (m < 60) return `${m}m ago`;
    const h = Math.floor(m / 60);
    if (h < 24) return `${h}h ago`;
    return `${Math.floor(h / 24)}d ago`;
  }

  private cell(text: string, style?: Partial<CSSStyleDeclaration>): HTMLTableCellElement {
    const td = document.createElement('td');
    Object.assign(td.style, { padding: '6px 10px', verticalAlign: 'top' } as CSSStyleDeclaration);
    if (style) Object.assign(td.style, style);
    td.textContent = text;
    return td;
  }

  private basename(serverRelativeUrl: string): string {
    if (!serverRelativeUrl) return '';
    const idx = serverRelativeUrl.lastIndexOf('/');
    return idx >= 0 ? serverRelativeUrl.substring(idx + 1) : serverRelativeUrl;
  }

  private makeBadge(value: MigrationLifecycleStatus | string | number | undefined): HTMLSpanElement {
    const badge = document.createElement('span');
    badge.textContent = formatLabel(value);
    Object.assign(badge.style, {
      display: 'inline-block', padding: '2px 8px', borderRadius: '12px',
      fontSize: '12px', background: colorFor(value), color: '#fff',
      whiteSpace: 'nowrap',
    } as CSSStyleDeclaration);
    return badge;
  }

  private makeSpinnerBlock(message: string): HTMLElement {
    const wrap = document.createElement('div');
    Object.assign(wrap.style, {
      display: 'flex', alignItems: 'center', gap: '12px',
      padding: '12px 0',
    } as CSSStyleDeclaration);
    const spinner = document.createElement('div');
    Object.assign(spinner.style, {
      width: '20px', height: '20px', borderRadius: '50%',
      border: '2px solid #c8c6c4', borderTopColor: '#0078d4',
      animation: 'cs-dialog-spin 0.9s linear infinite',
    } as CSSStyleDeclaration);
    MigrationProgressDialog.ensureSpinnerKeyframes();
    const text = document.createElement('span');
    text.textContent = message;
    Object.assign(text.style, { fontSize: '14px' } as CSSStyleDeclaration);
    wrap.appendChild(spinner);
    wrap.appendChild(text);
    return wrap;
  }

  private makeErrorBlock(message: string, retry?: () => void): HTMLElement {
    const wrap = document.createElement('div');
    const banner = document.createElement('div');
    Object.assign(banner.style, {
      background: '#fde7e9', border: '1px solid #a4262c',
      color: '#a4262c', padding: '10px 14px', borderRadius: '2px',
      fontSize: '13px', marginBottom: '12px',
    } as CSSStyleDeclaration);
    banner.textContent = message;
    wrap.appendChild(banner);
    if (retry) {
      const btn = document.createElement('button');
      btn.type = 'button';
      btn.textContent = 'Try again';
      Object.assign(btn.style, {
        background: '#0078d4', color: '#fff', border: 'none',
        padding: '6px 14px', borderRadius: '2px', cursor: 'pointer',
        fontSize: '13px',
      } as CSSStyleDeclaration);
      btn.onclick = () => {
        this.errorMessage = undefined;
        this.retryHandler = undefined;
        this.phase = this.jobs.length > 0 ? 'polling' : 'submitting';
        this.render();
        retry();
      };
      wrap.appendChild(btn);
    }
    return wrap;
  }

  private static spinnerStyleInjected = false;
  private static ensureSpinnerKeyframes(): void {
    if (MigrationProgressDialog.spinnerStyleInjected) return;
    const style = document.createElement('style');
    style.textContent = '@keyframes cs-dialog-spin { to { transform: rotate(360deg); } }';
    document.head.appendChild(style);
    MigrationProgressDialog.spinnerStyleInjected = true;
  }

  // ----- Polling state machine -----

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

    // Worker liveness, once per poll. Best-effort: a failure here keeps the last
    // known value and never interrupts job polling.
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
        // Activity log is best-effort; its failure must not fail the poll.
        try {
          job.logs = await this.client.getJobLogs(job.jobId);
        } catch { /* keep previous logs */ }
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

    // Adaptive backoff on persistent transport / 5xx failures.
    if (anyFailure) {
      this.currentPollDelay = Math.min(this.currentPollDelay * 2, MAX_POLL_INTERVAL_MS);
    } else {
      this.currentPollDelay = POLL_INTERVAL_MS;
    }

    // Compute overall terminality across every tracked job.
    const allTerminal = this.jobs.every(j => {
      const items = j.lastResponse?.items ?? [];
      if (items.length === 0) {
        // No items doesn't always mean "still starting": folder expansion can
        // finish a job with zero queued items (e.g. CompletedWithWarning). Fall
        // back to the job-level status so we don't poll a done job for 15 minutes.
        const s = j.lastResponse?.status;
        return s !== undefined && isTerminal(s);
      }
      return items.every((i: IJobItemStatus) => isTerminal(i.status));
    });

    if (allTerminal) {
      this.phase = 'terminal';
      this.render();
      return; // stop polling
    }

    this.phase = 'polling';
    this.statusMessage = `Refreshing every ${Math.round(this.currentPollDelay / 1000)}s — last update ${new Date().toLocaleTimeString()}`;
    this.render();
    this.scheduleNextPoll(this.currentPollDelay);
  }
}
