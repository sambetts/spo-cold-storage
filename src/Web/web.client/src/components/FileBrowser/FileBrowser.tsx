import { BlobServiceClient, ContainerClient } from '@azure/storage-blob';
import { BlobFileList } from './BlobFileList';
import '../NavMenu.css';
import './FileExplorer.css';
import React from 'react';
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { getStorageConfigFromAPI, ServiceConfiguration } from '../ConfigReader';
import { storageRequest } from '../../authConfig';

type LoadPhase = 'idle' | 'acquiring-token' | 'loading-config' | 'connecting' | 'ready' | 'error';

const phaseMessages: Record<Exclude<LoadPhase, 'ready' | 'error'>, string> = {
  'idle': 'Preparing...',
  'acquiring-token': 'Acquiring storage access token...',
  'loading-config': 'Loading storage configuration...',
  'connecting': 'Connecting to Azure Blob storage...'
};

const describeError = (e: unknown): string => {
  if (!e) return 'Unknown error.';
  if (e instanceof Error) return e.message;
  if (typeof e === 'string') return e;
  try { return JSON.stringify(e); } catch { return String(e); }
};

const getStorageAccountName = (accountUri: string | undefined | null): string | null => {
  if (!accountUri) return null;
  try {
    const host = new URL(accountUri).hostname;
    // e.g. myaccount.blob.core.windows.net  -> "myaccount"
    //      myaccount.dfs.core.windows.net   -> "myaccount"
    const first = host.split('.')[0];
    return first || null;
  } catch {
    return null;
  }
};

