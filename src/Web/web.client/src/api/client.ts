import {
  AccountInfo,
  AuthenticationResult,
  BrowserAuthError,
  IPublicClientApplication,
  InteractionRequiredAuthError,
} from "@azure/msal-browser";
import { useMsal } from "@azure/msal-react";
import { useMemo } from "react";
import { loginRequest } from "../authConfig";

// Silent token acquisition renews tokens in a hidden iframe. That path can fail
// even when interactive sign-in would succeed — most commonly because the browser
// blocks third-party cookies (so the iframe can't reach Entra) or the renewal
// simply doesn't return within the hash-monitor window. MSAL surfaces these as a
// BrowserAuthError (e.g. `monitor_window_timeout`) rather than an
// InteractionRequiredAuthError, so without this set they would bubble up as a raw
// error banner instead of prompting the user to re-authenticate.
const SILENT_IFRAME_FAILURE_CODES = new Set([
  "monitor_window_timeout",
  "empty_window_error",
  "iframe_closed_prematurely",
  "silent_prompt_value_error",
  "hash_empty_error",
]);

function requiresInteraction(err: unknown): boolean {
  if (err instanceof InteractionRequiredAuthError) {
    return true;
  }
  return err instanceof BrowserAuthError && SILENT_IFRAME_FAILURE_CODES.has(err.errorCode);
}

// MSAL forbids more than one interactive request at a time (it throws
// `interaction_in_progress`), and several components + the auto-refresh timer can
// hit a silent failure at once. Share a single popup across concurrent callers so
// they all resolve from one interactive sign-in instead of stacking popups.
let interactiveRequest: Promise<AuthenticationResult> | null = null;

function acquireTokenInteractive(
  instance: IPublicClientApplication,
  request: Parameters<IPublicClientApplication["acquireTokenPopup"]>[0],
): Promise<AuthenticationResult> {
  if (!interactiveRequest) {
    interactiveRequest = instance.acquireTokenPopup(request).finally(() => {
      interactiveRequest = null;
    });
  }
  return interactiveRequest;
}

/**
 * Error carrying the HTTP status so callers can render friendly messages
 * (403 -> not permitted, 404 -> not found, ...) without re-parsing text.
 */
export class ApiError extends Error {
  constructor(public status: number, message: string, public detail?: string) {
    super(message);
    this.name = "ApiError";
  }
}

/**
 * Acquire an access token for THIS app's API for every request. MSAL caches and
 * silently refreshes, so this fixes the old "token grabbed once on mount, app
 * breaks after ~1h" bug and the "authenticated but no token" dead-end: a silent
 * failure falls back to an interactive popup instead of leaving the user stuck.
 *
 * The silent renewal happens in a hidden iframe, which can time out
 * (`monitor_window_timeout`) when third-party cookies are blocked or the network
 * is slow. Those arrive as BrowserAuthError — not InteractionRequiredAuthError —
 * so we treat both as "needs interaction" and recover with a popup rather than
 * surfacing the raw MSAL error to the UI.
 */
async function acquireToken(instance: IPublicClientApplication, account: AccountInfo | undefined): Promise<string> {
  const request = { ...loginRequest, account };
  try {
    const result = await instance.acquireTokenSilent(request);
    return result.accessToken;
  } catch (err) {
    if (requiresInteraction(err)) {
      const result = await acquireTokenInteractive(instance, request);
      return result.accessToken;
    }
    throw err;
  }
}

export interface ApiClient {
  /** GET + parse JSON. Throws ApiError on non-2xx. */
  get<T>(path: string, signal?: AbortSignal): Promise<T>;
  /** GET returning the raw Response (for blob downloads). Throws ApiError on non-2xx. */
  getResponse(path: string, signal?: AbortSignal): Promise<Response>;
  /** POST JSON + parse JSON. Throws ApiError on non-2xx. */
  post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T>;
  /** DELETE. Throws ApiError on non-2xx. */
  del(path: string, signal?: AbortSignal): Promise<void>;
}

export function createApiClient(instance: IPublicClientApplication, account: AccountInfo | undefined): ApiClient {
  async function request(path: string, init: RequestInit = {}): Promise<Response> {
    const token = await acquireToken(instance, account);
    const headers = new Headers(init.headers);
    headers.set("Authorization", `Bearer ${token}`);
    return fetch(path, { ...init, headers });
  }

  async function ensureOk(response: Response): Promise<Response> {
    if (response.ok) {
      return response;
    }
    let detail = "";
    try {
      detail = await response.text();
    } catch {
      /* ignore body read errors */
    }
    throw new ApiError(response.status, `HTTP ${response.status} ${response.statusText}`, detail || undefined);
  }

  return {
    async get<T>(path: string, signal?: AbortSignal): Promise<T> {
      const response = await ensureOk(await request(path, { method: "GET", signal }));
      return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
    },
    async getResponse(path: string, signal?: AbortSignal): Promise<Response> {
      return ensureOk(await request(path, { method: "GET", signal }));
    },
    async post<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
      const response = await ensureOk(
        await request(path, {
          method: "POST",
          headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
          body: body !== undefined ? JSON.stringify(body) : undefined,
          signal,
        }),
      );
      return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
    },
    async del(path: string, signal?: AbortSignal): Promise<void> {
      await ensureOk(await request(path, { method: "DELETE", signal }));
    },
  };
}

/**
 * Hook returning a memoised API client bound to the signed-in account. Use this
 * in every page/component instead of threading a raw bearer token as a prop.
 */
export function useApi(): ApiClient {
  const { instance, accounts } = useMsal();
  const account = accounts[0];
  return useMemo(() => createApiClient(instance, account), [instance, account]);
}
