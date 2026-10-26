import { useQuery } from "@tanstack/react-query";
import { createContext, useContext, type ReactNode } from "react";

import { ErrorState, SkeletonRows } from "../ui";
import { getSession, type Session } from "./auth";

const SessionContext = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const query = useQuery({
    queryKey: ["session"],
    queryFn: () => getSession(),
    staleTime: Infinity,
    retry: false,
  });
  if (query.isPending)
    return (
      <main style={{ padding: "var(--gutter)" }}>
        <SkeletonRows rows={4} label="Signing in" />
      </main>
    );
  if (query.isError)
    return (
      <main style={{ padding: "var(--gutter)" }}>
        <ErrorState error={query.error} onRetry={() => void query.refetch()} />
      </main>
    );
  return <SessionContext.Provider value={query.data}>{children}</SessionContext.Provider>;
}

export function useSession(): Session {
  const s = useContext(SessionContext);
  if (!s) throw new Error("useSession outside SessionProvider");
  return s;
}
