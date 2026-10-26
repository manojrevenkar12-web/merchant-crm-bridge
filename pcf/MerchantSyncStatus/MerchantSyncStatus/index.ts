import { IInputs, IOutputs } from "./generated/ManifestTypes";
import { view } from "./logic";

/** Read-only field control: renders the onboarding path and what happens next. The stage is written by
 * the bridge and the custom API, never by this control. */
export class MerchantSyncStatus implements ComponentFramework.StandardControl<IInputs, IOutputs> {
  private root!: HTMLDivElement;

  public init(_context: ComponentFramework.Context<IInputs>, _notify: () => void, _state: ComponentFramework.Dictionary, container: HTMLDivElement): void {
    this.root = document.createElement("div");
    this.root.className = "mcb-sync";
    container.appendChild(this.root);
  }

  public updateView(context: ComponentFramework.Context<IInputs>): void {
    const p = context.parameters;
    const v = view({
      stage: p.stage.raw ?? null,
      riskBand: p.riskBand?.raw ?? null,
      lastSyncedOn: p.lastSyncedOn?.raw ?? null,
      now: new Date(),
    });
    this.root.dataset.tone = v.tone;
    this.root.replaceChildren();

    const steps = document.createElement("ol");
    steps.className = "mcb-sync__steps";
    steps.setAttribute("aria-label", "Onboarding path");
    for (const s of v.steps) {
      const li = document.createElement("li");
      li.className = `mcb-sync__step mcb-sync__step--${s.state}`;
      li.textContent = s.label;
      if (s.state === "current") li.setAttribute("aria-current", "step");
      steps.appendChild(li);
    }
    const headline = document.createElement("strong");
    headline.className = "mcb-sync__headline";
    headline.textContent = v.headline;
    const detail = document.createElement("p");
    detail.className = "mcb-sync__detail";
    detail.textContent = v.detail;
    this.root.append(steps, headline, detail);
  }

  public getOutputs(): IOutputs {
    return {};
  }

  public destroy(): void {
    this.root.replaceChildren();
  }
}
