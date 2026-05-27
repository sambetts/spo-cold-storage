import { BlobServiceClient, ContainerClient } from '@azure/storage-blob';
import { BlobFileList } from './BlobFileList';
import '../NavMenu.css';
import './FileExplorer.css';
import React from 'react';
import { useIsAuthenticated, useMsal } from "@azure/msal-react";
import { getStorageConfigFromAPI, ServiceConfiguration } from '../ConfigReader';
import { storageRequest } from '../../authConfig';

export const FileBrowser: React.FC<{ token: string }> = (props) => {

  const [client, setClient] = React.useState<ContainerClient | null>(null);
  const [serviceConfiguration, setServiceConfiguration] = React.useState<ServiceConfiguration | null>(null);
  const [storageToken, setStorageToken] = React.useState<string | null>(null);

  const [loading, setLoading] = React.useState<boolean>(false);

  const isAuthenticated = useIsAuthenticated();
  const { accounts, instance } = useMsal();
  const getStorageConfig = React.useCallback(async (token : string) => 
  {
    return await getStorageConfigFromAPI(token).then((response : ServiceConfiguration)  => {
      setLoading(false);
      return Promise.resolve(response);
    });
  }, []);

  // Acquire storage token separately
  React.useEffect(() => {
    if (isAuthenticated && !storageToken) {
      const request = {
        ...storageRequest,
        account: accounts[0]
      };

      instance.acquireTokenSilent(request)
        .then((response) => {
          setStorageToken(response.accessToken);
        })
        .catch((e) => {
          instance.acquireTokenPopup(request)
            .then((response) => {
              setStorageToken(response.accessToken);
            });
        });
    }
  }, [isAuthenticated, storageToken, accounts, instance]);

  React.useEffect(() => {
    if (!storageToken) return;

    // Load storage config first
    getStorageConfig(props.token)
      .then((storageConfigInfo: ServiceConfiguration) => {
        console.log('Got service config from site API');
        setServiceConfiguration(storageConfigInfo);

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
      });

  }, [getStorageConfig, props.token, storageToken]);

  const name = accounts[0] && accounts[0].name;
  return (
    <div className="file-browser-container">
      <div className="spo-content-card">
        <div className="file-browser-header">
          <h1 className="spo-section-header">Cold Storage Browser</h1>
          <p className="file-browser-description">
            Browse and access files that have been moved into Azure Blob cold storage.
          </p>
        </div>

        {!loading && client ? (
          <div className="file-browser-content">
            <BlobFileList 
              client={client!} 
              accessToken={props.token} 
              storageInfo={serviceConfiguration!.storageInfo} 
            />
          </div>
        ) : (
          <div className="loading-container">
            <div className="loading-spinner"></div>
            <p>Loading storage...</p>
          </div>
        )}
      </div>
    </div>
  );
};
