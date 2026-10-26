import { AnimatePresence, motion } from "framer-motion";
import { useState, type ReactNode } from "react";
import { Link, useParams } from "react-router-dom";

import { ApiError } from "../../shared/api/client";
import type { AspectView, MerchantDetail, TimelineEntry } from "../../shared/api/types";
import { ago, at, count, eventName, money, ratio, time, words } from "../../shared/lib/format";
import { LEGAL_FORM_LABEL, limits, motion as m } from "../../shared/theme/tokens";
import {
  Button,
  EmptyState,
  ErrorState,
  Id,
  Notice,
  Skeleton,
  SkeletonRows,
  StagePill,
} from "../../shared/ui";
import { useMerchant } from "./api";
import styles from "./Merchant.module.css";

/** Why two sides disagree, in words an operator can act on. */
export function disagreement(a: AspectView): string {
  if (a.crmVersion < a.platformVersion)
    return `The CRM is ${a.platformVersion - a.crmVersion} version${a.platformVersion - a.crmVersion === 1 ? "" : "s"} behind: an event has not been applied yet (or was lost; the reconciler heals that).`;
  if (a.crmVersion > a.platformVersion)
    return "The CRM is ahead of what the platform reports, which only happens while the platform's view is catching up.";
  return "Same version, different value: someone changed a platform-owned field in the CRM. It is listed under Drift for review.";
}

function Stitch({ ok }: { ok: boolean }) {
  return (
    <div className={[styles.stitch, ok ? "" : styles.torn].join(" ")} aria-hidden>
      <svg viewBox="0 0 48 24" preserveAspectRatio="none">
        {ok ? (
          <>
            <line className={styles.thread} x1="0" y1="12" x2="48" y2="12" />
            <circle className={styles.knot} cx="24" cy="12" r="3" />
          </>
        ) : (
          <>
            <line className={styles.thread} x1="0" y1="12" x2="16" y2="12" />
            <line className={styles.thread} x1="32" y1="12" x2="48" y2="12" />
            <text className={styles.tornMark} x="24" y="17" textAnchor="middle">
              ≠
            </text>
          </>
        )}
      </svg>
    </div>
  );
}

function Seam({ aspects, reachable }: { aspects: AspectView[]; reachable: boolean }) {
  return (
    <div className={styles.seam} role="table" aria-label="CRM and platform side by side">
      <div role="row" style={{ display: "contents" }}>
        <span role="columnheader" className={styles.headCrm}>
          Dynamics 365
        </span>
        <span role="columnheader" aria-label="Agreement" />
        <span role="columnheader" className={styles.headPlatform}>
          Payments platform
        </span>
      </div>
      {aspects.map((a) => (
        <div
          role="row"
          key={a.aspect}
          style={{ display: "contents" }}
          aria-label={`${a.aspect}: ${a.inSync ? "in step" : "differs"}`}
        >
          <span className={styles.aspect} role="rowheader">
            {a.aspect}
          </span>
          <div role="cell" className={[styles.side, styles.left, styles.rowCrm].join(" ")}>
            <strong>{words(a.crm)}</strong>
            {a.aspect !== "Platform account" && <small>version {a.crmVersion}</small>}
          </div>
          <div role="cell">
            <Stitch ok={a.inSync} />
          </div>
          <div role="cell" className={[styles.side, styles.rowPlatform].join(" ")}>
            <strong>{reachable ? words(a.platform) : "unknown"}</strong>
            {a.aspect !== "Platform account" && reachable && <small>version {a.platformVersion}</small>}
          </div>
          {!a.inSync && (
            <p className={styles.why} role="note">
              {disagreement(a)}
            </p>
          )}
        </div>
      ))}
    </div>
  );
}

