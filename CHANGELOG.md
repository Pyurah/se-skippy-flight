# Changelog

All notable changes to **SkippyFlight** are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project adheres to
[Semantic Versioning](https://semver.org/).

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
