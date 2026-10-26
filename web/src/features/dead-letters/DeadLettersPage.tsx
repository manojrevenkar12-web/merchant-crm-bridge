import { AnimatePresence, motion } from "framer-motion";
import { useState } from "react";
import { Link } from "react-router-dom";

import { ApiError } from "../../shared/api/client";
import type { DeadLetter } from "../../shared/api/types";
import { canOperate } from "../../shared/auth/auth";
import { useSession } from "../../shared/auth/SessionContext";
import { eventName, plural } from "../../shared/lib/format";
import { DEAD_LETTER_REASON, motion as m } from "../../shared/theme/tokens";
import { Button, EmptyState, Id, PageHeader, QueryView, SkeletonRows } from "../../shared/ui";
import { useDeadLetters, useReplay } from "./api";
import styles from "./DeadLetters.module.css";

const ADVICE: Record<string, string> = {
  contract: "Replay after the platform team fixes the event or the bridge learns the new schema version.",
  unknown_merchant: "Replay once the merchant exists in Dynamics 365.",
  error: "Look at the detail; replay when the cause is fixed.",
};

function Replay({ d }: { d: DeadLetter }) {
  const session = useSession();
  const replay = useReplay();
  const [asking, setAsking] = useState(false);
  if (d.replayed || replay.isSuccess) return <span className={styles.done}>Replayed</span>;
  if (!canOperate(session))
    return <span className={styles.hint}>Replaying needs the Sync.Operate role.</span>;
  const error = replay.error instanceof ApiError ? replay.error : null;
  return (
    <div className={styles.actions}>
      <AnimatePresence mode="wait" initial={false}>
        {asking ? (
          <motion.div
            key="confirm"
            className={styles.confirm}
            initial={{ opacity: 0, x: 8 }}
            animate={{ opacity: 1, x: 0 }}
            exit={{ opacity: 0 }}
            transition={{ duration: m.fast }}
          >
            <span>Put it back on the platform topic?</span>
            <Button
              variant="primary"
              busy={replay.isPending}
              onClick={() => replay.mutate(d, { onSettled: () => setAsking(false) })}
            >
              Replay
            </Button>
            <Button variant="quiet" onClick={() => setAsking(false)}>
              Cancel
            </Button>
          </motion.div>
        ) : (
          <motion.div
            key="ask"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            exit={{ opacity: 0 }}
            transition={{ duration: m.fast }}
          >
            <Button onClick={() => setAsking(true)}>Replay</Button>
          </motion.div>
        )}
      </AnimatePresence>
      {error && (
        <span className={styles.failed} role="alert">
          {error.detail ?? error.title}
        </span>
      )}
    </div>
  );
}

function Item({ d }: { d: DeadLetter }) {
  return (
    <li className={[styles.item, d.replayed ? styles.replayed : ""].join(" ")}>
      <div className={styles.what}>
        <span className={styles.reason}>{DEAD_LETTER_REASON[d.reason] ?? d.reason}</span>
        <span>
          {d.eventType ? eventName(d.eventType) : "Unreadable event"} for{" "}
          <Link to={`/merchants/${d.merchantId}`}>{d.merchantId}</Link>
        </span>
        <span className={styles.detail}>{d.detail}</span>
        <span className={styles.where}>
          {plural(d.deliveries, "delivery", "deliveries")}, from <Id>{d.source}</Id>
          {d.eventId && (
            <>
              , event <Id>{d.eventId}</Id>
            </>
          )}
        </span>
        {!d.replayed && ADVICE[d.reason] && <span className={styles.detail}>{ADVICE[d.reason]}</span>}
      </div>
      <Replay d={d} />
    </li>
  );
}

export function DeadLettersPage() {
  const query = useDeadLetters();
  return (
    <>
      <PageHeader
        title="Dead letters"
        lead="Platform events the bridge could not apply. Throttling and outages are never dead-lettered, they are waited out; what lands here needs a decision."
      />
      <QueryView
        query={query}
        skeleton={<SkeletonRows rows={4} label="Loading dead letters" height="var(--space-8)" />}
        isEmpty={(rows) => rows.length === 0}
        empty={
          <EmptyState title="No dead letters">
            Every platform event so far was applied, or skipped because the CRM already had something newer.
          </EmptyState>
        }
      >
        {(rows) => (
          <ul className={styles.list}>
            {rows.map((d) => (
              <Item key={`${d.partition}-${d.offset}`} d={d} />
            ))}
          </ul>
        )}
      </QueryView>
    </>
  );
}
