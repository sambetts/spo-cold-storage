import { CSSProperties, useCallback, useEffect, useState } from "react";
import { Badge, Button, Input, Select, Spinner } from "@fluentui/react-components";
import { Delete20Regular, LockClosed16Regular } from "@fluentui/react-icons";
import { ApiError, useApi } from "../../api/client";
import { ExclusionScope, ExtensionRule, ExtensionRuleMode, RuntimeSetting } from "../../api/types";
import { formatDateTime } from "../../utils/format";

/**
 * /admin/rules — runtime-editable archive rules (admin only). Three sections:
 *   1. Preservation settings (GET/PUT/DELETE /api/admin/settings)
 *   2. File-type rules  (GET/POST/DELETE /api/exclusions/extensions)
 *   3. Site & folder scopes (GET/POST/DELETE /api/exclusions)
 * `.url` is shown as a permanent, non-removable exclusion — cold-storage
 * placeholders are hardcoded to never be archived (ArchiveEligibilityEvaluator).
 */

const page: CSSProperties = { maxWidth: 960, margin: "0 auto", padding: "8px 4px" };
const card: CSSProperties = {
  border: "1px solid #edebe9",
  borderRadius: 8,
  padding: "16px 18px",
  marginBottom: 20,
  background: "#fff",
};
const th: CSSProperties = { textAlign: "left", padding: "6px 10px", borderBottom: "1px solid #edebe9", color: "#605e5c", fontSize: 12, fontWeight: 600 };
const td: CSSProperties = { padding: "6px 10px", borderBottom: "1px solid #f3f2f1", fontSize: 13, verticalAlign: "middle" };
const formRow: CSSProperties = { display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginTop: 12 };

function ErrorText({ children }: { children: React.ReactNode }) {
  return <div style={{ color: "#a4262c", fontSize: 13, marginTop: 8 }}>{children}</div>;
}

function apiErrText(err: unknown, fallback: string): string {
  if (err instanceof ApiError) {
    if (err.status === 403) return "Administrator access is required.";
    return err.detail || err.message;
  }
  return err instanceof Error ? err.message : String(err) || fallback;
}

