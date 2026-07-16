import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";
import React, { lazy, Suspense } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { Spinner } from "@fluentui/react-components";
import { Layout } from "./components/Layout";
import { Login } from "./components/Login";

import "./custom.css";

// Route components are lazy-loaded so each page's code (and its heavier imports)
// is only fetched when first visited, keeping the initial bundle small.
const FileBrowser = lazy(() => import("./components/FileBrowser/FileBrowser").then((m) => ({ default: m.FileBrowser })));
const SavingsDashboard = lazy(() => import("./components/Savings/SavingsDashboard").then((m) => ({ default: m.SavingsDashboard })));
const TransfersLog = lazy(() => import("./components/Transfers/TransfersLog").then((m) => ({ default: m.TransfersLog })));
const ColdStorageDownload = lazy(() => import("./components/ColdStorage/ColdStorageDownload").then((m) => ({ default: m.ColdStorageDownload })));
const ArchiveRules = lazy(() => import("./components/Admin/ArchiveRules").then((m) => ({ default: m.ArchiveRules })));

/**
 * Gates a page behind sign-in. Token acquisition itself happens per-request in
 * the API client (see api/client.ts), so pages never receive a raw token prop
 * and never break when the initial token expires.
 */
function RequireAuth({ children }: { children: React.ReactNode }) {
  return (
    <>
      <AuthenticatedTemplate>{children}</AuthenticatedTemplate>
      <UnauthenticatedTemplate>
        <Login />
      </UnauthenticatedTemplate>
    </>
  );
}

export default function App() {
  return (
    <Layout>
      <Suspense fallback={<div style={{ padding: 48, textAlign: "center" }}><Spinner label="Loading…" /></div>}>
        <Routes>
          <Route path="/" element={<RequireAuth><FileBrowser /></RequireAuth>} />
          <Route path="/transfers" element={<RequireAuth><TransfersLog /></RequireAuth>} />
          <Route path="/transfers/:jobId" element={<RequireAuth><TransfersLog /></RequireAuth>} />
          <Route path="/savings" element={<RequireAuth><SavingsDashboard /></RequireAuth>} />
          <Route path="/admin/rules" element={<RequireAuth><ArchiveRules /></RequireAuth>} />
          <Route path="/cold-storage/download/:itemId" element={<RequireAuth><ColdStorageDownload /></RequireAuth>} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </Suspense>
    </Layout>
  );
}
