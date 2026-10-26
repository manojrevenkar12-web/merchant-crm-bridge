/**
 * What the account form shows above the lifecycle stage: the path from prospect to live, where the
 * merchant is on it, and one sentence on what happens next. Values are the choice values of
 * dataverse/model.json (publisher option prefix 77100).
 */
export const Stage = {
  prospect: 771000000,
  onboarding: 771000001,
  kycPending: 771000002,
  approved: 771000003,
  rejected: 771000004,
  boarded: 771000005,
  live: 771000006,
  restricted: 771000007,
  terminated: 771000008,
} as const;

const Risk = { low: 771000400, medium: 771000401, high: 771000402 } as const;

/** Hours without a platform answer after onboarding was requested before the form says so. */
export const QUIET_HOURS = 24;

export const PATH = [
  { stage: Stage.prospect, label: "Prospect" },
  { stage: Stage.onboarding, label: "Requested" },
  { stage: Stage.kycPending, label: "KYC" },
  { stage: Stage.approved, label: "Approved" },
  { stage: Stage.boarded, label: "Boarded" },
  { stage: Stage.live, label: "Live" },
] as const;

export type Tone = "neutral" | "moving" | "working" | "stopped";

export interface View {
  steps: { label: string; state: "done" | "current" | "todo" }[];
  tone: Tone;
  headline: string;
  detail: string;
}

export interface Input {
  stage: number | null;
  riskBand: number | null;
  lastSyncedOn: Date | null;
  now: Date;
}

function since(d: Date | null, now: Date): string {
  if (!d) return "no update from the platform yet";
  const minutes = Math.max(0, Math.round((now.getTime() - d.getTime()) / 60000));
  if (minutes < 1) return "last update from the platform just now";
  if (minutes < 60) return `last update from the platform ${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `last update from the platform ${hours} h ago`;
  return `last update from the platform ${Math.round(hours / 24)} days ago`;
}

export function view({ stage, riskBand, lastSyncedOn, now }: Input): View {
  const s = stage ?? Stage.prospect;
  const index = PATH.findIndex((p) => p.stage === s);
  const reached = index >= 0 ? index : s === Stage.restricted ? PATH.length - 1 : -1;
  const steps = PATH.map((p, i) => ({
    label: p.label,
    state: (i < reached ? "done" : i === reached ? "current" : "todo") as "done" | "current" | "todo",
  }));
  const update = since(lastSyncedOn, now);
  const quiet =
    lastSyncedOn === null || (now.getTime() - lastSyncedOn.getTime()) / 3_600_000 > QUIET_HOURS ? " It has been quiet for a while; ask the onboarding team." : "";

  switch (s) {
    case Stage.prospect:
      return { steps, tone: "neutral", headline: "Not handed to the platform yet", detail: "Complete the details and choose Request onboarding." };
    case Stage.onboarding:
      return { steps, tone: "moving", headline: "Waiting for the platform", detail: `Onboarding was requested; KYC starts next (${update}).${quiet}` };
    case Stage.kycPending:
      return { steps, tone: "moving", headline: "KYC needs information", detail: `The platform asked for more documents; the reason is on the KYC tab (${update}).` };
    case Stage.approved:
      return { steps, tone: "moving", headline: "Approved, being boarded", detail: `The platform account is being set up (${update}).` };
    case Stage.boarded:
      return { steps, tone: "moving", headline: "Boarded, no terminal live yet", detail: `The merchant goes live with its first terminal (${update}).` };
    case Stage.live:
      return {
        steps,
        tone: riskBand === Risk.high ? "stopped" : "working",
        headline: riskBand === Risk.high ? "Live, rated high risk" : "Live",
        detail: riskBand === Risk.high ? `Review before agreeing to new terms (${update}).` : `Taking payments (${update}).`,
      };
    case Stage.restricted:
      return { steps, tone: "stopped", headline: "Restricted by the platform", detail: `Payments are limited; risk and status come from the platform (${update}).` };
    case Stage.rejected:
      return { steps, tone: "stopped", headline: "Rejected in KYC", detail: "Correct the details and request onboarding again to start a new review." };
    case Stage.terminated:
      return { steps, tone: "neutral", headline: "Terminated", detail: "The merchant no longer takes payments with us." };
    default:
      return { steps, tone: "neutral", headline: "Unknown stage", detail: update };
  }
}
