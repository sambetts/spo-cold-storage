import { useMsal } from "@azure/msal-react";
import { Button } from "@fluentui/react-components";
import { loginRequest } from "../authConfig";

/**
 * Renders a button which, when selected, redirects to Entra ID for sign-in.
 * Redirect (not popup) so the session survives reloads and isn't blocked by
 * popup blockers; the token cache is persisted in localStorage (see authConfig).
 */
export const SignInButton = () => {
  const { instance } = useMsal();

  const handleLogin = () => {
    instance.loginRedirect(loginRequest).catch((e: Error) => {
      console.error(e);
    });
  };

  return (
    <Button appearance="primary" onClick={handleLogin}>
      Sign in to Microsoft Entra ID
    </Button>
  );
};
