import { BlobFileList } from './BlobFileList';
import '../NavMenu.css';
import './FileExplorer.css';
import React from 'react';
import { useIsAuthenticated } from "@azure/msal-react";
import { getStorageConfigFromAPI, ServiceConfiguration } from '../ConfigReader';

type LoadPhase = 'idle' | 'loading-config' | 'ready' | 'error';

const phaseMessages: Record<Exclude<LoadPhase, 'ready' | 'error'>, string> = {
  'idle': 'Preparing...',
  'loading-config': 'Loading storage configuration...'
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
    // e.g. myaccount.blob.core.windows.net -> "myaccount"
    const first = host.split('.')[0];
    return first || null;
  } catch {
    return null;
  }
};

// The file browser no longer talks to Azure Storage from the browser. The storage
// account's public network access is disabled by policy, so we load display
// config from our API and let <BlobFileList/> list/download via the server proxy
// (which reaches storage over the Web App's private endpoint) using the same API
// access token.
export const FileBrowser: React.FC<{ token: string }> = (props) => {

  const [serviceConfiguration, setServiceConfiguration] = React.useState<ServiceConfiguration | null>(null);
  const [phase, setPhase] = React.useState<LoadPhase>('idle');
  const [errorMessage, setErrorMessage] = React.useState<string | null>(null);
  const [errorDetail, setErrorDetail] = React.useState<string | null>(null);
  const [retryCount, setRetryCount] = React.useState<number>(0);

  const isAuthenticated = useIsAuthenticated();

  const retry = React.useCallback(() => {
    setErrorMessage(null);
    setErrorDetail(null);
    setServiceConfiguration(null);
    setPhase('idle');
    setRetryCount(c => c + 1);
  }, []);

  React.useEffect(() => {
    if (!isAuthenticated) return;

    let cancelled = false;
    setPhase('loading-config');

    getStorageConfigFromAPI(props.token)
      .then((storageConfigInfo: ServiceConfiguration) => {
        if (cancelled) return;

        if (!storageConfigInfo?.storageInfo?.accountURI || !storageConfigInfo?.storageInfo?.containerName) {
          throw new Error('Service configuration is missing storage account URI or container name.');
        }

        setServiceConfiguration(storageConfigInfo);
        setPhase('ready');
      })
      .catch((err) => {
        if (cancelled) return;
        console.error('Failed to load storage configuration.', err);
        setErrorMessage('Could not load storage configuration from the server.');
        setErrorDetail(describeError(err));
        setPhase('error');
      });

    return () => { cancelled = true; };
  }, [props.token, isAuthenticated, retryCount]);

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

        {phase === 'ready' && serviceConfiguration ? (
          <div className="file-browser-content">
            <BlobFileList accessToken={props.token} />
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
