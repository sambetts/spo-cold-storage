import "./NavMenu.css";
import { SignInButton } from "./SignInButton";

export function Login() {
  return (
    <div style={{ maxWidth: 640, margin: "48px auto", padding: "0 16px", fontFamily: '"Segoe UI", Tahoma, sans-serif' }}>
      <h1 style={{ marginBottom: 8 }}>SharePoint Online Cold Storage</h1>
      <p style={{ color: "#605e5c", fontSize: 15 }}>
        Find and download files archived to Azure Blob cold storage, and review the full, searchable log of
        every transfer.
      </p>
      <p style={{ marginTop: 24 }}>Please sign in to continue.</p>
      <SignInButton />
    </div>
  );
}
