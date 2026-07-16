/**
 * /cold-storage/download/:itemId
 *
 * Landing page that placeholders point at. It acquires a token (via the shared
 * API client), calls GET /api/placeholders/download/{itemId} — which checks the
 * container ACL and issues a short-lived user-delegation SAS for the backing
 * blob — then navigates the browser to that SAS URL. Error states are rendered
 * inline so a user who followed a placeholder link never sees a blank tab.
 */
import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { ApiError, useApi } from "../../api/client";
import { DownloadUrl } from "../../api/types";

type Phase = "preparing" | "ready" | "error";

export function ColdStorageDownload() {
  const { itemId } = useParams<{ itemId: string }>();
  const api = useApi();
  const [phase, setPhase] = useState<Phase>("preparing");
  const [message, setMessage] = useState<string>("Preparing your download…");
  const [errorDetail, setErrorDetail] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    const run = async (): Promise<void> => {
      if (!itemId) {
        setPhase("error");
        setMessage("Missing item id in download URL.");
        return;
      }

      try {
        const payload = await api.get<DownloadUrl>(`/api/placeholders/download/${encodeURIComponent(itemId)}`);
        if (cancelled) return;
        setPhase("ready");
        setMessage(`Starting download${payload.fileName ? `: ${payload.fileName}` : ""}…`);
        window.location.replace(payload.url);
      } catch (err) {
        if (cancelled) return;
        setPhase("error");
        if (err instanceof ApiError) {
          if (err.status === 401 || err.status === 403) {
            setMessage("You do not have permission to download this file from cold storage.");
          } else if (err.status === 404) {
            setMessage("No cold-storage record found for that item. It may have been removed or never finished migrating.");
          } else if (err.status === 409) {
            setMessage("This item is not yet ready for download — its migration may still be in progress.");
          } else {
            setMessage(`Could not prepare download (HTTP ${err.status}).`);
          }
          setErrorDetail(err.detail ?? err.message);
        } else {
          setMessage("Network error contacting the cold-storage API.");
          setErrorDetail(err instanceof Error ? err.message : String(err));
        }
      }
    };

    void run();
    return () => {
      cancelled = true;
    };
  }, [itemId, api]);

  return (
    <div
      style={{
        maxWidth: 560,
        margin: "64px auto",
        padding: "24px 28px",
        border: "1px solid #edebe9",
        borderRadius: 4,
        fontFamily: '"Segoe UI", Tahoma, sans-serif',
        background: "#fff",
      }}
    >
      <h2 style={{ margin: "0 0 12px 0" }}>Cold storage download</h2>
      <p style={{ margin: "0 0 12px 0", color: phase === "error" ? "#a4262c" : "#323130" }}>{message}</p>
      {phase === "preparing" && (
        <div
          style={{
            width: 24,
            height: 24,
            borderRadius: "50%",
            border: "2px solid #c8c6c4",
            borderTopColor: "#0078d4",
            animation: "cs-dl-spin 0.9s linear infinite",
          }}
        />
      )}
      {phase === "error" && errorDetail && (
        <details style={{ marginTop: 12 }}>
          <summary style={{ cursor: "pointer", color: "#605e5c", fontSize: 12 }}>Technical details</summary>
          <pre
            style={{
              marginTop: 8,
              padding: 8,
              background: "#faf9f8",
              borderRadius: 2,
              fontSize: 11,
              color: "#323130",
              whiteSpace: "pre-wrap",
              wordBreak: "break-all",
            }}
          >
            {errorDetail}
          </pre>
        </details>
      )}
      <style>{`@keyframes cs-dl-spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
