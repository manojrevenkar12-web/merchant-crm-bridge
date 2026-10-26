import { Link } from "react-router-dom";

import { ApiError } from "../../shared/api/client";
import { canOperate } from "../../shared/auth/auth";
import { useSession } from "../../shared/auth/SessionContext";
import { at, plural, words } from "../../shared/lib/format";
import { Button, EmptyState, Notice, PageHeader, QueryView, SkeletonRows } from "../../shared/ui";
import { useDrift, useReconcile } from "./api";
import styles from "./Drift.module.css";

export function DriftPage() {
  const query = useDrift();
  const reconcile = useReconcile();
  const session = useSession();
  return (
    <>
      <PageHeader
        title="Drift"
        lead="Platform-owned values that differ from the platform at the same version: someone changed them in the CRM. The reconciler reports these instead of overwriting them."
        actions={
          canOperate(session) ? (
            <Button variant="primary" busy={reconcile.isPending} onClick={() => reconcile.mutate()}>
              Reconcile now
            </Button>
          ) : undefined
        }
      />
      {reconcile.isSuccess && reconcile.data && (
        <div className={styles.report}>
          <Notice>
            Checked {plural(reconcile.data.checked, "merchant", "merchants")}: {reconcile.data.healed} healed
            from the platform&apos;s view, {reconcile.data.needsReview}{" "}
            {reconcile.data.needsReview === 1 ? "needs" : "need"} review.
          </Notice>
        </div>
      )}
      {reconcile.isError && (
        <div className={styles.report}>
          <Notice>
            {reconcile.error instanceof ApiError
              ? (reconcile.error.detail ?? reconcile.error.title)
              : "Reconciliation failed."}
          </Notice>
        </div>
      )}
      <QueryView
        query={query}
        skeleton={<SkeletonRows rows={5} label="Loading drift findings" />}
        isEmpty={(rows) => rows.length === 0}
        empty={
          <EmptyState title="No drift">
            No platform-owned value was changed in the CRM. Reconciliation runs every 15 minutes.
          </EmptyState>
        }
      >
        {(rows) => (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">Merchant</th>
                <th scope="col">Field</th>
                <th scope="col">In the CRM</th>
                <th scope="col">On the platform</th>
                <th scope="col">Found</th>
                <th scope="col">Status</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={`${r.merchantId}-${r.field}-${r.detectedOn}`}>
                  <td data-label="Merchant">
                    <Link to={`/merchants/${r.merchantId}`}>{r.merchantId}</Link>
                  </td>
                  <td data-label="Field">{words(r.field)}</td>
                  <td data-label="In the CRM" className={styles.crm}>
                    {r.crmValue ?? "empty"}
                  </td>
                  <td data-label="On the platform" className={styles.platform}>
                    {r.platformValue ?? "empty"}
                  </td>
                  <td data-label="Found" className={styles.muted}>
                    {at(r.detectedOn)}
                  </td>
                  <td
                    data-label="Status"
                    className={r.resolution === "needs_review" ? styles.review : styles.healed}
                  >
                    {r.resolution === "needs_review" ? "Needs review" : "Healed"}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </QueryView>
    </>
  );
}
