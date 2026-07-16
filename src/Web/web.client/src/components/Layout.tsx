import { PropsWithChildren } from "react";
import { NavMenu } from "./NavMenu";

export function Layout({ children }: PropsWithChildren) {
  return (
    <div className="spo-app">
      <NavMenu />
      <main className="spo-page-container">
        <div className="spo-content">{children}</div>
      </main>
    </div>
  );
}
