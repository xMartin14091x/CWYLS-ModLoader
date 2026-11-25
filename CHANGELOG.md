# Changelog

Release notes per version: [docs/releases](docs/releases/).

## [0.9.3-preview] - 2026-11-25

Hotfix for game 2.0.0.

- Retargeted the 41 patch targets game 2.0.0 moved or changed: pack line pools (reaction talk pools, self-talk, pomodoro voice
  selectors with location and My Pace arguments, small-talk scheduling), Satone's look scale and look reset, the AI state change
  (now returns whether it applied), costume bone attachment and the pomodoro voice text command. Pack lines stay on base-game
  paths and never use My Pace or Bed/Stairs pools. Seamcheck against 2.0.0: 369 targets, 0 missing.
- Pack Maker's DLC gate reads the game's own paid-DLC flag on scenario groups (2.0.0) and the "My Pace" scenario types, and
  hides those groups for everyone (owners included; no ownership read in this release). If the flag cannot be read on any build
  after 1.18.1, every group outside the known-free set read from game 2.0.0 (619 ids) is hidden; a snapshot without novel
  rows or groups cannot be decided and hides everything. The 1.18.1 path (DLC story type) is unchanged.
- The same verdict now also applies in game: pack subtitles and dubs never answer for paid DLC lines and clips. It is computed
  from the game's untouched tables before any pack row is merged, independently of the master-data feature, and counts as
  "protected" until it is computed or when it fails (fail closed).
- Content packs cannot override or use paid DLC content: master-data rows that would replace a protected group, line or clip,
  speak a protected line, join or replace a protected group, or title a group with a protected line are refused (ids logged).
- Also protected: the lines the game picks when Satone moves between the DLC's locations (`goto_desk_`, `goto_bed_`,
  `goto_stairs_`, `location_arrival_`), and the title and subtitle rows of hidden groups. Pack Maker's Play and export checks
  now see lines no novel row speaks.
- On a game build newer than 2.0.0, only voiced lines outside every group are held back until a loader update; unvoiced
  interface rows keep their pack translations.
- Expected game version 2.0.0 (`GameVersionGate.ExpectedGameVersion`); example packs and templates name game 2.0.0;
  loader version 0.9.3.
