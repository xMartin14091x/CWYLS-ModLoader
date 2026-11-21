// Which page opens: Studio v2 or the v1 workspace. Pure, so the switch can be tested without a browser.
//   flipped = false (today): v1 is the default; `?ui=2` opens v2.
//   flipped = true  (after the gate): v2 is the default; `?ui=1` keeps v1 reachable.
// `?ui=2` always wins over the default, `?ui=1` always means v1, anything else follows the default.
export function chooseUi(search, flipped) {
  const ui = new URLSearchParams(search).get('ui');
  if (ui === '2') return 2;
  if (ui === '1') return 1;
  return flipped ? 2 : 1;
}
