/** Shared display formatters used across the SPA. */

/**
 * Culture-aware number formatting with grouping separators, using the user's locale:
 * 1000 -> "1,000" (en-US/GB), "1.000" (es-ES). Values under 1000 are unchanged. Any
 * fractional part is localised too (e.g. decimal comma in es-ES). Null/undefined/NaN -> "0".
 */
export function formatNumber(value: number | null | undefined, options?: Intl.NumberFormatOptions): string {
  if (value == null || Number.isNaN(value)) return "0";
  return value.toLocaleString(undefined, options);
}

export function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  const i = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
  const value = bytes / Math.pow(1024, i);
  return `${formatNumber(value, { maximumFractionDigits: 2 })} ${units[i]}`;
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  return new Intl.DateTimeFormat(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

export function formatRelative(value: string | null | undefined): string {
  if (!value) return "—";
  const then = new Date(value).getTime();
  if (Number.isNaN(then)) return "—";
  const secondsAgo = Math.round((Date.now() - then) / 1000);
  if (secondsAgo < 60) return "just now";
  const minutes = Math.round(secondsAgo / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  return formatDateTime(value);
}

/** Last path segment of a server-relative URL / blob key, for compact display. */
export function fileName(path: string | null | undefined): string {
  if (!path) return "";
  const trimmed = path.replace(/\/+$/, "");
  const idx = trimmed.lastIndexOf("/");
  return idx >= 0 ? trimmed.slice(idx + 1) : trimmed;
}

/**
 * Compact "time from now" for a future instant, e.g. "in 45s", "in 12m", "in 3h",
 * or "now" when it's due/overdue. Returns "—" for missing/invalid values.
 */
export function formatCountdown(value: string | null | undefined): string {
  if (!value) return "—";
  const target = new Date(value).getTime();
  if (Number.isNaN(target)) return "—";
  const seconds = Math.round((target - Date.now()) / 1000);
  if (seconds <= 0) return "now";
  if (seconds < 60) return `in ${seconds}s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `in ${minutes}m`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `in ${hours}h`;
  const days = Math.round(hours / 24);
  return `in ${days}d`;
}

/**
 * ETA label combining the clock time and a countdown, e.g. "~14:32 (in 12m)".
 * Returns "—" for missing/invalid values.
 */
export function formatEta(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "—";
  const clock = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit" }).format(date);
  return `~${clock} (${formatCountdown(value)})`;
}
