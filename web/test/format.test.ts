import { ago, eventName, words } from "../src/shared/lib/format";

describe("format", () => {
  it("reads event types and values like people say them", () => {
    expect(eventName("platform.merchant.kyc_decided")).toBe("KYC decided");
    expect(eventName("crm.merchant.details_changed")).toBe("Details changed");
    expect(words("kyc_pending")).toBe("KYC pending");
    expect(words(null)).toBe("—");
  });

  it("says how long ago", () => {
    const now = Date.parse("2026-10-26T12:00:00Z");
    expect(ago("2026-10-26T11:59:50Z", now)).toBe("10 s ago");
    expect(ago("2026-10-26T11:30:00Z", now)).toBe("30 min ago");
    expect(ago(null, now)).toBe("never");
  });
});
