import { readConfigVal } from "./utils/configReader";

export const msalConfig = {
  auth: {
    clientId: readConfigVal("MSAL_CLIENT_ID"),
    authority: readConfigVal("MSAL_AUTHORITY"),
  },
  cache: {
    cacheLocation: "sessionStorage", // This configures where your cache will be stored
    storeAuthStateInCookie: false, // Set this to "true" if you are having issues on IE11 or Edge
  }
};

// Scope of THIS app's web API (e.g. api://<client-id>/access_as_user).
// Used to call our own server endpoints like AppConfiguration/GetServiceConfiguration.
export const loginRequest = {
  scopes: [readConfigVal("MSAL_SCOPES")]
};

// Scope used when talking directly to Azure Blob Storage via RBAC (Storage Blob Data Reader, etc.).
// Configurable so different clouds / sovereign endpoints can be supported; defaults to public cloud.
const storageScope = readConfigVal("MSAL_STORAGE_SCOPES");
export const storageRequest = {
  scopes: [
    storageScope && storageScope !== "undefined"
      ? storageScope
      : "https://storage.azure.com/user_impersonation"
  ]
};

export const teamsAppConfig = {
  startLoginPageUrl: readConfigVal("TEAMSFX_START_LOGIN_PAGE_URL"),
}
