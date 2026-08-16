# Changelog

All notable changes to **SkippyFlight** are documented here. Format follows
[Keep a Changelog](https://keepachangelog.com/); this project adheres to
[Semantic Versioning](https://semver.org/).

## [Unreleased]

**Build tooling — migrated to MDK2.** No functional change to either script, so no version bump
(ship stays 0.14.0, tower 0.13.0); the deployed logic is identical, just compiled and minified by a
real toolchain instead of the old comment-stripper. Both scripts are now proper C# projects
(`SkippyFlight/`, `SkippyTower/`, tied together by `SkippyFleet.sln`) built with
[MDK2](https://github.com/malforge/mdk2) — `Mal.Mdk2.PbPackager` (pack + minify), `Mal.Mdk2.PbAnalyzers`
(Roslyn PB-whitelist analyzer), and `Mal.Mdk2.References` (SE API reference assemblies).

### Changed
- **Build/deploy is now `dotnet build -c Release`** on `SkippyFleet.sln` (or a single project). Each build
  compiles against the real Space Engineers PB API — the analyzer flags any non-whitelisted call, and
  Roslyn catches type errors the old brace-only gate could not — then packs the script straight into the
  game's local `IngameScripts\local\<name>` folder (no more copy-paste) and copies the packed result back
  into the repo as the committed `*.min.cs` artifact.
- **Minifier upgraded to full minification** (`minify=full` in each project's `mdk.ini`): strips *all*
  whitespace and renames identifiers (using Unicode letters for a large short-name pool — the "odd
  characters" seen in scripts like PAM). Verified that renaming touches identifiers only — **zero**
  non-ASCII characters land inside any string/char literal, so every `CMD|…` wire token, Custom Data key,
  and screen glyph survives verbatim. Output is deterministic (identical across rebuilds).
- **Char budget reclaimed:** `SkippyFlight.min.cs` **99,673 → 49,208 chars** (327 → **50,792** headroom);
  `SkippyTower.min.cs` **24,845 → 14,305 chars** (**85,695** headroom). The 100k paste limit is no longer a
  practical constraint.
- The heavily-commented source of truth now lives in each project's `Program.cs` (the old root
  `SkippyFlight.cs` / `SkippyTower.cs` flat-body files are removed — their content is byte-identical inside
  the project, wrapped in `partial class Program : MyGridProgram`). MDK2 strips comments at pack time, so
  documentation stays free.

### Removed
- Retired `tools/build-min.py` (the Python comment-stripper + brace-balance gate) — moved to
  `tools/legacy/` alongside the one-time `wrap-mdk.py` migration helper. Superseded by the MDK2 packager.

## [0.15.1] - 2026-08-16

### Changed
- Renamed the gravity thrust-handoff status marker from the cryptic `!xfer` to `handoff`.
  Same meaning (powered climb/descent while still inside a gravity well — status only, no
  control change), clearer label.

## [0.15.0] - 2026-08-16

### Added
- **Build version now shows in-world.** The full status header reads `Skippy v0.15.0 Idle [STOP]`, so
  you can confirm at a glance which build a programmable block is running (useful after a reload or an
  accidental overwrite). Previously `VERSION` was declared but never referenced, so the minifier stripped
  it and there was no way to tell builds apart from inside the game.

## [0.14.2] - 2026-08-16

### Fixed
- **Mode readout showed `A`/`B`/`C` instead of `Continuous`/`OneTrip`/`OneWay`.** The full minifier
  renames enum members, so any `enum.ToString()` (or enum-in-string concatenation) emitted the renamed
  letter rather than the name. String *literals* survive minification, so all name-producing sites now
  route through literal maps (`ModeName`, `TrigName`): the main menu, the `MODE`/`START` status lines,
  and the mode-cycle echo again read the real mode name.
- **Departure-trigger config could silently reset to `Auto`.** `homeTrigger`/`destTrigger` were persisted
  to Custom Data via `enum.ToString()`, writing a minified letter that `TrigFromString` (a literal switch)
  couldn't parse back — so a saved `Cargo`/`Timer`/`Manual` trigger reverted to `Auto` on the next config
  load, and the Depart menu showed the letter. Both now persist and display through `TrigName`.
- **Phase resume is now minifier-proof across rebuilds.** `[state] phase` is persisted as its integer
  value instead of the enum name (which was an unstable renamed letter), with range validation on load;
  an out-of-range or legacy value falls back to `Idle`. Resume-after-recompile within a build was already
  working, but a name written by one build was not guaranteed to read back correctly under a different one.

## [0.14.1] - 2026-08-16

Ship-only bug fix: **removes the low-speed thrust jitter felt while landing/holding in gravity.**

### Fixed
- **Gravity dock/hold jitter.** The docking and station-keep controllers (`FlyToPose`, `StationKeep`)
  drove thrust with `(desiredVel − vel)·mass·VEL_GAIN − grav·mass` but — unlike the cruise coast, which
  the code even calls *"the identical law to FlyToPose"* — never applied the `VEL_DEADBAND` guard. So
  near a dock, where `desiredVel → 0` and only residual velocity noise remains, a sub-threshold error
  flipped the *net* thrust sign every 60 Hz frame (up-thruster bank vs down-thruster bank), which is the
  jitter felt on a gravity landing. Both controllers now zero the velocity-correction term below
  `VEL_DEADBAND` (0.4 m/s) while always keeping the `−grav·mass` hover term, so the ship rides through the
  noise on hover alone. Position still self-corrects: `desiredVel` grows with distance, so any real drift
  pushes the error back past the band and thrust resumes. No new tuning knobs; reuses the existing cruise
  constant. Space docking is unaffected (the term was already near-zero there).



Ship-only. Restores the **TELEM instrument screen** (removed in Slice i for char budget) and, on it,
adds a **speed-derate breakdown** so a "why won't it reach `cruiseSpeed`?" question is answerable
in-world without a code dive. The cruise controller commands
`speed = min(cap, brakingCurve) * alignFac * velFac`; the new `Drt` line surfaces all four numbers, so a
steady shortfall reads directly as a heading miss (`a<1`), a velocity-vector miss (`v<1`), or the
braking curve pulling toward a near waypoint (`br<c`). `BuildTelem` was rewritten dense (single-`Append`
lines, gravity folded onto the vertical-speed line) to fit under the 100k ship paste limit.

The screen immediately paid for itself: it showed cruise capping ~10% below the governor (183 vs a
`cruiseSpeed` of 200) on straight legs with `v=1.00` (velocity dead on-path) — the shortfall was the
**heading-align derate** firing on the 2–5° nose wobble the gyros always carry while thrust-torque
loads the frame under power. That derate now has a **deadzone** (see Fixed). Ship min is 99,673 chars
(327 headroom, braces balanced).

### Added
- **TELEM screen (`[SF:telem]` / `telem` in `[sf-screens]`)** — in-flight instrument readout: phase +
  run flag + time-in-phase, speed vs the active cap, the new speed-derate line, vertical rate, gravity,
  surface altitude, waypoint progress + remaining distance, attitude error, and fuel reserves.
- **Speed-derate telemetry (`Drt a<align> v<vel> br<brake> c<cap>`)** — the cruise controller's
  per-tick derate factors, latched for the TELEM view. `alignFac`/`velFac` from `RunCruiseControl`,
  the braking-curve speed, and the active governor cap. Restores `lastAlignErr` (attitude error, shown
  as `Att … deg`), also removed in Slice i.

### Changed
- `BuildTelem` reimplemented compactly (labels shortened, `Append` chains collapsed, the standalone
  gravity `m/s²` term dropped — `g` is retained on the VS line) to restore the screen within budget.

### Fixed
- **Cruise capped ~10% below `cruiseSpeed` on straight legs** (observed 183 vs 200, in atmosphere and
  in open space alike). The heading-align speed factor (`alignFac`, `RunCruiseControl`) derated on the
  small residual attitude error (~2–5°) the gyros always carry while thrust-torque loads the frame —
  jitter, not a turn. Added an `ALIGN_DEADZONE` (~7°): heading misses below it no longer cut speed, so
  the ship reaches its governor on straights. `velFac` still fully guards genuine sideways drift
  (it read a perfect 1.00 throughout, confirming the derate was the sole cause), and real turns beyond
  the deadzone still slow exactly as before.

## [0.13.0] - 2026-08-15

Slice i — **tower-relayed pad paths + holding zones**, and a script split so the flight half stands
alone. The target scenario is a station the ship does **not** own: a hole-in-the-wall entrance and a
bank of pads at varied angles *inside* the structure, where a straight taxi from the outer fix would
punch through a wall. The trip now splits into two ownership domains — **ship-owned** (home → cruise
breadcrumbs → **holding zone**) and **tower-owned** (**interior path → pad**, relayed on grant). The
station route's destination becomes a tower-owned **holding zone** (an oriented box); the ship loiters
anywhere inside it, so a queue spreads out instead of fighting for one point. On LAND clearance the
tower assigns a free pad, relays its pose (as in Slice h) **and streams that pad's recorded interior
breadcrumb path**; the ship threads it in at `dockSpeed` and docks. On DEPART the tower re-streams the
path and the ship reverses it back out to the zone.

Setup tooling (recording zones, pads, and interior paths) is extracted **out of SkippyFlight** into a
new **teach mode of SkippyTower** — run on a second Programmable Block on the ship. SkippyFlight keeps
only the flying half. This isolates the tower/setup concerns from flight automation (not everyone wants
a tower) and reclaims the ship's char budget.

### Added
- **Holding zone (tower)** — an oriented box (`center` + `fwd`/`up` axes + half-extents `ext`), taught
  once and persisted as a `[zone]` section, advertised in the heartbeat
  (`CMD|TOWER|<zone>|<center>|<fwd>|<up>|<ext>`). A zone-destination route arrives by being *inside the
  box* (`InZone`), not at a pin — so ships loiter where they entered and a queue naturally spreads.
- **Interior pad paths (tower)** — each pad gains a recorded breadcrumb trail (holding-zone → pad),
  persisted in its `[pad.<name>]` `path` key. On a LAND grant the tower streams the assigned pad's path
  (`CMD|PATH|<ship>|<seq>|<total>|<v>;<v>…`, chunked); on DEPART it re-streams the occupied pad's path.
- **`RECORD ZONE` (ship)** — finalize a route whose destination is a holding zone: snapshots the live
  open-space pose as `destPose` and sets a per-route `destZone` flag (persisted). Cruise aims at the
  hold point but arrival is **also** satisfied by `InZone`.
- **Interior-path following (ship)** — `ArmInterior(reversed)` loads the relayed path into the cruise
  follower; `CruiseCap()` creeps at `dockSpeed` while threading. Inbound (Taxi) follows the path to the
  pad then connects; outbound (DepartStaging) reverses it out to the zone then rejoins cruise home.
- **SkippyTower teach mode (`towerMode = teach`)** — a ship-side setup helper (2nd PB on the ship) that
  records the zone, pad poses, and interior paths by hand-flying, streaming them to the station tower.
  Commands: `REGZONE [w h d]`, `REGPATH <pad>` (fly in + dock auto-finalizes path **and** pad pose),
  `REGPATH END`/`CANCEL`, `REGPAD <pad>` (open-air pad, pose only). Emits **no heartbeat** and answers
  **no clearance** — a pure teaching tool, safe on a live fleet channel. New `[sf]` keys: `remoteName`,
  `teachSeg`, `teachTurn`.
- **Grid-scoped clearance** — the tower now advertises its construct's grid id in the heartbeat
  (`CMD|TOWER|<zone>|<gridId>[|zone geom]`) and every ship names its target dock's grid in each request
  (`CMD|REQ|<ship>|<action>|<dock>|<grid>`). A ship only obeys, requests from, and accepts `CLEAR`/`HOLD`
  grants from the tower whose grid matches the dock it is arriving at / departing from. **Fixes:** with
  more than one static grid on a shared channel, a tower could gate and clear a ship bound for a *different*
  grid (it "cleared it to land" regardless of destination). Connector routes carry the dock's grid via
  `destBaseId`/`homeBaseId`; `RECORD ZONE` now stamps the advertising tower's grid so zone routes are
  scoped too. A grid id of `0` (legacy tower, or a route recorded before this change) falls back to
  accept-any.

### Removed
- **`role = base` / board rendering from SkippyFlight** — the ship no longer doubles as a status board.
  Use `SkippyTower` with `towerMode = board` (a byte-for-byte drop-in) instead. Frees the char budget
  that the interior-path flight wiring needed.
- **Ship-side `REGPAD` / teaching commands** — pad, zone, and path teaching now live in SkippyTower's
  teach mode. The ship is receive-only for pad/zone/path data.

### Notes
- **Backward compatible.** A pad with no recorded path is treated as open-air: the ship uses the
  Slice h straight-line last mile. A route with no zone / a legacy tower that never streams a path →
  the ship falls back cleanly. Scenario 1 (recorded home↔dest) is untouched. Grid scoping is additive:
  the heartbeat's grid id and the request's grid field are appended, and a `0`/absent id falls back to
  accept-any — legacy towers govern same-grid ships and legacy ships still get cleared. All protocol
  extensions append fields or add verbs; old ships/towers split unbounded and read only the fields they
  know.
- **Version bump:** tower `0.12.0 → 0.13.0`, ship `0.11.0 → 0.12.0`. MINOR — additive protocol, and
  the removals are covered by SkippyTower's board/teach modes (pre-1.0, so a MINOR may carry them).
- Build after Slice i: `SkippyFlight.min.cs` **97,824 chars** (2,176 headroom), braces 517/517;
  `SkippyTower.min.cs` **24,845 chars** (75,155 headroom), braces 116/116.

## [0.12.0] - 2026-08-14

Slice h — **dynamic pad bank.** The tower can own a pool of interchangeable docking pads and assign a
*free* one to each arriving ship at clearance time, so ships **park in parallel** while **movement
stays serialized** (the single-corridor anti-collision guarantee is unchanged). This removes the old
implicit assumption that every ship has its own dedicated connector — with one shared connector, a
second lander used to hover and deadlock behind a parked ship.

The pad geometry problem (a dock pose bakes in the recording ship's Remote-Control-to-connector offset,
which the station can't derive) is solved by **record-once-relay**: a pad's pose is recorded **once by
any ship** (`REGPAD`), pushed to and stored on the tower, and relayed to whichever ship is later
assigned that pad. This assumes a **geometrically uniform drone fleet** (a pose recorded by one ship
docks another).

### Added
- **Pad registry on the tower** — a pool of named pads, each with a stored world dock pose, persisted
  as `[pad.<name>]` Custom Data sections (`pos/fwd/up/connFwd`, `X:Y:Z`). Delete a section to retire a
  pad; occupancy is live-only (never persisted).
- **`REGPAD <name>`** (ship command, while docked) — capture this dock's pose and broadcast it to the
  tower as `CMD|PAD|<name>|<pos>|<fwd>|<up>|<connFwd>`. Any one ship registers each pad, once.
- **Pad-aware LAND clearance** — a LAND grant now needs the corridor free **and** a free pad; the tower
  reserves the pad (`OccupiedBy = ship`) and appends its pose to the grant:
  `CMD|CLEAR|<ship>|LAND|<pad>|<pos>|<fwd>|<up>|<connFwd>`. The ship steers to the assigned pad for the
  terminal approach and undock (`DestP()` override); gravity/scenario classification stays on its own
  recorded `destPose`. DEPART grants are unchanged.
- **Parallel parking, serial movement** — a pad is occupied from LAND-grant until its ship departs (or
  is lost/faulted/idle), which outlives the corridor slot (freed the moment the ship docks). Two ships
  park on two pads; the corridor still admits only one maneuvering craft at a time.
- **`no pad` hold** — a LAND with every pad occupied is held (`CMD|HOLD|<ship>|LAND|no pad`) and, in
  auto mode, a waiting DEPART is served ahead of it (departing frees a pad, clearing the jam).
- **`PADFREE <name>`** (tower command) — force-free a pad's occupancy (deadlock breaker).
- **Pads block on the tower board** — each pad listed `free` or `<ship>`.

### Notes
- **Backward compatible.** With no pads registered (or no tower), LAND grants carry no pose and ships
  dock at their own recorded `destPose` — exactly the pre-pad-bank behaviour. An un-upgraded `0.10.0`
  ship ignores the extra grant fields (`DrainIgc` reads only `f[0..3]`) and docks at its own pose.
- **Version bump:** tower `0.11.0 → 0.12.0`, ship `0.10.0 → 0.11.0` (both gain the feature). MINOR —
  the protocol extension is additive and the fallback path preserves old behaviour.
- Build after Slice h: `SkippyTower.min.cs` stripped **13,586 chars** (86,414 headroom), braces
  balanced 70/70; `SkippyFlight.min.cs` **98,618 chars** (1,382 headroom), braces 511/511.

## [0.11.0] - 2026-08-14

Slice g — **manual approval mode for SkippyTower.cs.** An air-traffic-controller option: "man" the
tower to approve every clearance by hand, or leave it unmanned to auto-approve. The operator flips
between the two **at runtime** with a PB command — no Custom Data edit, no recompile. The shuttle side
is untouched; a hand-issued `CMD|CLEAR` is byte-identical to an auto one.

### Added
- **Grant sub-mode `grant = auto | manual`** in `[sf]` (default `auto`; only meaningful in `control`
  mode, ignored in `board`). Persisted to Custom Data on every toggle, so it survives a recompile.
- **Runtime operator commands** (run the PB with an argument, or bind to a button; control mode only):
  - `MANUAL` — take the controls: stop auto-granting, hold every request for your OK.
  - `AUTO` — hand back: resume auto-granting the best waiting craft.
  - `CLEAR` — approve the top of the queue (best: a `LAND` before a `DEPART`, then oldest-first).
  - `CLEAR <ship>` — approve a specific waiting ship by name (queue-jump; name kept raw, may contain spaces).
  - `RELEASE` — force-free the current slot now (manual deadlock breaker, no 180 s `GRANT_MAX_SEC` wait).
- **Sub-mode-aware board** — mode line reads `CONTROL/AUTO` or `CONTROL/MANUAL`; a held ship is tagged
  `|| WAITING (your OK)` in manual vs `|| HOLD (traffic)` in auto; the footer shows
  `Next: <ship> (<action>) - run CLEAR` when manual with a free slot and a waiting queue.

### Notes
- The **heartbeat keeps beating in both sub-modes**, so a ship held for manual approval stays
  controlled (never reverts to independent past the 6 s timeout) while the operator deliberates.
- **Automatic slot-release stays ON in manual** (anti-deadlock): after the operator clears a ship and
  it departs/docks, the slot frees itself and the next craft waits for the next `CLEAR`.
- **`SkippyFlight.cs` is unchanged this release** → stays `0.10.0`. Intentional version skew: the fleet
  interoperates by the stable wire protocol, so no ship rebuild is needed. Only the tower advances.
- Build after Slice g: `SkippyTower.min.cs` stripped **9,807 chars** (90,193 headroom), braces
  balanced 50/50; `SkippyFlight.min.cs` unchanged at **97,544 chars** (2,456 headroom), braces 507/507.

## [0.10.0] - 2026-08-14

Slice f — **SkippyTower.cs**, the active traffic-control tower that answers the Slice e handshake. A
new, *separate* Programmable Block script (its own 100k-char budget) that a station PB runs. It emits
the `CMD|TOWER` heartbeat that flips ships from independent into controlled, consumes their `CMD|REQ`
requests, and **serializes clearances so only one craft maneuvers at the station at a time** — the
guarantee that stops two shuttles both undocking into, or both taxiing onto, the same corridor. It is
a superset of the base board: a `control|board` toggle runs it as an active controller or as a plain
passive status board.

### Added
- **`SkippyTower.cs`** (new script; `SkippyTower.min.cs` is the paste artifact). Speaks the Slice e
  wire protocol from the tower side:
  - `CMD|TOWER|<zone>` heartbeat every `HEARTBEAT_SEC` (2 s), well under the ships' 6 s timeout.
  - Consumes the unchanged 7-field status report (so it knows every ship's phase/dock for free) and
    incoming `CMD|REQ`.
  - `CMD|CLEAR|<ship>|<action>` grants and `CMD|HOLD|<ship>|<action>|traffic` denials.
- **Single-slot serialization** — one craft cleared at a time; a waiting `LAND` is served before a
  waiting `DEPART` (a holding ship burns fuel, a docked one can wait), else oldest-first (FIFO). The
  slot is held from grant until the ship's own status shows it cleared the resource (departed to
  cruise / docked), with an anti-deadlock release if the ship is lost, faults/stops, or overruns
  `GRANT_MAX_SEC` (180 s).
- **Clearance-annotated board** — the base-board layout plus a mode line (`CONTROL — zone <zone>` /
  `BOARD (passive)`), a per-ship `> CLEARED: <action>` / `|| HOLD (traffic)` tag, and a
  `Slot: <ship> (<action>)` / `Slot: free` footer. Config keys `channel` / `zone` / `lcdTag` /
  `towerMode` in `[sf]`, auto-templated on first compile.
- **`towerMode = board`** — passive status board with **no heartbeat**, so the fleet stays
  independent. A drop-in replacement for a `role=base` status board.

### Changed
- **`tools/build-min.py`** now strips and size/brace-checks **both** scripts (`SkippyFlight.cs` and
  `SkippyTower.cs`), each against its own 100k budget; it fails if any output is over-limit or
  unbalanced.

### Fixed
- **Board render no longer hijacks a docked ship's LCDs.** Both the tower (`SkippyTower.cs`) and the
  base board (`SkippyFlight.cs` `RunBase`) queried `[SF]`-tagged panels with no grid filter. When a
  shuttle docks, the connector merges terminal systems, so the station would overwrite the visiting
  ship's own `[SF]` screens. Both renders now scope to `IsSameConstructAs(Me.CubeGrid)` — mechanically
  linked subgrids (rotor/piston display arms) still show the board; a connector-docked ship is
  excluded.

### Notes
- `SkippyFlight.cs` bumps to `0.10.0` alongside the tower this release: it carries the same board
  cross-grid fix, so the cosmetic-skew note from the plan no longer applies. The fleet still
  interoperates by the stable wire protocol, not the version string.
- The tower holds **no persisted state**: the fleet table and clearance queue are rebuilt live from
  broadcasts, matching the ships' ephemeral handshake.
- Build after Slice f: `SkippyTower.min.cs` stripped **8,079 chars** (91,921 headroom), braces
  balanced 41/41; `SkippyFlight.min.cs` **97,544 chars** (2,456 headroom), braces balanced 507/507.

## [0.9.0] - 2026-08-14

Slice e — **tower clearance (shuttle side).** An optional traffic-control overlay that layers on top
of the two clearance gates a ship already runs locally (the departure decision at the dock and the
arrival corridor check at the holding fix). When enabled and a tower is broadcasting, a ship asks
for clearance before it undocks or taxis onto a connector and holds until the grant arrives — so two
shuttles sharing a station can no longer both undock or both taxi into the same corridor. The tower
PB itself is a later slice; this release is shuttle-side only and is a **no-op unless a tower is
actually heard**.

### Added
- **`useTower` config (`Auto`/`Off`, default `Off`)** in the `[sf]` section, with a matching **Tower:
  Auto/Off** row on the DEPART menu page. Off (the default) is byte-for-byte the previous behavior.
- **Tower-clearance handshake** over the existing IGC channel, all additive `CMD|`-family verbs so
  existing status broadcasts and the `CMD|DEPART` override are untouched:
  - `CMD|TOWER|<zone>` — tower heartbeat; a ship that hasn't heard one within `TOWER_TIMEOUT` (6 s)
    treats the tower as offline and flies independently (anti-stranding).
  - `CMD|REQ|<ship>|<DEPART|LAND>|<dock>` — the ship's clearance request, resent every `REQ_RESEND`
    (2 s) while it waits at a gate.
  - `CMD|CLEAR|<ship>|<DEPART|LAND>` / `CMD|HOLD|<ship>|<DEPART|LAND>[|<reason>]` — grant / deny; a
    HOLD reason surfaces on the status line ("HOLD: <reason>").
- **Two gate hooks** consulted only when a tower is live: the `Loading`/`Unloading → Undock` commit
  (the ship stays docked, out of the corridor, until cleared) and the `Holding → Taxi` commit (holds
  at the outer fix until the landing is granted). Status shows "Awaiting tower - DEPART/LAND".

### Changed
- A manual/remote `CMD|DEPART` override now also bypasses the departure tower gate — an explicit
  human/station command always departs immediately.

### Notes
- Clearance state (`towerAge`, `cleared`, `holdReason`, …) is ephemeral: never persisted and reset on
  every phase change, so each gate requests fresh and a recompile simply re-requests. No changes to
  `Save()`/`LoadState()`. The base board is unaffected (it drops the short `CMD|` verbs).
- Deferred as not worth the bytes this slice: per-route `useTower` override and a telemetry Tower
  line (the status line and menu row already surface tower state).
- Stripped size **97,498 chars** (2,502 headroom, +2,261 vs 0.8.1), braces balanced (507/507).

## [0.8.1] - 2026-08-14

### Fixed
- **Depart/Start from an unrelated dock no longer beelines to a route dock.** When the ship was
  connected to a connector that isn't the route's home or destination, `Depart Now` (ship button
  or remote `DEPART`) and `START`/`GO` would dispatch a leg anyway and fly straight to the recorded
  dock — dragging along the ground or through obstacles. `AtHomeEnd` only picked the *nearer*
  recorded end; it never confirmed the ship was actually *at* it. A new `AtRouteEnd` proximity check
  (within `DOCK_MATCH_DIST` = 10 m of the home or dest pose) now gates every docked dispatch:
  `RequestDepart`, the `START`/`GO` command, and the autonomous `TickIdle` handoff all refuse with a
  clear status ("not at a route dock — move to home/dest first") instead of departing. Starting from
  a real route end, or resuming a flight while undocked, is unchanged.

### Notes
- Stripped size **95,237 chars** (4,763 headroom, +786 vs 0.8.0), braces balanced (498/498).

## [0.8.0] - 2026-08-14

Slice d — **environment sensing.** The `Climb → Cruise → Descent` stage boundaries for same-planet
(**PlanetLocal**) legs now read the ship's real sea-level altitude trend instead of the coarse
Slice-c distance proxies. The recorded waypoints already are the altitude plan — the operator flew
the route — so the controller simply watches the altitude it's actually flying: Climb hands to
Cruise when the climb levels off, and Cruise hands to Descent on a sustained sink toward the dock.
Fully automatic; no new config. The Ascent/Descent (planet↔space) gravity boundaries are unchanged.

### Added
- **Altitude-trend PlanetLocal boundaries.** A new `TrySeaAlt` reader
  (`TryGetPlanetElevation(Sealevel)`) feeds a per-tick vertical rate (`vRate`). Sea-level (not
  surface/AGL) altitude is used so flying level over rising terrain is never mistaken for a descent.
  Climb → Cruise fires once the ship is clear of the launch pad (`CLIMB_MIN_DIST`) and no longer
  climbing (`vRate < LEVEL_RATE`); Cruise → Descent fires on a sustained sink (`vRate < -DESCENT_RATE`).
  Both use the existing confirm dwell. A flat hop that never really climbs still hands to Cruise
  (then Descent) via the distance guard — no dead-end in Climb.
- **Handoff danger-zone marker.** The status/telemetry appends `!xfer` while the ship is in a
  powered Climb/Descent inside a gravity well (the atmosphere/gravity thrust-handoff region).
  Status-only — no change to speed or control law.

### Changed
- PlanetLocal Climb/Descent boundaries are now derived from the recorded altitude profile rather
  than fixed 500 m distance gates. No behavior change for SpaceLocal, Ascent, or Descent legs, and
  none felt on any route until a `climbSpeed`/`descentSpeed` cap is lowered by hand.

### Removed
- `PLANET_CLIMB_DIST` / `PLANET_DESCENT_DIST` constants (the Slice-c distance proxies), replaced by
  the altitude-trend triggers.

### Notes
- Stripped size **94,451 chars** (5,549 headroom under the 100,000 limit, +1,014 vs 0.7.0), braces
  balanced 493/493.



Slice c — **flight plan + scenario.** The single cruise phase is now scenario-aware: each leg is
classified from the two docks' recorded gravity into one of four scenarios, and the cruise splits
into **Climb → Cruise → Descent** stages as appropriate. The stages share the exact same flight
law (`RunCruiseControl` over the same recorded path) — they differ only in an optional per-stage
speed governor and the operator status label. Precise altitude-based stage boundaries and the
handoff danger-zone status remain deferred to Slice d; this slice uses a coarse gravity / distance
proxy.

### Added
- **`Climb` and `Descent` phases.** Selected automatically per leg scenario and advanced by a
  gravity/distance boundary. They reuse the cruise controller, so attitude, cornering, braking
  and the stuck-watchdog behave exactly as in `Cruise`. The status LCD shows `Climbing >` /
  `Descending <` and the ETA/remaining-distance line now spans all three cruise-family stages.
- **Scenario classification.** Each dock's natural-gravity magnitude is captured at record time
  (`homeG`/`destG`, persisted per `[route.<name>]`). A leg is classified in flight order into:
  - **SpaceLocal** (space↔space) → `Cruise` only — identical to pre-0.7 behavior.
  - **Ascent** (gravity→space) → `Climb → Cruise`; Climb hands off once the ship leaves the
    gravity well (`gMag` drops below the space threshold, held briefly to debounce).
  - **Descent** (space→gravity) → `Cruise → Descent`; Descent engages as the ship enters the well.
  - **PlanetLocal** (gravity↔gravity, same planet) → `Climb → Cruise → Descent`, staged on
    monotonic distance gates (natural gravity barely changes within one planet, so the gravity
    threshold never fires). A short hop degrades gracefully (Climb → Holding, no fault).
  Because classification uses the leg's own from→to gravity, an outbound Ascent is automatically
  an inbound Descent — no direction bookkeeping.
- **`climbSpeed` / `descentSpeed` config** (`[sf]` section) — optional per-stage top-speed caps,
  clamped to `(5, cruiseSpeed]`. **Both default to `cruiseSpeed`, so the governors are a no-op
  out of the box** — Climb and Descent fly at today's speeds until you lower a cap (e.g. for a
  gentler descent into a planet dock). File-only knobs; not surfaced in the LCD settings menu.

### Changed
- `DepartStaging` now hands off to the first cruise-family phase for the leg's scenario (`Climb`
  or `Cruise`) instead of always `Cruise`. Manual/recovery entries that begin airborne still enter
  `Cruise` directly, so a ship recovered mid-descent doesn't restart in `Climb`.
- The telemetry `cap` readout shows the lower of the waypoint profile and the active governor, so
  a lowered climb/descent cap is visible live.

### Migration
- **Existing routes keep working untouched.** A route recorded before 0.7.0 has no `homeG`/`destG`
  keys → both read as 0 → classified **SpaceLocal** → flies `Cruise` only, exactly as before.
  `homeG`/`destG` are added to the legacy single-`[route]` → `[route.Main]` migration key list, and
  re-recording a route captures the gravity at each dock. No re-recording is required for correct
  behavior on space routes.
- New enum members (`Climb`/`Descent`) round-trip through `[state]` persistence automatically, and
  a mid-flight recompile into either resumes on the correct stage (the governor is derived live
  from the phase, not a cached field, so it survives the `Enter`-less resume path).

### IGC wire
- `Climb` and `Descent` report as `CruiseToDest` / `CruiseToHome` on the IGC channel, so a
  Skippy-Shuttle base board (which predates these phases) still decodes a ship in either stage as
  cruising. No wire-format change.

### Notes
- Stripped deploy size: 93,437 chars (6,563 under the 100,000 PB limit; +4,403 vs 0.6.0).
  Braces balanced (488/488). Version constant bumped to 0.7.0. Pre-1.0 MINOR: additive and
  backward-compatible thanks to the SpaceLocal-default classification.

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
