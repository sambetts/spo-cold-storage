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
import { colorFor, describeStatus, formatCountdown, formatEta, formatLabel, formatNumber, isTerminal, normalizeStatus } from '../../common/statusFormat';

export type DialogPhase = 'submitting' | 'confirm' | 'polling' | 'terminal' | 'expired' | 'error' | 'browse';

/** A single item shown on the pre-submit confirmation screen. */
export interface IConfirmItem {
  name: string;
  kind: 'File' | 'Folder';
}

/** The pre-submit confirmation prompt (issue: confirm before submitting a job). */
export interface IConfirmRequest {
  message: string;
  confirmLabel: string;
  items: IConfirmItem[];
}

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
  confirm?: IConfirmRequest;
  jobs: ITrackedJob[];
  workerHealth?: IWorkerHealth;
  maximised: boolean;
  expandedFolders: ReadonlySet<string>;
  hasRetry: boolean;
  /** When true, closing the dialog reloads the page so the library shows the changes. */
  refreshOnClose: boolean;
}

export interface IDialogViewHandlers {
  onRefresh(): void;
  onToggleMaximise(): void;
  onClose(): void;
  onRetry(): void;
  onConfirm(): void;
  onCancel(): void;
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

interface FileTreeNode {
  name: string;
  path: string;
  item?: IJobItemStatus;
  folders: FileTreeNode[];
  files: FileTreeNode[];
  descendants: IJobItemStatus[];
}

/** Build a nested folder/subfolder/file tree from the flat item list. */
function buildFileTree(items: IJobItemStatus[]): FileTreeNode {
  const root: FileTreeNode = { name: '', path: '', folders: [], files: [], descendants: [] };
  const index = new Map<string, FileTreeNode>([['', root]]);
  for (const it of items) {
    const full = (it.spServerRelativeUrl || '').replace(/\/+$/, '');
    const segs = full.split('/').filter(Boolean);
    const leaf = segs.pop() ?? full;
    let parent = root;
    let acc = '';
    for (const seg of segs) {
      acc += '/' + seg;
      let node = index.get(acc);
      if (!node) {
        node = { name: seg, path: acc, folders: [], files: [], descendants: [] };
        index.set(acc, node);
        parent.folders.push(node);
      }
      parent = node;
    }
    parent.files.push({ name: leaf, path: full || leaf, item: it, folders: [], files: [], descendants: [it] });
  }
  fillDescendants(root);
  root.folders = root.folders.map(compactFolder).sort((a, b) => a.name.localeCompare(b.name));
  root.files.sort((a, b) => a.name.localeCompare(b.name));
  return root;
}

function fillDescendants(node: FileTreeNode): IJobItemStatus[] {
  const acc: IJobItemStatus[] = [];
  for (const f of node.files) { if (f.item) { acc.push(f.item); } }
  for (const f of node.folders) { acc.push(...fillDescendants(f)); }
  node.descendants = acc;
  return acc;
}

/** Collapse chains of single-child folders (a/b/c) into one node, VS Code style. */
function compactFolder(node: FileTreeNode): FileTreeNode {
  node.folders = node.folders.map(compactFolder);
  while (node.folders.length === 1 && node.files.length === 0) {
    const only = node.folders[0];
    node.name = node.name ? `${node.name}/${only.name}` : only.name;
    node.path = only.path;
    node.files = only.files;
    node.folders = only.folders;
  }
  node.folders.sort((a, b) => a.name.localeCompare(b.name));
  node.files.sort((a, b) => a.name.localeCompare(b.name));
  return node;
}

function allTreeFolderPaths(node: FileTreeNode, out: string[] = []): string[] {
  for (const f of node.folders) { out.push(f.path); allTreeFolderPaths(f, out); }
  return out;
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
  if (inProgress > 0) return { label: `${formatNumber(done)}/${formatNumber(total)} done`, color: '#0078d4' };
  if (failed > 0) return { label: `${formatNumber(done)}/${formatNumber(total)} done · ${formatNumber(failed)} issue${failed === 1 ? '' : 's'}`, color: '#a4262c' };
  return { label: `All ${formatNumber(total)} done`, color: '#107c10' };
}

interface JobCounts { completed: number; failed: number; skipped: number; inprogress: number; throttled: number; total: number; }

/** Categorise items into progress-bar segments (mirrors the SPA's TransferProgress). */
function jobCounts(items: IJobItemStatus[]): JobCounts {
  let completed = 0, failed = 0, skipped = 0, inprogress = 0, throttled = 0;
  for (const it of items) {
    const s = normalizeStatus(it.status);
    if (s === MigrationLifecycleStatus.ColdStorageMigrationCompleted
      || s === MigrationLifecycleStatus.RestoreCompleted
      || s === MigrationLifecycleStatus.CompletedWithWarning) { completed++; }
    else if (s === MigrationLifecycleStatus.Skipped) { skipped++; }
    else if (s === MigrationLifecycleStatus.RetryScheduled) { throttled++; inprogress++; }
    else if (s !== undefined && isTerminal(s)) { failed++; }
    else { inprogress++; }
  }
  return { completed, failed, skipped, inprogress, throttled, total: items.length };
}

/**
 * Horizontal progress bar + ETA for a job — parity with the SPA "Transfers" view.
 * Shows completed/failed/in-progress/skipped segments, the estimated completion time,
 * and (when throttled) the count and when the queue resumes.
 */
const JobProgress: React.FC<{ job: ITrackedJob; items: IJobItemStatus[]; active: boolean }> = ({ job, items, active }) => {
  const c = jobCounts(items);
  if (c.total === 0) return null;
  const pct = (n: number): string => `${(n / c.total) * 100}%`;
  const eta = job.lastResponse?.estimatedCompletionUtc;
  const throttled = job.lastResponse?.throttledCount ?? c.throttled;
  const nextRetry = job.lastResponse?.nextRetryUtc;
  return (
    <div style={{ padding: '6px 12px 10px' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: '12px', color: '#605e5c', marginBottom: '4px', gap: '12px', flexWrap: 'wrap' }}>
        <span>
          <strong style={{ color: '#201f1e' }}>{c.completed}</strong> of {c.total} done
          {c.failed > 0 ? ` · ${c.failed} failed` : ''}
          {c.skipped > 0 ? ` · ${c.skipped} skipped` : ''}
          {throttled > 0 ? ` · ${throttled} throttled` : ''}
        </span>
        <span>{active ? `${c.inprogress} in progress · auto-refreshing…` : 'Finished'}</span>
      </div>
      <div style={{ display: 'flex', height: '10px', borderRadius: '6px', overflow: 'hidden', background: '#edebe9' }}>
        {c.completed > 0 && <div style={{ width: pct(c.completed), background: '#107c10' }} />}
        {c.failed > 0 && <div style={{ width: pct(c.failed), background: '#a4262c' }} />}
        {c.inprogress > 0 && <div style={{ width: pct(c.inprogress), background: '#0f6cbd' }} />}
        {c.skipped > 0 && <div style={{ width: pct(c.skipped), background: '#c8c6c4' }} />}
      </div>
      {active && (eta || (throttled > 0 && nextRetry)) && (
        <div style={{ display: 'flex', gap: '16px', flexWrap: 'wrap', fontSize: '12px', color: '#605e5c', marginTop: '6px' }}>
          {eta && <span>Estimated done <strong style={{ color: '#201f1e' }}>{formatEta(eta)}</strong></span>}
          {throttled > 0 && nextRetry && (
            <span style={{ color: '#835c00' }} title={`Next automatic retry at ${new Date(nextRetry).toLocaleString()}`}>
              {`\u23F3 ${formatNumber(throttled)} throttled — next retry ${formatCountdown(nextRetry)}`}
            </span>
          )}
        </div>
      )}
    </div>
  );
};

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

const CollapsibleMessageList: React.FC<{ title: string; messages: string[]; color: string; defaultOpen?: boolean }> = ({ title, messages, color, defaultOpen }) => {
  const [open, setOpen] = React.useState<boolean>(!!defaultOpen);
  if (messages.length === 0) { return null; }
  const toggle = (): void => setOpen(o => !o);
  return (
    <div style={{ margin: '8px 12px' }}>
      <div
        role="button"
        tabIndex={0}
        aria-expanded={open}
        onClick={toggle}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } }}
        style={{ display: 'flex', alignItems: 'center', gap: '6px', cursor: 'pointer', fontSize: '12px', fontWeight: 600, color }}
      >
        <span style={{ color: '#605e5c' }}>{open ? '▾' : '▸'}</span>
        <span>{`${title} (${formatNumber(messages.length)})`}</span>
      </div>
      {open && (
        <ul style={{ margin: '4px 0 0', paddingLeft: '18px', fontSize: '12px', color: '#323130', maxHeight: '220px', overflow: 'auto' }}>
          {messages.map((m, i) => <li key={i} style={{ wordBreak: 'break-word', marginBottom: '2px' }}>{m}</li>)}
        </ul>
      )}
    </div>
  );
};

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

