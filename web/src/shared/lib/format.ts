const number = new Intl.NumberFormat("en-GB");
const euro = new Intl.NumberFormat("en-GB", { style: "currency", currency: "EUR", maximumFractionDigits: 0 });
const percent = new Intl.NumberFormat("en-GB", {
  style: "percent",
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const dateTime = new Intl.DateTimeFormat("en-GB", {
  day: "numeric",
  month: "short",
  hour: "2-digit",
  minute: "2-digit",
  second: "2-digit",
});
const clock = new Intl.DateTimeFormat("en-GB", { hour: "2-digit", minute: "2-digit" });

export const count = (n: number) => number.format(n);
export const money = (n: number | null) => (n === null ? "—" : euro.format(n));
export const ratio = (n: number | null) => (n === null ? "—" : percent.format(n));
export const at = (iso: string) => dateTime.format(new Date(iso));
export const time = (iso: string) => clock.format(new Date(iso));

/** "12 s ago", "4 min ago", "3 h ago", or the date for anything older than a day. */
export function ago(iso: string | null, now: number = Date.now()): string {
  if (!iso) return "never";
  const seconds = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return `${seconds} s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86_400) return `${Math.round(seconds / 3600)} h ago`;
  return at(iso);
}

/** platform.merchant.kyc_decided → KYC decided; crm.merchant.details_changed → Details changed. */
export function eventName(type: string): string {
  const last = type.split(".").pop() ?? type;
  const words = last.replace(/_/g, " ");
  return words.replace(/^kyc/, "KYC").replace(/^./, (c) => c.toUpperCase());
}

/** snake_case values from the API, readable: kyc_pending → KYC pending, needs_review → Needs review. */
export function words(value: string | null | undefined): string {
  if (!value) return "—";
  return value
    .replace(/_/g, " ")
    .replace(/^kyc/, "KYC")
    .replace(/^./, (c) => c.toUpperCase());
}

export const plural = (n: number, one: string, many: string) => `${count(n)} ${n === 1 ? one : many}`;
