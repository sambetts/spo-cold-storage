import { CSSProperties, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Badge, Button, Checkbox, Input, Select, Spinner } from "@fluentui/react-components";
import { ArrowClockwise20Regular, ArrowLeft20Regular, ArrowSync20Regular } from "@fluentui/react-icons";
import { ApiError, useApi } from "../../api/client";
import { JobLogEntry, JobStatus, JobSummary, MigrationOperationKind, WorkerHealth } from "../../api/types";
import { describeLogLevel, describeOperation, describeStatus, isErrorLevel, isFailedStatus, isWarnLevel } from "../../api/status";
import { fileName, formatDateTime, formatRelative } from "../../utils/format";

const REFRESH_MS = 10000;

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
          ? `${health.workerCount} instance${health.workerCount === 1 ? "" : "s"}, last beat ${formatRelative(
              health.lastSeenUtc,
            )}`
          : "No recent heartbeat — queued transfers may not progress until it wakes."}
      </span>
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
      const res = await api.post<{ requeued: number; skipped: number; publishFailed: number }>(
        "/api/admin/queue/requeue",
        { status: "AllFailed", max: 5000 },
      );
      setRecoverMsg(
        `Recovered ${res.requeued}` +
          (res.skipped ? `, skipped ${res.skipped}` : "") +
          (res.publishFailed ? `, ${res.publishFailed} failed to publish` : "") +
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
          ? `Re-published ${res.requeued} stuck item(s).`
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
            {recovering ? "Recovering…" : `Recover ${totalFailed} failed`}
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
        <div style={{ border: "1px solid #edebe9", borderRadius: 8, overflow: "hidden" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
            <thead>
              <tr style={{ background: "#faf9f8", textAlign: "left", color: "#605e5c" }}>
                <th style={th}>When</th>
                <th style={th}>Operation</th>
                <th style={th}>Status</th>
                <th style={th}>Site</th>
                <th style={th}>Requested by</th>
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
                  <td style={{ ...td, maxWidth: 320, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={job.siteUrl}>
                    {job.siteUrl}
                  </td>
                  <td style={td}>{job.requestedByUpn}</td>
                  <td style={td}>
                    <span title="total">{job.itemCount}</span>
                    {job.completedCount > 0 && <span style={{ color: "#107c10" }}> · {job.completedCount}✓</span>}
                    {job.inProgressCount > 0 && <span style={{ color: "#0f6cbd" }}> · {job.inProgressCount}⋯</span>}
                    {job.failedCount > 0 && <span style={{ color: "#a4262c" }}> · {job.failedCount}✕</span>}
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
// Detail view — one transfer: its items + full log timeline.
// ---------------------------------------------------------------------------
function TransferDetail({ jobId }: { jobId: string }) {
  const api = useApi();
  const [job, setJob] = useState<JobStatus | null>(null);
  const [logs, setLogs] = useState<JobLogEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [requeueing, setRequeueing] = useState(false);
  const [requeueMsg, setRequeueMsg] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [j, l] = await Promise.all([
        api.get<JobStatus>(`/api/jobs/${jobId}`),
        api.get<JobLogEntry[]>(`/api/jobs/${jobId}/logs?take=1000`),
      ]);
      setJob(j);
      setLogs(l);
      setLoading(false);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : String(err));
      setLoading(false);
    }
  }, [api, jobId]);

  useEffect(() => {
    void load();
  }, [load]);

  const failedCount = useMemo(() => (job?.items ?? []).filter((i) => isFailedStatus(i.status)).length, [job]);

  const requeue = useCallback(async () => {
    if (!job) return;
    if (
      !window.confirm(
        `Requeue ${failedCount} failed file(s) for re-processing? They are re-copied and re-validated from scratch; ` +
          `a source file is never deleted without a confirmed good copy.`,
      )
    ) {
      return;
    }
    setRequeueing(true);
    setRequeueMsg(null);
    try {
      const res = await api.post<{ requeued: number; skipped: number; publishFailed: number }>(
        "/api/admin/queue/requeue",
        { jobId: job.jobId },
      );
      setRequeueMsg(
        `Requeued ${res.requeued}` +
          (res.skipped ? `, skipped ${res.skipped}` : "") +
          (res.publishFailed ? `, ${res.publishFailed} failed to publish` : "") +
          ".",
      );
      await load();
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
  }, [api, job, failedCount, load]);

  return (
    <div style={{ maxWidth: 1200, margin: "0 auto" }}>
      <div style={{ display: "flex", alignItems: "center", gap: 12, marginBottom: 8, flexWrap: "wrap" }}>
        <Link to="/transfers" style={{ display: "inline-flex", alignItems: "center", gap: 4, color: "#0f6cbd", textDecoration: "none" }}>
          <ArrowLeft20Regular /> All transfers
        </Link>
        <Button icon={<ArrowClockwise20Regular />} appearance="subtle" size="small" onClick={() => void load()}>
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
            {requeueing ? "Requeuing…" : `Requeue ${failedCount} failed`}
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

          {(job.errors.length > 0 || job.warnings.length > 0) && (
            <div style={{ marginBottom: 16 }}>
              {job.errors.map((e, i) => (
                <div key={`e${i}`} style={{ color: "#a4262c", fontSize: 13 }}>
                  ✕ {e}
                </div>
              ))}
              {job.warnings.map((w, i) => (
                <div key={`w${i}`} style={{ color: "#835c00", fontSize: 13 }}>
                  ⚠ {w}
                </div>
              ))}
            </div>
          )}

          <h3 style={{ margin: "0 0 8px 0", fontSize: 15 }}>Files ({job.items.length})</h3>
          <div style={{ border: "1px solid #edebe9", borderRadius: 8, overflow: "hidden", marginBottom: 24 }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
              <thead>
                <tr style={{ background: "#faf9f8", textAlign: "left", color: "#605e5c" }}>
                  <th style={th}>File</th>
                  <th style={th}>Status</th>
                  <th style={th}>Attempts</th>
                  <th style={th}>Updated</th>
                  <th style={th}>Detail</th>
                </tr>
              </thead>
              <tbody>
                {job.items.map((item) => (
                  <tr key={item.itemId} style={{ borderTop: "1px solid #f3f2f1" }}>
                    <td style={{ ...td, maxWidth: 380, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }} title={item.spServerRelativeUrl}>
                      {fileName(item.spServerRelativeUrl)}
                    </td>
                    <td style={td}>
                      <StatusBadge status={item.status} />
                    </td>
                    <td style={td}>{item.attempts}</td>
                    <td style={td} title={formatDateTime(item.updatedAt)}>
                      {formatRelative(item.updatedAt)}
                    </td>
                    <td style={{ ...td, color: item.lastError ? "#a4262c" : "#605e5c" }}>{item.lastError ?? "—"}</td>
                  </tr>
                ))}
                {job.items.length === 0 && (
                  <tr>
                    <td style={{ ...td, color: "#605e5c" }} colSpan={5}>
                      No files were queued for this transfer.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>

          <h3 style={{ margin: "0 0 8px 0", fontSize: 15 }}>Log timeline</h3>
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
          </div>
        </>
      )}
    </div>
  );
}

const th: CSSProperties = { padding: "10px 12px", fontWeight: 600 };
const td: CSSProperties = { padding: "10px 12px", verticalAlign: "top" };

export function TransfersLog() {
  const { jobId } = useParams<{ jobId?: string }>();
  return jobId ? <TransferDetail jobId={jobId} /> : <TransfersList />;
}