const FileLeaf: React.FC<{ item: IJobItemStatus; depth: number }> = ({ item, depth }) => (
  <div style={{ display: 'flex', alignItems: 'flex-start', gap: '8px', borderTop: '1px solid #f8f7f6', fontSize: '13px', paddingTop: '5px', paddingBottom: '5px', paddingRight: '12px', paddingLeft: 12 + (depth + 1) * 16 }}>
    <span style={{ flex: '1 1 auto', wordBreak: 'break-all' }} title={item.spServerRelativeUrl}>{basename(item.spServerRelativeUrl)}</span>
    {item.lastError && <span style={{ color: '#a4262c', fontSize: '12px', maxWidth: '200px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={item.lastErrorDetail ?? item.lastError}>{item.lastError}</span>}
    {normalizeStatus(item.status) === MigrationLifecycleStatus.RetryScheduled && item.nextRetryAt && (
      <span
        style={{ color: '#835c00', fontSize: '12px', whiteSpace: 'nowrap', flex: '0 0 auto' }}
        title={`Throttled — automatic retry at ${new Date(item.nextRetryAt).toLocaleString()}${item.lastRetryAfterSeconds ? ` (server asked to wait ${item.lastRetryAfterSeconds}s)` : ''}`}
      >{`\u23F3 retry ${formatCountdown(item.nextRetryAt)}`}</span>
    )}
    <div style={{ flex: '0 0 auto', textAlign: 'right' }}>
      <Badge value={item.status} />
      <ItemStep item={item} />
    </div>
  </div>
);

const TreeFolderNode: React.FC<{
  node: FileTreeNode; depth: number; jobId: string;
  expandedFolders: ReadonlySet<string>; onToggleFolder: (nsKey: string) => void;
}> = ({ node, depth, jobId, expandedFolders, onToggleFolder }) => {
  const nsKey = `${jobId}::${node.path}`;
  const expanded = expandedFolders.has(nsKey);
  const rollup = folderRollup(node.descendants);
  const toggle = (): void => onToggleFolder(nsKey);
  return (
    <div style={{ borderTop: depth === 0 ? undefined : '1px solid #f8f7f6' }}>
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={toggle}
        onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } }}
        style={{ display: 'flex', alignItems: 'center', gap: '8px', cursor: 'pointer', paddingTop: '8px', paddingBottom: '8px', paddingRight: '12px', paddingLeft: 12 + depth * 16, background: '#faf9f8' }}
      >
        <span style={{ color: '#605e5c', flex: '0 0 auto' }}>{expanded ? '▾' : '▸'}</span>
        <span style={{ fontWeight: 600, fontSize: '13px', flex: '1 1 auto', wordBreak: 'break-all' }} title={node.path}>{node.name}</span>
        <span style={{ fontSize: '12px', color: '#605e5c', background: '#f3f2f1', borderRadius: '10px', padding: '1px 8px', flex: '0 0 auto' }}>{formatNumber(node.descendants.length)}</span>
        <span style={{ fontSize: '11px', color: '#fff', background: rollup.color, borderRadius: '10px', padding: '2px 8px', whiteSpace: 'nowrap', flex: '0 0 auto' }}>{rollup.label}</span>
      </div>
      {expanded && (
        <div>
          {node.folders.map(f => <TreeFolderNode key={f.path} node={f} depth={depth + 1} jobId={jobId} expandedFolders={expandedFolders} onToggleFolder={onToggleFolder} />)}
          {node.files.map(f => <FileLeaf key={f.item!.itemId} item={f.item!} depth={depth + 1} />)}
        </div>
      )}
    </div>
  );
};

