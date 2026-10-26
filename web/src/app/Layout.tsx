import { motion } from "framer-motion";
import type { ReactNode } from "react";
import { NavLink, Outlet } from "react-router-dom";

import { useOverview } from "../features/overview/api";
import { canOperate } from "../shared/auth/auth";
import { useSession } from "../shared/auth/SessionContext";
import { motion as m } from "../shared/theme/tokens";
import styles from "./Layout.module.css";

const icon = {
  overview: <path d="M4 12h5l2-6 2 12 2-6h5" />,
  merchants: <path d="M4 9h16M6 9v10h12V9M8 5h8l2 4H6l2-4Z" />,
  dead: (
    <path d="M12 8v5m0 3h.01M10.3 3.9 2.6 17a2 2 0 0 0 1.7 3h15.4a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" />
  ),
  drift: <path d="M7 4v16M17 4v16M7 8h4m2 8h4M11 8c2 0 2 8 2 8" />,
};

function Item({ to, label, glyph, badge }: { to: string; label: string; glyph: ReactNode; badge?: number }) {
  return (
    <NavLink
      to={to}
      end={to === "/"}
      className={({ isActive }) => [styles.link, isActive && styles.active].filter(Boolean).join(" ")}
    >
      {({ isActive }) => (
        <>
          {isActive && (
            <motion.span
              layoutId="nav-indicator"
              className={styles.indicator}
              transition={{ duration: m.base, ease: m.ease }}
            />
          )}
          <svg
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            strokeWidth="1.8"
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden
          >
            {glyph}
          </svg>
          <span>{label}</span>
          {!!badge && (
            <span className={styles.badge} aria-label={`${badge} waiting`}>
              {badge}
            </span>
          )}
        </>
      )}
    </NavLink>
  );
}

export function Layout() {
  const session = useSession();
  const overview = useOverview();
  return (
    <div className={styles.shell}>
      <header className={styles.top}>
        <NavLink to="/" className={styles.brand}>
          <svg className={styles.mark} viewBox="0 0 32 32" aria-hidden>
            <rect width="32" height="32" rx="7" fill="var(--ink)" />
            <path d="M8 11h9" stroke="var(--crm)" strokeWidth="3" strokeLinecap="round" />
            <path d="M15 21h9" stroke="var(--platform)" strokeWidth="3" strokeLinecap="round" />
            <path d="M16 8v16" stroke="var(--paper)" strokeWidth="2" strokeDasharray="2 2" />
          </svg>
          Merchant sync
        </NavLink>
        <div className={styles.who}>
          {session.mode === "local" && <span className={styles.env}>Demo environment</span>}
          <span className={styles.name}>
            {session.name}
            <span className="visually-hidden">, </span>
            {canOperate(session) ? "" : " (read only)"}
          </span>
        </div>
      </header>
      <nav className={styles.nav} aria-label="Sections">
        <Item to="/" label="Overview" glyph={icon.overview} />
        <Item to="/merchants" label="Merchants" glyph={icon.merchants} />
        <Item to="/dead-letters" label="Dead letters" glyph={icon.dead} badge={overview.data?.deadLetters} />
        <Item to="/drift" label="Drift" glyph={icon.drift} badge={overview.data?.driftNeedsReview} />
      </nav>
      <main className={styles.main} id="main">
        <Outlet />
      </main>
    </div>
  );
}
