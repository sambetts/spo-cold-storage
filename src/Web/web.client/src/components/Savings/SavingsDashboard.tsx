import { useEffect, useState } from "react";
import { Spinner } from "@fluentui/react-components";
import { ApiError, useApi } from "../../api/client";
import { SavingsReport } from "../../api/types";
import { formatNumber } from "../../utils/format";

function money(value: number, currency: string): string {
  // Culture-aware currency: "$1,234.56" (en-US), "1.234,56 €" (es-ES), etc.
  try {
    return new Intl.NumberFormat(undefined, { style: "currency", currency }).format(value);
  } catch {
    // Unknown/invalid currency code — fall back to a localised number + the code.
    return `${formatNumber(value, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} ${currency}`;
  }
}

function Kpi({ label, value, accent }: { label: string; value: string; accent?: string }) {
  return (
    <div style={{ border: "1px solid #edebe9", borderRadius: 8, padding: "16px 20px", minWidth: 200 }}>
      <div style={{ fontSize: 13, color: "#605e5c" }}>{label}</div>
      <div style={{ fontSize: 28, fontWeight: 600, color: accent ?? "#201f1e", marginTop: 4 }}>{value}</div>
    </div>
  );
}

/**
 * /savings — cost & savings KPIs. Fetches GET /api/reports/savings (admin-only)
 * and shows reclaimed GB, estimated Azure cost and net monthly savings.
 */
export function SavingsDashboard() {
  const api = useApi();
  const [report, setReport] = useState<SavingsReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    api
      .get<SavingsReport>("/api/reports/savings")
      .then((data) => {
        if (cancelled) return;
        setReport(data);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        if (err instanceof ApiError && err.status === 403) {
          setError("Administrator access is required to view savings.");
        } else {
          setError(err instanceof ApiError ? err.message : String(err));
        }
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [api]);

  return (
    <div style={{ maxWidth: 1000, margin: "0 auto" }}>
      <h2 style={{ margin: "0 0 2px 0" }}>Savings</h2>
      <div style={{ color: "#605e5c", fontSize: 13, marginBottom: 16 }}>
        Storage reclaimed in SharePoint by archiving to Azure cold storage, and the estimated net monthly saving.
      </div>

      {loading && <Spinner label="Calculating savings…" size="small" />}
      {error && (
        <div style={{ color: "#a4262c", border: "1px solid #f3d6d8", background: "#fdf3f4", padding: 12, borderRadius: 6 }}>
          {error}
        </div>
      )}

      {report && !loading && !error && (
        <>
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
            <Kpi label="Files archived" value={formatNumber(report.archivedItemCount)} />
            <Kpi label="Storage reclaimed" value={`${formatNumber(report.reclaimedGb, { maximumFractionDigits: 2 })} GB`} />
            <Kpi
              label="Net saving / month"
              value={money(report.estimatedNetSavingsPerMonth, report.currency)}
              accent="#107c10"
            />
          </div>
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap", marginTop: 16 }}>
            <Kpi label="Azure cost / month" value={money(report.estimatedAzureCostPerMonth, report.currency)} />
            <Kpi label="Reclaimed SPO value / month" value={money(report.estimatedSpoValuePerMonth, report.currency)} />
          </div>
          <div style={{ marginTop: 16, fontSize: 12, color: "#605e5c" }}>
            Based on {money(report.azurePricePerGbMonth, report.currency)}/GB Azure vs{" "}
            {money(report.spoPricePerGbMonth, report.currency)}/GB SharePoint.
          </div>
        </>
      )}
    </div>
  );
}
