import { useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";

import type { MerchantSummary } from "../../shared/api/types";
import { ago, plural, words } from "../../shared/lib/format";
import { limits, STAGE_LABEL, STAGES, type Stage } from "../../shared/theme/tokens";
import { Button, EmptyState, Id, PageHeader, QueryView, SkeletonRows, StagePill } from "../../shared/ui";
import { useMerchants } from "./api";
import styles from "./Merchants.module.css";

function useDebounced(value: string, ms: number) {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

function Versions({ m }: { m: MerchantSummary }) {
  return (
    <span className={styles.versions}>
      <span className={styles.crm} title="CRM version: changes sales made">
        CRM v{m.crmVersion}
      </span>{" "}
      /{" "}
      <span className={styles.platform} title="Platform version: events the CRM applied">
        platform v{m.platformVersion}
      </span>
    </span>
  );
}

function Table({ rows }: { rows: MerchantSummary[] }) {
  const navigate = useNavigate();
  return (
    <table className={[styles.table, styles.wide].join(" ")}>
      <thead>
        <tr>
          <th scope="col">Merchant</th>
          <th scope="col">Stage</th>
          <th scope="col" className={styles.hideNarrow}>
            Plan
          </th>
          <th scope="col" className={styles.hideNarrow}>
            Risk
          </th>
          <th scope="col">Versions</th>
          <th scope="col">Last platform update</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((m) => (
          <tr key={m.merchantId} onClick={() => navigate(`/merchants/${m.merchantId}`)}>
            <td>
              <span className={styles.name}>
                <Link to={`/merchants/${m.merchantId}`} onClick={(e) => e.stopPropagation()}>
                  {m.legalName ?? m.merchantId}
                </Link>
                <small>
                  <Id>{m.merchantId}</Id>
                </small>
              </span>
            </td>
            <td>
              <StagePill stage={m.stage} />
            </td>
            <td className={styles.hideNarrow}>{words(m.pricingPlan)}</td>
            <td className={styles.hideNarrow}>{words(m.riskBand)}</td>
            <td>
              <Versions m={m} />
            </td>
            <td className={styles.muted}>{ago(m.lastSyncedOn)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function List({ rows }: { rows: MerchantSummary[] }) {
  return (
    <ul className={[styles.list, styles.narrow].join(" ")}>
      {rows.map((m) => (
        <li key={m.merchantId}>
          <Link to={`/merchants/${m.merchantId}`} className={styles.card}>
            <span className={styles.cardTop}>
              <span className={styles.name}>
                <strong>{m.legalName ?? m.merchantId}</strong>
                <small>
                  <Id>{m.merchantId}</Id>
                </small>
              </span>
              <StagePill stage={m.stage} />
            </span>
            <Versions m={m} />
          </Link>
        </li>
      ))}
    </ul>
  );
}

export function MerchantsPage() {
  const [params, setParams] = useSearchParams();
  const [search, setSearch] = useState(params.get("q") ?? "");
  const stage = params.get("stage") ?? "";
  const debounced = useDebounced(search.trim(), limits.searchDebounceMs);
  const query = useMerchants(debounced, stage);

  const update = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };

  useEffect(() => update("q", debounced), [debounced]); // eslint-disable-line react-hooks/exhaustive-deps

  const filtered = !!(debounced || stage);
  return (
    <>
      <PageHeader
        title="Merchants"
        lead="Every merchant in Dynamics 365 with its lifecycle stage and how far each side has got."
      />
      <div className={styles.filters} role="search">
        <label className={styles.field}>
          Find a merchant
          <input
            type="search"
            value={search}
            maxLength={limits.searchMaxLength}
            placeholder="Name or merchant ID, e.g. M-004211"
            onChange={(e) => setSearch(e.target.value)}
          />
        </label>
        <label className={styles.field}>
          Stage
          <select value={stage} onChange={(e) => update("stage", e.target.value)}>
            <option value="">All stages</option>
            {STAGES.map((s: Stage) => (
              <option key={s} value={s}>
                {STAGE_LABEL[s]}
              </option>
            ))}
          </select>
        </label>
      </div>
      <QueryView
        query={query}
        skeleton={<SkeletonRows rows={8} label="Loading merchants" />}
        isEmpty={(rows) => rows.length === 0}
        empty={
          filtered ? (
            <EmptyState
              title="No merchant matches"
              action={
                <Button
                  onClick={() => {
                    setSearch("");
                    setParams({}, { replace: true });
                  }}
                >
                  Clear the filters
                </Button>
              }
            >
              Try part of the name, or the full merchant ID.
            </EmptyState>
          ) : (
            <EmptyState title="No merchants yet">
              Merchants appear here once sales create them in Dynamics 365.
            </EmptyState>
          )
        }
      >
        {(rows) => (
          <>
            <Table rows={rows} />
            <List rows={rows} />
            <p className={styles.count} aria-live="polite">
              {rows.length >= limits.merchantsPage
                ? `Showing the first ${limits.merchantsPage}; narrow the search to see others.`
                : plural(rows.length, "merchant", "merchants")}
            </p>
          </>
        )}
      </QueryView>
    </>
  );
}
