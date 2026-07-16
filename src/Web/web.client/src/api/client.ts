import { AccountInfo, IPublicClientApplication, InteractionRequiredAuthError } from "@azure/msal-browser";
import { useMsal } from "@azure/msal-react";
import { useMemo } from "react";
import { loginRequest } from "../authConfig";

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
 */
async function acquireToken(instance: IPublicClientApplication, account: AccountInfo | undefined): Promise<string> {
  const request = { ...loginRequest, account };
  try {
    const result = await instance.acquireTokenSilent(request);
    return result.accessToken;
  } catch (err) {
    if (err instanceof InteractionRequiredAuthError) {
      const result = await instance.acquireTokenPopup(request);
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
