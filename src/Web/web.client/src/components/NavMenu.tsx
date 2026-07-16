import { Link, useLocation } from "react-router-dom";
import "./NavMenu.css";
import { AuthenticatedTemplate, useMsal } from "@azure/msal-react";
import { Button } from "@fluentui/react-components";
import { Apps24Regular, History24Regular, Home24Regular, Money24Regular, Options24Regular } from "@fluentui/react-icons";

const NAV_ITEMS = [
  { to: "/", label: "Cold Storage", icon: <Home24Regular className="spo-nav-icon" /> },
  { to: "/transfers", label: "Transfers & Logs", icon: <History24Regular className="spo-nav-icon" /> },
  { to: "/savings", label: "Savings", icon: <Money24Regular className="spo-nav-icon" /> },
  { to: "/admin/rules", label: "Archive Rules", icon: <Options24Regular className="spo-nav-icon" /> },
];

export function NavMenu() {
  return (
    <header className="spo-header">
      <div className="spo-header-top">
        <div className="spo-header-left">
          <span className="spo-waffle-button" aria-hidden="true">
            <Apps24Regular />
          </span>
          <div className="spo-site-title">
            <Link to="/" className="spo-title-link">
              SharePoint Online Cold Storage
            </Link>
          </div>
        </div>
        <div className="spo-header-right">
          <SignOutButton />
        </div>
      </div>
      <NavLinks />
    </header>
  );
}

function NavLinks() {
  const location = useLocation();
  const isActive = (path: string) =>
    (path === "/" ? location.pathname === "/" : location.pathname.startsWith(path))
      ? "spo-nav-link active"
      : "spo-nav-link";

  return (
    <nav className="spo-nav">
      <AuthenticatedTemplate>
        <div className="spo-nav-links">
          {NAV_ITEMS.map((item) => (
            <Link key={item.to} to={item.to} className={isActive(item.to)}>
              {item.icon}
              <span>{item.label}</span>
            </Link>
          ))}
        </div>
      </AuthenticatedTemplate>
    </nav>
  );
}

function SignOutButton() {
  const { instance } = useMsal();
  return (
    <AuthenticatedTemplate>
      <Button appearance="subtle" onClick={() => void instance.logoutRedirect()}>
        Sign out
      </Button>
    </AuthenticatedTemplate>
  );
}
