/**
 * Who is calling. With Microsoft Entra ID (VITE_ENTRA_CLIENT_ID set) the console signs in with MSAL and
 * asks for the API's scope; the API checks the app roles Sync.Read and Sync.Operate. Without it the
 * console runs against the API's demo mode and asks /dev/token for a token with the same roles.
 */
import { ROLES } from "../theme/tokens";

export interface Session {
  token: string;
  name: string;
  roles: string[];
  mode: "entra" | "local";
}

const env = import.meta.env;
const entra = {
  clientId: env["VITE_ENTRA_CLIENT_ID"] as string | undefined,
  tenantId: env["VITE_ENTRA_TENANT_ID"] as string | undefined,
  scope: (env["VITE_API_SCOPE"] as string | undefined) ?? "api://merchant-crm-bridge/.default",
};

function claims(token: string): Record<string, unknown> {
  const part = token.split(".")[1];
  if (!part) return {};
  try {
    const bytes = Uint8Array.from(atob(part.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
    return JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
  } catch {
    return {};
  }
}

export function sessionFrom(token: string, mode: Session["mode"]): Session {
  const c = claims(token);
  const roles = Array.isArray(c["roles"])
    ? (c["roles"] as string[])
    : typeof c["roles"] === "string"
      ? [c["roles"]]
      : [];
  return { token, name: typeof c["name"] === "string" ? c["name"] : "Operations", roles, mode };
}

export const canOperate = (s: Session | null) => !!s && s.roles.includes(ROLES.operate);

let current: Promise<Session> | null = null;

async function localSession(): Promise<Session> {
  const response = await fetch("/dev/token", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ name: "Operations (demo)" }),
  });
  if (!response.ok) throw new Error("The API did not issue a development token. Is it running in demo mode?");
  const { access_token } = (await response.json()) as { access_token: string };
  return sessionFrom(access_token, "local");
}

async function entraSession(): Promise<Session> {
  const { PublicClientApplication, InteractionRequiredAuthError } = await import("@azure/msal-browser");
  const app = new PublicClientApplication({
    auth: {
      clientId: entra.clientId!,
      authority: `https://login.microsoftonline.com/${entra.tenantId ?? "organizations"}`,
      redirectUri: window.location.origin,
    },
    cache: { cacheLocation: "sessionStorage" },
  });
  await app.initialize();
  const redirect = await app.handleRedirectPromise();
  const account = redirect?.account ?? app.getAllAccounts()[0];
  if (!account) {
    await app.loginRedirect({ scopes: [entra.scope] });
    return new Promise<Session>(() => {}); // the page navigates away
  }
  try {
    const result = await app.acquireTokenSilent({ account, scopes: [entra.scope] });
    return sessionFrom(result.accessToken, "entra");
  } catch (e) {
    if (e instanceof InteractionRequiredAuthError)
      await app.acquireTokenRedirect({ account, scopes: [entra.scope] });
    throw e;
  }
}

/** The session, created once; call again after a 401 with renew=true. */
export function getSession(renew = false): Promise<Session> {
  if (!current || renew) {
    current = (entra.clientId ? entraSession() : localSession()).catch((e: unknown) => {
      current = null;
      throw e;
    });
  }
  return current;
}
