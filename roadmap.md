# SkippyFlight Roadmap

Master tracking document for **SkippyFlight** — a phase-based flight controller for
Space Engineers Programmable Blocks. This project is the next-generation successor to
`Skippy-Shuttle` (v0.15.0), which stays frozen and in active use. SkippyFlight starts as a
faithful copy of that script and is refactored onto a phase-object architecture.

## Current status

- **Version:** ship 0.14.0 / tower 0.13.0 (ship 0.14.0 — **TELEM instrument screen restored** with a
  new speed-derate breakdown line, which surfaced and fixed a cruise **heading-align derate** that
  capped straight-leg speed ~10% below `cruiseSpeed`; see the note after the Slice i summary. Slice i delivered — **tower-relayed pad paths + holding
  zones**, and a **script split**: setup tooling moved out of SkippyFlight into a new SkippyTower **teach
  mode**, so the flight script stands alone. For a station the ship does *not* own — a hole-in-the-wall
  entrance with interior pads at varied angles — the trip splits into **ship-owned** (home → cruise →
  **holding zone**) and **tower-owned** (**interior path → pad**, relayed on grant). The station route's
  destination is now a tower-owned **holding zone** (an oriented box, taught via `REGZONE`, advertised in
  the heartbeat); arrival is being *inside the box* (`InZone`), so a queue loiters where it entered and
  spreads out. On LAND the tower assigns a free pad, relays its pose (Slice h) **and streams that pad's
  recorded interior breadcrumb path** (`CMD|PATH`, chunked); the ship threads it in at `dockSpeed` via the
  reused cruise follower (`ArmInterior`, `CruiseCap` capped) and docks — on a pad/interior it never
  recorded. On DEPART the tower re-streams the path; the ship reverses it back out to the zone, then
  rejoins cruise home. A pad with no path (open-air) or a legacy tower falls back to the Slice h
  straight-line last mile. **Clearance is grid-scoped:** the tower advertises its grid id in the heartbeat
  and the ship names its target dock's grid in each request, so with several static grids on one channel a
  tower only governs ships bound for its own grid — no more clearing a ship headed elsewhere. **Setup lives
  in SkippyTower `towerMode = teach`** (a 2nd PB on the ship):
  `REGZONE`/`REGPATH`/`REGPAD` record the zone, pad poses, and interior paths by hand-flying and stream
  them to the station tower — emitting **no heartbeat** and answering **no clearance**. `role = base` is
  **removed from SkippyFlight** (use `SkippyTower towerMode = board`, a drop-in), reclaiming the ship's
  char budget. Fully backward compatible; Scenario 1 (recorded home↔dest) untouched. Tower `0.12.0 →
  0.13.0`, ship `0.11.0 → 0.12.0`. Built on Slice h's **dynamic pad bank**: the tower owns a
  pool of interchangeable docking pads and assigns a *free* one to each arriving ship at clearance
  time, so ships **park in parallel** while **movement stays serialized** (the single-corridor
  anti-collision guarantee is unchanged). A pad's world dock pose is recorded **once by any ship**,
  stored on the tower, and **relayed** to whichever ship is later assigned that pad, which solves the
  RC-offset problem by reuse and assumes a **geometrically uniform drone fleet**. Built on Slice g's
  manual approval mode; Slice f's active traffic-control tower; Slice e's shuttle-side clearance gates;
  Slice d's altitude-trend `Climb → Cruise → Descent` boundaries; Slice c's scenario-aware cruise;
  Slice b's `DepartStaging`/`Holding`/`Taxi` phases; and Slice a's phase-object base controller and
  multiple named routes. Block tags and Custom Data sections are keyed to `SF`.)
- **Post-Slice i (ship 0.14.0):** restored the **TELEM screen** (`[SF:telem]`) that Slice i had cut for
  budget, and added a **speed-derate breakdown** (`Drt a<align> v<vel> br<brake> c<cap>`) so an
  in-flight "why is it capped below `cruiseSpeed`?" is answerable on-screen — it exposes the
  `speed = min(cap, brake) * alignFac * velFac` factors the cruise controller uses. The screen paid off
  immediately: it showed straight-leg cruise capping ~10% low (183 vs 200) with `v=1.00`, isolating the
  cause to the **heading-align derate** firing on the 2–5° gyro/thrust-torque nose wobble. Added an
  `ALIGN_DEADZONE` (~7°) so that jitter no longer cuts speed (`velFac` still guards true sideways drift;
  real turns still slow). Corner decelerations at waypoints were investigated and left as-is — they are
  the profiler correctly braking to `sqrt(cruiseAccel·R)` for the configured `cornerLen=30` rounding
  (raise `cornerLen`/`simplifyMeters` in Custom Data to take gentle bends faster). `BuildTelem` was
  rewritten dense to fit: **ship min 99,673 chars (327 headroom, braces 518/518)**.
- **Environment:** Space Engineers in-game Programmable Block (single-file C#, no external
  build/test tooling; all validation is in-world)
- **Relationship to Skippy-Shuttle:** shares the IGC wire protocol (`SkippyShuttleNet`,
  pipe-delimited reports) so a SkippyFlight ship and a Skippy-Shuttle base interoperate. Config
  idioms (`MyIni`, `[sf]`/`[route]`/`[state]` sections) shared by convention, not code.

---

## Why this project exists

`Skippy-Shuttle` flies A→B with an **11-state flat enum** (`State`) dispatched by a
hand-maintained `switch` in `Main`. Three problems block a richer flight model:

1. **Direction is baked into the enum.** Every flight phase exists twice
   (`CruiseToDest`/`CruiseToHome`, `ApproachDest`/`ApproachHome`, `UndockHome`/`UndockDest`).
   Adding Taxi/Climb/Descent/Approach doubles the enum again.
2. **Transitions are hardcoded inline** inside each `Tick*` method, and the state set is
   re-enumerated by hand in 4+ switches (`Main`, `IsFlightControlState`, `ShipState`,
   `PrettyState`, plus `LoadState`). Every new phase edits all of them.
