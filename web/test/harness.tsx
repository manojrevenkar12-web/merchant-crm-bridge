/** Renders the real routes against a stubbed API and records what the console sent. */
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import { MotionConfig } from "framer-motion";
import { MemoryRouter } from "react-router-dom";

import { AppRoutes } from "../src/app/App";
import { getSession } from "../src/shared/auth/auth";

export type Reply = unknown | Response | ((url: string) => unknown | Response);
export type Routes = Record<string, Reply>;

export const json = (data: unknown, status = 200) =>
  new Response(JSON.stringify(data), {
    status,
    headers: { "content-type": status >= 400 ? "application/problem+json" : "application/json" },
  });

export const problem = (status: number, title: string, detail?: string) =>
  json({ status, title, detail }, status);

/** A token shaped like the API's: the console reads name and roles from it. */
export function token(roles: string[], name = "Test Operator") {
  const b64 = (o: unknown) =>
    btoa(JSON.stringify(o)).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  return `${b64({ alg: "HS256", typ: "JWT" })}.${b64({ name, roles })}.signature`;
}

export interface Call {
  method: string;
  path: string;
  auth: string | null;
}

/** Routes keyed "GET /v1/x?y=1"; the exact key wins, then the path without its query. */
export function stubApi(routes: Routes, roles: string[] = ["Sync.Read", "Sync.Operate"]): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const auth = (init?.headers as Record<string, string> | undefined)?.["authorization"] ?? null;
      calls.push({ method, path: input, auth });
      if (input === "/dev/token") return json({ access_token: token(roles) });
      const key = `${method} ${input}`;
      const reply = key in routes ? routes[key] : routes[`${method} ${input.split("?")[0]}`];
      if (reply === undefined) return problem(404, `no stub for ${key}`);
      const value = typeof reply === "function" ? (reply as (u: string) => unknown)(input) : reply;
      return value instanceof Response ? value : json(value);
    }),
  );
  return calls;
}

export async function renderAt(path: string) {
  await getSession(true); // a fresh session per test, from the stubbed /dev/token
  const qc = new QueryClient({
    defaultOptions: { queries: { retry: false, staleTime: Infinity, refetchInterval: false } },
  });
  return render(
    <QueryClientProvider client={qc}>
      <MotionConfig reducedMotion="always">
        <MemoryRouter initialEntries={[path]}>
          <AppRoutes />
        </MemoryRouter>
      </MotionConfig>
    </QueryClientProvider>,
  );
}

export const clone = <T,>(x: T): T => JSON.parse(JSON.stringify(x)) as T;
