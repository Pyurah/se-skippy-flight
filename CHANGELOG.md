# Changelog

All notable changes to **SkippyFlight** are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project adheres to
[Semantic Versioning](https://semver.org/).

## [0.4.0] - 2026-08-14

### Added
- **Telemetry debug view (`telem`).** A new screen view that surfaces the flight-law
  signals behind a climb/cruise/descent problem: current phase + time-in-phase, ship speed
  vs. the governor's cap at the active waypoint, vertical rate (climb/descent) along
  gravity-up, surface altitude, gravity magnitude (the atmo↔space boundary the flight law
  pivots on) in both m/s² and g, waypoint progress `i/N` with straight-line remaining
  distance, attitude error in degrees, and H2/battery reserves.
- The view is **opt-in by assignment** — it appears only on a surface you point at it, so it
  never crowds the main info screen. Assign it like any other view: name a panel
  `[SHUTTLE:telem]`, or add `0 = telem` (any surface index) under a cockpit's
  `[shuttle-screens]` Custom Data section.
- `lastAlignErr` is latched in `AlignTo` and shown on the telem view, so an attitude stall
  (the class of bug behind the historical 45-second undock hang) is visible live on a panel.

### Notes
- Stripped deploy size: 83,473 chars (16,527 under the 100,000 PB limit; +2,095 vs 0.3.0).
  Braces balanced (407/407). Version constant bumped to 0.4.0.

## [0.3.0] - 2026-08-14

### Added
- **Multiple named routes.** Routes are no longer limited to one — each is stored in its own
  `[route.<name>]` section of the PB's Custom Data, and a `[routes] active=<name>` pointer tracks
  the one currently loaded. Record a named route with `RECORD HOME <name>` (the name carries
  through to `RECORD DEST`); the name is optional and defaults to the active route, or `Main`.
- **Routes menu page.** A new **Routes** page (Record ▸ Routes) lists every saved route with the
  active one marked `*`; APPLY loads the highlighted route. Switching is blocked while operating
  (a live leg would otherwise have its target swapped mid-flight — STOP first).
- **`ROUTE <name>`** command — switch the active route from a cockpit toolbar button / PB argument
  without opening the menu. `ROUTE` with no name reports the active route and saved count.
- **`DELROUTE <name>`** command — delete a saved route; if it was active, the ship falls back to
  another saved route (or none). The menu's **Clear Route** now deletes the active route the same
  way (previously it wiped the single route).
- Route name shown in the status header and Trip view (`Route <name> <n>wp`).

### Changed
- Route persistence moved from the single `[route]` section to per-route `[route.<name>]` sections.
  Route names are sanitised to `[A-Za-z0-9_-]`, max 16 chars, so they are safe as section suffixes.

### Migration
- An existing single `[route]` section is copied once to `[route.Main]` on first load and then
  removed, so a ship already carrying a route keeps it (as "Main") with no re-recording. If named
  routes already exist, a stale `[route]` is simply dropped.

### Notes
- Stripped deploy size: 81,378 chars (18,622 under the 100,000 PB limit; +5,585 vs 0.2.2). Braces
  balanced (404/404). Version constant bumped to 0.3.0.

## [0.2.2] - 2026-08-14

### Fixed
- Undock no longer pitches the nose up hard before leveling off for cruise. The v0.2.1 fix aimed
  the nose straight at the first cruise waypoint; when that waypoint climbs (destination up-and-over
  a hill) the ship pitched up steeply to point at it, then cruise's level-flight law dropped the
  nose back to horizontal the instant it engaged — a visible pitch-up/level-off flip. Undock now
  pre-aims the *same* attitude cruise will hold: in level flight (in gravity, up-thrust dominant)
  it faces the horizontal heading with up away from gravity, so the handoff is seamless. Nose-
  forward flight (space, or up-thrust-poor craft) keeps the direct-facing behavior from 0.2.1,
  including the orthogonalized up that prevents the 45s undock stall.

## [0.2.1] - 2026-08-14

### Fixed
- Undock no longer stalls to the 45-second approach timeout before starting a leg. When rotating
  to face the first cruise waypoint, the target "up" is now kept perpendicular to the (possibly
  steeply pitched) facing. Previously it paired that pitched forward with the near-vertical
  recorded dock up — a pose the gyros cannot satisfy — so `AlignTo` never fell under `ALIGN_TOL`
  and the undock only advanced when the watchdog fired. Most visible on a sparse route whose first
  cruise target is the far, low approach point: the ship sat on the pad with its nose aimed at the
  ground for ~45s before departing. Behavior on well-recorded routes (near-level first target) is
  unchanged. Inherited from Skippy-Shuttle; not introduced by the phase refactor.

## [0.2.0] - 2026-08-14

Phase-object extraction. The direction-baked 11-value `State` enum and its hand-maintained
dispatch/label switches are replaced by a direction-free phase-object base controller. Behavior
is byte-for-byte equivalent to Skippy-Shuttle v0.15.0 — no new phases, no scenario logic yet.
This is Slice a of the roadmap: it proves the abstraction and the character budget.

### Added
- `enum PhaseId` (direction-free: Idle, Recording, Loading, Undock, Cruise, Approach, Unloading,
  Faulted) with the next slices' phases reserved in a comment (DepartStaging, Climb, Descent,
  Holding, Taxi).
