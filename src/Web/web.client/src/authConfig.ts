import { readConfigVal } from "./utils/configReader";

export const msalConfig = {
  auth: {
    clientId: readConfigVal("MSAL_CLIENT_ID"),
    authority: readConfigVal("MSAL_AUTHORITY"),
    // Pin the redirect URI to the SPA root so it doesn't drift with deep links
    // (e.g. /cold-storage/download/:itemId). Without this, MSAL uses the current
    // page URL as the redirect URI, which means EVERY new route needs a separate
    // SPA redirect entry on the AAD app registration. Pinning to '/' keeps the
    // AAD app config minimal.
    redirectUri: window.location.origin,
    // After login MSAL redirects to redirectUri. We want the user to land back
    // on the page they came from (typically the deep download link), so capture
    // it now and use it as postLogoutRedirectUri / state.
    postLogoutRedirectUri: window.location.origin,
    navigateToLoginRequestUrl: true,
  },
  cache: {
    // localStorage persists tokens across tabs and browser restarts, so users
    // aren't forced to re-authenticate every time they reopen the app (sessionStorage
    // is cleared when the tab/browser closes).
    cacheLocation: "localStorage",
    // Also keep auth state in a cookie — the redirect flow uses it to carry state
    // across the round-trip to Entra ID.
    storeAuthStateInCookie: true,
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
