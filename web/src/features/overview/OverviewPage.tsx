import { motion } from "framer-motion";
import { Link } from "react-router-dom";

import type { Overview } from "../../shared/api/types";
import { ago, count, plural, time } from "../../shared/lib/format";
import {
  chart,
  healthy,
  limits,
  motion as m,
  STAGE_LABEL,
  STAGE_TONE,
  STAGES,
} from "../../shared/theme/tokens";
import { QueryView, Skeleton, SkeletonRows } from "../../shared/ui";
import { useOverview } from "./api";
import styles from "./Overview.module.css";

/** What needs a person, in plain words; empty when the bridge is in step. */
export function attention(o: Overview): string[] {
  const items: string[] = [];
  if (o.deadLetters > 0) items.push(plural(o.deadLetters, "dead letter", "dead letters"));
  if (o.driftNeedsReview > 0) items.push(plural(o.driftNeedsReview, "drift finding", "drift findings"));
  if (o.consumerLag > healthy.maxConsumerLag) items.push(`${count(o.consumerLag)} platform events waiting`);
  if (o.outboxPending > healthy.maxOutboxPending) items.push(`${count(o.outboxPending)} CRM changes waiting`);
  return items;
}

/** "1 dead letter needs a look." / "2 dead letters and 1 drift finding need a look." */
export function headline(items: string[]): string {
  const list =
    items.length > 1 ? `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}` : (items[0] ?? "");
  const singular = items.length === 1 && /^1 /.test(list);
  return `${list.charAt(0).toUpperCase()}${list.slice(1)} ${singular ? "needs" : "need"} a look.`;
}

function Verdict({ o }: { o: Overview }) {
  const items = attention(o);
  const calm = items.length === 0;
  return (
    <header className={[styles.verdict, calm ? styles.calm : styles.attention].join(" ")}>
      <h1>{calm ? "CRM and platform are in step." : headline(items)}</h1>
      <p>
        {count(o.merchants)} merchants in Dynamics 365. Events applied in the last {limits.throughputMinutes}{" "}
        minutes: {count(o.recentOutcomes.reduce((n, x) => n + x.count, 0))}. Last reconciliation{" "}
        {ago(o.lastReconcileAt)}
        {o.lastReconcileHealed
          ? `, which healed ${plural(o.lastReconcileHealed, "merchant", "merchants")}`
          : ""}
        .
      </p>
    </header>
  );
}

function Arrow({ index }: { index: number }) {
  return (
    <svg className={styles.connector} viewBox="0 0 32 16" preserveAspectRatio="none" aria-hidden>
      <motion.path
        d="M0 8 H26"
        initial={{ pathLength: 0 }}
        animate={{ pathLength: 1 }}
        transition={{ duration: m.draw, delay: index * m.stagger * 4, ease: m.ease }}
      />
      <path className={styles.head} d="M26 3 L32 8 L26 13 Z" />
    </svg>
  );
}

type Station = {
  label: string;
  value: string;
  note: string;
  tone?: "waiting" | "problem";
  end?: "crm" | "platform";
  to?: string;
  topic?: boolean;
};

function Lane({ title, aside, stations }: { title: string; aside: string; stations: Station[] }) {
  return (
    <section className={styles.lane} aria-label={title}>
      <p className={styles.laneTitle}>
        {title} <span>{aside}</span>
      </p>
      <div className={styles.stations}>
        {stations.map((s, i) => {
          const cls = [
            styles.station,
            s.tone && styles[s.tone],
            s.end === "crm" && styles.crmEnd,
            s.end === "platform" && styles.platformEnd,
          ]
            .filter(Boolean)
            .join(" ");
          const body = (
            <>
              <small>{s.label}</small>
              <strong className={s.topic ? styles.topic : undefined}>{s.value}</strong>
              <small>{s.note}</small>
            </>
          );
          return [
            i > 0 && <Arrow key={`a${i}`} index={i} />,
            s.to ? (
              <Link key={s.label} to={s.to} className={cls}>
                {body}
              </Link>
            ) : (
              <div key={s.label} className={cls}>
                {body}
              </div>
            ),
          ];
        })}
      </div>
    </section>
  );
}

function Pipeline({ o }: { o: Overview }) {
  const applied = o.recentOutcomes.find((x) => x.outcome === "applied")?.count ?? 0;
  const stale = o.recentOutcomes.find((x) => x.outcome === "stale")?.count ?? 0;
  return (
    <div className={styles.lanes}>
      <Lane
        title="CRM to platform"
        aside="changes sales make, through the outbox"
        stations={[
          { label: "Dynamics 365", value: count(o.merchants), note: "merchants", end: "crm" },
          {
            label: "Outbox",
            value: count(o.outboxPending),
            note: "changes not yet published",
            tone: o.outboxPending > healthy.maxOutboxPending ? "waiting" : undefined,
          },
          {
            label: "Relay",
            value: o.lastRelayAt ? time(o.lastRelayAt) : "—",
            note: `last run ${ago(o.lastRelayAt)}`,
          },
          {
            label: "Payments platform",
            value: "crm.merchant.events",
            note: "Kafka topic",
            topic: true,
            end: "platform",
          },
        ]}
      />
      <Lane
        title="Platform to CRM"
        aside="KYC, boarding, terminals, risk and volume"
        stations={[
          {
            label: "Payments platform",
            value: "platform.merchant.events",
            note: "Kafka topic",
            topic: true,
            end: "platform",
          },
          {
            label: "Waiting",
            value: count(o.consumerLag),
            note: "events not yet applied",
            tone: o.consumerLag > healthy.maxConsumerLag ? "waiting" : undefined,
          },
          {
            label: "Dead letters",
            value: count(o.deadLetters),
            note: o.deadLetters ? "need a decision" : "none",
            tone: o.deadLetters ? "problem" : undefined,
            to: "/dead-letters",
          },
          {
            label: "Dynamics 365",
            value: count(applied),
            note: `applied, ${count(stale)} older than the CRM's`,
            end: "crm",
          },
        ]}
      />
    </div>
  );
}