const ItemsByFolder: React.FC<{
  job: ITrackedJob; items: IJobItemStatus[]; expandedFolders: ReadonlySet<string>;
  onToggleFolder: (nsKey: string) => void; onToggleAllFolders: (nsKeys: string[], expand: boolean) => void;
}> = ({ job, items, expandedFolders, onToggleFolder, onToggleAllFolders }) => {
  const tree = React.useMemo(() => buildFileTree(items), [items]);
  const folderPaths = React.useMemo(() => allTreeFolderPaths(tree), [tree]);
  const nsKeys = folderPaths.map(p => `${job.jobId}::${p}`);
  const allExpanded = nsKeys.length > 0 && nsKeys.every(k => expandedFolders.has(k));

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '6px 12px', fontSize: '12px', color: '#605e5c', borderBottom: '1px solid #f3f2f1' }}>
        <span>{`${formatNumber(items.length)} file${items.length === 1 ? '' : 's'} in ${formatNumber(folderPaths.length)} folder${folderPaths.length === 1 ? '' : 's'}`}</span>
        <button
          type="button"
          onClick={() => onToggleAllFolders(nsKeys, !allExpanded)}
          style={{ background: 'transparent', border: 'none', color: '#0078d4', cursor: 'pointer', fontSize: '12px', padding: '2px 4px' }}
        >{allExpanded ? 'Collapse all' : 'Expand all'}</button>
      </div>
      {tree.folders.map(f => (
        <TreeFolderNode key={f.path} node={f} depth={0} jobId={job.jobId} expandedFolders={expandedFolders} onToggleFolder={onToggleFolder} />
      ))}
      {tree.files.map(f => <FileLeaf key={f.item!.itemId} item={f.item!} depth={0} />)}
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
  const jobTerminal = job.lastResponse ? isTerminal(job.lastResponse.status) : false;
  const active = !jobTerminal;

  // Completed migrations collapse to a 1–2 line summary that expands on demand, so a big
  // batch doesn't fill the dialog with finished detail. Active jobs are always expanded.
  const [expanded, setExpanded] = React.useState(false);
  const showDetail = active || expanded;
  const c = jobCounts(items);

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

  // Compact one-line result shown in the (collapsed) header of a finished job.
  const collapsedSummary = jobTerminal && c.total > 0
    ? `${c.completed}/${c.total} done${c.failed > 0 ? ` · ${c.failed} failed` : ''}${c.skipped > 0 ? ` · ${c.skipped} skipped` : ''}`
    : undefined;

  const headerToggle = jobTerminal ? (): void => setExpanded(v => !v) : undefined;

  return (
    <section style={{ border: '1px solid #edebe9', borderRadius: '2px', marginBottom: '12px' }}>
      <div
        role={headerToggle ? 'button' : undefined}
        tabIndex={headerToggle ? 0 : undefined}
        aria-expanded={headerToggle ? expanded : undefined}
        onClick={headerToggle}
        onKeyDown={headerToggle ? (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setExpanded(v => !v); } } : undefined}
        style={{ display: 'flex', flexWrap: 'wrap', gap: '12px', alignItems: 'center', padding: '10px 12px', background: '#faf9f8', borderBottom: '1px solid #edebe9', cursor: headerToggle ? 'pointer' : undefined }}
      >
        {jobTerminal && <span style={{ color: '#605e5c', flex: '0 0 auto' }}>{expanded ? '▾' : '▸'}</span>}
        <span style={{ fontWeight: 600 }}>{job.label}</span>
        <Badge value={overallStatus} />
        {collapsedSummary && !expanded && (
          <span style={{ fontSize: '12px', color: c.failed > 0 ? '#a4262c' : '#605e5c' }}>{collapsedSummary}</span>
        )}
        <span style={{ fontSize: '12px', color: '#605e5c', fontFamily: 'Consolas, "Courier New", monospace' }}>{job.jobId}</span>
        {job.lastPollError && (
          <span style={{ fontSize: '12px', color: '#a4262c' }}>
            {` Refresh failing (${job.lastPollError.status === 0 ? 'network' : job.lastPollError.status}) — retrying…`}
          </span>
        )}
      </div>

      {/* Progress bar + ETA is always shown for active jobs (parity with the SPA). */}
      {active && <JobProgress job={job} items={items} active={active} />}

      {showDetail && (
        <>
          <JobMeta job={job} overallStatus={overallStatus} />

          {emptyText
            ? <p style={{ margin: '12px', color: '#605e5c', fontSize: '13px' }}>{emptyText}</p>
            : <ItemsByFolder job={job} items={items} expandedFolders={expandedFolders} onToggleFolder={onToggleFolder} onToggleAllFolders={onToggleAllFolders} />}

          {warnings.length > 0 && <CollapsibleMessageList title="Warnings" messages={warnings} color="#797775" />}
          {job.lastResponse && job.lastResponse.errors.length > 0 && <CollapsibleMessageList title="Errors" messages={job.lastResponse.errors} color="#a4262c" defaultOpen />}
          {job.lastResponse?.summary && <p style={{ margin: '8px 12px', fontSize: '12px', color: '#605e5c' }}>{job.lastResponse.summary}</p>}
          {job.logs && job.logs.length > 0 && <Timeline logs={job.logs} />}
        </>
      )}
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
  return <Banner kind="info">{`The background worker is online and processing${health.workerCount ? ` (${formatNumber(health.workerCount)} active instance${health.workerCount === 1 ? '' : 's'})` : ''} — queued items will start shortly.`}</Banner>;
};

