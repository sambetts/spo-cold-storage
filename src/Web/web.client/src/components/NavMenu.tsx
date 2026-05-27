import React, { Component } from 'react';
import { Link, useLocation } from 'react-router-dom';
import './NavMenu.css';
import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from "@azure/msal-react";
import { Button } from '@fluentui/react-components';
import { 
  Home24Regular, 
  Search24Regular, 
  Document24Regular, 
  Settings24Regular,
  Grid24Regular,
  Navigation24Regular
} from '@fluentui/react-icons';

export class NavMenu extends Component<{}, { collapsed: boolean }> {
  static displayName = NavMenu.name;

  constructor(props: any) {
    super(props);

    this.toggleNavbar = this.toggleNavbar.bind(this);
    this.state = {
      collapsed: true
    };
  }

  toggleNavbar() {
    this.setState({
      collapsed: !this.state.collapsed
    });
  }

  render() {
    return (
      <header className="spo-header">
        <div className="spo-header-top">
          <div className="spo-header-left">
            <button className="spo-waffle-button" aria-label="App launcher">
              <Grid24Regular />
            </button>
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
}

const NavLinks: React.FC = () => {
  const location = useLocation();
  
  const isActive = (path: string) => {
    return location.pathname === path ? 'spo-nav-link active' : 'spo-nav-link';
  };
  
  return (
    <nav className="spo-nav">
      <AuthenticatedTemplate>
        <div className="spo-nav-links">
          <Link to="/" className={isActive('/')}>
            <Home24Regular className="spo-nav-icon" />
            <span>Browser</span>
          </Link>
          <Link to="/FindFile" className={isActive('/FindFile')}>
            <Search24Regular className="spo-nav-icon" />
            <span>File Search</span>
          </Link>
          <Link to="/FindMigrationLog" className={isActive('/FindMigrationLog')}>
            <Document24Regular className="spo-nav-icon" />
            <span>Logs</span>
          </Link>
          <Link to="/MigrationTargets" className={isActive('/MigrationTargets')}>
            <Settings24Regular className="spo-nav-icon" />
            <span>Targets</span>
          </Link>
        </div>
      </AuthenticatedTemplate>
      
      <UnauthenticatedTemplate>
        <div className="spo-nav-links">
          <Link to="/" className={isActive('/')}>
            <Home24Regular className="spo-nav-icon" />
            <span>Login</span>
          </Link>
        </div>
      </UnauthenticatedTemplate>
    </nav>
  );
};

const SignOutButton: React.FC = () => {
  const { instance } = useMsal();
  
  const handleLogout = () => {
    instance.logoutPopup();
  };
  
  return (
    <AuthenticatedTemplate>
      <Button appearance="subtle" onClick={handleLogout}>
        Sign Out
      </Button>
    </AuthenticatedTemplate>
  );
};
