# Changelog

All notable changes to **SkippyFlight** are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project adheres to
[Semantic Versioning](https://semver.org/).

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
