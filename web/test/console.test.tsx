import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { attention, headline } from "../src/features/overview/OverviewPage";
import { clone, problem, renderAt, stubApi } from "./harness";
import { deadLetter, detail, drift, overview, summary } from "./fixtures";

const base = { "GET /v1/overview": overview };

describe("overview", () => {
  it("says the bridge is in step when nothing waits", async () => {
    stubApi(base);
    await renderAt("/");
    expect(
      await screen.findByRole("heading", { level: 1, name: "CRM and platform are in step." }),
    ).toBeInTheDocument();
    expect(screen.getByRole("img", { name: /Live 29/ })).toBeInTheDocument();
  });

  it("names what needs a person, with the right verb", () => {
    const o = { ...overview, deadLetters: 1 };
    expect(headline(attention(o))).toBe("1 dead letter needs a look.");
    expect(headline(attention({ ...o, deadLetters: 2, driftNeedsReview: 1 }))).toBe(
      "2 dead letters and 1 drift finding need a look.",
    );
    expect(attention({ ...overview, consumerLag: 500 })).toEqual(["500 platform events waiting"]);
  });

  it("shows a skeleton while loading, then the error and a retry that works", async () => {
    let fail = true;
    stubApi({
      "GET /v1/overview": () =>
        fail ? problem(503, "The bridge API is unavailable", "Dataverse is throttling.") : overview,
    });
    await renderAt("/");
    expect(screen.getByRole("status", { name: /Loading the overview|Signing in/ })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "The bridge API is unavailable" })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(
      await screen.findByRole("heading", { level: 1, name: "CRM and platform are in step." }),
    ).toBeInTheDocument();
  });

  it("invites the first onboarding when there are no merchants", async () => {
    stubApi({ "GET /v1/overview": { ...overview, merchants: 0, stages: [] } });
    await renderAt("/");
    expect(await screen.findByRole("heading", { name: "No merchants yet." })).toBeInTheDocument();
  });

  it("sends the token with every call", async () => {
    const calls = stubApi(base);
    await renderAt("/");
    await screen.findByRole("heading", { level: 1 });
    expect(calls.filter((c) => c.path.startsWith("/v1/")).every((c) => c.auth?.startsWith("Bearer "))).toBe(
      true,
    );
  });
});

describe("merchants", () => {
  it("lists merchants and filters by stage through the API", async () => {
    const calls = stubApi({ ...base, "GET /v1/merchants": [summary] });
    await renderAt("/merchants?stage=live");
    expect((await screen.findAllByText("Kaffeerösterei Heidelberg GmbH")).length).toBeGreaterThan(0);
    expect(calls.some((c) => c.path.includes("stage=live"))).toBe(true);
  });

  it("offers to clear the filters when nothing matches", async () => {
    stubApi({ ...base, "GET /v1/merchants": [] });
    await renderAt("/merchants?stage=terminated");
    expect(await screen.findByRole("heading", { name: "No merchant matches" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Clear the filters" })).toBeInTheDocument();
  });
});

describe("merchant", () => {
  it("shows both sides and explains each disagreement", async () => {
    stubApi({ ...base, "GET /v1/merchants/M-004211": detail });
    await renderAt("/merchants/M-004211");
    expect(
      await screen.findByRole("heading", { name: "CRM and platform differ on 2 aspects" }),
    ).toBeInTheDocument();
    expect(screen.getByText(/The CRM is 2 versions behind/)).toBeInTheDocument();
    expect(screen.getByText(/Same version, different value/)).toBeInTheDocument();
    expect(screen.getByText("GmbH")).toBeInTheDocument();
    expect(screen.getByText("DE89 •••• 3000")).toBeInTheDocument();
    const events = screen.getByRole("list", { name: "Events, newest first" });
    expect(within(events).getByText("Sent by the CRM", { exact: false })).toBeInTheDocument();
  });

  it("says when the platform's view is unavailable", async () => {
    stubApi({ ...base, "GET /v1/merchants/M-004211": { ...clone(detail), platformReachable: false } });
    await renderAt("/merchants/M-004211");
    expect(await screen.findByText(/could not be loaded just now/)).toBeInTheDocument();
  });

  it("explains a merchant that does not exist instead of offering a retry", async () => {
    stubApi({ ...base, "GET /v1/merchants/M-999999": problem(404, "Merchant not found") });
    await renderAt("/merchants/M-999999");
    expect(await screen.findByRole("heading", { name: "No merchant M-999999" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Try again" })).not.toBeInTheDocument();
  });
});

/** Asks to replay, waits for the question, and confirms it. */
async function confirmReplay() {
  await userEvent.click(await screen.findByRole("button", { name: "Replay" }));
  const question = await screen.findByText("Put it back on the platform topic?");
  await userEvent.click(within(question.parentElement!).getByRole("button", { name: "Replay" }));
}

describe("dead letters", () => {
  it("replays after confirming and shows the result", async () => {
    const calls = stubApi({
      ...base,
      "GET /v1/dead-letters": [deadLetter],
      "POST /v1/dead-letters/2/69/replay": new Response(null, { status: 202 }),
    });
    await renderAt("/dead-letters");
    await confirmReplay();
    expect(await screen.findByText("Replayed")).toBeInTheDocument();
    expect(calls.filter((c) => c.method === "POST" && c.path.endsWith("/replay"))).toHaveLength(1);
  });

  it("shows why a replay was refused", async () => {
    stubApi({
      ...base,
      "GET /v1/dead-letters": [deadLetter],
      "POST /v1/dead-letters/2/69/replay": problem(
        409,
        "Already replayed",
        "This dead letter was replayed before.",
      ),
    });
    await renderAt("/dead-letters");
    await confirmReplay();
    expect(await screen.findByRole("alert")).toHaveTextContent("This dead letter was replayed before.");
  });

  it("tells a reader that replaying needs the operate role", async () => {
    stubApi({ ...base, "GET /v1/dead-letters": [deadLetter] }, ["Sync.Read"]);
    await renderAt("/dead-letters");
    expect(await screen.findByText("Replaying needs the Sync.Operate role.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Replay" })).not.toBeInTheDocument();
  });

  it("is calm when there is nothing to decide", async () => {
    stubApi({ ...base, "GET /v1/dead-letters": [] });
    await renderAt("/dead-letters");
    expect(await screen.findByRole("heading", { name: "No dead letters" })).toBeInTheDocument();
  });
});

describe("drift", () => {
  it("lists findings and reconciles on demand", async () => {
    stubApi({
      ...base,
      "GET /v1/drift": [drift],
      "POST /v1/reconcile": { checked: 40, healed: 2, needsReview: 1, missingOnPlatform: 0, leaseHeld: true },
    });
    await renderAt("/drift");
    expect(await screen.findByText("Needs review")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Reconcile now" }));
    await waitFor(() => expect(screen.getByText(/Checked 40 merchants: 2 healed/)).toBeInTheDocument());
  });

  it("hides reconciliation from readers", async () => {
    stubApi({ ...base, "GET /v1/drift": [] }, ["Sync.Read"]);
    await renderAt("/drift");
    expect(await screen.findByRole("heading", { name: "No drift" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Reconcile now" })).not.toBeInTheDocument();
  });
});
