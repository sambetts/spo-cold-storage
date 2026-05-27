import { Component, PropsWithChildren } from 'react';
import { NavMenu } from './NavMenu';

export class Layout extends Component<PropsWithChildren<{}>> {
  static displayName = Layout.name;

  render () {
    return (
      <div className="spo-app">
        <NavMenu />
        <main className="spo-page-container">
          <div className="spo-content">
            {this.props.children}
          </div>
        </main>
      </div>
    );
  }
}
