/**
 * /cold-storage/download/:itemId
 *
 * Landing page that placeholders point at. Sequence:
 *   1. If the user isn't signed in, MSAL's outer AuthenticatedTemplate switches
 *      them to the Login view first - they come back here after sign-in via
 *      MSAL's redirect handling.
 *   2. Acquire an access token for our own API.
 *   3. Call GET /api/placeholders/download/{itemId}. The endpoint checks the
 *      container ACL, issues a short-lived (~5 min) user-delegation SAS for the
 *      backing blob, and returns the URL.
 *   4. Navigate the browser to the SAS URL. Because the SAS is on the URL
 *      query string (no headers required), the storage account accepts the
 *      request regardless of public-network-access state for the issuing
 *      principal.
 *
 * Error states are rendered inline rather than being thrown away into the
 * MSAL/console log - the entry point for this page is a user-initiated
 * navigation, so silent failures would leave them staring at a blank tab.
 */
import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useMsal } from '@azure/msal-react';
import { loginRequest } from '../../authConfig';

interface IDownloadResponse {
    url: string;
    expiresAt: string;
    fileName?: string;
    contentLength: number;
}

type Phase = 'preparing' | 'ready' | 'error';

interface IProps { token: string; }

export function ColdStorageDownload({ token }: IProps) {
    const { itemId } = useParams<{ itemId: string }>();
    const { instance, accounts } = useMsal();
    const [phase, setPhase] = useState<Phase>('preparing');
    const [message, setMessage] = useState<string>('Preparing your download…');
    const [errorDetail, setErrorDetail] = useState<string | null>(null);

    useEffect(() => {
        let cancelled = false;

        const run = async (): Promise<void> => {
            if (!itemId) {
                setPhase('error');
                setMessage('Missing item id in download URL.');
                return;
            }

            // Acquire a fresh token for our own API. The outer App.tsx already
            // grabs one and passes it in via the `token` prop, but for an
            // initial cold-load navigation (user opened the link in a new tab)
            // it might be stale. Re-acquire silently here.
            let accessToken = token;
            try {
                const result = await instance.acquireTokenSilent({ ...loginRequest, account: accounts[0] });
                accessToken = result.accessToken;
            } catch {
                // Silent failed (e.g. token expired). Fall back to popup so the
                // user has a chance to re-consent without losing their place.
                try {
                    const result = await instance.acquireTokenPopup({ ...loginRequest, account: accounts[0] });
                    accessToken = result.accessToken;
                } catch (err) {
                    if (cancelled) return;
                    setPhase('error');
                    setMessage('Sign-in required to download.');
                    setErrorDetail(err instanceof Error ? err.message : String(err));
                    return;
                }
            }

            try {
                const response = await fetch(`/api/placeholders/download/${encodeURIComponent(itemId)}`, {
                    headers: { Authorization: `Bearer ${accessToken}` },
                });
                if (!response.ok) {
                    const body = await response.text();
                    if (cancelled) return;
                    setPhase('error');
                    if (response.status === 401 || response.status === 403) {
                        setMessage('You do not have permission to download this file from cold storage.');
                    } else if (response.status === 404) {
                        setMessage('No cold-storage record found for that item. It may have been removed or never finished migrating.');
                    } else if (response.status === 409) {
                        setMessage('This item is not yet ready for download — its migration may still be in progress.');
                    } else {
                        setMessage(`Could not prepare download (HTTP ${response.status}).`);
                    }
                    setErrorDetail(body);
                    return;
                }
                const payload = await response.json() as IDownloadResponse;
                if (cancelled) return;
                setPhase('ready');
                setMessage(`Starting download${payload.fileName ? `: ${payload.fileName}` : ''}…`);
                // Replace location so the back button takes the user back to
                // wherever they came from (usually SharePoint), not the SAS URL.
                window.location.replace(payload.url);
            } catch (err) {
                if (cancelled) return;
                setPhase('error');
                setMessage('Network error contacting the cold-storage API.');
                setErrorDetail(err instanceof Error ? err.message : String(err));
            }
        };

        void run();
        return () => { cancelled = true; };
    }, [itemId, instance, accounts, token]);

    return (
        <div style={{
            maxWidth: 560,
            margin: '64px auto',
            padding: '24px 28px',
            border: '1px solid #edebe9',
            borderRadius: 4,
            fontFamily: '"Segoe UI", Tahoma, sans-serif',
            background: '#fff',
        }}>
            <h2 style={{ margin: '0 0 12px 0' }}>Cold storage download</h2>
            <p style={{ margin: '0 0 12px 0', color: phase === 'error' ? '#a4262c' : '#323130' }}>{message}</p>
            {phase === 'preparing' && (
                <div style={{
                    width: 24, height: 24, borderRadius: '50%',
                    border: '2px solid #c8c6c4', borderTopColor: '#0078d4',
                    animation: 'cs-dl-spin 0.9s linear infinite',
                }} />
            )}
            {phase === 'error' && errorDetail && (
                <details style={{ marginTop: 12 }}>
                    <summary style={{ cursor: 'pointer', color: '#605e5c', fontSize: 12 }}>Technical details</summary>
                    <pre style={{
                        marginTop: 8, padding: 8, background: '#faf9f8', borderRadius: 2,
                        fontSize: 11, color: '#323130', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
                    }}>{errorDetail}</pre>
                </details>
            )}
            <style>{`@keyframes cs-dl-spin { to { transform: rotate(360deg); } }`}</style>
        </div>
    );
}
