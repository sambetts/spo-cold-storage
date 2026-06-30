import { useEffect, useState } from 'react';

/**
 * /Savings — cost & savings KPI dashboard (issue #8).
 * Fetches GET /api/reports/savings (admin-only) and shows reclaimed GB,
 * estimated Azure cost and net monthly savings.
 */
interface ISavingsReport {
    archivedItemCount: number;
    reclaimedBytes: number;
    reclaimedGb: number;
    azurePricePerGbMonth: number;
    spoPricePerGbMonth: number;
    estimatedAzureCostPerMonth: number;
    estimatedSpoValuePerMonth: number;
    estimatedNetSavingsPerMonth: number;
    currency: string;
}

interface IProps { token: string; }

function money(value: number, currency: string): string {
    const fixed = value.toFixed(2);
    return currency === 'USD' ? `$${fixed}` : `${fixed} ${currency}`;
}

function Kpi({ label, value, accent }: { label: string; value: string; accent?: string }) {
    return (
        <div style={{ border: '1px solid #edebe9', borderRadius: 8, padding: '16px 20px', minWidth: 200 }}>
            <div style={{ fontSize: 13, color: '#605e5c' }}>{label}</div>
            <div style={{ fontSize: 28, fontWeight: 600, color: accent ?? '#201f1e', marginTop: 4 }}>{value}</div>
        </div>
    );
}

export function SavingsDashboard({ token }: IProps) {
    const [report, setReport] = useState<ISavingsReport | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [loading, setLoading] = useState<boolean>(true);

    useEffect(() => {
        let cancelled = false;
        setLoading(true);
        setError(null);
        fetch('/api/reports/savings', { headers: { Authorization: `Bearer ${token}` } })
            .then(async (r) => {
                if (!r.ok) {
                    throw new Error(r.status === 403 ? 'You need cold-storage admin rights to view this.' : `Server returned ${r.status}`);
                }
                return (await r.json()) as ISavingsReport;
            })
            .then((data) => {
                if (!cancelled) {
                    setReport(data);
                    setLoading(false);
                }
            })
            .catch((e: unknown) => {
                if (!cancelled) {
                    setError(e instanceof Error ? e.message : 'Failed to load savings.');
                    setLoading(false);
                }
            });
        return () => { cancelled = true; };
    }, [token]);

    return (
        <div style={{ padding: 24 }}>
            <h2>Cold storage — cost &amp; savings</h2>
            {loading && <p>Loading savings…</p>}
            {error && <p style={{ color: '#a4262c' }}>Could not load savings: {error}</p>}
            {report && (
                <>
                    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 16, marginTop: 16 }}>
                        <Kpi label="Files archived" value={report.archivedItemCount.toLocaleString()} />
                        <Kpi label="Reclaimed in SharePoint" value={`${report.reclaimedGb.toLocaleString(undefined, { maximumFractionDigits: 2 })} GB`} />
                        <Kpi label="Est. Azure cost / month" value={money(report.estimatedAzureCostPerMonth, report.currency)} />
                        <Kpi
                            label="Est. net saving / month"
                            value={money(report.estimatedNetSavingsPerMonth, report.currency)}
                            accent={report.estimatedNetSavingsPerMonth >= 0 ? '#107c10' : '#a4262c'}
                        />
                    </div>
                    <p style={{ marginTop: 16, color: '#605e5c', fontSize: 13 }}>
                        Based on {report.reclaimedGb.toFixed(2)} GB reclaimed, Azure @ {money(report.azurePricePerGbMonth, report.currency)}/GB/mo
                        vs SharePoint @ {money(report.spoPricePerGbMonth, report.currency)}/GB/mo. Figures are estimates.
                    </p>
                </>
            )}
        </div>
    );
}
