import { useCallback, useEffect, useRef, useState } from "react";

/**
 * Calls fn now and every ms while enabled - but never while the browser tab is hidden (it catches
 * up the moment the tab is shown again). The one way the dashboard polls, so no page keeps the
 * server busy in a background tab.
 */
export function usePolling(fn, ms, enabled = true) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    if (!enabled) return undefined;
    let t = null;
    const start = () => { clearInterval(t); t = setInterval(() => { if (!document.hidden) ref.current(); }, ms); };
    const onVis = () => { if (document.hidden) clearInterval(t); else { ref.current(); start(); } };
    ref.current();
    start();
    document.addEventListener("visibilitychange", onVis);
    return () => { clearInterval(t); document.removeEventListener("visibilitychange", onVis); };
  }, [ms, enabled]);
}

// One URL scheme for the whole dashboard: #/<section>/<path...>?<view state>
//   #/x/...           Explorer (server, alliance, player, map, compare, rankings)
//   #/chat/<room>     #/ops/<operation>?<args>   #/commands/<command>
//   #/fleet  #/sessions  #/wire  #/data
// The path says WHERE you are and is a history entry (Back works). The query holds view state on
// that page (a filter, a tab) and is replaced in place, so tweaking a filter never floods Back.

export const hashPath = () => (window.location.hash || "#/x").replace(/^#\/?/, "").split("?")[0];
export const hashQuery = () => new URLSearchParams((window.location.hash.split("?")[1]) || "");

/** Rewrites the query part of the current hash without adding history or firing hashchange. */
export function replaceQuery(patch) {
  const q = hashQuery();
  Object.entries(patch).forEach(([k, v]) => (v === undefined || v === null || v === "" ? q.delete(k) : q.set(k, v)));
  const s = q.toString();
  window.history.replaceState(null, "", `#/${hashPath()}${s ? `?${s}` : ""}`);
}

/** State that lives in the URL query: survives reload, is in the link you copy, restores on Back. */
export function useHashParam(key, fallback = "") {
  const [value, setValue] = useState(() => hashQuery().get(key) ?? fallback);
  useEffect(() => {
    const on = () => setValue(hashQuery().get(key) ?? fallback);
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, [key, fallback]);
  const set = useCallback((v) => {
    setValue(v);
    replaceQuery({ [key]: v === fallback ? undefined : v });
  }, [key, fallback]);
  return [value, set];
}

/** The path segments after a section, e.g. #/ops/scan.map -> ["scan.map"]. */
export const sectionArgs = () => hashPath().split("/").slice(1).map(decodeURIComponent);

export function setTitle(...parts) {
  document.title = [...parts.filter(Boolean), "Last War Brain"].join(" · ");
}
