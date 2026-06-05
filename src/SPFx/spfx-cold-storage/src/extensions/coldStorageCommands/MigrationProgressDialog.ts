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
import { ColdStorageApiClient, ColdStorageApiError, DialogMode, IAcceptedJobResponse, IJobItemStatus, IJobStatusResponse, MigrationLifecycleStatus } from '../../common/ColdStorageApiClient';
import { colorFor, formatLabel, isTerminal } from '../../common/statusFormat';

const POLL_INTERVAL_MS = 3000;
const MAX_POLL_INTERVAL_MS = 30000;
const POLL_HARD_CAP_MS = 15 * 60 * 1000;

type DialogPhase = 'submitting' | 'polling' | 'terminal' | 'expired' | 'error' | 'browse';

interface ITrackedJob {
  jobId: string;
  label: string;
  lastResponse?: IJobStatusResponse;
  acceptResponse?: IAcceptedJobResponse;
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

  private phase: DialogPhase = 'submitting';
  private statusMessage = '';
  private errorMessage?: string;
  private jobs: ITrackedJob[] = [];
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
      width: 'min(720px, 92vw)', maxHeight: '86vh',
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
    const close = document.createElement('button');
    close.type = 'button';
    close.setAttribute('aria-label', 'Close');
    close.textContent = '\u2715';
    Object.assign(close.style, {
      background: 'transparent', border: 'none', cursor: 'pointer',
      fontSize: '16px', padding: '4px 8px', color: '#605e5c',
    } as CSSStyleDeclaration);
    close.onclick = () => this.close();
    header.appendChild(title);
    header.appendChild(close);

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
  }

  // ----- Body re-render per state -----

  private retryHandler?: () => void;

  private render(): void {
    if (!this.bodyEl) return;
    this.bodyEl.innerHTML = ''; // static markup ahead - safe to clear

    if (this.phase === 'submitting' && this.jobs.length === 0) {
      this.bodyEl.appendChild(this.makeSpinnerBlock(this.statusMessage || 'Working…'));
      return;
    }

    if (this.phase === 'error') {
      this.bodyEl.appendChild(this.makeErrorBlock(this.errorMessage ?? 'Unknown error.', this.retryHandler));
      return;
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
      wrap.appendChild(this.renderItemsTable(items));
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
    return wrap;
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
      tr.appendChild(statusTd);
      tr.appendChild(this.cell(String(item.attempts)));
      tr.appendChild(this.cell(item.lastError ?? '', { color: '#a4262c', fontSize: '12px' }));
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
    await Promise.all(this.jobs.map(async job => {
      try {
        const resp = await this.client.getJob(job.jobId);
        if (this.closed) return;
        job.lastResponse = resp;
        job.lastPollError = undefined;
        job.pollFailures = 0;
      } catch (err) {
        if (this.closed) return;
        const apiErr = err instanceof ColdStorageApiError ? err : ColdStorageApiError.fromTransport(err);
        job.lastPollError = apiErr;
        job.pollFailures++;
        anyFailure = true;
        if (apiErr.isUnauthorized) unauthorized = true;
      }
    }));

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
      if (items.length === 0) return false; // no items yet; keep polling
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
