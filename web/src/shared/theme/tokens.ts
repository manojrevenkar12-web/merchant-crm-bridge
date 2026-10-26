/** Constants the components share: motion, polling, sizes, and the vocabulary of the sync. */

/** Motion for framer-motion, in seconds (mirrors --duration-* in tokens.css). */
export const motion = {
  fast: 0.12,
  base: 0.2,
  slow: 0.42,
  draw: 0.9,
  ease: [0.2, 0.7, 0.2, 1] as [number, number, number, number],
  stagger: 0.035,
};

/** How often views refresh while open, in milliseconds. Operations watch a live system. */
export const refresh = {
  overview: 5_000,
  list: 10_000,
  detail: 5_000,
};

/** Chart geometry in SVG user units (charts scale with their container). */
export const chart = {
  throughputHeight: 72,
  barWidth: 8,
  barGap: 3,
  minBar: 1,
  laneHeight: 40,
};

/** When the overview calls the bridge "in step": nothing waiting longer than these. */
export const healthy = {
  maxConsumerLag: 20,
  maxOutboxPending: 10,
};

export const limits = {
  merchantsPage: 100,
  searchMaxLength: 100,
  searchDebounceMs: 250,
  timelineInitial: 12,
  throughputMinutes: 30,
};

/** Lifecycle stages in the order a merchant moves through them; labels match the CRM's choice labels. */
export const STAGES = [
  "prospect",
  "onboarding",
  "kyc_pending",
  "approved",
  "boarded",
  "live",
  "restricted",
  "rejected",
  "terminated",
] as const;
export type Stage = (typeof STAGES)[number];

export const STAGE_LABEL: Record<Stage, string> = {
  prospect: "Prospect",
  onboarding: "Onboarding requested",
  kyc_pending: "KYC: information needed",
  approved: "Approved",
  boarded: "Boarded",
  live: "Live",
  restricted: "Restricted",
  rejected: "Rejected",
  terminated: "Terminated",
};

/** How a stage reads at a glance: still moving, working, or stopped. */
export const STAGE_TONE: Record<Stage, "moving" | "working" | "stopped" | "quiet"> = {
  prospect: "quiet",
  onboarding: "moving",
  kyc_pending: "moving",
  approved: "moving",
  boarded: "moving",
  live: "working",
  restricted: "stopped",
  rejected: "stopped",
  terminated: "quiet",
};

export const DEAD_LETTER_REASON: Record<string, string> = {
  contract: "Breaks the event contract",
  unknown_merchant: "Merchant not in the CRM",
  error: "Failed repeatedly",
  unknown: "Unknown reason",
};

export const ROLES = { read: "Sync.Read", operate: "Sync.Operate" } as const;

/** Choice labels the API sends as names (the CRM's labels, dataverse/model.json). */
export const LEGAL_FORM_LABEL: Record<string, string> = {
  gmbh: "GmbH",
  ug: "UG (haftungsbeschränkt)",
  ag: "AG",
  ek: "e.K.",
  gbr: "GbR",
  sole_trader: "Sole trader",
  other: "Other",
};
