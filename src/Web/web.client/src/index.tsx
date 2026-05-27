import React from 'react';
import { BrowserRouter } from 'react-router-dom';
import App from './App';
import * as serviceWorkerRegistration from './serviceWorkerRegistration';
import { MsalProvider } from "@azure/msal-react";
import { PublicClientApplication } from "@azure/msal-browser";
import { msalConfig } from "./authConfig.js";
import { createRoot } from 'react-dom/client';

const msalInstance = new PublicClientApplication(msalConfig);

const baseUrl = document.getElementsByTagName('base')[0]?.getAttribute('href') ?? '/';
const rootElement = document.getElementById('root');
const root = createRoot(rootElement!); 

root.render(
  <React.StrictMode>
    <MsalProvider instance={msalInstance}>
      <BrowserRouter basename={baseUrl!}>
        <App />
      </BrowserRouter>
    </MsalProvider>
  </React.StrictMode>);

// If you want your app to work offline and load faster, you can change
// unregister() to register() below. Note this comes with some pitfalls.
// Learn more about service workers: https://cra.link/PWA
serviceWorkerRegistration.unregister();
