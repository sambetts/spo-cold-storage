import { CSSProperties, MouseEvent as ReactMouseEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Badge, Button, Checkbox, Input, Select, Spinner, Tooltip } from "@fluentui/react-components";
import { ArrowClockwise20Regular, ArrowLeft20Regular, ArrowSync20Regular, Info16Regular } from "@fluentui/react-icons";
import { ApiError, useApi } from "../../api/client";
import { JobItemStatus, JobLogEntry, JobStatus, JobSummary, MigrationOperationKind, WorkerHealth } from "../../api/types";
import { describeLogLevel, describeOperation, describeStatus, isErrorLevel, isFailedStatus, isInProgressStatus, isWarnLevel, StatusCategory, statusCategory } from "../../api/status";
import { fileName, formatCountdown, formatDateTime, formatEta, formatNumber, formatRelative } from "../../utils/format";

const REFRESH_MS = 10000;

// Resizable columns for the transfers table. Widths persist in localStorage so a
// user's layout survives reloads. The trailing "Files" column is auto-sized so it
// absorbs any remaining width.
const RESIZABLE_COLUMNS = [
  { key: "when", label: "When" },
  { key: "operation", label: "Operation" },
  { key: "status", label: "Status" },
  { key: "site", label: "Site" },
  { key: "requestedBy", label: "Requested by" },
] as const;
type ColKey = (typeof RESIZABLE_COLUMNS)[number]["key"];
const DEFAULT_COL_WIDTHS: Record<ColKey, number> = {
  when: 90,
  operation: 110,
  status: 150,
  site: 340,
  requestedBy: 280,
};
const COL_WIDTHS_KEY = "spocs.transfers.colWidths";
const MIN_COL_WIDTH = 60;

function useColumnWidths() {
  const [widths, setWidths] = useState<Record<ColKey, number>>(() => {
    try {
      const raw = localStorage.getItem(COL_WIDTHS_KEY);
      if (raw) return { ...DEFAULT_COL_WIDTHS, ...(JSON.parse(raw) as Partial<Record<ColKey, number>>) };
    } catch {
      /* ignore malformed storage */
    }
    return DEFAULT_COL_WIDTHS;
  });

  const startResize = useCallback(
    (key: ColKey, e: ReactMouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      const startX = e.clientX;
      const startWidth = widths[key];
      const onMove = (ev: MouseEvent) =>
        setWidths((prev) => ({ ...prev, [key]: Math.max(MIN_COL_WIDTH, startWidth + (ev.clientX - startX)) }));
      const onUp = () => {
        window.removeEventListener("mousemove", onMove);
        window.removeEventListener("mouseup", onUp);
        setWidths((prev) => {
          try {
            localStorage.setItem(COL_WIDTHS_KEY, JSON.stringify(prev));
          } catch {
            /* ignore storage write errors */
          }
          return prev;
        });
      };
      window.addEventListener("mousemove", onMove);
      window.addEventListener("mouseup", onUp);
    },
    [widths],
  );

  return { widths, startResize };
}

function ResizeHandle({ onMouseDown }: { onMouseDown: (e: ReactMouseEvent) => void }) {
  return (
    <span
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize column"
      onMouseDown={onMouseDown}
      onClick={(e) => e.stopPropagation()}
      style={{ position: "absolute", top: 0, right: 0, height: "100%", width: 10, cursor: "col-resize", userSelect: "none" }}
    />
  );
}

function StatusBadge({ status }: { status: JobSummary["status"] }) {
  const d = describeStatus(status);
  return (
    <Badge appearance="tint" color={d.tone}>
      {d.label}
    </Badge>
  );
}

// ---------------------------------------------------------------------------
// Worker health banner — surfaces whether the background worker is online, so a
// stuck "Queued" transfer is explainable ("worker offline") rather than a mystery.
// ---------------------------------------------------------------------------
function WorkerBanner() {
  const api = useApi();
  const [health, setHealth] = useState<WorkerHealth | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const poll = () =>
      api
        .get<WorkerHealth>("/api/worker/health")
        .then((h) => !cancelled && (setHealth(h), setFailed(false)))
        .catch(() => !cancelled && setFailed(true));
    void poll();
    const t = setInterval(poll, REFRESH_MS);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, [api]);

  if (failed || !health) return null;

  const online = health.isOnline;
  const windowSecs = health.onlineWindowSeconds || 100;
  const explanation =
    `The background worker is the service that actually moves files: it archives them to ` +
    `(and restores them from) Azure cold storage by processing the transfer queue. It runs ` +
    `serverless and auto-scales, so the number of active instances rises while a large ` +
    `migration is running and drops back to a small baseline when things are idle — a higher ` +
    `number just means more parallel throughput, not a problem. "Online" means at least one ` +
    `instance reported a heartbeat within the last ${windowSecs} seconds. If it shows offline, ` +
    `queued transfers pause until the worker wakes (it starts automatically when new items are queued).`;
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 8,
        padding: "8px 12px",
        borderRadius: 6,
        fontSize: 13,
        marginBottom: 12,
        background: online ? "#f1faf1" : "#fdf3f4",
        border: `1px solid ${online ? "#c5e8c5" : "#f3d6d8"}`,
        color: online ? "#0e700e" : "#a4262c",
      }}
    >
      <span style={{ fontWeight: 600 }}>{online ? "● Worker online" : "○ Worker offline"}</span>
      <span style={{ color: "#605e5c" }}>
        {online
          ? `${formatNumber(health.workerCount)} active instance${health.workerCount === 1 ? "" : "s"} · last beat ${formatRelative(
              health.lastSeenUtc,
            )}`
          : `No heartbeat in the last ${windowSecs}s — queued transfers won't progress until the worker wakes (it starts automatically when new items are queued).`}
      </span>
      <Tooltip content={explanation} relationship="description" withArrow>
        <span
          tabIndex={0}
          role="img"
          aria-label="What is the background worker?"
          style={{ display: "inline-flex", alignItems: "center", color: "#605e5c", cursor: "help" }}
        >
          <Info16Regular />
        </span>
      </Tooltip>
    </div>
  );
}