// Decode a JWT (no signature verification — for diagnostic display only).
const decodeJwtPayload = (jwt: string): Record<string, any> | null => {
  try {
    const parts = jwt.split('.');
    if (parts.length < 2) return null;
    const b64 = parts[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = b64 + '='.repeat((4 - (b64.length % 4)) % 4);
    const json = decodeURIComponent(
      atob(padded)
        .split('')
        .map(c => '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2))
        .join('')
    );
    return JSON.parse(json);
  } catch {
    return null;
  }
};

const summarizeStorageToken = (jwt: string): string => {
  const payload = decodeJwtPayload(jwt);
  if (!payload) return 'Token: <could not decode>';
  const expIso = payload.exp ? new Date(payload.exp * 1000).toISOString() : 'n/a';
  return [
    `aud:    ${payload.aud ?? '<missing>'}`,
    `iss:    ${payload.iss ?? '<missing>'}`,
    `scp:    ${payload.scp ?? '<missing>'}`,
    `roles:  ${Array.isArray(payload.roles) ? payload.roles.join(', ') : '<none>'}`,
    `appid:  ${payload.appid ?? payload.azp ?? '<missing>'}`,
    `tid:    ${payload.tid ?? '<missing>'}`,
    `oid:    ${payload.oid ?? '<missing>'}`,
    `upn:    ${payload.upn ?? payload.unique_name ?? payload.preferred_username ?? '<missing>'}`,
    `exp:    ${expIso}`
  ].join('\n');
};

export const FileBrowser: React.FC<{ token: string }> = (props) => {

  const [client, setClient] = React.useState<ContainerClient | null>(null);
  const [serviceConfiguration, setServiceConfiguration] = React.useState<ServiceConfiguration | null>(null);
  const [storageToken, setStorageToken] = React.useState<string | null>(null);

  const [phase, setPhase] = React.useState<LoadPhase>('idle');
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);
  const [errorDetail, setErrorDetail] = React.useState<string | null>(null);
  const [retryCount, setRetryCount] = React.useState<number>(0);

  const isAuthenticated = useIsAuthenticated();
  const { accounts, instance } = useMsal();

  const retry = React.useCallback(() => {
    setErrorMessage(null);
    setErrorDetail(null);
    setStorageToken(null);
    setServiceConfiguration(null);
    setClient(null);
    setPhase('idle');
    setRetryCount(c => c + 1);
  }, []);

  // Acquire storage token separately
  React.useEffect(() => {
    if (!isAuthenticated || storageToken || phase === 'error') return;

    const request = {
      ...storageRequest,
      account: accounts[0],
      // Always pull a fresh token from AAD so that any newly-granted RBAC roles
      // or consented scopes are reflected in the claims (cached tokens do NOT
      // get retroactively updated when roles change).
      forceRefresh: true
    };

    setPhase('acquiring-token');
    setErrorMessage(null);

    instance.acquireTokenSilent(request)
      .then((response) => {
        console.log('Storage token acquired. Decoded claims:\n' + summarizeStorageToken(response.accessToken));
        setStorageToken(response.accessToken);
      })
      .catch((silentError) => {
        console.warn('Silent storage token acquisition failed, falling back to popup.', silentError);
        instance.acquireTokenPopup(request)
          .then((response) => {
            console.log('Storage token acquired via popup. Decoded claims:\n' + summarizeStorageToken(response.accessToken));
            setStorageToken(response.accessToken);
          })
          .catch((popupError) => {
            console.error('Storage token acquisition failed.', popupError);
            setErrorMessage('Could not acquire an access token for Azure Storage.');
            setErrorDetail(describeError(popupError));
            setPhase('error');
          });
      });
  }, [isAuthenticated, storageToken, accounts, instance, phase, retryCount]);

  React.useEffect(() => {
    if (!storageToken) return;
    if (phase === 'error') return;

    let cancelled = false;

    setPhase('loading-config');

    getStorageConfigFromAPI(props.token)
      .then((storageConfigInfo: ServiceConfiguration) => {
        if (cancelled) return;
        console.log('Got service config from site API');

        if (!storageConfigInfo?.storageInfo?.accountURI || !storageConfigInfo?.storageInfo?.containerName) {
          throw new Error('Service configuration is missing storage account URI or container name.');
        }

        setServiceConfiguration(storageConfigInfo);
        setPhase('connecting');

        // Create a custom credential that uses the storage access token
        const tokenCredential = {
          getToken: async () => {
            return {
              token: storageToken,
              expiresOnTimestamp: Date.now() + 3600000 // 1 hour from now
            };
          }
        };

        // Create a new BlobServiceClient using Azure AD authentication
        const blobServiceClient = new BlobServiceClient(
          storageConfigInfo.storageInfo.accountURI,
          tokenCredential
        );

        const containerName = storageConfigInfo.storageInfo.containerName;
        const blobStorageClient = blobServiceClient.getContainerClient(containerName);

        setClient(blobStorageClient);
        setPhase('ready');
      })
      .catch((err) => {
        if (cancelled) return;
        console.error('Failed to load storage configuration / connect to storage.', err);
        setErrorMessage('Could not load storage configuration from the server.');
        setErrorDetail(describeError(err));
        setPhase('error');
      });

    return () => { cancelled = true; };
  }, [props.token, storageToken, retryCount]);

  return (
    <div className="file-browser-container">
      <div className="spo-content-card">
        <div className="file-browser-header">
          <h1 className="spo-section-header">Cold Storage Browser</h1>
          <p className="file-browser-description">
            Browse and access files that have been moved into Azure Blob cold storage.
          </p>
          {serviceConfiguration?.storageInfo?.accountURI && (
            <div className="storage-connection-info" aria-label="Storage connection">
              <svg className="storage-connection-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
                <ellipse cx="12" cy="5" rx="9" ry="3" />
                <path d="M3 5v6c0 1.66 4 3 9 3s9-1.34 9-3V5" />
                <path d="M3 11v6c0 1.66 4 3 9 3s9-1.34 9-3v-6" />
              </svg>
              <span className="storage-connection-label">Connected to</span>
              <span className="storage-connection-account">
                {getStorageAccountName(serviceConfiguration.storageInfo.accountURI) ?? serviceConfiguration.storageInfo.accountURI}
              </span>
              <span className="storage-connection-separator">/</span>
              <span className="storage-connection-container">
                {serviceConfiguration.storageInfo.containerName}
              </span>
            </div>
          )}
        </div>

        {phase === 'ready' && client && serviceConfiguration ? (
          <div className="file-browser-content">
            <BlobFileList
              client={client}
              accessToken={props.token}
              storageInfo={serviceConfiguration.storageInfo}
              storageTokenSummary={storageToken ? summarizeStorageToken(storageToken) : null}
            />
          </div>
        ) : phase === 'error' ? (
          <div className="error-container" role="alert">
            <svg className="error-icon" width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
              <circle cx="12" cy="12" r="10" />
              <line x1="12" y1="8" x2="12" y2="12" />
              <line x1="12" y1="16" x2="12.01" y2="16" />
            </svg>
            <h2 className="error-title">Unable to load storage</h2>
            <p className="error-message">{errorMessage ?? 'An unexpected error occurred.'}</p>
            {errorDetail && (
              <details className="error-details">
                <summary>Technical details</summary>
                <pre>{errorDetail}</pre>
              </details>
            )}
            <button type="button" className="error-retry-button" onClick={retry}>
              Try again
            </button>
          </div>
        ) : (
          <div className="loading-container">
            <div className="loading-spinner"></div>
            <p>{phaseMessages[phase as Exclude<LoadPhase, 'ready' | 'error'>] ?? 'Loading storage...'}</p>
          </div>
        )}
      </div>
    </div>
  );
};
