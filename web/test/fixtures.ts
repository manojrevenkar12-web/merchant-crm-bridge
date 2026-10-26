import type {
  DeadLetter,
  DriftItem,
  MerchantDetail,
  MerchantSummary,
  Overview,
} from "../src/shared/api/types";

const minute = (i: number) => new Date(Date.UTC(2026, 9, 26, 14, i)).toISOString();

export const overview: Overview = {
  merchants: 42,
  stages: [
    { stage: "prospect", count: 4 },
    { stage: "live", count: 29 },
    { stage: "restricted", count: 4 },
    { stage: "rejected", count: 3 },
    { stage: "terminated", count: 2 },
  ],
  outboxPending: 0,
  recentOutcomes: [
    { outcome: "applied", count: 284 },
    { outcome: "stale", count: 4 },
  ],
  throughput: Array.from({ length: 30 }, (_, i) => ({
    minute: minute(i),
    applied: i % 7,
    stale: i % 11 === 0 ? 1 : 0,
    rejected: 0,
  })),
  deadLetters: 0,
  consumerLag: 0,
  driftNeedsReview: 0,
  lastReconcileAt: minute(28),
  lastReconcileHealed: 3,
  lastRelayAt: minute(29),
};

export const summary: MerchantSummary = {
  merchantId: "M-004211",
  legalName: "Kaffeerösterei Heidelberg GmbH",
  stage: "live",
  pricingPlan: "growth",
  riskBand: "low",
  platformStatus: "live",
  lastSyncedOn: minute(20),
  crmVersion: 3,
  platformVersion: 8,
};

export const detail: MerchantDetail = {
  summary,
  contactEmail: "kasse@kaffee.example",
  legalForm: "gmbh",
  country: "DE",
  ibanMasked: "DE89 •••• 3000",
  mcc: "5812",
  platformAccountId: "PA-004211",
  kycDecision: "approved",
  kycReason: null,
  chargebackRatio: 0.0027,
  volumeMonth: "2026-09",
  lastMonthVolume: 33622.12,
  lastMonthTransactions: 1344,
  terminals: ["T-004211-1"],
  platformReachable: true,
  aspects: [
    { aspect: "KYC", crm: "approved", platform: "approved", crmVersion: 2, platformVersion: 2, inSync: true },
    { aspect: "Status", crm: "boarded", platform: "live", crmVersion: 4, platformVersion: 6, inSync: false },
    { aspect: "Risk", crm: "high", platform: "low", crmVersion: 7, platformVersion: 7, inSync: false },
    {
      aspect: "Platform account",
      crm: "PA-004211",
      platform: "PA-004211",
      crmVersion: 0,
      platformVersion: 0,
      inSync: true,
    },
  ],
  timeline: [
    {
      at: minute(20),
      direction: "platform-to-crm",
      eventType: "platform.merchant.status_changed",
      sequence: 6,
      status: "applied",
      detail: "status",
      eventId: "e-6",
    },
    {
      at: minute(10),
      direction: "crm-to-platform",
      eventType: "crm.merchant.details_changed",
      sequence: 2,
      status: "sent",
      detail: null,
      eventId: "e-c2",
    },
    {
      at: minute(5),
      direction: "platform-to-crm",
      eventType: "platform.merchant.kyc_decided",
      sequence: 2,
      status: "stale",
      detail: "kyc",
      eventId: "e-2",
    },
  ],
};

export const deadLetter: DeadLetter = {
  partition: 2,
  offset: 69,
  merchantId: "M-004128",
  eventId: "8f43de70",
  eventType: "platform.merchant.risk_changed",
  reason: "contract",
  detail: "/chargebackRatio: 3.7 should be at most 1",
  source: "platform.merchant.events/2/69",
  deliveries: 1,
  replayed: false,
};

export const drift: DriftItem = {
  merchantId: "M-004211",
  field: "riskBand",
  crmValue: "high",
  platformValue: "low",
  resolution: "needs_review",
  detectedOn: minute(15),
};