function Entry({ e, index }: { e: TimelineEntry; index: number }) {
  const out = e.direction === "crm-to-platform";
  return (
    <motion.li
      className={styles.entryRow}
      initial={{ opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{
        duration: m.base,
        delay: Math.min(index, limits.timelineInitial) * m.stagger,
        ease: m.ease,
      }}
    >
      <div className={[styles.entry, out ? styles.out : ""].join(" ")}>
        <div className={styles.entryTop}>
          <strong>
            {eventName(e.eventType)} <span className={styles.whenInline}>#{e.sequence}</span>
          </strong>
          <span className={[styles.status, styles[e.status] ?? ""].join(" ")}>{words(e.status)}</span>
        </div>
        <small>
          {out ? "Sent by the CRM" : "From the platform"}, {at(e.at)}
          {e.detail ? `: ${e.detail}` : ""}
        </small>
        <small>
          Event <Id>{e.eventId}</Id>
        </small>
      </div>
      <span className={[styles.when, styles.whenCol].join(" ")}>
        {time(e.at)} #{e.sequence}
      </span>
    </motion.li>
  );
}

function Timeline({ entries }: { entries: TimelineEntry[] }) {
  const [all, setAll] = useState(false);
  const shown = all ? entries : entries.slice(0, limits.timelineInitial);
  if (entries.length === 0)
    return (
      <EmptyState title="No events yet">
        Events appear when onboarding is requested and when the platform answers.
      </EmptyState>
    );
  return (
    <>
      <ol className={styles.timeline} aria-label="Events, newest first">
        <li className={styles.timelineHead} aria-hidden>
          <span>CRM to platform</span>
          <span>Platform to CRM</span>
        </li>
        <AnimatePresence initial={true}>
          {shown.map((e, i) => (
            <Entry key={`${e.eventId}-${e.direction}`} e={e} index={i} />
          ))}
        </AnimatePresence>
      </ol>
      {entries.length > shown.length && (
        <Button className={styles.more} onClick={() => setAll(true)}>
          Show all {count(entries.length)} events
        </Button>
      )}
    </>
  );
}

function Detail({ d }: { d: MerchantDetail }) {
  const s = d.summary;
  const facts: [string, ReactNode][] = [
    ["Pricing plan", words(s.pricingPlan)],
    ["Legal form", d.legalForm ? (LEGAL_FORM_LABEL[d.legalForm] ?? words(d.legalForm)) : "—"],
    ["Country", d.country ?? "—"],
    ["Settlement IBAN", d.ibanMasked ? <Id key="iban">{d.ibanMasked}</Id> : "—"],
    ["Category (MCC)", d.mcc ?? "—"],
    ["Contact", d.contactEmail ?? "—"],
    ["Platform account", d.platformAccountId ? <Id key="pa">{d.platformAccountId}</Id> : "Not boarded yet"],
    ["Terminals", d.terminals.length ? d.terminals.join(", ") : "None active"],
    [
      `Volume ${d.volumeMonth ?? ""}`.trim(),
      d.lastMonthVolume === null
        ? "—"
        : `${money(d.lastMonthVolume)} in ${count(d.lastMonthTransactions ?? 0)} payments`,
    ],
    ["Chargeback ratio", ratio(d.chargebackRatio)],
    ["KYC", d.kycReason ? `${words(d.kycDecision)}: ${d.kycReason}` : words(d.kycDecision)],
    ["Last platform update", ago(s.lastSyncedOn)],
  ];
  const differ = d.aspects.filter((a) => !a.inSync).length;
  return (
    <>
      <header className={styles.title}>
        <h1>{s.legalName ?? s.merchantId}</h1>
        <div className={styles.titleMeta}>
          <Id>{s.merchantId}</Id>
          <StagePill stage={s.stage} />
        </div>
      </header>
      <dl className={styles.facts}>
        {facts.map(([k, v]) => (
          <div key={k}>
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
      </dl>
      <section className={styles.section} aria-labelledby="seam">
        <h2 id="seam">
          {differ
            ? `CRM and platform differ on ${differ === 1 ? "one aspect" : `${differ} aspects`}`
            : "CRM and platform agree"}
        </h2>
        <p className={styles.lead}>
          Each aspect carries the platform's version; the CRM applies only newer ones, so the two meet again
          whatever order events arrive in.
        </p>
        {!d.platformReachable && (
          <Notice>The platform's view could not be loaded just now; the CRM side is current.</Notice>
        )}
        <Seam aspects={d.aspects} reachable={d.platformReachable} />
      </section>
      <section className={styles.section} aria-labelledby="events">
        <h2 id="events">Events</h2>
        <Timeline entries={d.timeline} />
      </section>
    </>
  );
}

export function MerchantPage() {
  const { merchantId = "" } = useParams();
  const query = useMerchant(merchantId);
  const back = (
    <Link to="/merchants" className={styles.back}>
      <span aria-hidden>‹</span> All merchants
    </Link>
  );
  if (query.isPending)
    return (
      <>
        {back}
        <div role="status" aria-label="Loading the merchant">
          <Skeleton height="var(--space-6)" width="50%" />
          <div style={{ height: "var(--space-5)" }} />
          <SkeletonRows rows={5} label="Loading the merchant's events" />
        </div>
      </>
    );
  if (query.isError && !query.data) {
    const e = query.error;
    if (e instanceof ApiError && (e.status === 404 || e.status === 400))
      return (
        <>
          {back}
          <EmptyState title={e.status === 404 ? `No merchant ${merchantId}` : "That is not a merchant ID"}>
            {e.status === 404
              ? "It is not in Dynamics 365. Check the ID, or find the merchant by name."
              : "Merchant IDs look like M-004211."}
          </EmptyState>
        </>
      );
    return (
      <>
        {back}
        <ErrorState error={e} onRetry={() => void query.refetch()} />
      </>
    );
  }
  return (
    <>
      {back}
      <Detail d={query.data!} />
    </>
  );
}
