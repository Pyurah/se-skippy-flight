# Changelog

All notable changes to **SkippyFlight** are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project adheres to
[Semantic Versioning](https://semver.org/).

## [0.6.0] - 2026-08-14

Block-tag and config-section rename — the script no longer calls itself "shuttle" on the
blocks it touches. The default grid tags and the Custom Data sections it reads are now keyed
to **`SF`** (for SkippyFlight). The IGC channel keyword (`SkippyShuttleNet`) and the flying
`role = shuttle` value are unchanged — those are network/role identifiers, not device tags.

### Changed
- **Default block tags renamed** to the `[SF]` family:
  - `lcdTag` `[SHUTTLE]` → `[SF]` (and the derived view tags `[SF:trip]`, `[SF:menu:1.2]`,
    `[SF:status:1.4:6]`, `[SF:telem]` follow automatically — they are `lcdTag` minus its `]`).
  - `loadTag` `[SHUTTLE:LOAD]` → `[SF:LOAD]`, `unloadTag` `[SHUTTLE:UNLOAD]` → `[SF:UNLOAD]`.
  - `cameraTag` `[SHUTTLE:CAM]` → `[SF:CAM]`.
- **Config section renamed** `[shuttle]` → `[sf]` in the PB's Custom Data.
- **Cockpit screen-map section renamed** `[shuttle-screens]` → `[sf-screens]`.

### Migration
- **Config is migrated automatically and losslessly.** On first load after the update,
  `LoadConfig` copies every key from an existing `[shuttle]` section into `[sf]` and drops the
  old section (`MigrateLegacyConfig`, same one-time pattern as the `[route]` → `[route.Main]`
  migration). No tuning, trigger, or tag value is lost.
- **The legacy `[shuttle-screens]` cockpit section is still honoured.** Discovery reads
  `[sf-screens]` first and falls back to `[shuttle-screens]`, so an existing multi-surface
  cockpit map keeps working with no edit.
- **Existing block tags keep working.** Because the tags are stored *values* (not hardcoded),
  a ship already carrying `lcdTag = [SHUTTLE]` in its Custom Data keeps that value through the
  migration and its `[SHUTTLE]`-named panels/sorters/cameras stay matched. The `[SF]` defaults
  apply to **fresh installs**. To adopt the new tags on an existing grid, edit the tag keys in
  the `[sf]` section (or clear Custom Data to re-seed defaults) and rename the blocks to match.

### Notes
- Stripped deploy size: 89,034 chars (10,966 under the 100,000 PB limit; +199 vs 0.5.1).
  Braces balanced (453/453). Version constant bumped to 0.6.0. Pre-1.0 MINOR: this is a
  default-tag change, non-destructive to existing deployments thanks to the migration.

## [0.5.1] - 2026-08-14

### Fixed
- **`DepartStaging` no longer swings 57↔113° for the full watchdog before committing to
  cruise.** On a space leg the ship reached the staging fix, began its turn to the route
  heading, then oscillated wildly (attitude error alternating ~57° and ~113° at 0 m/s in
  0 g) for ~45 s until the `APPROACH_TIMEOUT` force-switched it into cruise — which then flew
  fine. Three compounding faults, all fixed:
  - **Target ping-pong.** `atFix` was recomputed each tick from a bare 3 m threshold with no
    hysteresis. The attitude target was the *route heading* when `atFix` was true and the
    *recorded dock pose* when false, and the two are ~57° apart. Sub-metre drift toggled the
    flag every few ticks, so the gyros chased first one target then the other and never
    settled. Now a latch (`stagingAtFix`) arms at <3 m and only disarms on a real drift back
    out (>8 m); once the fix is reached the ship commits to the route heading and never
    reverts to the dock attitude on small drift.
  - **Unattainable precision in space.** The staging turn ran through `FlyToPose`, whose
    completion test demands `ALIGN_TOL` (~1.7°) precision — which in space is impossible (the
    nose target inches around and the gyros hunt it forever, the same reason cruise uses
    coast-hold). So the confirm dwell never latched. The turn now aligns with the **same
    coast-hold law cruise holds** (`AlignTo(..., coastHold: true)`) and confirms at
    `COAST_HOLD_WAKE` tolerance.
  - **No station-keeping during the turn.** `FlyToPose` withholds all translation while
    misaligned (align ≥ `ALIGN_MOVE_TOL`, ~12°), so during the 57° turn the ship — dampeners
    off in flight — coasted off the fix, which is exactly what re-toggled the old `atFix`
    flag. A new `StationKeep(pos)` helper nulls residual velocity and cancels gravity
    independent of attitude, so the ship holds the fix *while* it turns.
- **ETA no longer prints twice on the status LCD.** The cruise status line appended
  `"  ETA hh:mm"` on top of the dedicated `ETA hh:mm <dist>km` header line, so the front LCD
  showed the ETA in two places — and the extra text could force the panel to resize to fit.
  The cruise `statusMsg` is now just `Cruising to destination` / `Cruising home`; the ETA is
  shown only on its dedicated header line.

### Notes
- Bug fix only; no new behavior, no new config. The departure anti-dive guarantee is
  unchanged (Undock still only clears the connector; the route-heading turn still happens at
  the staging fix). Version constant bumped to 0.5.1.

## [0.5.0] - 2026-08-14

Slice b — **staging, holding & taxi (the anti-dive guarantee).** The ship no longer flies
straight from undock into a climb, or from cruise straight onto a connector. Every dock is now
bracketed by an outer stand-off fix: a **departure staging fix** on the way out and an **arrival
holding fix** on the way in. The only phase that ever moves the ship onto the connector is
`Taxi`, and it is clearance-gated.

### Added
- **`DepartStaging` phase.** Undock now only *clears* the connector — it backs straight out to
  the inner stand-off holding the recorded docked attitude, with no rotation. The route-heading
  turn moved here: the ship flies out to the outer staging fix (`holdDist`), rotates in place to
  the exact attitude cruise will hold, and holds a `STAGE_CONFIRM_SEC` confirm dwell before
  committing to cruise. This is the "assemble before flying" gate — the ship never pitches while
  still nose-in on the dock, which directly answers the concern that pitching early bleeds the
  braking authority the strong thrust axis provides in gravity.
- **`Holding` phase.** Cruise now hands off at the outer holding fix (not the connector). The
  ship station-keeps there until the docking corridor reads clear for `CLEAR_CONFIRM_SEC`, then
  commits to `Taxi`. Reorientation to the dock attitude is **gravity-gated** ("stop only in
  gravity"): in gravity the ship holds a level, belly-down attitude — keeping the lift bank
  pointed against gravity for braking — until it has actually stopped (`vmag < ARRIVE_SPEED`),
  and only then rotates to the dock pose; in space it blends straight to the dock attitude on
  arrival, since there is no braking authority to lose.
- **`Taxi` phase.** The cleared final move: hold the dock attitude and translate straight down
  the connector axis from the holding fix onto the connector, then connect. If the corridor
  fouls mid-taxi, the ship abandons the commit and falls back to `Holding` rather than pressing
  into it — the clearance gate re-arms and it only re-taxis once clear.
- **`holdDist` config** (`[shuttle] holdDist`, default 40 m) — the outer stand-off distance,
  always forced ≥ `approachDist + 5` so the holding fix sits clear outside the taxi start.
- **Per-dock override** — `homeHoldDist`/`destHoldDist` keys in each `[route.<name>]` section
  (0 = use the global `holdDist`), for docks where the global stand-off isn't clear of the
  structure. Round-trips through `WriteRoute`/`LoadRouteInto` and the legacy-route migration.

### Changed
- `BuildLeg` drops crumbs inside each end's *holding-fix* radius (per-end `EffHoldDist`) and
  appends the arrival **holding fix** as the final cruise target, replacing the inner approach
  point. Cruise → `Holding` → `Taxi` → `Dock` replaces the old Cruise → `Approach` → `Dock`.
- The legacy `Approach` phase now delegates to `TickHolding`, so a mid-flight recompile from an
  older `[state]` (or an IGC report decoded by a Skippy-Shuttle base board) resumes cleanly on
  the holding fix. The IGC wire names (`ApproachDest`/`ApproachHome`, `UndockHome`/`UndockDest`)
  are unchanged — `DepartStaging`, `Holding`, and `Taxi` map onto them for the base board.

### Notes
- Stripped deploy size: 87,841 chars (12,159 under the 100,000 PB limit; +4,368 vs 0.4.1).
  Braces balanced (448/448). Version constant bumped to 0.5.0.

## [0.4.1] - 2026-08-14

### Fixed
- Undock from a **space** dock no longer swings back and forth for ~45 s before committing
  to cruise. In space, cruise is roll-agnostic — it holds the ship's *current* up
  (`RunCruiseControl`, the space branch) — but undock was aligning to the recorded dock's
  up, which for a space station is essentially arbitrary (no gravity to define it). The
  gyros hunted a roll cruise never wanted, so `AlignTo` never fell under `ALIGN_TOL` and the
  undock only advanced when the 45 s watchdog fired. Undock now pre-aims the *exact* attitude
  cruise will hold: current up in space (zero roll demand — alignment only has to point the
  nose), and gravity-up orthogonalized to the heading for an up-thrust-poor craft still in
  air. The telemetry view's `Att` line (added in 0.4.0) surfaced this directly: attitude
  error pinned at ~80-89° with speed 0 m/s and gravity 0 while the phase timer climbed.
- The v0.2.1 fix (orthogonalized dock up) only addressed the in-gravity/hill case, where the
  recorded dock up ≈ gravity-up ≈ what cruise holds, so it happened to match; that equivalence
  breaks in space. This replaces it with a direct match to cruise's attitude law for both cases.

### Notes
- Bug fix only; no new behavior. Version constant bumped to 0.4.1.

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
