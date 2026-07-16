import * as React from 'react';
import {
  ColdStorageApiError,
  DialogMode,
  IAcceptedJobResponse,
  IJobItemStatus,
  IJobLogEntry,
  IJobStatusResponse,
  IWorkerHealth,
  MigrationLifecycleStatus,
} from '../../common/ColdStorageApiClient';
import { colorFor, describeStatus, formatLabel, isTerminal } from '../../common/statusFormat';

export type DialogPhase = 'submitting' | 'polling' | 'terminal' | 'expired' | 'error' | 'browse';

/** A job the dialog is tracking (accepted for polling, or listed in browse mode). */
export interface ITrackedJob {
  jobId: string;
  label: string;
  lastResponse?: IJobStatusResponse;
  acceptResponse?: IAcceptedJobResponse;
  logs?: IJobLogEntry[];
  pollFailures: number;
  lastPollError?: ColdStorageApiError;
}

/** Immutable snapshot the controller hands to the view on each render. */
export interface IDialogViewState {
  operation: DialogMode;
  phase: DialogPhase;
  statusMessage: string;
  errorMessage?: string;
  jobs: ITrackedJob[];
  workerHealth?: IWorkerHealth;
  maximised: boolean;
  expandedFolders: ReadonlySet<string>;
  hasRetry: boolean;
}

export interface IDialogViewHandlers {
  onRefresh(): void;
  onToggleMaximise(): void;
  onClose(): void;
  onRetry(): void;
  onToggleFolder(nsKey: string): void;
  onToggleAllFolders(nsKeys: string[], expand: boolean): void;
}

// ---------------------------------------------------------------------------
// Pure display helpers (moved out of the old DOM class — all side-effect free).
// ---------------------------------------------------------------------------

/** Parse a server timestamp as UTC (EF may omit the timezone designator). */
function parseServerDate(iso?: string): number {
  if (!iso) return NaN;
  const hasTz = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(iso);
  return Date.parse(hasTz ? iso : iso + 'Z');
}

function formatDuration(ms: number): string {
  let s = Math.floor((isFinite(ms) && ms > 0 ? ms : 0) / 1000);
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  s = s % 60;
  if (m < 60) return s ? `${m}m ${s}s` : `${m}m`;
  const h = Math.floor(m / 60);
  const mm = m % 60;
  return mm ? `${h}h ${mm}m` : `${h}h`;
}

