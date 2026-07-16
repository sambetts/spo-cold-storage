import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./FileExplorer.css";
import { Button, Input, Spinner } from "@fluentui/react-components";
import { ArrowClockwise20Regular, Search20Regular } from "@fluentui/react-icons";
import { ApiError, useApi } from "../../api/client";
import { StorageListing } from "../../api/types";
import { fileName, formatBytes, formatDateTime } from "../../utils/format";

type Phase = "loading" | "ready" | "error";

export function FileBrowser() {
  const api = useApi();
  const [prefix, setPrefix] = useState("");
  const [listing, setListing] = useState<StorageListing | null>(null);
  const [phase, setPhase] = useState<Phase>("loading");
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState("");
  const reqId = useRef(0);

  const load = useCallback(
    async (targetPrefix: string) => {
      const id = ++reqId.current;
      setPhase("loading");
      setError(null);
      try {
        const data = await api.get<StorageListing>(`/api/storage/blobs?prefix=${encodeURIComponent(targetPrefix)}`);
        if (id !== reqId.current) return;
        setListing(data);
        setPhase("ready");
      } catch (err) {
        if (id !== reqId.current) return;
        setError(err instanceof ApiError ? `${err.message}${err.detail ? ` — ${err.detail}` : ""}` : String(err));
        setPhase("error");
      }
    },
    [api],
  );

  useEffect(() => {
    void load(prefix);
  }, [prefix, load]);

  const download = useCallback(
    async (blobName: string) => {
      try {
        const response = await api.getResponse(`/api/storage/download?blob=${encodeURIComponent(blobName)}`);
        const blob = await response.blob();
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName(blobName);
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(url);
      } catch (err) {
        setError(err instanceof ApiError ? `Download failed: ${err.message}` : `Download failed: ${String(err)}`);
      }
    },
    [api],
  );

  const crumbs = prefix.split("/").filter(Boolean);
  const folders = useMemo(
    () => (listing?.folders ?? []).filter((f) => fileName(f).toLowerCase().includes(filter.toLowerCase())),
    [listing, filter],
  );
  const files = useMemo(
    () => (listing?.files ?? []).filter((f) => fileName(f.name).toLowerCase().includes(filter.toLowerCase())),
    [listing, filter],
  );
  const hasItems = folders.length > 0 || files.length > 0;

  const goTo = (p: string) => setPrefix(p);
  const crumbTo = (idx: number) => setPrefix(crumbs.slice(0, idx + 1).join("/") + "/");

  return (
    <div style={{ maxWidth: 1100, margin: "0 auto" }}>
      <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", flexWrap: "wrap", gap: 12 }}>
        <div>
          <h2 style={{ margin: "0 0 2px 0" }}>Cold storage</h2>
          <div style={{ color: "#605e5c", fontSize: 13 }}>
            Browse and download files archived to Azure Blob{listing ? ` — container “${listing.container}”` : ""}.
          </div>
        </div>
        <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <Input
            value={filter}
            onChange={(_, d) => setFilter(d.value)}
            placeholder="Filter this folder…"
            contentBefore={<Search20Regular />}
            aria-label="Filter files in this folder"
          />
          <Button
            icon={<ArrowClockwise20Regular />}
            onClick={() => void load(prefix)}
            appearance="subtle"
            aria-label="Refresh"
          >
            Refresh
          </Button>
        </div>
      </div>

      <div className="file-explorer" style={{ marginTop: 16 }}>
        <div className="breadcrumb-bar">
          <div className="breadcrumb-navigation">
            <button onClick={() => goTo("")} className="breadcrumb-link">
              Root
            </button>
            {crumbs.map((c, idx) => (
              <span key={idx}>
                <span className="breadcrumb-separator-text"> / </span>
                <button onClick={() => crumbTo(idx)} className="breadcrumb-link">
                  {c}
                </button>
              </span>
            ))}
          </div>
        </div>

        <div className="file-list-header">
          <div className="file-list-column file-name-column">Name</div>
          <div className="file-list-column file-modified-column">Date modified</div>
          <div className="file-list-column file-size-column">Size</div>
        </div>

        <div className="file-list-content">
          {phase === "error" && (
            <div className="list-error" role="alert">
              <strong>Could not list files.</strong>
              <p>{error}</p>
              <button type="button" className="error-retry-button" onClick={() => void load(prefix)}>
                Retry
              </button>
            </div>
          )}

          {phase === "loading" && (
            <div style={{ padding: 24, display: "flex", justifyContent: "center" }}>
              <Spinner label="Loading files…" size="small" />
            </div>
          )}

          {phase === "ready" && !hasItems && (
            <div className="empty-folder">
              <p className="empty-folder-text">{filter ? "No files match your filter." : "This folder is empty."}</p>
            </div>
          )}

          {phase === "ready" &&
            folders.map((dir) => (
              <div key={dir} className="file-list-item folder-item" onClick={() => goTo(dir)}>
                <div className="file-list-cell file-name-cell">
                  <span className="file-name">📁 {fileName(dir)}</span>
                </div>
                <div className="file-list-cell file-modified-cell">—</div>
                <div className="file-list-cell file-size-cell">—</div>
              </div>
            ))}

          {phase === "ready" &&
            files.map((file) => (
              <div key={file.name} className="file-list-item file-item">
                <div className="file-list-cell file-name-cell">
                  <button
                    type="button"
                    className="file-name file-link"
                    style={{
                      background: "none",
                      border: "none",
                      padding: 0,
                      font: "inherit",
                      color: "#0f6cbd",
                      cursor: "pointer",
                      textAlign: "left",
                    }}
                    onClick={() => void download(file.name)}
                    title="Download"
                  >
                    📄 {fileName(file.name)}
                  </button>
                </div>
                <div className="file-list-cell file-modified-cell">{formatDateTime(file.lastModified)}</div>
                <div className="file-list-cell file-size-cell">{formatBytes(file.size || 0)}</div>
              </div>
            ))}
        </div>
      </div>
    </div>
  );
}
