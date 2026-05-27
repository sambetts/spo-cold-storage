
export const getStorageConfigFromAPI = async (token: string): Promise<ServiceConfiguration> => {
  let response: Response;
  try {
    response = await fetch('AppConfiguration/GetServiceConfiguration', {
      method: 'GET',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': 'Bearer ' + token,
      }
    });
  } catch (networkError: any) {
    throw new Error(`Network error contacting AppConfiguration/GetServiceConfiguration: ${networkError?.message ?? networkError}`);
  }

  if (!response.ok) {
    let errorText = '';
    try { errorText = await response.text(); } catch { /* ignore */ }
    throw new Error(`HTTP ${response.status} ${response.statusText} from AppConfiguration/GetServiceConfiguration${errorText ? `: ${errorText}` : ''}`);
  }

  try {
    const data: ServiceConfiguration = await response.json();
    return data;
  } catch (parseError: any) {
    throw new Error(`Failed to parse storage configuration response as JSON: ${parseError?.message ?? parseError}`);
  }
};


export interface StorageInfo {
  sharedAccessToken: string,
  accountURI: string,
  containerName: string
}

export interface SearchConfiguration {
  indexName: string,
  serviceName: string,
  queryKey: string
}

export interface ServiceConfiguration {
  storageInfo: StorageInfo,
  searchConfiguration: SearchConfiguration
}
