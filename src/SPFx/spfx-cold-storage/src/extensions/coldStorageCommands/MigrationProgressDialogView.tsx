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
        <span>{`${title} (${messages.length})`}</span>
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
        <span style={{ fontSize: '12px', color: '#605e5c', background: '#f3f2f1', borderRadius: '10px', padding: '1px 8px', flex: '0 0 auto' }}>{node.descendants.length}</span>
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
        <span>{`${items.length} file${items.length === 1 ? '' : 's'} in ${folderPaths.length} folder${folderPaths.length === 1 ? '' : 's'}`}</span>
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

      {warnings.length > 0 && <CollapsibleMessageList title="Warnings" messages={warnings} color="#797775" />}
      {job.lastResponse && job.lastResponse.errors.length > 0 && <CollapsibleMessageList title="Errors" messages={job.lastResponse.errors} color="#a4262c" defaultOpen />}
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
        {confirm.items.length} item{confirm.items.length === 1 ? '' : 's'} selected
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