export function ArchiveRules() {
  const api = useApi();
  const [rules, setRules] = useState<ExtensionRule[] | null>(null);
  const [scopes, setScopes] = useState<ExclusionScope[] | null>(null);
  const [settings, setSettings] = useState<RuntimeSetting[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [r, s, cfg] = await Promise.all([
        api.get<ExtensionRule[]>("/api/exclusions/extensions"),
        api.get<ExclusionScope[]>("/api/exclusions"),
        api.get<RuntimeSetting[]>("/api/admin/settings"),
      ]);
      setRules(r);
      setScopes(s);
      setSettings(cfg);
    } catch (err) {
      setError(apiErrText(err, "Could not load archive rules."));
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div style={page}>
      <h1 style={{ fontSize: 22, fontWeight: 600, margin: "4px 0 4px" }}>Archive rules</h1>
      <p style={{ color: "#605e5c", fontSize: 13, marginTop: 0 }}>
        Control which files are eligible for cold storage. Changes take effect within a minute — no redeploy.
      </p>

      {loading && (
        <div style={{ padding: 24 }}>
          <Spinner label="Loading rules…" />
        </div>
      )}
      {error && !loading && <ErrorText>{error}</ErrorText>}

      {!loading && !error && (
        <>
          <SettingsCard settings={settings ?? []} onChanged={load} />
          <ExtensionRulesCard rules={rules ?? []} onChanged={load} />
          <ScopesCard scopes={scopes ?? []} onChanged={load} />
        </>
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Preservation settings (runtime-configurable, no redeploy)
// ---------------------------------------------------------------------------
const SETTING_LABELS: Record<string, string> = {
  CaptureVersionHistory: "Preserve version history",
};

function SettingsCard({ settings, onChanged }: { settings: RuntimeSetting[]; onChanged: () => Promise<void> }) {
  const api = useApi();
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [formErr, setFormErr] = useState<string | null>(null);

  const toggle = async (setting: RuntimeSetting, next: boolean) => {
    setBusyKey(setting.key);
    setFormErr(null);
    try {
      await api.put(`/api/admin/settings/${encodeURIComponent(setting.key)}`, { value: next ? 1 : 0 });
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not save the setting."));
    } finally {
      setBusyKey(null);
    }
  };

  const reset = async (setting: RuntimeSetting) => {
    setBusyKey(setting.key);
    setFormErr(null);
    try {
      await api.del(`/api/admin/settings/${encodeURIComponent(setting.key)}`);
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not reset the setting."));
    } finally {
      setBusyKey(null);
    }
  };

  return (
    <div style={card}>
      <h2 style={{ fontSize: 16, fontWeight: 600, margin: "0 0 4px" }}>Preservation settings</h2>
      <p style={{ color: "#605e5c", fontSize: 13, margin: "0 0 12px" }}>
        Apply to both the web app and the background worker. Changes take effect within a minute.
      </p>

      {settings.length === 0 && <div style={{ color: "#605e5c", fontSize: 13 }}>No configurable settings.</div>}

      {settings.map(s => (
        <div key={s.key} style={{ borderTop: "1px solid #f3f2f1", paddingTop: 12, marginTop: 12 }}>
          <div style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
            <strong style={{ fontSize: 14 }}>{SETTING_LABELS[s.key] ?? s.key}</strong>
            <Badge appearance="filled" color={s.value > 0 ? "success" : "informative"}>
              {s.value > 0 ? "On" : "Off"}
            </Badge>
            {s.isOverridden && (
              <Badge appearance="outline" color="warning">
                Overridden here (deployed default: {s.deployedValue > 0 ? "On" : "Off"})
              </Badge>
            )}
            <div style={{ marginLeft: "auto", display: "flex", gap: 8 }}>
              <Button
                size="small"
                appearance={s.value > 0 ? "secondary" : "primary"}
                disabled={busyKey === s.key}
                onClick={() => void toggle(s, s.value <= 0)}
              >
                {s.value > 0 ? "Turn off" : "Turn on"}
              </Button>
              {s.isOverridden && (
                <Button size="small" appearance="subtle" disabled={busyKey === s.key} onClick={() => void reset(s)}>
                  Reset to deployed
                </Button>
              )}
            </div>
          </div>
          <p style={{ color: "#605e5c", fontSize: 12, margin: "6px 0 0" }}>{s.description}</p>
          {s.updatedBy && (
            <p style={{ color: "#8a8886", fontSize: 12, margin: "4px 0 0" }}>
              Last changed by {s.updatedBy}
              {s.updatedAt ? ` on ${formatDateTime(s.updatedAt)}` : ""}.
            </p>
          )}
        </div>
      ))}

      {formErr && <ErrorText>{formErr}</ErrorText>}
    </div>
  );
}

// ---------------------------------------------------------------------------
// File-type rules
// ---------------------------------------------------------------------------
function ExtensionRulesCard({ rules, onChanged }: { rules: ExtensionRule[]; onChanged: () => Promise<void> }) {
  const api = useApi();
  const [ext, setExt] = useState("");
  const [mode, setMode] = useState<ExtensionRuleMode>("Exclude");
  const [desc, setDesc] = useState("");
  const [busy, setBusy] = useState(false);
  const [formErr, setFormErr] = useState<string | null>(null);

  const add = async () => {
    if (!ext.trim()) {
      setFormErr('Enter a file extension, e.g. ".tmp".');
      return;
    }
    setBusy(true);
    setFormErr(null);
    try {
      await api.post("/api/exclusions/extensions", { extension: ext.trim(), mode, description: desc.trim() || undefined });
      setExt("");
      setDesc("");
      setMode("Exclude");
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not add the rule."));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: number) => {
    setBusy(true);
    setFormErr(null);
    try {
      await api.del(`/api/exclusions/extensions/${id}`);
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not remove the rule."));
    } finally {
      setBusy(false);
    }
  };

  const hasInclude = rules.some((r) => r.mode === "Include" && r.enabled);

  return (
    <section style={card}>
      <h2 style={{ fontSize: 16, fontWeight: 600, margin: "0 0 4px" }}>File-type rules</h2>
      <p style={{ color: "#605e5c", fontSize: 13, marginTop: 0 }}>
        <strong>Exclude</strong> keeps a file type out of cold storage. <strong>Include</strong> turns on an allow-list:
        once any Include rule exists, <em>only</em> those types are archived.
        {hasInclude && (
          <Badge appearance="tint" color="warning" style={{ marginLeft: 8 }}>
            Allow-list active
          </Badge>
        )}
      </p>

      <table style={{ width: "100%", borderCollapse: "collapse", marginTop: 8 }}>
        <thead>
          <tr>
            <th style={th}>Extension</th>
            <th style={th}>Rule</th>
            <th style={th}>Note</th>
            <th style={th}>Added</th>
            <th style={{ ...th, width: 44 }} />
          </tr>
        </thead>
        <tbody>
          <tr>
            <td style={{ ...td, fontWeight: 600 }}>.url</td>
            <td style={td}>
              <Badge appearance="tint" color="danger">
                Always excluded
              </Badge>
            </td>
            <td style={{ ...td, color: "#605e5c" }}>Cold-storage placeholders can never be archived.</td>
            <td style={{ ...td, color: "#a19f9d" }}>—</td>
            <td style={td}>
              <LockClosed16Regular aria-label="Permanent — cannot be removed" style={{ color: "#a19f9d" }} />
            </td>
          </tr>
          {rules.map((r) => (
            <tr key={r.id}>
              <td style={{ ...td, fontWeight: 600 }}>{r.extension}</td>
              <td style={td}>
                <Badge appearance="tint" color={r.mode === "Include" ? "success" : "informative"}>
                  {r.mode === "Include" ? "Include (allow-list)" : "Exclude"}
                </Badge>
              </td>
              <td style={{ ...td, color: "#605e5c" }}>{r.description || ""}</td>
              <td style={{ ...td, color: "#605e5c", whiteSpace: "nowrap" }}>{formatDateTime(r.createdAt)}</td>
              <td style={td}>
                <Button
                  appearance="subtle"
                  icon={<Delete20Regular />}
                  aria-label={`Remove rule for ${r.extension}`}
                  title="Remove"
                  disabled={busy}
                  onClick={() => void remove(r.id)}
                />
              </td>
            </tr>
          ))}
          {rules.length === 0 && (
            <tr>
              <td style={{ ...td, color: "#605e5c" }} colSpan={5}>
                No custom file-type rules. Only <code>.url</code> is excluded.
              </td>
            </tr>
          )}
        </tbody>
      </table>

      <div style={formRow}>
        <div>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>Extension</label>
          <Input value={ext} placeholder=".tmp" onChange={(_, d) => setExt(d.value)} style={{ width: 140 }} />
        </div>
        <div>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>Rule</label>
          <Select value={mode} onChange={(_, d) => setMode(d.value as ExtensionRuleMode)}>
            <option value="Exclude">Exclude</option>
            <option value="Include">Include (allow-list)</option>
          </Select>
        </div>
        <div style={{ flex: "1 1 200px" }}>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>Note (optional)</label>
          <Input value={desc} placeholder="Why?" onChange={(_, d) => setDesc(d.value)} style={{ width: "100%" }} />
        </div>
        <Button appearance="primary" disabled={busy} onClick={() => void add()}>
          Add rule
        </Button>
      </div>
      {formErr && <ErrorText>{formErr}</ErrorText>}
    </section>
  );
}

// ---------------------------------------------------------------------------
// Site & folder scopes
// ---------------------------------------------------------------------------
function ScopesCard({ scopes, onChanged }: { scopes: ExclusionScope[]; onChanged: () => Promise<void> }) {
  const api = useApi();
  const [siteUrl, setSiteUrl] = useState("");
  const [prefix, setPrefix] = useState("");
  const [desc, setDesc] = useState("");
  const [busy, setBusy] = useState(false);
  const [formErr, setFormErr] = useState<string | null>(null);

  const add = async () => {
    if (!siteUrl.trim() && !prefix.trim()) {
      setFormErr("Enter a site URL or a folder path to exclude.");
      return;
    }
    setBusy(true);
    setFormErr(null);
    try {
      await api.post("/api/exclusions", {
        siteUrl: siteUrl.trim() || undefined,
        serverRelativePrefix: prefix.trim() || undefined,
        description: desc.trim() || undefined,
      });
      setSiteUrl("");
      setPrefix("");
      setDesc("");
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not add the scope."));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (id: number) => {
    setBusy(true);
    setFormErr(null);
    try {
      await api.del(`/api/exclusions/${id}`);
      await onChanged();
    } catch (err) {
      setFormErr(apiErrText(err, "Could not remove the scope."));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section style={card}>
      <h2 style={{ fontSize: 16, fontWeight: 600, margin: "0 0 4px" }}>Site &amp; folder scopes</h2>
      <p style={{ color: "#605e5c", fontSize: 13, marginTop: 0 }}>
        Protect a whole site collection or a library/folder subtree from archiving. Folder matching is segment-aware.
      </p>

      <table style={{ width: "100%", borderCollapse: "collapse", marginTop: 8 }}>
        <thead>
          <tr>
            <th style={th}>Scope</th>
            <th style={th}>Note</th>
            <th style={th}>Added</th>
            <th style={{ ...th, width: 44 }} />
          </tr>
        </thead>
        <tbody>
          {scopes.map((s) => (
            <tr key={s.id}>
              <td style={{ ...td, wordBreak: "break-all" }}>
                {s.siteUrl && (
                  <div>
                    <Badge appearance="tint" color="informative" style={{ marginRight: 6 }}>
                      Site
                    </Badge>
                    {s.siteUrl}
                  </div>
                )}
                {s.serverRelativePrefix && (
                  <div>
                    <Badge appearance="tint" color="brand" style={{ marginRight: 6 }}>
                      Folder
                    </Badge>
                    {s.serverRelativePrefix}
                  </div>
                )}
              </td>
              <td style={{ ...td, color: "#605e5c" }}>{s.description || ""}</td>
              <td style={{ ...td, color: "#605e5c", whiteSpace: "nowrap" }}>{formatDateTime(s.createdAt)}</td>
              <td style={td}>
                <Button
                  appearance="subtle"
                  icon={<Delete20Regular />}
                  aria-label="Remove scope"
                  title="Remove"
                  disabled={busy}
                  onClick={() => void remove(s.id)}
                />
              </td>
            </tr>
          ))}
          {scopes.length === 0 && (
            <tr>
              <td style={{ ...td, color: "#605e5c" }} colSpan={4}>
                No site or folder scopes are excluded.
              </td>
            </tr>
          )}
        </tbody>
      </table>

      <div style={formRow}>
        <div style={{ flex: "1 1 240px" }}>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>Site URL</label>
          <Input
            value={siteUrl}
            placeholder="https://contoso.sharepoint.com/sites/Legal"
            onChange={(_, d) => setSiteUrl(d.value)}
            style={{ width: "100%" }}
          />
        </div>
        <div style={{ flex: "1 1 240px" }}>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>…or folder path</label>
          <Input
            value={prefix}
            placeholder="/sites/Legal/Shared Documents/Cases"
            onChange={(_, d) => setPrefix(d.value)}
            style={{ width: "100%" }}
          />
        </div>
        <div style={{ flex: "1 1 160px" }}>
          <label style={{ display: "block", fontSize: 12, color: "#605e5c", marginBottom: 2 }}>Note (optional)</label>
          <Input value={desc} placeholder="Why?" onChange={(_, d) => setDesc(d.value)} style={{ width: "100%" }} />
        </div>
        <Button appearance="primary" disabled={busy} onClick={() => void add()}>
          Add scope
        </Button>
      </div>
      {formErr && <ErrorText>{formErr}</ErrorText>}
    </section>
  );
}
