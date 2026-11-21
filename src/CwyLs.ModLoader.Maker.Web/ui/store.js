// One plain-object store. Zones subscribe through `mountZones` (main.js); nothing here knows the DOM.
export function createStore(initial) {
  let state = initial;
  const listeners = new Set();
  return {
    get: () => state,
    /** Shallow-merges a patch (or the result of fn(state)) and tells the listeners. */
    set(patch) {
      const next = typeof patch === 'function' ? patch(state) : patch;
      state = { ...state, ...next };
      for (const fn of [...listeners]) fn(state);
    },
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); }
  };
}