- `struct Leg` (`Outbound`) — the traversal-direction axis that replaces the duplicated
  `*ToDest`/`*ToHome` states and the `bool toDest`/`fromHome` params.
- `abstract class FlightPhase` + one nested phase object per `PhaseId`, dispatched through a
  `Dictionary<PhaseId, FlightPhase>` built once in `Program()`. Each phase exposes `IsFlightControl`
  (replaces `IsFlightControlState()`) and `Label` (replaces the `ShipState`/`PrettyState` switches),
  and dispatches to the existing `Tick*` body (no flight logic copied).
- `SwitchPhase` transition hook (Exit/Enter); `Loading`/`Unloading` set the leg direction on Enter.
- `LegacyStateName`/`ApplyLegacyState` — map (phase, outbound) ↔ the pre-0.2.0 `State` names so the
  IGC report wire stays unchanged (a Skippy-Shuttle base board still decodes it) and an existing
  ship's `[state]` Custom Data resumes on the correct phase after an in-place script swap.

### Changed
- `Main` collapses from an 11-case `switch (state)` to a single `phases[phase].Tick(this)` dispatch;
  the 60/6 Hz loop-rate selection reads `phases[phase].IsFlightControl`.
- `[state]` persistence now stores `phase` + `outbound` (reads the legacy `state` key as a fallback).
- The flight ticks (`TickUndock`/`TickCruise`/`TickApproach`/`OnDocked`) drop their direction params
  and read `leg.Outbound`; the `Faulted` inline case becomes `TickFaulted()`.

### Notes
- Stripped deploy size: 75,112 chars (24,888 under the 100,000 PB limit; +4,333 vs the 0.1.0 copy).
- Version constant bumped to 0.2.0.

## [0.1.0] - 2026-08-14

Baseline. SkippyFlight forks from `Skippy-Shuttle` v0.15.0 as an exact, unmodified copy so the
phase-object rewrite has a byte-for-byte behavioral reference. `Skippy-Shuttle` stays frozen and
in active use.

### Added
- New project `Skippy-Flight/` with `SkippyFlight.cs` (faithful copy of `SkippyShuttle.cs`),
  `tools/build-min.py` (filenames retargeted to `SkippyFlight`), `README.md`, and this changelog.
- `roadmap.md` — the phase-based flight controller design: three-axis model (phase / leg /
  scenario), phase-object base controller, staging/holding/taxi phases, scenario auto-detection,
  and the separate `SkippyTower.cs` traffic-control plan.

### Notes
- Stripped deploy size at baseline: 70,780 chars (29,220 under the 100,000 PB limit).
- Behavior identical to Skippy-Shuttle v0.15.0. The phase-object extraction lands in 0.2.0.
