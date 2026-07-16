import { useMsal } from "@azure/msal-react";
import { Button } from "@fluentui/react-components";
import { loginRequest } from "../authConfig";

/**
 * Renders a button which, when selected, opens a popup for sign-in.
 */
export const SignInButton = () => {
  const { instance } = useMsal();

  const handleLogin = () => {
    instance.loginPopup(loginRequest).catch((e: Error) => {
      console.error(e);
    });
  };

  return (
    <Button appearance="primary" onClick={handleLogin}>
      Sign in to Microsoft Entra ID
    </Button>
  );
};