3. **Environment sensing is binary** — `GetNaturalGravity()` magnitude only (`inGrav`/`inSpace`).
   No altitude/atmosphere signal, so a planet↔space route is one undifferentiated cruise leg
   that silently changes flight law mid-flight. No hook to split Climb/Cruise/Descent or to
   insert a holding pattern.

What is **not** a problem: the low-level primitives — `FlyToPose`, `AlignTo`, `ApplyForce`,
`RunCruiseControl` — are already scenario- and thruster-type-agnostic (they read live
`MaxEffectiveThrust`, which the game auto-zeros for the wrong environment, so the "thruster
handoff" at the gravity boundary already works implicitly). **The granular phases are policy
over these primitives, not new physics.**

The "line-number limit" worry is really the **100,000-character PB limit**. The minified script
is ~70.8 KB → ~29 KB headroom. Traffic control lives in a **separate `SkippyTower.cs`**, so it
spends none of the ship's budget.

**Intended outcome:** a controller whose *phase behavior*, *direction*, and *scenario* are three
independent axes, so the flight model can grow (Taxi→Takeoff→Climb→Cruise→Descent→Approach→
Landing→Taxi) and holding/traffic-control can slot in cleanly — without the combinatorial enum
blow-up or touching the flight physics that already works.

---

## The mental model — three independent axes

- **Phase** = *what behavior is running* (Undock, DepartStaging, Climb, Cruise, Descent, Approach,
  Holding, Taxi, Dock, Load, Unload, Record, Idle, Fault). Direction-free.
- **Leg** = *the current traversal context*: `outbound` (home→dest) vs inbound, the origin
  `DockPose`, the target `DockPose`, the path, and the detected scenario. Replaces the duplicated
  `*ToDest`/`*ToHome` states and the `bool toDest` params.
- **Scenario** = *which phases apply and how they're tuned*, auto-detected from the recorded
  endpoints. Selects the ordered **flight plan**.

### Phase catalog

The controller never flies straight from cruise onto a connector, and never straight from undock
into a climb. Every dock is bracketed by a **staging fix** (assembly point clear of the
structure) on departure and a **holding fix** (arrival wait point) on arrival. The **only** phase
that ever moves the ship onto the connector is `Taxi`, and it is **clearance-gated** — a craft
waits at the holding fix until the pad/"door" is free and no other craft is in the corridor
before it proceeds.

| Phase | SE behavior | Built on |
|---|---|---|
| `Undock` | Disconnect, powered back-off off the connector to the departure stand-off | `TickUndock` body |
| `DepartStaging` | Fly to and hold at the **departure staging fix** — clear of the structure and traffic; wait for clearance to begin the flight, then rotate toward the route | `FlyToPose` loiter + clearance gate |
| `Climb` | Powered ascent; full-power governor; until altitude/gravity threshold | `RunCruiseControl` + climb policy |
| `Cruise` | Steady traverse; efficient governor; in space coasts thrust-free | `RunCruiseControl` |
| `Descent` | Controlled loss / braking burn; conservative governor | `RunCruiseControl` + descent policy |
| `Approach` | Fly the approach corridor to the **arrival holding fix** — stops *at the holding fix, not the connector* | `TickApproach` body, retargeted |
| `Holding` | Station-keep at the holding fix; wait until the pad/door is clear of other craft and cleared (local corridor check now; tower grant later) | `FlyToPose` loiter + clearance gate |
| `Taxi` | The cleared final move: from the holding fix down the connector axis onto the connector | `FlyToPose` down the corridor |
| `Dock` | `Connect()`, confirm, hand to cargo/next leg | `OnDocked` |
| `Load`/`Unload`/`Idle`/`Record`/`Fault` | unchanged | existing ticks |

Climb / Cruise / Descent are **the same `RunCruiseControl` with different speed governors and
altitude targets**, plus distinct operator status — not three physics engines.
`DepartStaging`/`Holding` are the same loiter primitive with different fixes and clearance
sources.

### Staging & holding fixes — where they come from

- **Default (derived):** two stand-offs on the connector axis, reusing `ApproachPoint(p)` at two
  distances — an inner one (taxi start, ≈ today's `approachDist`) and an outer one (the
  staging/holding fix, a new `holdDist` config). Zero extra teaching; behavior degrades to "back
  off, pause, proceed" for a solo craft on a dedicated pad.
- **Recordable (optional):** `RECORD` can additionally capture an explicit staging/holding pose
  per end for structures where a straight axial stand-off isn't clear of the geometry.
- **Tower-assigned (Phase 2+):** for a shared bank of pads, the tower hands back a holding fix + a
  dynamic pad pose; the ship flies the assigned pose. Additive over IGC.

### Clearance model (what releases `Holding`/`DepartStaging`)

- **Dedicated spot / solo:** local corridor clear (`DockCorridorBlocked`) + a short confirm dwell.
  A lone craft on its own pad effectively auto-clears once the corridor reads empty — but it still
  *stages* rather than diving in.
- **Shared bank / multi-craft:** the tower grants clearance and a slot; until then the craft
  holds. Mirrors a station with one door and limited access — craft wait clear of each other for
  the door to open.

### Scenario → flight plan (outbound leg)

Staging, Holding, and Taxi are present in **every** scenario — the anti-"straight-onto-the-
connector" guarantee. Only Climb/Descent are scenario-gated.

| Scenario | Detected from | Outbound phases |
|---|---|---|
| **PlanetLocal** (A) | home in gravity, dest in gravity | Undock → DepartStaging → Climb → Cruise → Descent → Approach → Holding → Taxi → Dock |
| **Ascent** (B) | home in gravity, dest in space | Undock → DepartStaging → Climb → Cruise(coast) → Approach → Holding → Taxi → Dock |
| **Descent** (D) | home in space, dest in gravity | Undock → DepartStaging → Cruise(coast) → Descent → Approach → Holding → Taxi → Dock |
| **SpaceLocal** (C) | home in space, dest in space | Undock → DepartStaging → Cruise → Approach → Holding → Taxi → Dock |

The inbound leg reverses the scenario (Ascent outbound → Descent inbound) — one plan definition
serves both directions via the `Leg.outbound` flag.

---

## Base-controller architecture (phase objects)

Lightweight **phase objects** nested in `Program`:

```
abstract class FlightPhase {
    public abstract PhaseId Id { get; }
    public abstract bool IsFlightControl { get; }   // replaces IsFlightControlState switch
    public abstract string Label { get; }            // replaces ShipState/PrettyState switches
    public virtual void Enter(Program p, Leg leg) {} // reset phaseTimer, arm cruise, etc.
    public abstract PhaseId Tick(Program p, Leg leg);// drive control; return next PhaseId
    public virtual void Exit(Program p, Leg leg) {}
}
```

- Concrete phases are **nested classes** — full access to `Program`'s private members through the
  passed `Program p`, so they call the *existing* `p.FlyToPose(...)`, `p.RunCruiseControl(...)`,
  etc. with **zero logic duplication**.
- Instantiated **once** into a `Dictionary<PhaseId, FlightPhase>` at `Program()` — no per-tick
  allocation.
- `Main` collapses to `var next = current.Tick(this, leg); if (next != current.Id) SwitchPhase(next);`.
- Loop-rate and labels come from the object — the parallel switches disappear.
- Transitions stay inside each phase for the extraction slice; a later slice lifts the sequence
  into a data-driven flight plan (`PhaseId[]` per scenario).

---

## Scenario auto-detection

At `RECORD` time, capture each dock's natural-gravity magnitude into the `[route]` section:
`homeG = rc.GetNaturalGravity().Length()` at home, `destG` at dest. `Classify(homeG, destG)` →
PlanetLocal / Ascent / Descent / SpaceLocal (thresholds mirror the `1e-3` test). The inbound leg
swaps origin/target and flips Ascent↔Descent. Routes without the gravity keys fall back to today's
single-cruise behavior (no regression). Altitude sensing (`rc.TryGetPlanetElevation`) is added
only when Climb/Cruise/Descent split.

---

## Traffic control / holding (separate tower)

The control tower is a **separate `SkippyTower.cs`** (own char budget). The staging/holding/taxi
phases and the two departure gates (`DepartureAllowed` + `DepartFuelOk`) are the seams it plugs
into. `Taxi` is the gated commit — a craft only leaves `Holding` for `Taxi` (the sole phase that
touches the connector) once cleared; departure is the mirror — the ship only leaves `Loading`/
`Unloading` for `Undock` once cleared.

### The overlay model (the guiding rule)

The tower is **an additional AND-gate layered on top of the local gates, not a replacement for
them, and not a `DepartTrigger` value.** A trigger enum value would be *exclusive* (you couldn't
have "cargo-full **and** tower-cleared"); the requirement is that the ship first satisfies its own
local gate, **then** waits on the tower. Formally, for either the departure commit or the
arrival→taxi commit:

```
proceed when:  local gate satisfied            (trigger + fuel for departure; corridor clear + dwell for arrival)
          AND ( NOT towerActive  OR  cleared )   (tower overlay)
```

- **`towerActive`** is driven by a **tower heartbeat**, not mere presence. A tower that is powered
  but not controlling this channel simply doesn't emit the heartbeat, so ships stay independent —
  which is exactly "present AND *actively controlling*." If the heartbeat goes stale
  (`TOWER_TIMEOUT`), the ship **reverts to independent** and proceeds on the local gate alone — the
  anti-stranding guarantee (a destroyed/unpowered tower never strands the fleet).
- **`cleared`** is set only by an addressed grant from the tower for the pending action.
- A per-connector setting **`useTower` (Auto / Off)** opts in. `Auto` (default) respects a live
  tower and falls back to independent when none is heard; `Off` ignores the tower entirely (pure
  local behavior — the regression-safe path). A ship on a channel with no tower behaves exactly as
  it does today.

### Wire protocol (additive; all command messages `CMD|`-tagged)

The existing status report (`name|state|eta|dist|fill|mass|running`, broadcast ~6 Hz) is
**unchanged** — the tower already receives it as the base role, so it knows every ship's phase,
distance and dock without extra telemetry. New messages, all prefixed `CMD|` so pre-Slice-e
shuttles skip them in `DrainIgc` and degrade gracefully:

| Message | Direction | Meaning |
|---|---|---|
| `CMD\|TOWER\|<zone>` | tower → all | Heartbeat / "actively controlling this channel." Periodic; resets each ship's `towerAge`. |
| `CMD\|REQ\|<ship>\|<DEPART\|LAND>\|<dock>` | ship → tower | Clearance request; ship's local gate is green and it wants to move. Re-sent on an interval while holding. |
| `CMD\|CLEAR\|<ship>\|<DEPART\|LAND>[\|<pose>]` | tower → ship | Grant. Optional `pose` reserved for shared-bank pad assignment (Slice e stretch / f). |
| `CMD\|HOLD\|<ship>\|<DEPART\|LAND>[\|<reason>]` | tower → ship | Deny/keep holding; optional reason surfaces on the status line. |
| `CMD\|DEPART\|<who>` | tower/operator → ship | **Existing, unchanged.** A *force* override that bypasses both the local trigger and the tower gate (manual/emergency dispatch). |

Clearance is **one-shot and ephemeral** — consumed when the ship undocks/taxis, and **not
persisted** (a recompile mid-hold simply re-`REQ`s; the tower re-grants). It clears on STOP/START
like `departRequested`.

---

## Character-budget strategy

Phase objects add class scaffolding, offset by: (1) duplicated `*ToDest`/`*ToHome` ticks collapse
to one phase each and 4 hand-maintained switches become object properties; (2) comments are
stripped by `build-min.py` so documentation is free; (3) phases are thin dispatchers to existing
methods. **Gate:** after each slice, run `python tools/build-min.py` and record the stripped size.
Fallback if the full model threatens the ceiling: a lean `enum PhaseId` + transition table + `Leg`
context (same decoupling, lower char cost).

Baseline (v0.1.0, unmodified copy): stripped **70,780 chars**, **29,220** headroom.
After Slice a (v0.2.0, phase objects): stripped **75,112 chars**, **24,888** headroom (+4,332).
After Slice b (v0.5.0, staging/holding/taxi): stripped **87,841 chars**, **12,159** headroom
(+4,368 vs 0.4.1; the intervening named-routes and telemetry extras account for the rest).
After the v0.6.0 `SF` rename: stripped **89,034 chars**, **10,966** headroom (+199 vs
0.5.0; the `stagingAtFix` latch, coast-hold staging turn, and `StationKeep` helper, less the
duplicate-ETA removal).
After Slice c (v0.7.0, scenario + Climb/Descent): stripped **93,437 chars**, **6,563** headroom
(+4,403 vs 0.6.0; the two phase classes, scenario classification, boundary logic, and the
gravity-capture persistence).

---

## Roadmap

### Slice a — scaffold + phase-object extraction (delivered, 0.2.0)

Behavior byte-for-byte equivalent to Skippy-Shuttle v0.15.0; the phase-object base controller
replaces the flat enum. No new phases, no scenario logic. Proves the abstraction and the budget.

- [x] Scaffold `Skippy-Flight\` from a faithful copy; baseline builds (70,780 chars).
- [x] `enum PhaseId` (direction-free) + `struct Leg` context.
- [x] `abstract class FlightPhase` + concrete phases wrapping existing tick bodies verbatim.
- [x] `Main` dispatch → `phases[phase].Tick(this)`; `SwitchPhase`; `IsFlightControl`/`Label` from
      the phase objects.
- [x] Entry dispatch (`START`/`GO`/`HOME`/`RESUME`, `RequestDepart`, `TickIdle`) sets
      `(PhaseId, outbound)`.
- [x] Persistence: `[state]` stores `phase` + `outbound`, reading the legacy `state` name as a
      fallback; `LegacyStateName` keeps the IGC report wire unchanged for cross-version base boards.
- [x] Rebuild + budget check; version → 0.2.0. **Stripped: 75,112 chars (24,888 headroom;
      +4,333 vs the 0.1.0 copy).** Braces balanced.

### Slice b — staging, holding & taxi (the anti-dive guarantee) — delivered, 0.5.0

Add `DepartStaging`, `Holding`, `Taxi` phases with derived stand-off fixes (`holdDist` config) and
the local clearance gate. Assemble before flying, hold clear before docking, never straight onto a
connector.

- [x] `DepartStaging` phase — Undock only clears the connector (backs to the inner stand-off,
      holding dock attitude, no rotation); the route-heading turn moved here (fly out to the outer
      staging fix, rotate to cruise's exact attitude, `STAGE_CONFIRM_SEC` dwell, then Cruise).
- [x] `Holding` phase — Cruise hands off at the outer holding fix; station-keep until the corridor
      is clear + `CLEAR_CONFIRM_SEC`; **gravity-gated reorient** (hold level/belly-down for braking
      until stopped in gravity, then rotate to dock attitude; blend straight to dock attitude in
      space). Commits to `Taxi` only when settled at the fix in the dock attitude.
- [x] `Taxi` phase — the cleared final move down the connector axis onto the connector; corridor
      foul mid-taxi falls back to `Holding` rather than pressing in.
- [x] `holdDist` config + per-dock `homeHoldDist`/`destHoldDist` route overrides (forced clear
      outside the inner stand-off); persisted in `[sf]` and each `[route.<name>]`.
- [x] `BuildLeg` final waypoint → the arrival holding fix; per-end `EffHoldDist` crumb-skip radius.
- [x] Legacy `Approach` phase delegates to `TickHolding`; IGC wire names unchanged for a
      Skippy-Shuttle base board. Rebuild + budget check: **87,841 chars (12,159 headroom)**, braces
      balanced (448/448).

### Slice c — flight plan + scenario — delivered, 0.7.0

Capture `homeG`/`destG` at record; add `Classify`; lift the phase sequence into a data-driven
plan per scenario; add `Climb`/`Descent` phases (governor + status, reusing `RunCruiseControl`).

- [x] `struct DockPose` carries `Grav` (natural-gravity magnitude captured at `CapturePose`);
      persisted per `[route.<name>]` as `homeG`/`destG` and added to the legacy-route migration key
      list. Pre-0.7 routes read 0 → classify SpaceLocal → fly `Cruise` only (no regression).
- [x] `Scenario { PlanetLocal, Ascent, Descent, SpaceLocal }` + `Classify(fromG, toG)` and
      `ClassifyLeg()` reading the leg's own from→to gravity (so an outbound Ascent is an inbound
      Descent with no direction bookkeeping). `FirstCruisePhase()`/`NextCruisePhase()` encode the
      per-scenario plan; `DepartStaging` hands off to the scenario's first cruise-family phase.
- [x] `Climb`/`Descent` phases (`IsFlightControl`, labels `Climbing`/`Descending`) reuse
      `RunCruiseControl` over the same recorded path. Speed governor is derived live from `phase`
      via `CruiseCap()` (not a cached field — survives the `Enter`-less resume path); the profile is
      still built at the `cruiseSpeed` ceiling, so a lowered cap is always braking-safe with no
      rebuild. `climbSpeed`/`descentSpeed` config in `[sf]`, clamped `(5, cruiseSpeed]`, both
      default to `cruiseSpeed` (governor is a no-op out of the box). File-only; not in the LCD menu.
- [x] Stage boundaries via `BoundaryReady()`: gravity crossing `GRAV_EPS` held `BOUNDARY_CONFIRM_SEC`
      for Ascent/Descent; monotonic distance gates (`PLANET_CLIMB_DIST`/`PLANET_DESCENT_DIST`) for
      PlanetLocal (SE gravity barely varies within a planet, so a gravity trigger would dead-end).
      Coarse Slice-c proxies — Slice d replaces both with altitude bands. Intermediate advances
      preserve `cruiseIdx`/`cruiseArmed` (no `ReleaseControl`); only done/stuck release.
- [x] IGC wire unchanged: `Climb`/`Descent` report as `CruiseToDest`/`CruiseToHome` via
      `LegacyStateName`, so a Skippy-Shuttle base board decodes them as cruising. Manual/recovery
      entries still start in `Cruise` (a ship recovered mid-descent must not restart in Climb).
- [x] Rebuild + budget check: **93,437 chars (6,563 headroom, +4,403 vs 0.6.0)**, braces balanced
      (488/488). Version → 0.7.0.

### Slice d — environment sensing ✅ (v0.8.0)

Replaces the coarse Slice-c PlanetLocal distance proxies with a real altitude signal. The recorded
waypoints already are the altitude plan, so the controller reads the altitude it is actually flying
and detects the climb-out plateau and the descent to the dock. Fully automatic — no new config.

- [x] Sea-level altitude reader `TrySeaAlt()` (`TryGetPlanetElevation(MyPlanetElevation.Sealevel)`),
      feeding a per-tick vertical rate `vRate`. Sealevel (not Surface/AGL) so level flight over
      rising terrain isn't read as a descent; returns false in space (gravity gates cover that).
- [x] PlanetLocal boundaries in `BoundaryReady()`: **Climb → Cruise** when clear of the pad
      (`CLIMB_MIN_DIST`) and no longer climbing (`vRate < LEVEL_RATE`), held `BOUNDARY_CONFIRM_SEC`;
      **Cruise → Descent** on a sustained sink (`vRate < -DESCENT_RATE`), held `BOUNDARY_CONFIRM_SEC`.
      Reuses the existing `boundaryFor` dwell and `legStartPos`; the distance guard degrades a flat
      hop straight to Cruise (no dead-end in Climb). Ascent/Descent gravity boundaries unchanged.
      Removed `PLANET_CLIMB_DIST`/`PLANET_DESCENT_DIST`; added `CLIMB_MIN_DIST`/`LEVEL_RATE`/`DESCENT_RATE`
      (internal consts, not operator config).
- [x] Handoff danger-zone: `InTransition()` (in a well + powered Climb/Descent) appends an `!xfer`
      marker to the status/telemetry. Status-only; no control change. Derived, so resume-safe.
- [x] New leg state (`prevSeaAlt`/`haveSeaAlt`/`vRate`) reset in `ArmCruise`; `CruisePhase.Enter`
      now zeroes `boundaryFor` for a clean Cruise → Descent dwell. IGC wire unchanged.
- [x] Rebuild + budget check: **94,451 chars (5,549 headroom, +1,014 vs 0.7.0)**, braces balanced
      (493/493). Version → 0.8.0.
- [x] **In-world validation (2026-08-14).** Flew a PlanetLocal pad→pad route on Earth
      (Climb→Cruise→Descent on the altitude trend) and a planet↔space station round trip
      (Ascent/Descent gravity boundaries) — both clean with the shipped defaults
      (`CLIMB_MIN_DIST` 100 m / `LEVEL_RATE` 0.75 / `DESCENT_RATE` 1.5). No calibration change needed.

#### Fix — depart/start from an unrelated dock (v0.8.1)

- [x] `AtRouteEnd()` proximity gate (within `DOCK_MATCH_DIST` = 10 m of the home or dest pose)
      guards every docked dispatch — `RequestDepart`, `START`/`GO`, and the autonomous `TickIdle`
      handoff. Previously `AtHomeEnd()` only picked the *nearer* recorded end, so departing while
      connected to some other connector beelined to the recorded dock (dragging the ground / hitting
      obstacles). Now those paths refuse with a clear status; real-endpoint and undocked-resume
      starts are unchanged. Rebuild: **95,237 chars (4,763 headroom, +786 vs 0.8.0)**, braces balanced (498/498).

### Slice e — tower clearance (shuttle side) — ✅ delivered (v0.9.0)

Layer the tower overlay (see **Traffic control / holding** above) onto the two existing commit
points, keeping the local gates intact and independent operation as the fallback. This slice is
**shuttle-side only** — it teaches the ship to speak the handshake and obey a live tower; the
tower that answers is Slice f. Until Slice f exists, `useTower=Auto` with no tower on the channel
is a no-op, so this slice ships without regression.

**Design decisions (locked):**
- Tower is an **overlay AND-gate**, not a `DepartTrigger` value — it composes with cargo/timer/
  manual rather than replacing them.
- **Heartbeat-driven** `towerActive`, with staleness → independent (anti-stranding). "Present" is
  not enough; the tower must be actively broadcasting `CMD|TOWER`.
- Clearance gates **both** ends: departure (`Loading`/`Unloading` → `Undock`) and arrival
  (`Holding` → `Taxi`) — "traffic to *and* from the station."
- Clearance is **ephemeral and unpersisted**; the existing `CMD|DEPART` force override still
  bypasses everything.

**Ship-side additions:**
- [x] Config `useTower` (Auto/Off), persisted in `[sf]`; new **Tower: Auto/Off** row on the Depart
      settings page (`PAGE_DEPART` 7 → 8 items). *(Per-`[route.<name>]` override deferred — not
      worth the bytes this slice; the global toggle covers the common case.)*
- [x] State: `towerAge` (since last heartbeat, init stale at 9999), `TowerActive() = useTower &&
      towerAge < TOWER_TIMEOUT`; per-action `clearanceRequested` / `cleared` / `reqAction` /
      `holdReason`; consts `TOWER_TIMEOUT` (6 s), `REQ_RESEND` (2 s). All ephemeral, reset in
      `SwitchPhase` (no `Save()`/`LoadState()` change).
- [x] `DrainIgc` extended to parse `TOWER` (reset `towerAge`), `CLEAR` (set `cleared` for the
      addressed action), `HOLD` (keep holding + capture reason). `DEPART` force path unchanged.
- [x] Helper `bool ClearedToProceed(string action, string dock)` — returns `!TowerActive() ||
      cleared`; while blocked, (re)sends `CMD|REQ` on the `REQ_RESEND` interval; `TowerWait()` sets
      the status line. Called after `DepartureAllowed && DepartFuelOk` in `TickLoading`/
      `TickUnloading` (bypassed by `departRequested`), and after the corridor-clear + confirm dwell
      in the `Holding`→`Taxi` commit.
- [x] Status surfacing: "Awaiting tower - DEPART/LAND" at each gate and "HOLD: <reason>" on a deny.
      *(Dedicated telemetry Tower line deferred — the status line + menu row already surface it.)*
- [ ] Edge cases to prove in-world: tower dies mid-hold → reverts to independent after
      `TOWER_TIMEOUT`; recompile mid-hold → re-`REQ`s and re-grants (no persisted clearance);
      operator `DEPART` during a tower hold → override wins; `useTower=Off` → byte-for-byte
      current behavior.
- [x] Rebuild + budget check (`python tools/build-min.py`): stripped **97,498 chars**
      (2,502 headroom, +2,261 vs 0.8.1), braces balanced 507/507.

### Slice f — SkippyTower.cs ✅ (v0.10.0)

The control tower as its own script (own char budget), speaking the Slice-e protocol. A superset of
the passive base/board role with an active-control mode. **Scope delivered: minimal core** — a single
global slot; dynamic pad-bank + `pose` assignment deferred (the `pose` field stays reserved).

- [x] Emits the `CMD|TOWER|<zone>` heartbeat every `HEARTBEAT_SEC` (2 s) in control mode — the
      presence signal that flips ships from independent to controlled.
- [x] Consumes the existing status reports (builds the same `fleet` table) plus incoming `CMD|REQ`;
      **serializes grants** so only one craft occupies the corridor/pad at a time, issuing
      `CMD|CLEAR` / `CMD|HOLD|…|traffic`. A waiting `LAND` outranks a waiting `DEPART`, else FIFO.
      Ship side is oblivious — it just waits for its addressed grant.
- [x] Slot held from grant until the granted ship's own status shows it cleared the resource
      (`DEPART` → cruise-family; `LAND` → docked), with anti-deadlock release on lost signal,
      `Faulted`/`Idle`, or `GRANT_MAX_SEC` (180 s). No persisted state — the fleet table and queue
      are rebuilt live from broadcasts.
- [x] `towerMode` toggle (`control` / `board`): `board` runs it as a plain status board with **no
      heartbeat**, so the fleet stays independent — a drop-in for a `role=base` board.
- [x] Board render scoped to `IsSameConstructAs(Me.CubeGrid)` (fixed post-delivery): a docked
      shuttle's own `[SF]` LCDs are no longer hijacked when the connector merges terminal systems.
      Same fix applied to `SkippyFlight.cs` `RunBase`, which had the identical bug.
- [x] Board render: base-board layout + mode line + per-ship `> CLEARED`/`|| HOLD` tag + slot footer.
- [x] `tools/build-min.py` extended to build **both** scripts, each against its own 100k budget.
- [x] Rebuild + budget check: `SkippyTower.min.cs` **8,079 chars (91,921 headroom)**, braces balanced
      (41/41). `SkippyFlight.min.cs` **97,544 chars (2,456 headroom)**, braces balanced (507/507).
      Both scripts → 0.10.0.
- [ ] In-world validation (needs a live tower): board mode = independent; control single-ship
      grant at both gates; two-ship contention (`LAND` preferred, other shows "HOLD: traffic");
      anti-deadlock release; tower-death revert (also proves the deferred **Slice e** edge cases,
      which required a live tower to exercise).

#### Pad bank → delivered in Slice h
- The shared pad bank (pad registry + dynamic pad assignment, `pose` relayed in `CMD|CLEAR`) was
  deferred here and is now **delivered in Slice h** (v0.12.0) via record-once-relay. See that section.

### Slice g — Manual approval mode ✅ (v0.11.0)

An air-traffic-controller sub-mode on the Slice f tower: the operator "mans" the tower to approve each
clearance by hand, or leaves it unmanned to auto-approve — flipped **at runtime**, not by editing
Custom Data. All work is in `SkippyTower.cs` + docs; the shuttle side is untouched (a manual
`CMD|CLEAR` is byte-identical to an auto one), so `SkippyFlight.cs` stays 0.10.0 (intentional skew).

- [x] `grant = auto | manual` config key in `[sf]` (default `auto`; only meaningful in `control`,
      ignored in `board`). New runtime `bool manual`, **persisted** to Custom Data on every toggle via
      `SaveGrantMode()`, so it survives a recompile.
- [x] `HandleCommand(argument)` at the top of `Main` (before the control tick, so a `CLEAR` lands the
      same tick). Verbs: `MANUAL` / `AUTO` (toggle + persist), `CLEAR` (approve best of queue via
      `ManualGrant(null)` → `GrantNext`), `CLEAR <ship>` (queue-jump by name, kept raw for spaces),
      `RELEASE` (force-free the slot via `ClearSlot`, no 180 s wait).
- [x] `Main` gate: `if (!manual) GrantNext();` — auto grants every tick, manual only on `CLEAR`.
      `ReleaseIfDone()` and `Heartbeat()` run in **both** sub-modes (anti-deadlock release stays on; a
      held ship never reverts to independent while the operator deliberates).
- [x] Sub-mode-aware board: mode line `CONTROL/AUTO` | `CONTROL/MANUAL`; held-ship tag
      `|| WAITING (your OK)` (manual) vs `|| HOLD (traffic)` (auto); footer `Next: <ship> (<action>) -
      run CLEAR` when manual with a free slot and a waiting queue, else `Slot: …`/`Slot: free`.
- [x] File header + `WriteSection`/`LoadConfig` document and round-trip the `grant` key; README tower
      section extended with the key and the command table.
- [x] Rebuild + budget check: `SkippyTower.min.cs` **9,807 chars (90,193 headroom)**, braces balanced
      (50/50). `SkippyFlight.min.cs` unchanged at **97,544 chars (2,456 headroom)**, braces (507/507).
      Tower → 0.11.0.

### Slice h — Dynamic pad bank ✅ (v0.12.0)

The tower owns a pool of interchangeable docking pads and assigns a *free* one to each arriving ship at
clearance time, so ships **park in parallel** while **movement stays serialized** (the single-corridor
anti-collision guarantee is unchanged). This lifts the old implicit one-connector-per-ship assumption:
with a single shared connector, a second lander used to hover and ~180 s-deadlock behind a parked ship.

The RC-offset problem (a `DockPose` bakes in the recording ship's Remote-Control-to-connector geometry,
which the station can't derive) is solved by **record-once-relay**: a pad's world pose is recorded
**once by any ship** via the existing `CapturePose` path, stored on the tower, and relayed to whichever
ship is assigned that pad. This assumes a **geometrically uniform drone fleet** (a pose recorded by one
ship docks another) — the accepted constraint. Tower `0.11.0 → 0.12.0`, ship `0.10.0 → 0.11.0`.

- [x] **Tower pad registry:** `class Pad { Name; Pos/Fwd/Up/ConnFwd; OccupiedBy }` + `Dictionary<string,
      Pad> pads`. First coordinate handling on the tower — added `Vec`/`TryVec` mirroring the ship.
- [x] **Registration:** `CMD|PAD|<name>|<pos>|<fwd>|<up>|<connFwd>` handled in `DrainMessages` →
      `UpsertPad` (preserves live occupancy) → `SavePads()` writes `[pad.<name>]` sections. `LoadConfig`
      enumerates `pad.` sections (`ini.GetSections`) back into the registry (all free at boot).
- [x] **Assignment + occupancy:** `GrantNext`/`ManualGrant` route through `Grant()` + `Grantable()` — a
      LAND needs a free pad (`FirstFreePad`), reserves it (`OccupiedBy = ship`), and appends its pose to
      the grant via `ClearMsg()`. When pads are full a waiting DEPART is served instead (departing frees
      a pad). `OnRequest` re-confirms with the pad pose and sends `no pad` holds. `ReleasePads()` frees a
      pad once its ship departs (cruise state) or is lost/faulted/idle — occupancy outlives the corridor
      slot (freed on dock). `ClearSlot` drops the reservation pointer but not the pad.
- [x] **Ship override:** ephemeral `asgPose`/`asg`; `DestP() => asg ? asgPose : destPose` routed through
      the terminal sites — `TickUndock`/`TickDepartStaging` (dest branch), `TickHolding`, `TickTaxi`.
      `BuildLeg(toDest)` clears `asg` so an assignment never leaks into the next trip. `DrainIgc` CLEAR
      branch parses the appended pose when `reqAction == "LAND"` (gravity/scenario stays on `destPose`).
- [x] **`REGPAD <name>`** ship command (while docked): `CapturePose(ConnectedConnector())` → broadcast
      `CMD|PAD`. **`PADFREE <name>`** tower command force-frees a pad. Board gained a Pads block.
- [x] **Backward compatible:** no pads / no tower ⇒ LAND grants carry no pose, ships dock at their own
      `destPose`. An un-upgraded `0.10.0` ship ignores the extra grant fields (`DrainIgc` reads `f[0..3]`).
- [x] File headers (both) document `CMD|PAD` / extended `CMD|CLEAR` / `REGPAD` / `PADFREE`; README tower
      section extended with pad registration, the pad-bank behaviour, and the uniform-fleet requirement.
- [x] Rebuild + budget check: `SkippyTower.min.cs` **13,586 chars (86,414 headroom)**, braces balanced
      (70/70). `SkippyFlight.min.cs` **98,618 chars (1,382 headroom, +1,074 vs 0.10.0)**, braces
      balanced (511/511). Tower → 0.12.0, ship → 0.11.0.
- [ ] In-world validation (needs a live tower + ≥2 ships): register pads (`REGPAD`), then prove the
      **cross-ship pose assumption** — register a pad with ship 1, send ship 2, it docks cleanly on a
      pose it never recorded; two inbound ships park on distinct pads in parallel; a third gets
      `HOLD: no pad`; corridor stays serialized while two are parked; departure frees the pad; `PADFREE`
      and `RELEASE` deadlock breakers; a `0.10.0` ship ignores the assignment (no crash / parse error).
- [ ] In-world validation: manual holds a finished-loading ship at `Awaiting tower - DEPART`; `CLEAR`
      departs it and the slot auto-frees at cruise; `CLEAR <name>` queue-jumps; `AUTO` resumes
      instant grants; `RELEASE` breaks a stuck slot; toggle survives recompile.

### Slice i — Tower-relayed pad paths + holding zones ✅ (tower 0.13.0, ship 0.12.0)

The target scenario is a station the ship does **not** own: a hole-in-the-wall entrance and a bank of
pads at varied angles *inside* the structure, where a straight taxi from the outer fix would punch
through a wall. The trip splits into two ownership domains — **ship-owned** (home → cruise breadcrumbs →
**holding zone**) and **tower-owned** (**interior path → pad**, relayed on grant). The station route's
destination becomes a tower-owned **holding zone** (an oriented box); arrival is being *inside the box*,
so a queue spreads out instead of fighting for one pin. On LAND the tower assigns a free pad, relays its
pose (Slice h) **and streams that pad's recorded interior path**; the ship threads it in at `dockSpeed`
and docks. On DEPART the tower re-streams the path and the ship reverses it out to the zone.

Setup tooling is extracted **out of SkippyFlight** into a new **teach mode of SkippyTower** (a 2nd PB on
the ship). SkippyFlight keeps only the flight half — isolating the tower/setup concerns (not everyone
wants a tower) and reclaiming the ship's char budget. `role = base` board rendering is **removed from the
ship**; use `SkippyTower towerMode = board` instead (a byte-for-byte drop-in).

- [x] **Tower holding zone:** oriented box (`zoneCenter`/`zoneFwd`/`zoneUp`/`zoneExt`), `UpsertZone`,
      `[zone]` section persistence, appended to the heartbeat (`CMD|TOWER|<zone>|<gridId>|<center>|<fwd>|<up>|<ext>`).
- [x] **Tower interior paths:** `Pad.Path`; `CMD|PADPATH` reassembly (`OnPadPathChunk`, `padPathRx`) →
      `[pad.<name>]` `path` key; `StreamGrantPath`/`StreamPath` stream `CMD|PATH` on LAND (assigned pad)
      and DEPART (occupied pad); `PATH_CHUNK = 18` points/message.
- [x] **Ship zone destination:** `RECORD ZONE` captures the open-space pose + `destZone` flag
      (persisted in `WriteRoute`/`LoadRouteInto`); `InZone` arrival test; cruise/`TickHolding` loiter
      inside the box; `DrainIgc` TOWER branch parses the appended zone geometry.
- [x] **Ship interior follow:** `CMD|PATH` reassembly (`OnPathChunk` → `asgPath`/`asgPathReady`);
      `ArmInterior(reversed)` loads the follower; `CruiseCap()` creeps at `dockSpeed` while `interior`;
      Taxi threads inbound then connects; DepartStaging reverses out then rejoins cruise; latches
      (`interior`/`interiorDone`/`zoneWait`) reset per departure; open-air / no-path → straight-line.
- [x] **SkippyTower teach mode** (`towerMode = teach`, 2nd PB on the ship): `DiscoverTeach` (RC +
      connectors), breadcrumb recorder (`teachSeg`/`teachTurn`), `REGZONE [w h d]`, `REGPATH <pad>`
      (dock auto-finalizes path + pose), `REGPATH END`/`CANCEL`, `REGPAD <pad>`; streams
      `CMD|ZONE`/`CMD|PADPATH`/`CMD|PAD`; **no heartbeat, no clearance** — pure teaching tool.
- [x] **Ship budget reclaim:** removed `Role`/`RunBase`/board render/`Trim`+`WriteBaseSection`/`role`
      config from SkippyFlight, dropping the ship min from 100,824 (over) to fit.
- [x] **Grid-scoped clearance:** tower advertises its construct's `EntityId` in the heartbeat
      (`CMD|TOWER|<zone>|<gridId>|…`); ship names its target dock's grid in each request
      (`CMD|REQ|<ship>|<action>|<dock>|<grid>`) via `TargetGrid(forLanding)`; `TowerActive`/`CLEAR`/`HOLD`
      acceptance gated on `GridMatch(towerGrid, target)`; heartbeat adopted only when relevant to this
      route; `RECORD ZONE` stamps `destPose.BaseGridId = towerGrid`. **Fixes** a tower on one grid gating/
      clearing a ship bound for another; grid `0` (legacy tower / pre-scoping route) → accept-any.
- [x] File headers (both) document `CMD|ZONE`/`CMD|PADPATH`/`CMD|PATH`/extended heartbeat + teach
      commands + grid-scoped clearance; README updated (zone-as-destination, teach workflow, two-domain
      model, board-mode note).
- [x] Rebuild + budget check: `SkippyFlight.min.cs` **97,824 chars (2,176 headroom)**, braces balanced
      (517/517); `SkippyTower.min.cs` **24,845 chars (75,155 headroom)**, braces balanced (116/116).
      Tower → 0.13.0, ship → 0.12.0.
- [ ] In-world validation: teach a station (`REGZONE`, `REGPATH PadA` fly-in+dock) → tower board shows a
      defined zone + `PadA (Nwp)`, sections survive recompile; a zone-destination ship cruises in, halts
      *inside* the box, gets `PadA` + path, threads the interior at `dockSpeed`, docks on a pad it never
      recorded; two ships loiter at distinct spots, corridor serializes threading; egress reverses out to
      the zone and rejoins cruise; open-air pad / legacy tower falls back to straight-line; multi-chunk
      path reassembles (WP count matches).
- [ ] In-world validation (grid scoping): with two static grids on one channel, a tower on grid A does
      **not** gate/clear a ship whose route docks on grid B; a ship docking on grid A still obeys grid A;
      DEPART is gated by the grid being left (home outbound, dest inbound); a legacy tower still governs a
      same-grid ship.

### Later

- Multi-stop routes (generalize `Leg` beyond two poses).
- Per-item load manifests.

### Delivered extras (outside the slice sequence)

- **Multiple named routes + menu switcher (0.3.0).** Routes are stored one-per-section as
  `[route.<name>]` with a `[routes] active=<name>` pointer. `RECORD HOME <name>` names a route;
  a Routes menu page (Record ▸ Routes) lists them and APPLY switches the active one (blocked while
  operating); `ROUTE <name>` / `DELROUTE <name>` do the same by command. A legacy single `[route]`
  section migrates once to `[route.Main]`. This is distinct from "multi-stop routes" above — it is
  *many two-pose routes*, selectable, not one route with many stops. Stripped size after: 81,378
  chars (18,622 headroom).
- **Telemetry debug view (0.4.0).** A `telem` screen view surfacing live flight-law signals
  (phase + time-in-phase, speed vs governor cap, vertical rate, surface altitude, gravity
  magnitude, waypoint `i/N` + remaining distance, attitude error, H2/battery). Opt-in by
  assignment — a panel named `[SF:telem]` or a cockpit `[sf-screens]` entry `0 = telem`
  — so it never crowds the main info screen. `AlignTo` now latches `lastAlignErr` for the view,
  making an attitude stall visible live. Groundwork for Slice d's environment sensing (it already
  reads `TryGetPlanetElevation` and gravity magnitude). Stripped size after: 83,473 chars
  (16,527 headroom).

---

## Known gaps / risks (inherited from Skippy-Shuttle)

- Custom gyro + thruster controller; geometry- and mass-sensitive. Flight loop runs at 60 Hz;
  attitude gains (`gyroGain`/`gyroDamp`) live-tunable; fault-on-timeout prevents damage.
- Dampeners OFF while flying (fuel-free space coast), restored on stop/dock/fault/recompile.
- Obeys the world speed cap; absolute coordinates assume static grids only.
- No automated test harness is possible for PB scripts; all validation is in-world.
