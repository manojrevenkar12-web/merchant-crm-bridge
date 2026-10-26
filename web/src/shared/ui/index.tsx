import type { UseQueryResult } from "@tanstack/react-query";
import type { ButtonHTMLAttributes, ReactNode } from "react";

import { ApiError } from "../api/client";
import { STAGE_LABEL, STAGE_TONE, type Stage } from "../theme/tokens";
import styles from "./ui.module.css";

type Variant = "default" | "primary" | "quiet";

export function Button({
  variant = "default",
  busy,
  children,
  className,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean }) {
  const cls = [styles.button, variant !== "default" && styles[variant], className].filter(Boolean).join(" ");
  return (
    <button
      type="button"
      {...rest}
      className={cls}
      disabled={busy || rest.disabled}
      aria-busy={busy || undefined}
    >
      {busy && <span className={styles.spinner} aria-hidden />}
      {children}
    </button>
  );
}

export function Skeleton({ height = "var(--space-4)", width = "100%" }: { height?: string; width?: string }) {
  return <div className={styles.skeleton} style={{ height, width }} aria-hidden />;
}

export function SkeletonRows({
  rows = 6,
  label,
  height = "var(--space-7)",
}: {
  rows?: number;
  label: string;
  height?: string;
}) {
  return (
    <div className={styles.skeletons} role="status" aria-label={label}>
      {Array.from({ length: rows }, (_, i) => (
        <Skeleton key={i} height={height} width={i === rows - 1 ? "70%" : "100%"} />
      ))}
    </div>
  );
}

export function EmptyState({
  title,
  children,
  action,
}: {
  title: string;
  children?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <section className={styles.state} aria-live="polite">
      <h2>{title}</h2>
      {children && <p>{children}</p>}
      {action}
    </section>
  );
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  const e = error instanceof ApiError ? error : null;
  return (
    <section className={[styles.state, styles.error].join(" ")} role="alert">
      <h2>{e?.title ?? "This view could not load"}</h2>
      <p>{e?.detail ?? (error instanceof Error ? error.message : "Something went wrong while loading.")}</p>
      <Button onClick={onRetry}>Try again</Button>
    </section>
  );
}

/**
 * The three states every view has before it has data: loading (skeleton), failed (what happened and a
 * retry), empty (what it means and what to do). Data that is already shown stays while it refreshes.
 */
export function QueryView<T>({
  query,
  skeleton,
  isEmpty,
  empty,
  children,
}: {
  query: UseQueryResult<T>;
  skeleton: ReactNode;
  isEmpty?: (data: T) => boolean;
  empty?: ReactNode;
  children: (data: T) => ReactNode;
}) {
  if (query.isPending) return <>{skeleton}</>;
  if (query.isError && query.data === undefined)
    return <ErrorState error={query.error} onRetry={() => void query.refetch()} />;
  const data = query.data as T;
  if (isEmpty?.(data)) return <>{empty}</>;
  return <>{children(data)}</>;
}

export function StagePill({ stage }: { stage: Stage }) {
  const tone = STAGE_TONE[stage];
  return (
    <span className={[styles.pill, styles[tone === "quiet" ? "quietTone" : tone]].join(" ")}>
      {STAGE_LABEL[stage]}
    </span>
  );
}

export function PageHeader({
  title,
  lead,
  actions,
}: {
  title: ReactNode;
  lead?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <header className={styles.header}>
      <div>
        <h1>{title}</h1>
        {lead && <p>{lead}</p>}
      </div>
      {actions}
    </header>
  );
}

export function Id({ children }: { children: ReactNode }) {
  return <span className={styles.id}>{children}</span>;
}

export function Notice({ children }: { children: ReactNode }) {
  return (
    <p className={styles.notice} role="status">
      {children}
    </p>
  );
}
