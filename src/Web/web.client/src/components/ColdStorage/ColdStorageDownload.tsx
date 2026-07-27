/**
 * /cold-storage/download/:itemId
 *
 * Landing page that placeholders point at. It acquires a token (via the shared
 * API client), calls GET /api/placeholders/download/{itemId} — which checks the
 * container ACL and returns a short-lived, token-authorised URL to our own
 * content endpoint — then navigates the browser there to stream the file. We
 * stream through the API because the storage account denies public network
 * access, so the browser can't reach the blob directly. Error states are
 * rendered inline so a user who followed a placeholder link never sees a blank tab.
 */
import { useEffect, useState } from "react";
import { useParams } from "react-router-dom";
import { BrowserAuthError } from "@azure/msal-browser";
import { ApiError, useApi } from "../../api/client";
import { DownloadUrl } from "../../api/types";
import { formatBytes } from "../../utils/format";

type Phase = "loading" | "ready" | "error";

interface ErrorInfo {
  message: string;
  detail: string | null;
  canRetry: boolean;
}

// MSAL raises these BrowserAuthError codes when it can't open the interactive
// sign-in pop-up used to (re)confirm the user before minting a download token —
// almost always because the browser's pop-up blocker ate the window. That is NOT
// a network failure, so it gets its own actionable message.
const POPUP_BLOCKED_CODES = new Set(["popup_window_error", "empty_window_error", "popup_window_timeout"]);

function classifyError(err: unknown): ErrorInfo {
  if (err instanceof ApiError) {
    if (err.status === 401 || err.status === 403) {
      return {
        message: "You don't have permission to download this file from cold storage.",
        detail: err.detail ?? err.message,
        canRetry: false,
      };
    }
    if (err.status === 404) {
      return {
        message:
          "We couldn't find a cold-storage record for that item. It may have been removed or never finished migrating.",
        detail: err.detail ?? err.message,
        canRetry: false,
      };
    }
    if (err.status === 409) {
      return {
        message: "This item isn't ready to download yet — its migration may still be in progress. Try again shortly.",
        detail: err.detail ?? err.message,
        canRetry: true,
      };
    }
    return {
      message: `Something went wrong preparing your download (HTTP ${err.status}). Please try again.`,
      detail: err.detail ?? err.message,
      canRetry: true,
    };
  }
  if (err instanceof BrowserAuthError) {
    if (POPUP_BLOCKED_CODES.has(err.errorCode)) {
      return {
        message:
          "We need to confirm your sign-in before preparing this download, but your browser blocked the pop-up window. Please allow pop-ups for this site, then try again.",
        detail: err.message,
        canRetry: true,
      };
    }
    if (err.errorCode === "user_cancelled") {
      return {
        message: "Sign-in was cancelled, so we couldn't prepare your download. Try again when you're ready.",
        detail: err.message,
        canRetry: true,
      };
    }
    return {
      message: "We couldn't confirm your sign-in to prepare this download. Please try again.",
      detail: err.message,
      canRetry: true,
    };
  }
  return {
    message:
      "We couldn't reach the cold-storage service — this is usually a temporary connection hiccup. Check your connection and try again.",
    detail: err instanceof Error ? err.message : String(err),
    canRetry: true,
  };
}

export function ColdStorageDownload() {
  const { itemId } = useParams<{ itemId: string }>();
  const api = useApi();
  const [phase, setPhase] = useState<Phase>("loading");
  const [download, setDownload] = useState<DownloadUrl | null>(null);
  const [error, setError] = useState<ErrorInfo | null>(null);
  const [retryKey, setRetryKey] = useState(0);

  useEffect(() => {
    let cancelled = false;

    const run = async (): Promise<void> => {
      setPhase("loading");
      setError(null);
      setDownload(null);

      if (!itemId) {
        setError({ message: "This download link is incomplete — it's missing an item id.", detail: null, canRetry: false });
        setPhase("error");
        return;
      }

      try {
        // Validate access + fetch the file details, but do NOT auto-download — we present a
        // link the user clicks, alongside a warning that editing won't update cold storage.
        const payload = await api.get<DownloadUrl>(`/api/placeholders/download/${encodeURIComponent(itemId)}`);
        if (cancelled) return;
        setDownload(payload);
        setPhase("ready");
      } catch (err) {
        if (cancelled) return;
        setError(classifyError(err));
        setPhase("error");
      }
    };

    void run();
    return () => {
      cancelled = true;
    };
  }, [itemId, api, retryKey]);

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

      {phase === "loading" && (
        <>
          <p style={{ margin: "0 0 12px 0", color: "#323130" }}>Preparing your download…</p>
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
        </>
      )}

      {phase === "ready" && download && (
        <>
          <p style={{ margin: "0 0 12px 0", color: "#323130" }}>
            <strong>{download.fileName ?? "This file"}</strong>
            {download.contentLength > 0 && (
              <span style={{ color: "#605e5c" }}> · {formatBytes(download.contentLength)}</span>
            )}{" "}
            is archived in cold storage.
          </p>

          <div
            role="note"
            style={{
              margin: "0 0 16px 0",
              padding: "12px 14px",
              background: "#fff8e6",
              border: "1px solid #f2d98c",
              borderRadius: 4,
              color: "#6b5300",
              fontSize: 13,
              lineHeight: 1.5,
            }}
          >
            <strong>Before you download:</strong> this gives you a separate copy. Any edits you make to it{" "}
            <strong>won&apos;t</strong> update the archived version in cold storage. To change this file, restore
            it to SharePoint first, then edit it there.
          </div>

          <a
            href={download.url}
            download={download.fileName ?? undefined}
            style={{
              display: "inline-block",
              padding: "8px 18px",
              background: "#0078d4",
              color: "#fff",
              borderRadius: 4,
              textDecoration: "none",
              fontSize: 14,
              fontWeight: 600,
            }}
          >
            Download {download.fileName ?? "file"}
          </a>
        </>
      )}

      {phase === "error" && (
        <>
          <p style={{ margin: "0 0 12px 0", color: "#a4262c" }}>{error?.message ?? "Something went wrong."}</p>
          {error?.canRetry && (
            <button
              type="button"
              onClick={() => setRetryKey((k) => k + 1)}
              style={{
                marginTop: 4,
                padding: "6px 16px",
                border: "none",
                borderRadius: 4,
                background: "#0078d4",
                color: "#fff",
                fontSize: 14,
                cursor: "pointer",
              }}
            >
              Try again
            </button>
          )}
          {error?.detail && (
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
                {error.detail}
              </pre>
            </details>
          )}
        </>
      )}

      <style>{`@keyframes cs-dl-spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  );
}