function relativeTime(iso?: string): string {
  const t = parseServerDate(iso);
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

function logLevelColor(level: number): string {
  if (level >= 4) return '#a4262c';
  if (level === 3) return '#8a6d00';
  return '#0078d4';
}

function basename(serverRelativeUrl: string): string {
  if (!serverRelativeUrl) return '';
  const idx = serverRelativeUrl.lastIndexOf('/');
  return idx >= 0 ? serverRelativeUrl.substring(idx + 1) : serverRelativeUrl;
}

function folderOf(serverRelativeUrl: string): string {
  if (!serverRelativeUrl) return '/';
  const idx = serverRelativeUrl.lastIndexOf('/');
  return idx > 0 ? serverRelativeUrl.substring(0, idx) : '/';
}

function folderDisplayName(folderPath: string): string {
  const segs = folderPath.split('/').filter(Boolean);
  if (segs.length === 0) return '/ (site root)';
  if (segs.length <= 2) return '/' + segs.join('/');
  return '…/' + segs.slice(-2).join('/');
}

function folderRollup(items: IJobItemStatus[]): { label: string; color: string } {
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

function mergedWarnings(job: ITrackedJob): string[] {
  const out: string[] = [];
  const seen = new Set<string>();
  const push = (m: string | undefined): void => {
    if (!m || seen.has(m)) return;
    seen.add(m);
    out.push(m);
  };
  for (const w of job.acceptResponse?.warnings ?? []) push(w);
  for (const w of job.lastResponse?.warnings ?? []) push(w);
  return out;
}

function allJobsSucceeded(jobs: ITrackedJob[]): boolean {
  for (const job of jobs) {
    for (const item of job.lastResponse?.items ?? []) {
      if (item.status !== MigrationLifecycleStatus.ColdStorageMigrationCompleted &&
        item.status !== MigrationLifecycleStatus.RestoreCompleted) {
        return false;
      }
    }
  }
  return true;
}

function hasPendingWork(jobs: ITrackedJob[]): boolean {
  for (const job of jobs) {
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

const S = {
  badge: (value: MigrationLifecycleStatus | string | number | undefined): React.CSSProperties => ({
    display: 'inline-block', padding: '2px 8px', borderRadius: '12px',
    fontSize: '12px', background: colorFor(value), color: '#fff', whiteSpace: 'nowrap',
  }),
};

// ---------------------------------------------------------------------------
// Leaf components
// ---------------------------------------------------------------------------

const Badge: React.FC<{ value: MigrationLifecycleStatus | string | number | undefined }> = ({ value }) =>
  <span style={S.badge(value)}>{formatLabel(value)}</span>;

type BannerKind = 'info' | 'warn' | 'error' | 'ok';
const BANNER_PALETTE: Record<BannerKind, { bg: string; border: string; color: string }> = {
  info: { bg: '#eff6fc', border: '#0078d4', color: '#005a9e' },
  warn: { bg: '#fff4ce', border: '#f0c419', color: '#8a6d00' },
  error: { bg: '#fde7e9', border: '#a4262c', color: '#a4262c' },
  ok: { bg: '#dff6dd', border: '#107c10', color: '#107c10' },
};
const Banner: React.FC<{ kind: BannerKind; bold?: boolean; children: React.ReactNode }> = ({ kind, bold, children }) => {
  const p = BANNER_PALETTE[kind];
  return (
    <div style={{
      background: p.bg, border: `1px solid ${p.border}`, color: p.color,
      padding: '8px 12px', borderRadius: '2px', marginBottom: '12px', fontSize: '13px',
      lineHeight: '1.4', fontWeight: bold ? 600 : undefined,
    }}>{children}</div>
  );
};

const Spinner: React.FC<{ message: string }> = ({ message }) => (
  <div style={{ display: 'flex', alignItems: 'center', gap: '12px', padding: '12px 0' }}>
    <div style={{
      width: '20px', height: '20px', borderRadius: '50%',
      border: '2px solid #c8c6c4', borderTopColor: '#0078d4', animation: 'cs-dialog-spin 0.9s linear infinite',
    }} />
    <span style={{ fontSize: '14px' }}>{message}</span>
  </div>
);

const MessageList: React.FC<{ title: string; messages: string[]; color: string }> = ({ title, messages, color }) => (
  <div style={{ margin: '8px 12px' }}>
    <div style={{ fontSize: '12px', fontWeight: 600, color, marginBottom: '4px' }}>{title}</div>
    <ul style={{ margin: 0, paddingLeft: '18px', fontSize: '12px', color: '#323130' }}>
      {messages.map((m, i) => <li key={i}>{m}</li>)}
    </ul>
  </div>
);

const Timeline: React.FC<{ logs: IJobLogEntry[] }> = ({ logs }) => {
  const recent = logs.slice(-12); // oldest→newest; show the last dozen
  return (
    <div style={{ margin: '4px 12px 12px' }}>
      <div style={{ fontSize: '12px', fontWeight: 600, color: '#605e5c', marginBottom: '4px' }}>Activity</div>
      <ul style={{ margin: 0, padding: 0, listStyle: 'none' }}>
        {recent.map((log, i) => (
          <li key={i} style={{ display: 'flex', gap: '8px', fontSize: '12px', padding: '2px 0', alignItems: 'baseline' }}>
            <span style={{ color: '#a19f9d', whiteSpace: 'nowrap', fontSize: '11px', minWidth: '58px' }}>{relativeTime(log.timestamp)}</span>
            <span style={{ color: logLevelColor(log.level), fontWeight: 700, whiteSpace: 'nowrap' }}>•</span>
            <span style={{ color: '#323130', wordBreak: 'break-word' }}>{log.message}</span>
          </li>
        ))}
      </ul>
    </div>
  );
};

const ItemStep: React.FC<{ item: IJobItemStatus }> = ({ item }) => {
  const desc = describeStatus(item.status);
  let duration = '';
  if (!isTerminal(item.status) && item.updatedAt) {
    const ms = parseServerDate(item.updatedAt);
    if (!isNaN(ms)) {
      const d = formatDuration(Date.now() - ms);
      duration = item.status === MigrationLifecycleStatus.Queued ? ` · queued ${d}` : ` · ${d} in this step`;
    }
  }
  if (!desc && !duration) return null;
  return <div style={{ marginTop: '4px', fontSize: '11px', color: '#605e5c', maxWidth: '280px' }}>{`${desc}${duration}`}</div>;
};

const cellStyle: React.CSSProperties = { padding: '6px 10px', verticalAlign: 'top' };

const ItemsTable: React.FC<{ items: IJobItemStatus[] }> = ({ items }) => (
  <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '13px' }}>
    <thead>
      <tr>
        {['File', 'Kind', 'Status', 'Attempts', 'Last error'].map(h => (
          <th key={h} style={{ textAlign: 'left', padding: '6px 10px', borderBottom: '1px solid #edebe9', fontWeight: 600, color: '#605e5c', fontSize: '12px' }}>{h}</th>
        ))}
      </tr>
    </thead>
    <tbody>
      {items.map(item => (
        <tr key={item.itemId} style={{ borderBottom: '1px solid #f3f2f1' }}>
          <td style={{ ...cellStyle, wordBreak: 'break-all' }}>{basename(item.spServerRelativeUrl)}</td>
          <td style={cellStyle}>{item.itemKind}</td>
          <td style={cellStyle}>
            <Badge value={item.status} />
            <ItemStep item={item} />
          </td>
          <td style={cellStyle}>{String(item.attempts)}</td>
          <td style={{ ...cellStyle, color: '#a4262c', fontSize: '12px' }} title={item.lastErrorDetail ?? undefined}>{item.lastError ?? ''}</td>
        </tr>
      ))}
    </tbody>
  </table>
);

const FolderSection: React.FC<{
  nsKey: string; folderPath: string; items: IJobItemStatus[]; expanded: boolean; onToggle: (nsKey: string) => void;
}> = ({ nsKey, folderPath, items, expanded, onToggle }) => {
  const rollup = folderRollup(items);
  const toggle = (): void => onToggle(nsKey);
  return (
    <div style={{ borderBottom: '1px solid #f3f2f1' }}>
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={toggle}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } }}
        style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', padding: '8px 12px', background: '#faf9f8' }}
      >
        <span style={{ color: '#605e5c', flex: '0 0 auto' }}>{expanded ? '▾' : '▸'}</span>
        <span style={{ fontWeight: 600, fontSize: '13px', flex: '1 1 auto', wordBreak: 'break-all' }} title={folderPath}>{folderDisplayName(folderPath)}</span>
        <span style={{ fontSize: '12px', color: '#605e5c', background: '#f3f2f1', borderRadius: '10px', padding: '1px 8px', flex: '0 0 auto' }}>{items.length}</span>
        <span style={{ fontSize: '11px', color: '#fff', background: rollup.color, borderRadius: '10px', padding: '2px 8px', whiteSpace: 'nowrap', flex: '0 0 auto' }}>{rollup.label}</span>
      </div>
      {expanded && <ItemsTable items={items} />}
    </div>
  );
};

