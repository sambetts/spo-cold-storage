import React from "react";
import { BrowserRouter } from "react-router-dom";
import { createRoot } from "react-dom/client";
import { MsalProvider } from "@azure/msal-react";
import { AccountInfo, EventType, PublicClientApplication } from "@azure/msal-browser";
import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import App from "./App";
import { msalConfig } from "./authConfig";

const msalInstance = new PublicClientApplication(msalConfig);

const rootElement = document.getElementById("root");
const root = createRoot(rootElement!);

// MSAL v3 must be initialised before any other API call. Set an active account
// so the API client's acquireTokenSilent has a default even before a component
// passes one, and keep it in sync on subsequent logins.
msalInstance
  .initialize()
  .then(() => {
    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }

    msalInstance.addEventCallback((event) => {
      if (event.eventType === EventType.LOGIN_SUCCESS && event.payload && "account" in event.payload) {
        msalInstance.setActiveAccount((event.payload as { account: AccountInfo }).account);
      }
    });

    root.render(
      <React.StrictMode>
        <FluentProvider theme={webLightTheme}>
          <MsalProvider instance={msalInstance}>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </MsalProvider>
        </FluentProvider>
      </React.StrictMode>,
    );
  })
  .catch((err) => {
    // Rendering the raw error is better than a blank page if MSAL fails to init.
    root.render(
      <div style={{ maxWidth: 560, margin: "64px auto", fontFamily: '"Segoe UI", sans-serif' }}>
        <h2>Could not start the app</h2>
        <p>Sign-in could not be initialised. Please reload; if this persists, contact your administrator.</p>
        <pre style={{ whiteSpace: "pre-wrap", color: "#a4262c" }}>{String(err)}</pre>
      </div>,
    );
  });