function Stages({ o }: { o: Overview }) {
  const total = Math.max(
    1,
    o.stages.reduce((n, s) => n + s.count, 0),
  );
  const ordered = STAGES.map((stage) => ({
    stage,
    count: o.stages.find((s) => s.stage === stage)?.count ?? 0,
  }));
  return (
    <>
      <div
        className={styles.stageBar}
        role="img"
        aria-label={ordered
          .filter((s) => s.count)
          .map((s) => `${STAGE_LABEL[s.stage]} ${s.count}`)
          .join(", ")}
      >
        {ordered
          .filter((s) => s.count > 0)
          .map((s) => (
            <div
              key={s.stage}
              className={[styles.segment, styles[`tone-${STAGE_TONE[s.stage]}`]].join(" ")}
              style={{ flexGrow: s.count / total }}
            />
          ))}
      </div>
      <ul className={styles.legend}>
        {ordered.map((s) => (
          <li key={s.stage}>
            <span className={[styles.swatch, styles[`tone-${STAGE_TONE[s.stage]}`]].join(" ")} aria-hidden />
            <Link to={`/merchants?stage=${s.stage}`}>{STAGE_LABEL[s.stage]}</Link>
            <b>{count(s.count)}</b>
          </li>
        ))}
      </ul>
    </>
  );
}

function Throughput({ o }: { o: Overview }) {
  const max = Math.max(1, ...o.throughput.map((x) => x.applied + x.stale + x.rejected));
  const step = chart.barWidth + chart.barGap;
  const width = o.throughput.length * step;
  const h = chart.throughputHeight;
  const scale = (n: number) => (n === 0 ? 0 : Math.max(chart.minBar, (n / max) * h));
  const total = o.throughput.reduce((n, x) => n + x.applied + x.stale + x.rejected, 0);
  return (
    <figure style={{ margin: 0 }}>
      <svg
        className={styles.throughput}
        viewBox={`0 0 ${width} ${h + chart.minBar}`}
        role="img"
        aria-label={`${count(total)} events in the last ${limits.throughputMinutes} minutes`}
      >
        {o.throughput.map((x, i) => {
          const a = scale(x.applied);
          const s = scale(x.stale);
          const r = scale(x.rejected);
          return (
            <g key={x.minute}>
              <title>{`${time(x.minute)}: ${x.applied} applied, ${x.stale} stale, ${x.rejected} rejected`}</title>
              <rect className={styles.applied} x={i * step} y={h - a} width={chart.barWidth} height={a} />
              <rect className={styles.stale} x={i * step} y={h - a - s} width={chart.barWidth} height={s} />
              <rect
                className={styles.rejected}
                x={i * step}
                y={h - a - s - r}
                width={chart.barWidth}
                height={r}
              />
            </g>
          );
        })}
        <line className={styles.baseline} x1={0} x2={width} y1={h} y2={h} />
      </svg>
      <figcaption className={styles.keys}>
        <span>
          <i className={[styles.swatch].join(" ")} style={{ background: "var(--crm)" }} /> Applied
        </span>
        <span>
          <i className={styles.swatch} style={{ background: "var(--ink-faint)" }} /> Older than the CRM
          (skipped)
        </span>
        <span>
          <i className={styles.swatch} style={{ background: "var(--signal)" }} /> Rejected
        </span>
      </figcaption>
    </figure>
  );
}

function OverviewSkeleton() {
  return (
    <div role="status" aria-label="Loading the overview">
      <Skeleton height="var(--space-7)" width="60%" />
      <div style={{ height: "var(--space-5)" }} />
      <SkeletonRows rows={2} label="Loading the pipeline" height="var(--space-8)" />
    </div>
  );
}

export function OverviewPage() {
  const query = useOverview();
  return (
    <QueryView
      query={query}
      skeleton={<OverviewSkeleton />}
      isEmpty={(o) => o.merchants === 0}
      empty={
        <section>
          <h1>No merchants yet.</h1>
          <p style={{ color: "var(--ink-muted)", marginTop: "var(--space-2)" }}>
            When sales request onboarding for a merchant in Dynamics 365, it appears here and its events start
            to flow.
          </p>
        </section>
      }
    >
      {(o) => (
        <>
          <Verdict o={o} />
          <section className={styles.section} aria-labelledby="pipeline">
            <h2 id="pipeline">Where events are</h2>
            <Pipeline o={o} />
          </section>
          <div className={styles.two}>
            <section className={styles.section} aria-labelledby="flow">
              <h2 id="flow">Platform events applied, last {limits.throughputMinutes} minutes</h2>
              <Throughput o={o} />
            </section>
            <section className={styles.section} aria-labelledby="stages">
              <h2 id="stages">Merchants by stage</h2>
              <Stages o={o} />
            </section>
          </div>
        </>
      )}
    </QueryView>
  );
}