const ItemsByFolder: React.FC<{
  job: ITrackedJob; items: IJobItemStatus[]; expandedFolders: ReadonlySet<string>;
  onToggleFolder: (nsKey: string) => void; onToggleAllFolders: (nsKeys: string[], expand: boolean) => void;
}> = ({ job, items, expandedFolders, onToggleFolder, onToggleAllFolders }) => {
  const groups = new Map<string, IJobItemStatus[]>();
  for (const item of items) {
    const key = folderOf(item.spServerRelativeUrl);
    const arr = groups.get(key);
    if (arr) { arr.push(item); } else { groups.set(key, [item]); }
  }
  const folderKeys = Array.from(groups.keys()).sort((a, b) => a.localeCompare(b));
  const nsKeys = folderKeys.map(k => `${job.jobId}::${k}`);
  const allExpanded = nsKeys.every(k => expandedFolders.has(k));

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '6px 12px', fontSize: '12px', color: '#605e5c', borderBottom: '1px solid #f3f2f1' }}>
        <span>{`${items.length} file${items.length === 1 ? '' : 's'} in ${folderKeys.length} folder${folderKeys.length === 1 ? '' : 's'}`}</span>
        <button
          type="button"
          onClick={() => onToggleAllFolders(nsKeys, !allExpanded)}
          style={{ background: 'transparent', border: 'none', color: '#0078d4', cursor: 'pointer', fontSize: '12px', padding: '2px 4px' }}
        >{allExpanded ? 'Collapse all' : 'Expand all'}</button>
      </div>
      {folderKeys.map(key => {
        const nsKey = `${job.jobId}::${key}`;
        return (
          <FolderSection
            key={nsKey}
            nsKey={nsKey}
            folderPath={key}
            items={groups.get(key)!}
            expanded={expandedFolders.has(nsKey)}
            onToggle={onToggleFolder}
          />
        );
      })}
    </div>
  );
};