// ---------------------------------------------------------------------------
// List view — all recent transfers across sites, filterable.
// ---------------------------------------------------------------------------
type ResultFilter = "all" | "failures" | "inprogress" | "completed";

function TransfersList() {
  const api = useApi();
  const navigate = useNavigate();
  const { widths, startResize } = useColumnWidths();
  const [jobs, setJobs] = useState<JobSummary[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [operation, setOperation] = useState<"" | MigrationOperationKind>("");
  const [search, setSearch] = useState("");
  const [result, setResult] = useState<ResultFilter>("all");
  const [auto, setAuto] = useState(false);
  const [recovering, setRecovering] = useState(false);
  const [recoverMsg, setRecoverMsg] = useState<string | null>(null);
  const reqId = useRef(0);

  const load = useCallback(async () => {
    const id = ++reqId.current;
    setLoading(true);
    setError(null);
    try {
      const query = operation ? `?operation=${operation}&take=200` : "?take=200";
      const data = await api.get<JobSummary[]>(`/api/jobs/recent${query}`);
      if (id !== reqId.current) return;
      setJobs(data);
      setLoading(false);
    } catch (err) {
      if (id !== reqId.current) return;
      setError(
        err instanceof ApiError
          ? err.status === 403
            ? "Administrator access is required to view the transfers log."
            : err.message
          : String(err),
      );
      setLoading(false);
    }
  }, [api, operation]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    if (!auto) return;
    const t = setInterval(() => void load(), REFRESH_MS);
    return () => clearInterval(t);
  }, [auto, load]);

  const filtered = useMemo(() => {
    let rows = jobs ?? [];
    const needle = search.trim().toLowerCase();
    if (needle) {
      rows = rows.filter(
        (j) => j.siteUrl.toLowerCase().includes(needle) || j.requestedByUpn.toLowerCase().includes(needle),
      );
    }
    if (result === "failures") rows = rows.filter((j) => j.failedCount > 0);
    else if (result === "inprogress") rows = rows.filter((j) => j.inProgressCount > 0);
    else if (result === "completed") rows = rows.filter((j) => j.inProgressCount === 0 && j.failedCount === 0);
    return rows;
  }, [jobs, search, result]);

  const totalFailed = useMemo(() => (jobs ?? []).reduce((n, j) => n + j.failedCount, 0), [jobs]);

  const recoverAllFailed = useCallback(async () => {
    if (
      !window.confirm(
        `Recover all failed transfers? Every failed file is re-queued and re-processed from scratch; ` +
          `a source file is never deleted without a confirmed good copy.`,
      )
    ) {
      return;
    }
    setRecovering(true);
    setRecoverMsg(null);
    try {
      const res = await api.post<{ requeued: number; recovered?: number; skipped: number; publishFailed: number }>(
        "/api/admin/queue/requeue",
        { status: "AllFailed", max: 5000 },
      );
      setRecoverMsg(
        `Recovered ${formatNumber(res.requeued)}` +
          (res.recovered ? `, ${formatNumber(res.recovered)} already archived (fixed)` : "") +
          (res.skipped ? `, skipped ${formatNumber(res.skipped)}` : "") +
          (res.publishFailed ? `, ${formatNumber(res.publishFailed)} failed to publish` : "") +
          ".",
      );
      await load();
    } catch (err) {
      setRecoverMsg(
        err instanceof ApiError
          ? err.status === 403
            ? "Administrator access is required to recover failed transfers."
            : err.message
          : String(err),
      );
    } finally {
      setRecovering(false);
    }
  }, [api, load]);

  const recoverStuckQueued = useCallback(async () => {
    if (
      !window.confirm(
        `Recover stuck transfers? Items that have sat in 'Queued' for over 15 minutes (a lost worker ` +
          `message) are re-published. This is safe — duplicates are coalesced and no source is ever ` +
          `deleted without a confirmed copy.`,
      )
    ) {
      return;
    }
    setRecovering(true);
    setRecoverMsg(null);
    try {
      const res = await api.post<{ requeued: number; skipped: number; publishFailed: number }>(
        "/api/admin/queue/requeue",
        { status: "StaleQueued", olderThanMinutes: 15, max: 5000 },
      );
      setRecoverMsg(
        res.requeued > 0
          ? `Re-published ${formatNumber(res.requeued)} stuck item(s).`
          : "No stuck queued items found (older than 15 minutes).",
      );
      await load();
    } catch (err) {
      setRecoverMsg(
        err instanceof ApiError
          ? err.status === 403
            ? "Administrator access is required to recover stuck transfers."
            : err.message
          : String(err),
      );
    } finally {
      setRecovering(false);
    }
  }, [api, load]);

  return (
    <div style={{ maxWidth: 1200, margin: "0 auto" }}>
      <h2 style={{ margin: "0 0 2px 0" }}>Transfers &amp; logs</h2>
      <div style={{ color: "#605e5c", fontSize: 13, marginBottom: 12 }}>
        Every archive and restore across all sites. Select a transfer to see its per-file lifecycle and full log.
      </div>

      <WorkerBanner />

      <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap", marginBottom: 12 }}>
        <Select value={operation} onChange={(_, d) => setOperation(d.value as "" | MigrationOperationKind)} aria-label="Operation">
          <option value="">All operations</option>
          <option value="Migrate">Archive</option>
          <option value="Restore">Restore</option>
        </Select>
        <Select value={result} onChange={(_, d) => setResult(d.value as ResultFilter)} aria-label="Result">
          <option value="all">All results</option>
          <option value="inprogress">In progress</option>
          <option value="failures">With failures</option>
          <option value="completed">Completed</option>
        </Select>
        <Input
          value={search}
          onChange={(_, d) => setSearch(d.value)}
          placeholder="Search site or requester…"
          aria-label="Search transfers"
          style={{ minWidth: 240 }}
        />
        <Button icon={<ArrowClockwise20Regular />} appearance="subtle" onClick={() => void load()}>
          Refresh
        </Button>
        <Checkbox label="Auto-refresh" checked={auto} onChange={(_, d) => setAuto(!!d.checked)} />
        {totalFailed > 0 && (
          <Button
            icon={<ArrowSync20Regular />}
            appearance="primary"
            disabled={recovering}
            onClick={() => void recoverAllFailed()}
          >
            {recovering ? "Recovering…" : `Recover ${formatNumber(totalFailed)} failed`}
          </Button>
        )}
        <Button icon={<ArrowSync20Regular />} appearance="subtle" disabled={recovering} onClick={() => void recoverStuckQueued()}>
          Recover stuck queued
        </Button>
      </div>
      {recoverMsg && <div style={{ fontSize: 13, color: "#605e5c", marginBottom: 12 }}>{recoverMsg}</div>}

      {loading && !jobs && <Spinner label="Loading transfers…" size="small" />}
      {error && (
        <div style={{ color: "#a4262c", border: "1px solid #f3d6d8", background: "#fdf3f4", padding: 12, borderRadius: 6 }}>
          {error}
        </div>
      )}

      {jobs && !error && (
        <div style={{ border: "1px solid #edebe9", borderRadius: 8, overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13, tableLayout: "fixed" }}>
            <colgroup>
              {RESIZABLE_COLUMNS.map((c) => (
                <col key={c.key} style={{ width: widths[c.key] }} />
              ))}
              <col />
            </colgroup>
            <thead>
              <tr style={{ background: "#faf9f8", textAlign: "left", color: "#605e5c" }}>
                {RESIZABLE_COLUMNS.map((c) => (
                  <th key={c.key} style={{ ...th, position: "relative" }}>
                    {c.label}
                    <ResizeHandle onMouseDown={(e) => startResize(c.key, e)} />
                  </th>
                ))}
                <th style={th}>Files</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((job) => (
                <tr
                  key={job.jobId}
                  onClick={() => navigate(`/transfers/${job.jobId}`)}
                  style={{ borderTop: "1px solid #f3f2f1", cursor: "pointer" }}
                >
                  <td style={td} title={formatDateTime(job.createdAt)}>
                    {formatRelative(job.createdAt)}
                  </td>
                  <td style={td}>{describeOperation(job.operation)}</td>
                  <td style={td}>
                    <StatusBadge status={job.status} />
                  </td>
                  <td style={td} title={job.siteUrl}>
                    {job.siteUrl}
                  </td>
                  <td style={td}>{job.requestedByUpn}</td>
                  <td style={td}>
                    <span title="total">{formatNumber(job.itemCount)}</span>
                    {job.completedCount > 0 && <span style={{ color: "#107c10" }}> · {formatNumber(job.completedCount)}✓</span>}
                    {job.inProgressCount > 0 && <span style={{ color: "#0f6cbd" }}> · {formatNumber(job.inProgressCount)}⋯</span>}
                    {(job.throttledCount ?? 0) > 0 && <span style={{ color: "#835c00" }}> · {formatNumber(job.throttledCount)}⏳</span>}
                    {job.failedCount > 0 && <span style={{ color: "#a4262c" }}> · {formatNumber(job.failedCount)}✕</span>}
                    {job.inProgressCount > 0 && job.estimatedCompletionUtc && (
                      <div style={{ fontSize: 11, color: "#605e5c" }} title={`Estimated done ${formatEta(job.estimatedCompletionUtc)}`}>
                        ETA {formatCountdown(job.estimatedCompletionUtc)}
                      </div>
                    )}
                  </td>
                </tr>
              ))}
              {filtered.length === 0 && (
                <tr>
                  <td style={{ ...td, color: "#605e5c" }} colSpan={6}>
                    No transfers match your filters.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Detail view helpers — folder grouping, progress bar, collapsible sections.
// ---------------------------------------------------------------------------
function folderOf(path: string): string {
  const clean = (path || "").replace(/\/+$/, "");
  const i = clean.lastIndexOf("/");
  return i > 0 ? clean.slice(0, i) : "/";
}

function shortFolder(path: string): string {
  const segs = path.split("/").filter(Boolean);
  if (path === "/" || segs.length === 0) return "/ (site root)";
  if (segs.length <= 3) return "/" + segs.join("/");
  return "…/" + segs.slice(-2).join("/");
}

/** Pull the first quoted path out of a warning string (for folder grouping). */
function warningPath(w: string): string {
  const m = w.match(/'([^']+)'/);
  return m ? m[1] : "";
}

function groupByFolder<T>(items: readonly T[], pathOf: (t: T) => string): { folder: string; items: T[] }[] {
  const map = new Map<string, T[]>();
  for (const it of items) {
    const f = folderOf(pathOf(it));
    const arr = map.get(f);
    if (arr) arr.push(it);
    else map.set(f, [it]);
  }
  return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([folder, groupItems]) => ({ folder, items: groupItems }));
}

function folderRollup(items: JobItemStatus[]): { label: string; color: string } {
  let done = 0;
  let failed = 0;
  let inprog = 0;
  for (const it of items) {
    const c = statusCategory(it.status);
    if (c === "completed") done++;
    else if (c === "failed") failed++;
    else if (c === "inprogress") inprog++;
  }
  const total = items.length;
  if (inprog > 0) return { label: `${done}/${total} done`, color: "#0f6cbd" };
  if (failed > 0) return { label: `${done}/${total} · ${failed} failed`, color: "#a4262c" };
  return { label: `all ${total} done`, color: "#107c10" };
}

const disclosureHeaderStyle: CSSProperties = {
  display: "flex",
  alignItems: "center",
  gap: 8,
  width: "100%",
  padding: "8px 10px",
  border: "none",
  background: "transparent",
  cursor: "pointer",
  fontSize: 13,
  textAlign: "left",
};

function FolderDisclosure({
  folder,
  count,
  rollup,
  open,
  onToggle,
  children,
}: {
  folder: string;
  count: number;
  rollup?: { label: string; color: string };
  open: boolean;
  onToggle: () => void;
  children: React.ReactNode;
}) {
  return (
    <div style={{ borderBottom: "1px solid #f3f2f1" }}>
      <button type="button" onClick={onToggle} aria-expanded={open} style={{ ...disclosureHeaderStyle, background: "#faf9f8" }}>
        <span style={{ color: "#605e5c" }}>{open ? "▾" : "▸"}</span>
        <span style={{ fontWeight: 600, flex: "1 1 auto", wordBreak: "break-all" }} title={folder}>
          {shortFolder(folder)}
        </span>
        <span style={{ fontSize: 12, color: "#605e5c", background: "#f3f2f1", borderRadius: 10, padding: "1px 8px" }}>{formatNumber(count)}</span>
        {rollup && (
          <span style={{ fontSize: 11, color: "#fff", background: rollup.color, borderRadius: 10, padding: "2px 8px", whiteSpace: "nowrap" }}>
            {rollup.label}
          </span>
        )}
      </button>
      {open && <div style={{ padding: "0 8px" }}>{children}</div>}
    </div>
  );
}

// --- Nested file tree (folders → subfolders → files) ----------------------
interface FileTreeNode {
  name: string;
  path: string;
  item?: JobItemStatus;
  folders: FileTreeNode[];
  files: FileTreeNode[];
  descendants: JobItemStatus[];
}

/** Build a nested folder/subfolder/file tree from the flat item list. */
function buildFileTree(items: readonly JobItemStatus[]): FileTreeNode {
  const root: FileTreeNode = { name: "", path: "", folders: [], files: [], descendants: [] };
  const index = new Map<string, FileTreeNode>([["", root]]);
  for (const it of items) {
    const full = (it.spServerRelativeUrl || "").replace(/\/+$/, "");
    const segs = full.split("/").filter(Boolean);
    const leaf = segs.pop() ?? full;
    let parent = root;
    let acc = "";
    for (const seg of segs) {
      acc += "/" + seg;
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

function fillDescendants(node: FileTreeNode): JobItemStatus[] {
  const acc: JobItemStatus[] = [];
  for (const f of node.files) if (f.item) acc.push(f.item);
  for (const f of node.folders) acc.push(...fillDescendants(f));
  node.descendants = acc;
  return acc;
}

/** Collapse chains of single-child folders (e.g. a/b/c) into one node, VS Code style. */
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

function allFolderPaths(node: FileTreeNode, out: string[] = []): string[] {
  for (const f of node.folders) {
    out.push(f.path);
    allFolderPaths(f, out);
  }
  return out;
}

const treeRowBase: CSSProperties = {
  display: "flex",
  alignItems: "center",
  gap: 8,
  width: "100%",
  border: "none",
  background: "transparent",
  cursor: "pointer",
  fontSize: 13,
  textAlign: "left",
  paddingTop: 8,
  paddingRight: 10,
  paddingBottom: 8,
};

function TreeFolderNode({
  node,
  depth,
  expanded,
  onToggle,
}: {
  node: FileTreeNode;
  depth: number;
  expanded: ReadonlySet<string>;
  onToggle: (path: string) => void;
}) {
  const open = expanded.has(node.path);
  const rollup = folderRollup(node.descendants);
  return (
    <div style={{ borderTop: depth === 0 ? undefined : "1px solid #f8f7f6" }}>
      <button
        type="button"
        onClick={() => onToggle(node.path)}
        aria-expanded={open}
        style={{ ...treeRowBase, paddingLeft: 10 + depth * 16, background: "#faf9f8" }}
      >
        <span style={{ color: "#605e5c" }}>{open ? "▾" : "▸"}</span>
        <span style={{ fontWeight: 600, flex: "1 1 auto", wordBreak: "break-all" }} title={node.path}>
          {node.name}
        </span>
        <span style={{ fontSize: 12, color: "#605e5c", background: "#f3f2f1", borderRadius: 10, padding: "1px 8px" }}>{formatNumber(node.descendants.length)}</span>
        <span style={{ fontSize: 11, color: "#fff", background: rollup.color, borderRadius: 10, padding: "2px 8px", whiteSpace: "nowrap" }}>
          {rollup.label}
        </span>
      </button>
      {open && (
        <div>
          {node.folders.map((f) => (
            <TreeFolderNode key={f.path} node={f} depth={depth + 1} expanded={expanded} onToggle={onToggle} />
          ))}
          {node.files.map((f) => (
            <FileLeaf key={f.item!.itemId} item={f.item!} depth={depth + 1} />
          ))}
        </div>
      )}
    </div>
  );
}

function FileLeaf({ item, depth }: { item: JobItemStatus; depth: number }) {
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        gap: 8,
        borderTop: "1px solid #f8f7f6",
        fontSize: 13,
        paddingTop: 5,
        paddingRight: 10,
        paddingBottom: 5,
        paddingLeft: 10 + (depth + 1) * 16,
      }}
    >
      <span style={{ flex: "1 1 auto", wordBreak: "break-all" }} title={item.spServerRelativeUrl}>
        {fileName(item.spServerRelativeUrl)}
      </span>
      {item.lastError && (
        <span style={{ color: "#a4262c", fontSize: 12, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis", maxWidth: 220 }} title={item.lastErrorDetail ?? item.lastError}>
          {item.lastError}
        </span>
      )}
      {item.status === "RetryScheduled" && item.nextRetryAt && (
        <span
          style={{ color: "#835c00", fontSize: 12, whiteSpace: "nowrap" }}
          title={`Throttled — automatic retry at ${formatDateTime(item.nextRetryAt)}${item.lastRetryAfterSeconds ? ` (server asked to wait ${item.lastRetryAfterSeconds}s)` : ""}`}
        >
          ⏳ retry {formatCountdown(item.nextRetryAt)}
        </span>
      )}
      <span style={{ color: "#605e5c", fontSize: 12, whiteSpace: "nowrap" }} title={formatDateTime(item.updatedAt)}>
        {formatRelative(item.updatedAt)}
      </span>
      <StatusBadge status={item.status} />
    </div>
  );
}

interface Counts {
  completed: number;
  failed: number;
  skipped: number;
  inprogress: number;
  total: number;
}

function TransferProgress({
  counts,
  inProgress,
  eta,
  throttled,
  nextRetry,
}: {
  counts: Counts;
  inProgress: boolean;
  eta?: string | null;
  throttled?: number;
  nextRetry?: string | null;
}) {
  const { completed, failed, skipped, inprogress, total } = counts;
  if (total === 0) return null;
  const pct = (n: number) => `${(n / total) * 100}%`;
  const throttledCount = throttled ?? 0;
  return (
    <div style={{ marginBottom: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", fontSize: 13, color: "#605e5c", marginBottom: 4, gap: 12, flexWrap: "wrap" }}>
        <span>
          <strong style={{ color: "#201f1e" }}>{formatNumber(completed)}</strong> of {formatNumber(total)} done
          {failed > 0 ? ` · ${formatNumber(failed)} failed` : ""}
          {skipped > 0 ? ` · ${formatNumber(skipped)} skipped` : ""}
          {throttledCount > 0 ? ` · ${formatNumber(throttledCount)} throttled` : ""}
        </span>
        <span>{inProgress ? `${formatNumber(inprogress)} in progress · auto-refreshing…` : "Finished"}</span>
      </div>
      <div style={{ display: "flex", height: 10, borderRadius: 6, overflow: "hidden", background: "#edebe9" }}>
        {completed > 0 && <div style={{ width: pct(completed), background: "#107c10" }} />}
        {failed > 0 && <div style={{ width: pct(failed), background: "#a4262c" }} />}
        {inprogress > 0 && <div style={{ width: pct(inprogress), background: "#0f6cbd" }} />}
        {skipped > 0 && <div style={{ width: pct(skipped), background: "#c8c6c4" }} />}
      </div>
      {inProgress && (eta || (throttledCount > 0 && nextRetry)) && (
        <div style={{ display: "flex", gap: 16, flexWrap: "wrap", fontSize: 12, color: "#605e5c", marginTop: 6 }}>
          {eta && (
            <span>
              Estimated done <strong style={{ color: "#201f1e" }}>{formatEta(eta)}</strong>
            </span>
          )}
          {throttledCount > 0 && nextRetry && (
            <span style={{ color: "#835c00" }} title={`Next automatic retry at ${formatDateTime(nextRetry)}`}>
              ⏳ {formatNumber(throttledCount)} throttled — next retry {formatCountdown(nextRetry)}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Detail view — one transfer: its items + full log timeline.
// ---------------------------------------------------------------------------
function TransferDetail({ jobId }: { jobId: string }) {
  const api = useApi();
  const [job, setJob] = useState<JobStatus | null>(null);
  const [logs, setLogs] = useState<JobLogEntry[] | null>(null);
  const [logsOpen, setLogsOpen] = useState(false);
  const [logsLoading, setLogsLoading] = useState(false);
  const [logTake, setLogTake] = useState(500);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [requeueing, setRequeueing] = useState(false);
  const [requeueMsg, setRequeueMsg] = useState<string | null>(null);

  const loadJob = useCallback(async () => {
    setError(null);
    try {
      setJob(await api.get<JobStatus>(`/api/jobs/${jobId}`));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [api, jobId]);

  const loadLogs = useCallback(
    async (take: number) => {
      setLogsLoading(true);
      try {
        setLogs(await api.get<JobLogEntry[]>(`/api/jobs/${jobId}/logs?take=${take}`));
      } catch {
        /* keep the previous logs on a transient error */
      } finally {
        setLogsLoading(false);
      }
    },
    [api, jobId],
  );

  const refresh = useCallback(async () => {
    await loadJob();
    if (logsOpen) await loadLogs(logTake);
  }, [loadJob, loadLogs, logsOpen, logTake]);

  const loadMoreLogs = useCallback(() => {
    const next = Math.min(logTake + 1000, 5000);
    setLogTake(next);
    void loadLogs(next);
  }, [logTake, loadLogs]);

  useEffect(() => {
    void loadJob();
  }, [loadJob]);

  // Fetch logs lazily the first time the timeline is expanded, so the 8s
  // auto-refresh below doesn't pull up to 1,000 log rows on every tick.
  useEffect(() => {
    if (logsOpen && logs === null && !logsLoading) void loadLogs(logTake);
  }, [logsOpen, logs, logsLoading, loadLogs, logTake]);

  const failedCount = useMemo(() => (job?.items ?? []).filter((i) => isFailedStatus(i.status)).length, [job]);

  const items = useMemo(() => job?.items ?? [], [job]);
  const counts = useMemo<Counts>(() => {
    const c: Counts = { completed: 0, failed: 0, skipped: 0, inprogress: 0, total: items.length };
    for (const it of items) c[statusCategory(it.status)]++;
    return c;
  }, [items]);
  const inProgress = counts.inprogress > 0 || (job !== null && items.length === 0 && isInProgressStatus(job.status));

  const [statusFilter, setStatusFilter] = useState<"" | StatusCategory>("");
  const [expandedFolders, setExpandedFolders] = useState<Set<string>>(new Set());
  const [warningsOpen, setWarningsOpen] = useState(false);

  const filteredItems = useMemo(
    () => (statusFilter ? items.filter((i) => statusCategory(i.status) === statusFilter) : items),
    [items, statusFilter],
  );
  const fileTree = useMemo(() => buildFileTree(filteredItems), [filteredItems]);
  const warningGroups = useMemo(() => groupByFolder(job?.warnings ?? [], warningPath), [job]);

  const toggleFolder = useCallback((key: string) => {
    setExpandedFolders((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }, []);

  // Auto-refresh while the transfer is still in progress; stops once terminal.
  useEffect(() => {
    if (!inProgress) return;
    const t = setInterval(() => void refresh(), 8000);
    return () => clearInterval(t);
  }, [inProgress, refresh]);

  const requeue = useCallback(async () => {
    if (!job) return;
    if (
      !window.confirm(
        `Requeue ${formatNumber(failedCount)} failed file(s) for re-processing? They are re-copied and re-validated from scratch; ` +
          `a source file is never deleted without a confirmed good copy.`,
      )
    ) {
      return;
    }
    setRequeueing(true);
    setRequeueMsg(null);
    try {
      const res = await api.post<{ requeued: number; recovered?: number; skipped: number; publishFailed: number }>(
        "/api/admin/queue/requeue",
        { jobId: job.jobId },
      );
      setRequeueMsg(
        `Requeued ${formatNumber(res.requeued)}` +
          (res.recovered ? `, ${formatNumber(res.recovered)} already archived (fixed)` : "") +
          (res.skipped ? `, skipped ${formatNumber(res.skipped)}` : "") +
          (res.publishFailed ? `, ${formatNumber(res.publishFailed)} failed to publish` : "") +
          ".",
      );
      await refresh();
    } catch (err) {
      setRequeueMsg(
        err instanceof ApiError
          ? err.status === 403
            ? "Administrator access is required to requeue."
            : err.message
          : String(err),
      );
    } finally {
      setRequeueing(false);
    }
  }, [api, job, failedCount, refresh]);

  return (
    <div style={{ maxWidth: 1200, margin: "0 auto" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 8, flexWrap: "wrap" }}>
        <Link to="/transfers" style={{ display: "inline-flex", alignItems: "center", gap: 4, color: "#0f6cbd", textDecoration: "none" }}>
          <ArrowLeft20Regular /> All transfers
        </Link>
        <Button icon={<ArrowClockwise20Regular />} appearance="subtle" size="small" onClick={() => void refresh()}>
          Refresh
        </Button>
        {failedCount > 0 && (
          <Button
            icon={<ArrowSync20Regular />}
            appearance="primary"
            size="small"
            disabled={requeueing}
            onClick={() => void requeue()}
          >
            {requeueing ? "Requeuing…" : `Requeue ${formatNumber(failedCount)} failed`}
          </Button>
        )}
        {requeueMsg && <span style={{ fontSize: 13, color: "#605e5c" }}>{requeueMsg}</span>}
      </div>

      {loading && !job && <Spinner label="Loading transfer…" size="small" />}
      {error && (
        <div style={{ color: "#a4262c", border: "1px solid #f3d6d8", background: "#fdf3f4", padding: 12, borderRadius: 6 }}>
          {error}
        </div>
      )}

      {job && (
        <>
          <h2 style={{ margin: "0 0 4px 0" }}>
            {describeOperation(job.operation)} — <StatusBadge status={job.status} />
          </h2>
          <div style={{ color: "#605e5c", fontSize: 13, marginBottom: 4 }}>{job.siteUrl}</div>
          <div style={{ color: "#605e5c", fontSize: 12, marginBottom: 16 }}>
            Requested by {job.requestedByUpn} · started {formatDateTime(job.createdAt)}
            {job.completedAt ? ` · finished ${formatDateTime(job.completedAt)}` : ""} · job {job.jobId}
          </div>

          <TransferProgress
            counts={counts}
            inProgress={inProgress}
            eta={job?.estimatedCompletionUtc}
            throttled={job?.throttledCount}
            nextRetry={job?.nextRetryUtc}
          />

          {job.errors.length > 0 && (
            <div style={{ marginBottom: 12 }}>
              {job.errors.map((e, i) => (
                <div key={`e${i}`} style={{ color: "#a4262c", fontSize: 13 }}>
                  ✕ {e}
                </div>
              ))}
            </div>
          )}

          {job.warnings.length > 0 && (
            <div style={{ border: "1px solid #f2e2b3", background: "#fffdf5", borderRadius: 8, marginBottom: 16 }}>
              <button type="button" onClick={() => setWarningsOpen((v) => !v)} aria-expanded={warningsOpen} style={disclosureHeaderStyle}>
                <span style={{ color: "#605e5c" }}>{warningsOpen ? "▾" : "▸"}</span>
                <span style={{ fontWeight: 600, color: "#835c00" }}>
                  ⚠ {formatNumber(job.warnings.length)} warning{job.warnings.length === 1 ? "" : "s"}
                </span>
                <span style={{ color: "#605e5c" }}>· grouped by folder</span>
              </button>
              {warningsOpen && (
                <div style={{ padding: "0 8px 4px" }}>
                  {warningGroups.map((g) => (
                    <FolderDisclosure
                      key={`w-${g.folder}`}
                      folder={g.folder}
                      count={g.items.length}
                      open={expandedFolders.has(`w-${g.folder}`)}
                      onToggle={() => toggleFolder(`w-${g.folder}`)}
                    >
                      <ul style={{ margin: "4px 0 8px 0", paddingLeft: 22 }}>
                        {g.items.map((w, i) => (
                          <li key={i} style={{ color: "#835c00", fontSize: 12.5, wordBreak: "break-word" }}>
                            {w}
                          </li>
                        ))}
                      </ul>
                    </FolderDisclosure>
                  ))}
                </div>
              )}
            </div>
          )}

          <div style={{ display: "flex", alignItems: "center", gap: 12, margin: "4px 0 8px", flexWrap: "wrap" }}>
            <h3 style={{ margin: 0, fontSize: 15 }}>Files ({formatNumber(job.items.length)})</h3>
            <Select
              value={statusFilter}
              onChange={(_, d) => setStatusFilter(d.value as "" | StatusCategory)}
              aria-label="Filter files by status"
            >
              <option value="">All statuses</option>
              <option value="inprogress">In progress ({formatNumber(counts.inprogress)})</option>
              <option value="completed">Completed ({formatNumber(counts.completed)})</option>
              <option value="failed">Failed ({formatNumber(counts.failed)})</option>
              <option value="skipped">Skipped ({formatNumber(counts.skipped)})</option>
            </Select>
            <span style={{ flex: 1 }} />
            <Button size="small" appearance="subtle" onClick={() => setExpandedFolders(new Set(allFolderPaths(fileTree)))}>
              Expand all
            </Button>
            <Button size="small" appearance="subtle" onClick={() => setExpandedFolders(new Set())}>
              Collapse all
            </Button>
          </div>
          <div style={{ border: "1px solid #edebe9", borderRadius: 8, overflow: "hidden", marginBottom: 24 }}>
            {fileTree.folders.map((f) => (
              <TreeFolderNode key={f.path} node={f} depth={0} expanded={expandedFolders} onToggle={toggleFolder} />
            ))}
            {fileTree.files.map((f) => (
              <FileLeaf key={f.item!.itemId} item={f.item!} depth={0} />
            ))}
            {fileTree.folders.length === 0 && fileTree.files.length === 0 && (
              <div style={{ color: "#605e5c", fontSize: 13, padding: 12 }}>
                {job.items.length === 0 ? "No files were queued for this transfer." : "No files match this filter."}
              </div>
            )}
          </div>

          <button
            type="button"
            onClick={() => setLogsOpen((v) => !v)}
            aria-expanded={logsOpen}
            style={{ ...disclosureHeaderStyle, padding: "6px 0", fontSize: 15, fontWeight: 600 }}
          >
            <span style={{ color: "#605e5c" }}>{logsOpen ? "▾" : "▸"}</span> Log timeline
            {logsLoading && <Spinner size="tiny" />}
          </button>
          {logsOpen && (
            <div style={{ border: "1px solid #edebe9", borderRadius: 8, overflow: "hidden" }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 12.5 }}>
                <tbody>
                  {(logs ?? []).map((log, i) => (
                    <tr key={i} style={{ borderTop: i === 0 ? "none" : "1px solid #f3f2f1" }}>
                      <td style={{ ...td, whiteSpace: "nowrap", color: "#605e5c" }} title={formatDateTime(log.timestamp)}>
                        {formatDateTime(log.timestamp)}
                      </td>
                      <td style={{ ...td, whiteSpace: "nowrap" }}>
                        <span
                          style={{
                            fontWeight: 600,
                            color: isErrorLevel(log.level) ? "#a4262c" : isWarnLevel(log.level) ? "#835c00" : "#605e5c",
                          }}
                        >
                          {describeLogLevel(log.level)}
                        </span>
                      </td>
                      <td style={td}>
                        {log.message}
                        {log.exception && (
                          <details style={{ marginTop: 4 }}>
                            <summary style={{ cursor: "pointer", color: "#605e5c" }}>Exception</summary>
                            <pre style={{ whiteSpace: "pre-wrap", wordBreak: "break-word", fontSize: 11, color: "#a4262c" }}>
                              {log.exception}
                            </pre>
                          </details>
                        )}
                      </td>
                    </tr>
                  ))}
                  {logs && logs.length === 0 && (
                    <tr>
                      <td style={{ ...td, color: "#605e5c" }}>No log entries yet.</td>
                    </tr>
                  )}
                </tbody>
              </table>
              {logs && logs.length >= logTake && logTake < 5000 && (
                <div style={{ padding: 8, textAlign: "center", borderTop: "1px solid #f3f2f1" }}>
                  <Button size="small" appearance="subtle" disabled={logsLoading} onClick={loadMoreLogs}>
                    {logsLoading ? "Loading…" : "Load older entries"}
                  </Button>
                </div>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
}

const th: CSSProperties = { padding: "10px 12px", fontWeight: 600 };
const td: CSSProperties = { padding: "10px 12px", verticalAlign: "top", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" };

export function TransfersLog() {
  const { jobId } = useParams<{ jobId?: string }>();
  return jobId ? <TransferDetail jobId={jobId} /> : <TransfersList />;
}
