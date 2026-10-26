import { getSession } from "../auth/auth";
import type { Problem } from "./types";

/** An API failure with what the API said about it (problem details) and whether trying again may help. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly title: string,
    readonly detail?: string,
  ) {
    super(detail ? `${title}: ${detail}` : title);
  }

  get retryable() {
    return this.status === 0 || this.status === 429 || this.status >= 500;
  }
}

async function send<T>(method: string, path: string, renewed = false): Promise<T> {
  const session = await getSession();
  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers: { authorization: `Bearer ${session.token}`, accept: "application/json" },
    });
  } catch {
    throw new ApiError(
      0,
      "The bridge API cannot be reached",
      "Check your connection; the view retries on its own.",
    );
  }
  if (response.status === 401 && !renewed) {
    await getSession(true);
    return send<T>(method, path, true);
  }
  if (!response.ok) {
    let problem: Partial<Problem> = {};
    try {
      problem = (await response.json()) as Problem;
    } catch {
      /* no body */
    }
    const title =
      problem.title ??
      (response.status === 403 ? "Your role does not allow this" : `The API answered ${response.status}`);
    throw new ApiError(response.status, title, problem.detail);
  }
  if (response.status === 202 || response.status === 204) return undefined as T;
  return (await response.json()) as T;
}

export const api = {
  get: <T>(path: string) => send<T>("GET", path),
  post: <T>(path: string) => send<T>("POST", path),
};