const JobMeta: React.FC<{ job: ITrackedJob; overallStatus?: MigrationLifecycleStatus | string }> = ({ job, overallStatus }) => {
  const desc = overallStatus !== undefined ? describeStatus(overallStatus) : '';
  const timing: string[] = [];
  const created = job.lastResponse?.createdAt;
  const terminal = job.lastResponse ? isTerminal(job.lastResponse.status) : false;
  if (created) {
    const startedMs = parseServerDate(created);
    if (!isNaN(startedMs)) {
      if (terminal && job.lastResponse?.completedAt) {
        const endMs = parseServerDate(job.lastResponse.completedAt);
        if (!isNaN(endMs)) timing.push(`finished in ${formatDuration(endMs - startedMs)}`);
      } else {
        timing.push(`running for ${formatDuration(Date.now() - startedMs)}`);
      }
    }
  }
  const text = [desc, timing.join(' · ')].filter(Boolean).join(' — ');
  if (!text) return null;
  return <div style={{ padding: '8px 12px 4px', fontSize: '12px', color: '#605e5c' }}>{text}</div>;
};

const JobBlock: React.FC<{
  job: ITrackedJob; expandedFolders: ReadonlySet<string>;
  onToggleFolder: (nsKey: string) => void; onToggleAllFolders: (nsKeys: string[], expand: boolean) => void;
}> = ({ job, expandedFolders, onToggleFolder, onToggleAllFolders }) => {
  const overallStatus = job.lastResponse?.status ?? job.acceptResponse?.status;
  const items = job.lastResponse?.items ?? [];
  const warnings = mergedWarnings(job);

  let emptyText: string | undefined;
  if (items.length === 0) {
    if (job.lastResponse && isTerminal(job.lastResponse.status)) {
      emptyText = 'No items were queued for this job — see the warnings below for the reason.';
    } else if (job.lastResponse) {
      emptyText = 'Job has no items yet.';
    } else {
      emptyText = 'Waiting for first status update…';
    }
  }

  return (
    <section style={{ border: '1px solid #edebe9', borderRadius: '2px', marginBottom: '12px' }}>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '12px', alignItems: 'center', padding: '10px 12px', background: '#faf9f8', borderBottom: '1px solid #edebe9' }}>
        <span style={{ fontWeight: 600 }}>{job.label}</span>
        <Badge value={overallStatus} />
        <span style={{ fontSize: '12px', color: '#605e5c', fontFamily: 'Consolas, "Courier New", monospace' }}>{job.jobId}</span>
        {job.lastPollError && (
          <span style={{ fontSize: '12px', color: '#a4262c' }}>
            {` Refresh failing (${job.lastPollError.status === 0 ? 'network' : job.lastPollError.status}) — retrying…`}
          </span>
        )}
      </div>

      <JobMeta job={job} overallStatus={overallStatus} />

      {emptyText
        ? <p style={{ margin: '12px', color: '#605e5c', fontSize: '13px' }}>{emptyText}</p>
        : <ItemsByFolder job={job} items={items} expandedFolders={expandedFolders} onToggleFolder={onToggleFolder} onToggleAllFolders={onToggleAllFolders} />}

      {warnings.length > 0 && <MessageList title="Warnings" messages={warnings} color="#797775" />}
      {job.lastResponse && job.lastResponse.errors.length > 0 && <MessageList title="Errors" messages={job.lastResponse.errors} color="#a4262c" />}
      {job.lastResponse?.summary && <p style={{ margin: '8px 12px', fontSize: '12px', color: '#605e5c' }}>{job.lastResponse.summary}</p>}
      {job.logs && job.logs.length > 0 && <Timeline logs={job.logs} />}
    </section>
  );
};

const WorkerBannerView: React.FC<{ health: IWorkerHealth; jobs: ITrackedJob[] }> = ({ health, jobs }) => {
  if (!hasPendingWork(jobs)) return null;
  if (!health.isOnline) {
    const seen = health.lastSeenUtc ? `last seen ${relativeTime(health.lastSeenUtc)}` : 'no worker has checked in yet';
    return (
      <Banner kind="warn">
        {`⚠ The background worker appears to be offline (${seen}). Queued items won’t start until it’s running — on an idle app the worker may need to be started.`}
      </Banner>
    );
  }
  return <Banner kind="info">The background worker is online and processing — queued items will start shortly.</Banner>;
};

