// The page's own log (2026-11-09: a blank Studio page left no trace of why). Every API call and key page event is a small
// flat entry, always written with console.debug. While the game's `maker trace on` is set (learned from one probe post at
// boot and from the `maker.trace` event) or the page has `?debug=1`, entries are batch-posted to
// POST /v1/maker/studio/client-log, which writes them to the game log as MAKER-WEB lines. Entries carry codes, routes and
// timings only: never subtitle text, search text or the token. The log never throws into the page.

const MAX_VALUE = 200;

/** Keeps strings (capped), numbers, booleans and null; drops everything else. */
function scalars(data) {
  const out = {};
  for (const [k, v] of Object.entries(data || {})) {
    if (v === null || typeof v === 'boolean') out[k] = v;
    else if (typeof v === 'number') out[k] = Number.isFinite(v) ? Math.round(v * 10) / 10 : null;
    else if (typeof v === 'string') out[k] = v.length > MAX_VALUE ? `${v.slice(0, MAX_VALUE)}…` : v;
  }
  return out;
}

/** A request path without the search text (`q=` holds what the creator typed). */
export const safePath = (path) => String(path || '').replace(/([?&]q=)[^&]*/g, '$1~');

/**
 * @param {{send?:(entries:object[])=>Promise<any>, force?:boolean, timers?:{setTimeout:Function, clearTimeout:Function},
 *   now?:()=>number, flushMs?:number, keep?:number, out?:{debug?:Function}}} options
 */
export function createClientLog({ send = null, force = false, timers = { setTimeout: (...a) => setTimeout(...a), clearTimeout: (...a) => clearTimeout(...a) },
  now = () => Date.now(), flushMs = 2000, keep = 200, out = globalThis.console } = {}) {
  const t0 = now();
  let remote = !!force, queue = [], timer = 0, sending = false;

  function schedule() { if (!timer && send && remote && queue.length) timer = timers.setTimeout(flush, flushMs); }

  /** Logs one entry; returns it. */
  function log(kind, data) {
    const entry = { kind: String(kind), t: Math.round(now() - t0), ...scalars(data) };
    try { out?.debug?.('[maker]', entry.kind, entry); } catch { /* a console that throws is no reason to fail */ }
    if (send) {
      queue.push(entry);
      // Recent history is kept while the trace is off, so turning it on in the game also sends what led up to it.
      if (queue.length > keep) queue.splice(0, queue.length - keep);
      schedule();
    }
    return entry;
  }

  async function flush() {
    timer = 0;
    if (!send || sending || !remote || !queue.length) return;
    const batch = queue.splice(0, 100);
    sending = true;
    try {
      const res = await send(batch);
      if (res && typeof res.trace === 'boolean') remote = !!force || res.trace;
    } catch { /* dropped: the log never breaks the page */ }
    finally { sending = false; schedule(); }
  }

  /** One small post that asks the game whether it traces (the answer carries `trace`); the kept history stays queued. */
  async function probe() {
    if (!send) return remote;
    try {
      const res = await send([{ kind: 'probe', t: Math.round(now() - t0) }]);
      if (res && typeof res.trace === 'boolean') remote = !!force || res.trace;
    } catch { /* an older loader without the route: stay quiet */ }
    schedule();
    return remote;
  }

  /** The game's `maker trace on|off` (the `maker.trace` event). */
  function setRemote(on) { remote = !!force || !!on; schedule(); }

  return { log, flush, probe, setRemote, get remote() { return remote; }, get queued() { return queue.length; } };
}
