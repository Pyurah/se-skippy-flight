# SkippyFlight Roadmap

Master tracking document for **SkippyFlight** — a phase-based flight controller for
Space Engineers Programmable Blocks. This project is the next-generation successor to
`Skippy-Shuttle` (v0.15.0), which stays frozen and in active use. SkippyFlight starts as a
faithful copy of that script and is refactored onto a phase-object architecture.

## Current status

- **Version:** 0.7.0 (Slice c delivered — scenario-aware cruise: each leg classifies from its two
  docks' recorded gravity into PlanetLocal / Ascent / Descent / SpaceLocal, and the single cruise
  splits into `Climb → Cruise → Descent` stages as the scenario demands. The stages reuse the exact
  cruise flight law with optional per-stage speed governors (`climbSpeed`/`descentSpeed`,
  default = `cruiseSpeed`, so a no-op until lowered) and distinct operator labels. Precise
  altitude-based boundaries stay in Slice d; this slice uses a coarse gravity/distance proxy. Built
  on Slice b's `DepartStaging`/`Holding`/`Taxi` phases with derived
  outer stand-off fixes and the local clearance gate. The ship assembles at a staging fix before
  flying and holds at an arrival fix before docking; only the clearance-gated `Taxi` phase ever
  touches a connector. Reorientation to the dock attitude is gravity-gated. Plus Slice a's
  phase-object base controller, multiple named routes, and the telemetry debug view. Block tags
  and Custom Data sections are now keyed to `SF` — `[SF]`/`[SF:LOAD]`/`[SF:CAM]`, the `[sf]` config
  section, and `[sf-screens]` — with lossless auto-migration from the old `[shuttle]` layout.)
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

### Slice e — tower clearance (shuttle side)

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
- [ ] Config `useTower` (Auto/Off), persisted in `[sf]` and per-`[route.<name>]` override;
      new row on the Depart settings page (`PAGE_DEPART` 7 → 8 items).
- [ ] State: `towerAge` (since last heartbeat), `towerActive = useTower==Auto && towerAge <
      TOWER_TIMEOUT`; per-pending-action `clearanceRequested` / `cleared` / `holdReason`; consts
      `TOWER_TIMEOUT`, `REQ_RESEND`.
- [ ] `DrainIgc` extended to parse `TOWER` (reset `towerAge`), `CLEAR` (set `cleared` for the
      addressed action), `HOLD` (keep holding + capture reason). `DEPART` force path unchanged.
- [ ] Helper `bool ClearedToProceed(string action, string dock)` — returns `!towerActive ||
      cleared`; while blocked, (re)sends `CMD|REQ` on the `REQ_RESEND` interval and sets the status
      line. Called as the final AND after `DepartureAllowed && DepartFuelOk` in `TickLoading`/
      `TickUnloading`, and after the corridor-clear + confirm dwell in the `Holding`→`Taxi` commit.
- [ ] Status surfacing: `Holding - awaiting tower (DEPART)`, the tower's `HOLD` reason when given,
      and `Tower: independent (no signal)` after a timeout revert. Telemetry view shows
      `Tower age`/`active`.
- [ ] Edge cases proven in-world: tower dies mid-hold → reverts to independent after
      `TOWER_TIMEOUT`; recompile mid-hold → re-`REQ`s and re-grants (no persisted clearance);
      operator `DEPART` during a tower hold → override wins; `useTower=Off` → byte-for-byte
      current behavior.
- [ ] Rebuild + budget check (`python tools/build-min.py`); record stripped size (est. modest —
      one setting, one gate helper, ~4 message cases, status strings; current headroom ~10,966).

### Slice f — SkippyTower.cs

The control tower as its own script (own char budget), speaking the Slice-e protocol. Extends the
existing passive base/board role into an active-control mode:

- Emits the `CMD|TOWER|<zone>` heartbeat on an interval — the presence signal that flips ships from
  independent to controlled.
- Consumes the existing status reports (it already builds the `fleet` table) plus incoming
  `CMD|REQ`; **serializes grants** so only one craft occupies the corridor/pad at a time, issuing
  `CMD|CLEAR` / `CMD|HOLD` with an optional reason. Ship side is oblivious — it just waits for its
  addressed grant.
- Pad registry + slot scheduling for a shared bank; optional `pose` in `CMD|CLEAR` assigns a
  dynamic pad (pairs with the "Tower-assigned fixes" note under *Staging & holding fixes*).
- A control-mode toggle so the same block can run as a plain status board (no heartbeat → fleet
  stays independent) or an active controller.

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