const DialogBody: React.FC<{ state: IDialogViewState; handlers: IDialogViewHandlers }> = ({ state, handlers }) => {
  if (state.phase === 'submitting' && state.jobs.length === 0) {
    return <Spinner message={state.statusMessage || 'Working…'} />;
  }
  if (state.phase === 'error') {
    return (
      <div>
        <Banner kind="error">{state.errorMessage ?? 'Unknown error.'}</Banner>
        {state.hasRetry && (
          <button
            type="button"
            onClick={handlers.onRetry}
            style={{ background: '#0078d4', color: '#fff', border: 'none', padding: '6px 14px', borderRadius: '2px', cursor: 'pointer', fontSize: '13px' }}
          >Try again</button>
        )}
      </div>
    );
  }

  return (
    <div>
      {state.workerHealth && <WorkerBannerView health={state.workerHealth} jobs={state.jobs} />}

      {state.phase === 'browse' && <div style={{ fontSize: '12px', color: '#605e5c', marginBottom: '8px' }}>{state.statusMessage}</div>}
      {state.phase === 'expired' && (
        <Banner kind="warn">Still working after 15 minutes — stopping live refresh. You can close this dialog; the cold-storage status column will keep updating in the background.</Banner>
      )}
      {state.phase === 'polling' && state.statusMessage && (
        <div style={{ fontSize: '12px', color: '#605e5c', marginBottom: '8px' }}>{state.statusMessage}</div>
      )}
      {state.phase === 'terminal' && (
        <Banner kind="ok" bold>
          {allJobsSucceeded(state.jobs)
            ? 'All items reached a final state.'
            : 'All items have finished — some did not complete successfully (see details below).'}
        </Banner>
      )}

      {state.jobs.map(job => (
        <JobBlock
          key={job.jobId}
          job={job}
          expandedFolders={state.expandedFolders}
          onToggleFolder={handlers.onToggleFolder}
          onToggleAllFolders={handlers.onToggleAllFolders}
        />
      ))}
    </div>
  );
};

const HeaderButton: React.FC<{ glyph: string; label: string; onClick: () => void; big?: boolean }> = ({ glyph, label, onClick, big }) => {
  const [hover, setHover] = React.useState(false);
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      onMouseEnter={() => setHover(true)}
      onMouseLeave={() => setHover(false)}
      style={{
        background: hover ? '#f3f2f1' : 'transparent', border: 'none', cursor: 'pointer',
        fontSize: big ? '16px' : '15px', lineHeight: 1, padding: '6px 8px', color: '#605e5c', borderRadius: '2px',
      }}
    >{glyph}</button>
  );
};

export const MigrationProgressDialogView: React.FC<{ state: IDialogViewState; handlers: IDialogViewHandlers; onCloseButtonRef?: (el: HTMLButtonElement | null) => void }> = ({ state, handlers }) => {
  const title = state.operation === 'Migrate' ? 'Migrate to cold storage'
    : state.operation === 'Restore' ? 'Restore from cold storage'
    : 'Cold storage status';

  const cardSize: React.CSSProperties = state.maximised
    ? { width: '98vw', height: '96vh', maxHeight: '96vh' }
    : { width: 'min(720px, 92vw)', height: 'auto', maxHeight: '86vh' };

  return (
    <div
      role="presentation"
      style={{
        position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)', zIndex: 2147483600,
        display: 'flex', alignItems: 'center', justifyContent: 'center',
        fontFamily: '"Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif',
      }}
    >
      <style>{'@keyframes cs-dialog-spin { to { transform: rotate(360deg); } }'}</style>
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="cold-storage-dialog-title"
        tabIndex={-1}
        style={{ background: '#fff', color: '#201f1e', borderRadius: '4px', boxShadow: '0 8px 32px rgba(0,0,0,0.32)', display: 'flex', flexDirection: 'column', overflow: 'hidden', outline: 'none', ...cardSize }}
      >
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '16px 20px', borderBottom: '1px solid #edebe9' }}>
          <h2 id="cold-storage-dialog-title" style={{ margin: 0, fontSize: '18px', fontWeight: 600 }}>{title}</h2>
          <div style={{ display: 'flex', alignItems: 'center', gap: '4px' }}>
            <HeaderButton glyph="↻" label="Refresh now" onClick={handlers.onRefresh} />
            <HeaderButton glyph={state.maximised ? '⤡' : '⤢'} label={state.maximised ? 'Restore size' : 'Maximise'} onClick={handlers.onToggleMaximise} />
            <HeaderButton glyph="✕" label="Close" onClick={handlers.onClose} big />
          </div>
        </div>

        <div style={{ padding: '16px 20px', overflow: 'auto', flex: '1 1 auto' }}>
          <DialogBody state={state} handlers={handlers} />
        </div>

        <div style={{ padding: '10px 20px 14px', borderTop: '1px solid #edebe9', background: '#faf9f8' }}>
          <p style={{ margin: 0, fontSize: '12px', color: '#605e5c', lineHeight: 1.4 }}>
            Closing this dialog does not cancel the job — the server will keep working in the background and the cold-storage status column will update.
          </p>
        </div>
      </div>
    </div>
  );
};
