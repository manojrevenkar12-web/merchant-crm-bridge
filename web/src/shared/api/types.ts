/** The operations API (contracts/openapi.json). */
import type { Stage } from "../theme/tokens";

export interface StageCount {
  stage: Stage;
  count: number;
}

export interface OutcomeCount {
  outcome: "applied" | "stale" | "rejected" | "unknown";
  count: number;
}

export interface MinuteCount {
  minute: string;
  applied: number;
  stale: number;
  rejected: number;
}

export interface Overview {
  merchants: number;
  stages: StageCount[];
  outboxPending: number;
  recentOutcomes: OutcomeCount[];
  throughput: MinuteCount[];
  deadLetters: number;
  consumerLag: number;
  driftNeedsReview: number;
  lastReconcileAt: string | null;
  lastReconcileHealed: number | null;
  lastRelayAt: string | null;
}

export interface MerchantSummary {
  merchantId: string;
  legalName: string | null;
  stage: Stage;
  pricingPlan: string | null;
  riskBand: string | null;
  platformStatus: string | null;
  lastSyncedOn: string | null;
  crmVersion: number;
  platformVersion: number;
}

export interface AspectView {
  aspect: "KYC" | "Status" | "Risk" | "Platform account";
  crm: string | null;
  platform: string | null;
  crmVersion: number;
  platformVersion: number;
  inSync: boolean;
}

export interface TimelineEntry {
  at: string;
  direction: "crm-to-platform" | "platform-to-crm";
  eventType: string;
  sequence: number;
  status: string;
  detail: string | null;
  eventId: string;
}

export interface MerchantDetail {
  summary: MerchantSummary;
  contactEmail: string | null;
  legalForm: string | null;
  country: string | null;
  ibanMasked: string | null;
  mcc: string | null;
  platformAccountId: string | null;
  kycDecision: string | null;
  kycReason: string | null;
  chargebackRatio: number | null;
  volumeMonth: string | null;
  lastMonthVolume: number | null;
  lastMonthTransactions: number | null;
  terminals: string[];
  aspects: AspectView[];
  platformReachable: boolean;
  timeline: TimelineEntry[];
}

export interface DeadLetter {
  partition: number;
  offset: number;
  merchantId: string;
  eventId: string | null;
  eventType: string | null;
  reason: string;
  detail: string;
  source: string;
  deliveries: number;
  replayed: boolean;
}

export interface DriftItem {
  merchantId: string;
  field: string;
  crmValue: string | null;
  platformValue: string | null;
  resolution: "needs_review" | "healed";
  detectedOn: string;
}

export interface ReconcileReport {
  checked: number;
  healed: number;
  needsReview: number;
  missingOnPlatform: number;
  leaseHeld: boolean;
}

/** RFC 9457 problem details. */
export interface Problem {
  title: string;
  status: number;
  detail?: string;
}
