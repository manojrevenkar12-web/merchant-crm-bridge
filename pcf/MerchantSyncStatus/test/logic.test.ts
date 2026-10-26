import { describe, expect, it } from "vitest";

import { Stage, view } from "../MerchantSyncStatus/logic";

const now = new Date("2026-10-26T12:00:00Z");

describe("merchant sync status", () => {
  it("marks the path up to the current stage", () => {
    const v = view({ stage: Stage.approved, riskBand: null, lastSyncedOn: new Date("2026-10-26T11:50:00Z"), now });
    expect(v.steps.map((s) => s.state)).toEqual(["done", "done", "done", "current", "todo", "todo"]);
    expect(v.detail).toContain("10 min ago");
  });

  it("says when the platform has been quiet since onboarding was requested", () => {
    const v = view({ stage: Stage.onboarding, riskBand: null, lastSyncedOn: null, now });
    expect(v.tone).toBe("moving");
    expect(v.detail).toContain("quiet for a while");
  });

  it("flags a live merchant rated high risk", () => {
    const v = view({ stage: Stage.live, riskBand: 771000402, lastSyncedOn: now, now });
    expect([v.tone, v.headline]).toEqual(["stopped", "Live, rated high risk"]);
  });

  it("keeps a restricted merchant at the end of the path, stopped", () => {
    const v = view({ stage: Stage.restricted, riskBand: null, lastSyncedOn: now, now });
    expect(v.steps.at(-1)?.state).toBe("current");
    expect(v.tone).toBe("stopped");
  });

  it("tells sales how to start again after a rejection", () => {
    expect(view({ stage: Stage.rejected, riskBand: null, lastSyncedOn: null, now }).detail).toContain("request onboarding again");
  });
});