const ConfirmView: React.FC<{
  operation: DialogMode; confirm: IConfirmRequest; onConfirm: () => void; onCancel: () => void;
}> = ({ operation, confirm, onConfirm, onCancel }) => {
  const MAX_SHOWN = 100;
  const shown = confirm.items.slice(0, MAX_SHOWN);
  const overflow = confirm.items.length - shown.length;
  const primary: React.CSSProperties = {
    background: operation === 'Migrate' ? '#0078d4' : '#107c10', color: '#fff', border: 'none',
    padding: '8px 18px', borderRadius: '2px', cursor: 'pointer', fontSize: '14px', fontWeight: 600,
  };
  const secondary: React.CSSProperties = {
    background: '#fff', color: '#323130', border: '1px solid #8a8886',
    padding: '8px 18px', borderRadius: '2px', cursor: 'pointer', fontSize: '14px',
  };
  return (
    <div>
      <Banner kind={operation === 'Migrate' ? 'warn' : 'info'}>{confirm.message}</Banner>
      <div style={{ fontSize: '12px', fontWeight: 600, color: '#605e5c', margin: '4px 0 6px' }}>
        {formatNumber(confirm.items.length)} item{confirm.items.length === 1 ? '' : 's'} selected
      </div>
      <div style={{ maxHeight: '38vh', overflow: 'auto', border: '1px solid #edebe9', borderRadius: '2px' }}>
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: '13px' }}>
          <tbody>
            {shown.map((it, i) => (
              <tr key={i} style={{ borderBottom: '1px solid #f3f2f1' }}>
                <td style={{ padding: '5px 10px', width: '18px' }}>{it.kind === 'Folder' ? '📁' : '📄'}</td>
                <td style={{ padding: '5px 10px', wordBreak: 'break-all' }}>{it.name}</td>
                <td style={{ padding: '5px 10px', color: '#605e5c', fontSize: '12px', whiteSpace: 'nowrap' }}>{it.kind}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {overflow > 0 && (
        <div style={{ fontSize: '12px', color: '#605e5c', marginTop: '6px' }}>…and {overflow} more</div>
      )}
      <div style={{ display: 'flex', gap: '8px', marginTop: '16px' }}>
        <button type="button" onClick={onConfirm} style={primary}>{confirm.confirmLabel}</button>
        <button type="button" onClick={onCancel} style={secondary}>Cancel</button>
      </div>
    </div>
  );
};

const DialogBody: React.FC<{ state: IDialogViewState; handlers: IDialogViewHandlers }> = ({ state, handlers }) => {
  if (state.phase === 'submitting' && state.jobs.length === 0) {
    return <Spinner message={state.statusMessage || 'Working…'} />;
  }
  if (state.phase === 'confirm' && state.confirm) {
    return (
      <ConfirmView
        operation={state.operation}
        confirm={state.confirm}
        onConfirm={handlers.onConfirm}
        onCancel={handlers.onCancel}
      />
    );
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
          {(allJobsSucceeded(state.jobs)
            ? 'All items reached a final state.'
            : 'All items have finished — some did not complete successfully (see details below).')
            + (state.refreshOnClose ? ' Close this dialog to refresh the library and see the changes.' : '')}
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
            {state.refreshOnClose
              ? 'Closing this dialog will refresh the document library so the changes appear. The job itself already finished on the server.'
              : 'Closing this dialog does not cancel the job — the server will keep working in the background and the cold-storage status column will update.'}
          </p>
        </div>
      </div>
    </div>
  );
};
