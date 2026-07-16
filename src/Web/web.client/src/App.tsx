import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";
import React from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import { Layout } from "./components/Layout";
import { Login } from "./components/Login";
import { FileBrowser } from "./components/FileBrowser/FileBrowser";
import { SavingsDashboard } from "./components/Savings/SavingsDashboard";
import { TransfersLog } from "./components/Transfers/TransfersLog";
import { ColdStorageDownload } from "./components/ColdStorage/ColdStorageDownload";
import { ArchiveRules } from "./components/Admin/ArchiveRules";

import "./custom.css";

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
      <Routes>
        <Route path="/" element={<RequireAuth><FileBrowser /></RequireAuth>} />
        <Route path="/transfers" element={<RequireAuth><TransfersLog /></RequireAuth>} />
        <Route path="/transfers/:jobId" element={<RequireAuth><TransfersLog /></RequireAuth>} />
        <Route path="/savings" element={<RequireAuth><SavingsDashboard /></RequireAuth>} />
        <Route path="/admin/rules" element={<RequireAuth><ArchiveRules /></RequireAuth>} />
        <Route path="/cold-storage/download/:itemId" element={<RequireAuth><ColdStorageDownload /></RequireAuth>} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </Layout>
  );
}
