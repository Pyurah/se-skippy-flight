# SkippyFlight

An autonomous two-connector **delivery shuttle** script for Space Engineers: a ship that ferries
cargo between two docking points — for example, a planet base and an orbital station — flying the
whole route itself, from undock to dock.

SkippyFlight is built on a **phase-object flight model** (undock → staging → climb → cruise →
descent → holding → taxi → dock). It assembles at a staging fix clear of the structure before
flying and holds at an arrival fix before docking, so it never dives straight onto a connector.
Each leg is **scenario-aware** — it detects from the two docks' gravity whether it's leaving a
planet, entering one, hopping between planet docks, or staying in space, and stages the flight
accordingly (see **Flight scenarios** below). A separate control tower for traffic management is
being built out slice by slice — see [roadmap.md](roadmap.md).

- **Flight-only, tower-optional.** Paste this script into the ship PB — it flies the whole route on
  its own. For a status board or an active traffic-control tower, run the companion
  [`SkippyTower`](SkippyTower.min.cs) on a station PB (`towerMode = board` renders the fleet board — a
  drop-in for the old `role = base`; `towerMode = control` serializes arrivals). SkippyTower also has a
  ship-side **teach mode** for recording holding zones and interior pad paths (see below).
- **Teach a route by flying it.** Dock, `RECORD HOME`, fly the route by hand, dock, `RECORD DEST`.
  The route is saved as copy-pasteable text so you can share it across a fleet.
- **Orientation-matched, ship-agnostic docking.** Recording captures the full docked pose —
  position, facing, and the connector's mating axis — so the shuttle reproduces the exact
  attitude it was recorded in. Works for connectors facing **any** direction (nose, top,
  bottom, side), on any ship with gyros and thrusters.
- **Custom flight controller.** A single gyro + thruster controller flies the whole route —
  undock, cruise, and dock — on a precomputed velocity profile with a √(2·a·d) braking curve.
  It turns to face each waypoint, accelerates on straights, slows through corners, and eases
  into the dock. No stock autopilot, so no weaving or circling between waypoints. While flying
  it runs at **60 Hz** so the heading stays rock-steady (a slow loop overshoots and wobbles),
  and it **coasts in space** — dampeners off, thrust cut once up to speed — so a straight cruise
  leg burns effectively **no fuel**.
- **Cargo-aware.** Watches cargo fill and enforces a mass gate so the ship departs when the
  hold is full (or empty at the destination) and never leaves overweight. It reads the cargo —
  it does not move it: conveyors, sorters, and drain-all stay entirely under your control.
- **Live status + ETA.** Ship LCDs show state/ETA; the shuttle broadcasts its status so a
  `SkippyTower` board (or control tower) can render the whole fleet.
  Split the display across cockpit screens — menu on one, trip info on another, a compact
  status on a third — each sized to its own content (see **Screen views** below).

---

## Install

1. Paste [`SkippyFlight.min.cs`](SkippyFlight.min.cs) into the ship's Programmable Block and
   recompile. On first run it writes a config template into the PB's **Custom Data**.
2. Edit the Custom Data (see below), then recompile again.
3. For a fleet status board or an active control tower, paste
   [`SkippyTower.min.cs`](SkippyTower.min.cs) into a station PB and set `towerMode = board` (passive
   board) or `control` (active tower). See [Control tower](#control-tower-skippytower).

> **Paste the `.min.cs`, not the source.** The readable source lives in
> [`SkippyFlight/Program.cs`](SkippyFlight/Program.cs) (fully commented); with comments and formatting
> it's well over the 100,000-char PB limit, so it won't compile in-game as-is.
> [`SkippyFlight.min.cs`](SkippyFlight.min.cs) is the paste-ready deploy artifact — the same code
> minified to **49,208 chars** (~51 k under the cap). Keep editing `Program.cs`; the min file is
> generated. See [Building](#building) below.

## Custom Data (`[sf]` section)

| Key | Default | Meaning |
|---|---|---|
| `shipName` | `Skippy` | Label shown on the tower/board |
| `channel` | `SkippyShuttleNet` | IGC channel — **must match** on ship and tower |
| `useTower` | `off` | `off` (default) flies independently; `auto` obeys an optional control tower — the ship requests clearance before undocking and before taxiing onto a connector, holding until granted. If no tower is broadcasting on the channel it stays independent, so `auto` is safe to leave on |
| `runMode` | `CONTINUOUS` | Trip cycle: `CONTINUOUS`, `ONETRIP`, or `ONEWAY` (departure is a *separate* setting — see below) |
| `homeTrigger` | `Auto` | What releases the shuttle **from home**: `Auto`, `Cargo`, `Timer`, `Manual` |
| `destTrigger` | `Auto` | What releases the shuttle **from the destination** (same four options) |
| `dwellSec` | `30` | `Timer` trigger: seconds to dwell at the dock before departing regardless of fill |
| `minHydrogenPct` | `10` | Hard floor — never depart below this hydrogen %. Ignored if the ship has no hydrogen tanks |
| `minBatteryPct` | `10` | Hard floor — never depart below this battery charge %. Ignored if the ship has no batteries |
| `fuelMarginPct` | `25` | Safety margin on the measured per-leg fuel/charge estimate |
| `remoteName` | *(blank)* | Blank = auto-find a Remote Control on the grid |
| `lcdTag` | `[SF]` | LCDs whose name contains this tag show status |
| `cruiseSpeed` | `100` | Cruise speed cap (m/s); the controller stays at or below this |
| `climbSpeed` | `100` | Top-speed cap (m/s) while in the **Climb** stage of a leg that ascends out of gravity. Clamped to `(5, cruiseSpeed]`. Defaults to `cruiseSpeed`, so the climb governor does nothing until you lower it |
| `descentSpeed` | `100` | Top-speed cap (m/s) while in the **Descent** stage of a leg that drops into gravity. Clamped to `(5, cruiseSpeed]`. Defaults to `cruiseSpeed`; lower it for a gentler braked descent into a planet dock |
| `dockSpeed` | `5` | Final-approach speed cap (m/s, controller) |
| `maxMassKg` | `0` | `0` = no gate; otherwise stop loading near this mass |
| `departFill` | `95` | Cargo fill % that triggers departure |
| `unloadDrainSec` | `30` | Max seconds spent unloading before leaving |
| `segMeters` | `250` | Breadcrumb spacing on straight runs |
| `turnDegrees` | `12` | Extra breadcrumb when heading turns this much |
| `simplifyMeters` | `15` | How far the recorded path may bow from a straight chord before a waypoint is kept. Straight runs collapse to their endpoints, so the 250-waypoint budget goes to turns. `0` = off (dense recording) |
| `approachDist` | `15` | **Inner** on-axis stand-off (m) — the taxi start point, where the ship commits down the connector axis onto the dock |
| `holdDist` | `40` | **Outer** on-axis stand-off (m) — the departure **staging** fix and arrival **holding** fix, where the ship assembles clear of the structure before flying and holds before docking. Forced ≥ `approachDist` + 5. Per-dock override via `homeHoldDist` / `destHoldDist` keys in the `[route]` section |
| `gyroRpmCap` | `0` | Gyro rate cap (RPM) for gentle rotation. `0` = auto (15 small grid / 5 large) |
| `brakeFrac` | `0.6` | Fraction of the weakest thrust axis used for braking/cornering (headroom for gravity + saturation). Lower = brakes earlier/gentler. Clamped 0.1–1.0 |
| `cornerLen` | `30` | Corner-rounding length (m); also the look-ahead blend distance into turns. Larger = wider, faster corners |
| `gyroGain` | `4` | Attitude P gain — how hard the gyros rotate toward the target heading. Higher = snappier turns |
| `gyroDamp` | `3` | Attitude damping. Raise it if a hull still wobbles/overshoots/jiggles onto heading; lower toward `2` for snappier turns |
| `cruiseAttitude` | `auto` | Attitude while flying **in gravity**. `auto` = fly level (belly-down VTOL climb) if the hull is lift-heavy, else nose-to-path; `level` = force VTOL climb (strong down-thrusters lift); `nose` = force nose-along-path. In space it always flies nose-forward regardless |
| `dockClearCheck` | `true` | Anti-collision: raycast the docking corridor on final approach and **hold off** if another grid is parked on / crossing the connector, resuming when clear. Set `false` to disable |
| `cameraTag` | `[SF:CAM]` | Cameras that watch the dock (name **contains** the tag). If none are tagged, every camera on the grid is used and the one actually facing the dock is picked automatically |
| `dockBlockSec` | `0` | Seconds a blocked corridor is tolerated before the shuttle **faults**. `0` = wait indefinitely — so a false positive only ever costs time, never a fault |

The recorded route lives in a separate `[route]` section that the script writes for you.
**To clone a route to another identical ship, copy that whole `[route]` section into its PB.**

> **Upgrading from a `[SHUTTLE]` build (≤ 0.5.1).** As of 0.6.0 the config section is `[sf]`,
> the cockpit screen-map section is `[sf-screens]`, and the default block tags are the `[SF]`
> family. Your existing config migrates automatically on first recompile — every key is copied
> from `[shuttle]` into `[sf]` and nothing is lost — and legacy `[shuttle-screens]` cockpit
> sections are still read. Your existing `[SHUTTLE]`-named panels and cameras keep
> working too, because the tags are stored *values* that survive the migration. To switch a
> grid over to the new `[SF]` tags, edit the tag keys in `[sf]` (or clear Custom Data to
> re-seed the `[SF]` defaults) and rename the blocks to match.

### Cargo handling

**Loading and unloading logistics are yours.** SkippyFlight does not enable, disable, or move
items through any sorter, conveyor, or container — it only *watches* cargo fill to decide when
the hold is full (depart from home) or empty (delivered / depart from the destination), and
enforces the optional `maxMassKg` gate. Wire the actual movement however you like: an event
controller that runs drain-all sorters on connector lock, timers, throw-out, filters — all of
it stays under your control and the script never fights it.

> Because the script only senses fill, make sure your setup actually fills the hold at home and
> empties it at the destination within your chosen departure trigger. With the `Auto` trigger the
> ship still leaves the destination after `unloadDrainSec` seconds as a safety net even if the
> hold isn't fully empty; `Cargo` waits for a genuinely full/empty hold; `Timer` dwells for
> `dwellSec`; `Manual` waits for a DEPART command.

## Teaching a route

1. Manually dock the ship at its **home** connector.
2. Run `RECORD HOME`. Fly straight out ~50 m, then continue to the destination by hand.
3. Manually dock at the **destination** connector.
4. Run `RECORD DEST`. The route is saved.

The recorder is **self-thinning**: it drops a breadcrumb every `segMeters` and at every
heading change of `turnDegrees`, but then collapses breadcrumbs that lie on a straight line
back onto a single segment (within `simplifyMeters` of the chord). So a long straight cruise
costs ~2 waypoints while a twisty approach keeps full detail — the 250-waypoint cap is spent
where precision matters. If a route ever reports **"Path full"**, raise `segMeters` (coarser
sampling) or `simplifyMeters` (more aggressive straightening) and re-record. Set
`simplifyMeters = 0` to disable straightening and record densely.

At `RECORD` time the script also captures the **natural-gravity magnitude** at each connector
(`homeG`/`destG` in the route section). That's what drives scenario detection below — so a route
recorded before 0.7.0 (which has no gravity keys) is treated as space↔space and flies a plain
cruise. Re-record such a route once if you want climb/descent staging on it.

## Flight scenarios

Every leg is classified from the two docks' recorded gravity, then flown as the matching sequence
of cruise-family stages. All three stages (**Climb / Cruise / Descent**) are the *same* flight
controller over the *same* recorded path — they differ only in an optional per-stage speed cap
(`climbSpeed` / `descentSpeed`) and the status label. The inbound leg is the outbound leg's mirror
(an ascent home→dest is a descent dest→home) with no extra configuration.

| Scenario | Detected from | Flight stages | Stage handoff |
|---|---|---|---|
| **SpaceLocal** | both docks in space | Cruise | — (identical to pre-0.7 behavior) |
| **Ascent** | home in gravity, dest in space | Climb → Cruise | switches to Cruise once the ship clears the gravity well |
| **Descent** | home in space, dest in gravity | Cruise → Descent | switches to Descent as the ship enters the gravity well |
| **PlanetLocal** | both docks in the same gravity | Climb → Cruise → Descent | reads the recorded altitude the ship is flying: switches to Cruise when the climb levels off, and to Descent on a sustained sink toward the dock |

The Ascent/Descent handoffs trigger on the gravity-well boundary; the PlanetLocal handoffs read the
ship's real sea-level altitude trend (the recorded route already *is* the altitude plan, so there is
nothing to configure). While climbing or descending inside a gravity well the status shows an
`!xfer` marker for the atmosphere/gravity thrust handoff. Because both governor caps default to
`cruiseSpeed`, climb and descent fly at the same speed as cruise out of the box — lower
`descentSpeed` (for example) only when you want a gentler braked drop into a planet dock.

## Commands (run-argument on the ship PB)

| Command | Effect |
|---|---|
| `RECORD HOME [name]` | Bind the docked connector as home; start recording the path. `name` (optional) saves it as a named route — omit to re-record the active route (or `Main`) |
| `RECORD DEST` | Bind the docked connector as destination; finish + save the route |
| `RECORD ZONE` | Finish the route at a **tower holding zone** instead of a connector: while loitering in open space inside the zone, save the live pose as the destination (arrival = *inside the box*). For stations you don't own — see [Holding zones](#holding-zones--interior-pad-paths) |
| `ROUTE [name]` | Switch the active route to a saved one (blocked while operating). No name = report the active route + saved count |
| `DELROUTE <name>` | Delete a saved route; if it was active, fall back to another (or none) |
| `START` / `GO` | Begin operating per the run mode |
| `STOP` | Abort the flight, return to Idle |
| `HOME` | Fly back to the home connector and dock |
| `DEPART` | Release the shuttle from the dock it's holding at **now** (overrides its trigger) |
| `MODE CONTINUOUS\|ONETRIP\|ONEWAY` | Change the run mode live (`WAITFULL` still accepted — maps to Continuous + `homeTrigger=Cargo`) |
| `RESUME` | Continue the saved state after a recompile |
| `CLEARROUTE` | Delete the **active** route (falls back to another saved route, or none) |
| `UP` / `DOWN` | Move the LCD menu cursor (or change a value while editing) |
| `APPLY` | Select the highlighted item / save the value being edited |
| `BACK` | Leave a submenu / cancel an edit |

### LCD menu (bind to cockpit buttons)

Every command above is still usable as a run-argument, but day to day you drive the shuttle
from the **on-screen menu**. Bind four cockpit toolbar buttons to run the PB with the
arguments `UP`, `DOWN`, `APPLY`, and `BACK`. The ship's tagged LCDs (and the PB's own screen)
show a status header with a `>` cursor menu beneath it:

- **Main:** Start/Stop, Run Mode (APPLY cycles it), **Depart Now**, Go Home, and entries into
  the submenus. *Depart Now* releases the shuttle from the dock it's holding at right now.
- **Record:** Record Home connector, Record Dest connector, Clear Route, and a **Routes >>**
  entry. The **Routes** page lists every saved route (the active one marked `*`); `APPLY` loads the
  highlighted route (blocked while operating — `STOP` first). To *create* a named route, type
  `RECORD HOME <name>` as a run-argument (the menu is button-only, so it can't take a name); the
  menu's *Record Home* re-records whichever route is currently active.
- **Settings:** Cruise Speed, Dock Speed, Max Mass (tonnes), Depart Fill %, and a **Depart >>**
  entry into the departure page — `APPLY` to edit, `UP`/`DOWN` to change, `APPLY` to save, `BACK`
  to cancel. Every saved value is written back to Custom Data, so it survives recompiles.
- **Depart:** cycle **Home Trigger** / **Dest Trigger** (Auto/Cargo/Timer/Manual) and edit
  **Dwell** (Timer seconds), **Min H2 %**, **Min Batt %**, and **Fuel Margin %** in place.

### Screen views (split the display across screens)

By default every tagged screen shows the **full** display — the status header plus the
interactive menu. If that's too crowded (e.g. a cockpit with several screens), you can give
each screen a *different* view. Each screen also **sizes its font to its own content**, so a
small screen no longer shrinks a big wall LCD.

Five views:

| View | Shows |
|---|---|
| `full` *(default)* | Status header + interactive menu (unchanged from before) |
| `menu` | Just the interactive menu — the screen you drive from (no status header; pair it with a `status` screen) |
| `status` | Compact block: `-- Status --` / `<State> [RUN\|STOP]` / *(blank)* / `-- Cargo --` / `<fill>%  <mass>t  <speed>m/s` |
| `trip` | Route, current phase, ETA + distance while cruising, and the transient status line (delivered / holding at destination / holding at home / fuel-hold) |
| `telem` | In-flight **instrument + diagnostic** readout: phase/run/time, speed vs the active cap, a **speed-derate line** (`Drt a<align> v<vel> br<brake> c<cap>`), vertical rate, gravity, surface altitude, waypoint progress + remaining distance, attitude error, and fuel. The `Drt` line answers *"why won't it reach `cruiseSpeed`?"* — the controller flies `speed = min(cap, brake) × align × vel`, so a steady shortfall shows as `a<1` (nose off the path), `v<1` (velocity vector off the path), or `br<c` (braking curve pulling toward a near waypoint). |

Assign a view two ways:

- **Standalone LCD — name tag.** Append `:view` to the base tag in the panel's name:
  `[SF:trip]`, `[SF:telem]`, `[SF:menu]`, `[SF:status]`. A bare `[SF]` stays `full`.
- **Cockpit / multi-surface block — Custom Data.** Add an opt-in `[sf-screens]` section
  to the block's Custom Data mapping each surface index to a view:

  ```
  [sf-screens]
  0 = menu
  1 = trip
  2 = status
  ```

  Surface indices are the same ones the game numbers the block's screens with. This is the
  3-screen-cockpit case; because it's opt-in, an unconfigured cockpit is never touched. It
  works for the Programmable Block's own screens too.

**Pin a fixed font size** on any screen if you don't want auto-fit: `[SF:status:1.4]`
(name tag) or `2 = status@1.4` (Custom Data). Omit the size to keep the per-screen auto-fit.

**Pin the padding too** — the screen's `TextPadding` (% inset per side, clamped 0–40) as a
persistent option the auto-fit respects, so a cramped surface can breathe and won't reset on
the next recompile. Append it after the size: `[SF:status:1.4:6]` (name tag,
`:view:size:pad`) or `2 = status@1.4/6` (Custom Data, `view@font/pad`). Leave the font on
auto-fit while still setting padding with `[SF:status::6]` or `2 = status@/6`. The
auto-fit subtracts the padding from the usable area, so padded text still fits.

> **Recompile after editing `[sf-screens]`.** Screen assignments (and the base tag on
> LCD names) are read when the script discovers blocks, which happens on recompile. Change
> the section, then recompile the PB to see the new layout.

> Cockpit/ship screens only. The station board (SkippyTower) still shows its shuttle list; per-view
> station "marquees" are on the roadmap.

### Run modes vs. departure triggers

The **run mode** decides the *trip cycle*; the **departure trigger** decides *when each leg
starts*. They're independent — e.g. `CONTINUOUS` + `homeTrigger=Manual` loops forever but waits
for a `DEPART` at home each time.

**Run modes (`runMode`):**

- **CONTINUOUS** — loops forever: load → fly → unload → return → repeat, until `STOP`.
- **ONETRIP** — one round trip on `START`/`GO`, then waits.
- **ONEWAY** — one leg per `START`, then **holds at the far end** instead of returning.
  Docked at home, it loads, flies to the station, unloads, and waits there. The next
  `START` flies it straight back home and waits again. It decides which way to go from
  **which end it's physically parked at** (by proximity to the two recorded docked poses),
  so it works even on a ship that mates both ends with the **same connector**, and always
  knows whether it's sitting at home or at the station — you never have to tell it. Good
  for "take this load over and stay put until I send you back."

**Departure triggers (`homeTrigger` / `destTrigger`, per end):**

- **Auto** *(default)* — leave as soon as the cargo op finishes (loaded at home / emptied at the
  destination, keeping the unload drain-timeout safety net).
- **Cargo** — wait until the hold is genuinely full at home (`departFill`% / mass gate) or empty
  at the destination before leaving. This is what the old `WAITFULL` mode did.
- **Timer** — dwell at the dock for `dwellSec`, then depart regardless of fill.
- **Manual** — hold at the dock until a `DEPART` arrives (the ship's *Depart Now* button, the
  `DEPART` run-arg, or a `DEPART` broadcast from the base). Nothing leaves on its own.

A `DEPART` (button or command) always overrides the current trigger and releases the shuttle
immediately — subject only to the fuel/battery gate below.

### Fuel & battery gate

Whatever the trigger says, the shuttle won't leave a dock without enough hydrogen **and** charge
to reach the next one. It measures what each leg actually costs (per direction, and remembers it
across recompiles) and requires the current level to clear that estimate plus `fuelMarginPct`,
never dropping below the `minHydrogenPct` / `minBatteryPct` floors. Before it has flown a leg it
uses the floors alone. When it's gated it simply **holds at the dock** with a "low H2/charge"
status and departs the moment the level is met — it never faults. A ship with no hydrogen tanks
skips the hydrogen check; one with no batteries skips the charge check.

## Fleet status board

A status board is no longer part of the ship script — run [`SkippyTower`](SkippyTower.min.cs) on a
station PB in **`towerMode = board`** for a passive board (a byte-for-byte drop-in for the old
`role = base`), or `towerMode = control` for an active tower that also renders the board. Tag the
station's LCDs with `[SF]` (or your `lcdTag`). The board shows each shuttle's state, ETA, distance,
cargo % and mass, and flags **NO SIGNAL** if a shuttle drops off the network (e.g. beyond antenna
range). See [Control tower](#control-tower-skippytowercs) below.

> If your route is longer than the antenna's broadcast range, the board blanks to **NO SIGNAL**
> while the shuttle is out of range. Place a relay antenna near the midpoint for an unbroken
> board — the shuttle still flies fine without signal; only the board blanks while out of range.

## Control tower (`SkippyTower`)

For a station with more than one shuttle, `SkippyTower` is a **separate** script that actively
serializes traffic so only one craft maneuvers at the station at a time — no more two shuttles both
undocking into, or both taxiing onto, the same corridor. It renders the fleet status board and, in
control mode, clears traffic on top. The **same script** also has a ship-side **teach mode** for
recording holding zones and interior pad paths — see [Holding zones](#holding-zones--interior-pad-paths).

**Install:** paste `SkippyTower.min.cs` into a station Programmable Block (its own block — not the
same PB as a shuttle or a plain board), recompile once to seed the `[sf]` template, edit, recompile
again. On the shuttle side, set each shuttle's `useTower = auto` (see the config table above) so it
obeys the tower; a shuttle left at `off` ignores it.

| Key | Default | Meaning |
|---|---|---|
| `channel` | `SkippyShuttleNet` | IGC channel; must match the fleet |
| `zone` | `Main` | Label broadcast in the heartbeat (operator-facing; ships ignore the content) |
| `lcdTag` | `[SF]` | Board is written to `Me`'s surface and every LCD whose name contains this tag |
| `towerMode` | `control` | `control` = active tower (heartbeat + clearances); `board` = passive status board with **no heartbeat**, so the fleet stays independent (a drop-in for `role = base`); `teach` = ship-side setup helper (see [Holding zones](#holding-zones--interior-pad-paths)) |
| `grant` | `auto` | Control-mode sub-mode. `auto` = clear the best waiting craft automatically every tick; `manual` = hold all traffic until you approve each one by hand. Toggled at runtime (see commands below) and persisted here, so it survives a recompile. Ignored in `board`/`teach` mode |
| `remoteName` | *(blank)* | **Teach mode only:** exact name of the ship's Remote Control to read; blank = first one found on the grid |
| `teachSeg` | `2.5` | **Teach mode only:** interior-path breadcrumb spacing in metres (fine, because station walls are close) |
| `teachTurn` | `12` | **Teach mode only:** drop an extra breadcrumb when the heading changes by this many degrees over a short move |

**How clearance works:** the tower broadcasts a `CMD|TOWER` heartbeat; a shuttle running `useTower =
auto` that hears it switches from independent to controlled and requests clearance before undocking
and before taxiing onto a connector, holding until granted. The tower grants **one craft at a time**
— a waiting landing is served before a waiting departure — and holds the slot until that craft has
cleared the corridor (departed to cruise, or docked). If the tower is destroyed or unpowered, ships
stop hearing the heartbeat and revert to independent operation within a few seconds, so a dead tower
never strands the fleet. The board tags the cleared craft (`> CLEARED`) and any waiting one
(`|| HOLD (traffic)`), with a `Slot:` footer.

**Manual approval (be the controller):** by default the tower auto-approves. Set `grant = manual` (or
run the `MANUAL` command) to "man" it — every request then holds, tagged `|| WAITING (your OK)`, and
you approve each one by hand. Run these by typing the verb in the tower PB's **Run** argument (or bind
it to a button/sensor); they only act in `control` mode:

| Command | Effect |
|---|---|
| `MANUAL` | Take the controls — stop auto-granting; hold every request for your OK |
| `AUTO` | Hand back — resume auto-granting the best waiting craft |
| `CLEAR` | Approve the top of the queue (a landing before a departure, then oldest-first) |
| `CLEAR <ship>` | Approve a specific waiting ship by name (queue-jump) |
| `RELEASE` | Force-free the current slot now (deadlock breaker; skips the 180 s safety timeout) |
| `PADFREE <name>` | Force-free a pad's occupancy (pad-bank deadlock breaker; see below) |

The heartbeat keeps beating in both sub-modes, so a ship held for your approval stays controlled (it
never reverts to independent while you deliberate); once you clear it and it departs or docks, the
slot frees automatically and the next craft waits for your next `CLEAR`. The board's mode line reads
`CONTROL/AUTO` or `CONTROL/MANUAL`, and while manual with a free slot the footer shows
`Next: <ship> (<action>) - run CLEAR`.

> The tower keeps no saved state — its fleet list and clearance queue rebuild live from broadcasts,
> so a recompile mid-traffic simply re-establishes from the next round of reports and requests.
> (Registered pads are the one exception — they persist in `[pad.<name>]` Custom Data sections.)

### Multiple grids on one channel (grid-scoped clearance)

Clearance is bound to a **grid**. Each tower advertises its own construct's id in its heartbeat, and
every ship names the grid of the dock it's heading to (or leaving) in each request. A ship only obeys —
and only accepts clearance from — the tower on the grid it is actually arriving at or departing from.

So you can run several independent stations on the **same channel** (even sitting close together, or
next to a grid owned by someone else): each tower governs only its own traffic, and a tower can never
gate or clear a ship bound for a different grid. Connector routes carry the dock's grid automatically
(recorded by `RECORD HOME`/`RECORD DEST`); a holding-zone route picks up the governing grid when you
`REGZONE` it. A tower or route from before this change (no grid id) falls back to the old accept-any
behavior, so nothing breaks — re-record a route to enable scoping for it.

### Pad bank (parking more than one ship)

By default each shuttle docks at its **own recorded destination pose** — fine when every ship has its
own dedicated connector. For a station with a **pool of interchangeable pads** and several shuttles,
turn on the **pad bank**: the tower assigns a *free* pad to each arriving ship, so ships **park in
parallel** while movement stays serialized (still only one craft maneuvers at a time). No more hovering
and deadlocking behind a ship already parked on the one connector.

**Register the pads (once):** pads are taught with SkippyTower running in **teach mode** on a PB aboard
the ship (see [Holding zones](#holding-zones--interior-pad-paths) for the full setup). Dock the ship at a
station pad and run `REGPAD <name>` in the **teach PB's** Run argument. It records the exact dock pose and
pushes it to the station tower, which stores it as a `[pad.<name>]` section and lists it on the board
(`<name>: free`). Repeat for each pad, giving each a single-word name (`REGPAD A`, `REGPAD B`, …). You only
register each pad **once, with one ship** — the tower relays that pose to whichever ship it later assigns.

> **Requires a geometrically uniform fleet.** A recorded pose is a specific ship's docked position and
> attitude; relaying it to a *different* ship only seats correctly if the fleet's ships are dimensionally
> identical (same connector-to-hull geometry) — as drones built to one spec are. Mixed hull sizes will
> mis-seat. If you run non-uniform ships, don't register pads (leave the bank empty) and each ship docks
> at its own recorded pose as before.

**What happens then:** an arriving ship's landing clearance now needs both the corridor free **and** a
free pad. The tower reserves a pad, relays its pose in the grant, and the ship flies that pad's approach.
A pad stays occupied until its ship departs. If every pad is taken, further landings hold with
`HOLD: no pad` (and a waiting departure is cleared first, since departing frees a pad). The board gains a
**Pads** block listing each pad as `free` or the ship holding it. `PADFREE <name>` force-frees a pad
whose ship vanished without departing cleanly.

**Fully optional / backward compatible:** register no pads (or run no tower) and nothing changes — ships
dock at their own recorded pose exactly as before.

### Holding zones & interior pad paths

The pad bank above assumes an **open-air dock** — a straight line from the outer approach fix to the pad
is clear sky. But the hard case is a station you **don't own**: a hole-in-the-wall entrance and a bank of
pads at varied angles *inside* the structure. A straight line from the outer fix to an interior pad punches
through a wall. Holding zones split the trip into two ownership domains:

```
  ship-owned                              tower-owned (per assigned pad)
  ─────────────────────────────────────  ──────────────────────────────
  home → cruise breadcrumbs → HOLDING ZONE → interior path → PAD
                                          (relayed on grant, chunked)
```

- The route's **destination is a tower-owned holding zone** — an oriented box in open space just outside
  the entrance — not a connector. Arrival is satisfied by being **anywhere inside the box**, so a queue of
  ships loiters spread out instead of fighting for one point.
- Each pad stores a **recorded interior path** (holding-zone → pad), taught once and kept on the tower.
- On landing clearance the tower assigns a free pad, relays its pose **and streams that pad's interior
  path** to the ship. The ship flies straight to the path's first crumb (safe — open space just inside the
  zone), then **follows the interior path at `dockSpeed`** to the pad and docks — on a pad and corridor it
  never recorded itself.
- On departure the tower re-streams the same path; the ship **reverses** it to thread back out to the zone,
  then rejoins its recorded cruise route home. (v1 egress reuses the inbound path; there's no separate
  takeoff corridor.)
- **Manual fly-in:** because arrival is "inside the box," you can hand-fly a ship into the zone, hit
  `START`, and it will request landing and auto-park via the relayed path — no recorded route needed.
- **Fallback:** if no path is relayed (an owned open-air dock, or a legacy tower), taxi keeps the
  straight-line behavior. Scenario 1 (recorded home ↔ dest) and the plain pad bank are untouched.

**Teaching a station (SkippyTower teach mode).** Zones, pads, and interior paths are all recorded with a
**second PB aboard the ship** running `SkippyTower.min.cs` with `towerMode = teach`. Teach mode is
**listen-free and driver-free**: it never registers a listener, never beats a heartbeat, and never touches
thrusters or gyros — it only reads the ship's Remote Control position to record breadcrumbs and pushes the
results to the station's real tower over the fleet channel. That keeps your flight PB lean (flight
automation only) and makes it safe to run alongside a live fleet.

Set it up once: paste `SkippyTower.min.cs` into a spare PB on the ship, set `towerMode = teach` and matching
`channel`, recompile. Optionally set `remoteName` if the ship has more than one Remote Control. Then, from
that PB's Run argument:

| Teach command | What it does |
|---|---|
| `REGZONE [w h d]` | Define the holding zone as a box centred on the ship's current position, `w`×`h`×`d` metres (default `20 20 20`), oriented to the ship. Pushes `CMD\|ZONE` to the tower; the tower advertises it in its heartbeat and persists it in a `[zone]` section |
| `REGPATH <pad>` | Start recording an interior path for `<pad>`, seeded at the current position. Fly the ship in toward the pad; breadcrumbs drop as you move |
| *(dock the ship)* | Docking the connector **auto-finalizes**: the recorded path is streamed to the tower as `CMD\|PADPATH` and the exact dock pose as `CMD\|PAD`. The pad now shows `<pad> … (Nwp)` on the board |
| `REGPATH END` | Finish a path manually without docking (streams what's recorded so far) |
| `REGPATH CANCEL` | Discard the in-progress recording |
| `REGPAD <pad>` | Register just the dock pose for `<pad>` (no interior path) — the same open-air pad registration as the pad bank, issued from the teach PB while docked |

Breadcrumb density is tuned by `teachSeg` (metres between crumbs, default `2.5` — fine, since station walls
are close) and `teachTurn` (drop an extra crumb when the heading swings this many degrees, default `12`).
The teach PB writes a status board to its own screen and any `[SF]`-tagged panel so you can watch the
recording. Everything it teaches lands in the **station** tower's Custom Data (`[zone]`, `[pad.*]` with a
`path=` key) and survives a recompile there.

**Fully optional:** no zone taught and no path streamed → ships behave exactly as the plain pad bank (or
single-pose docking) above. You only need teach mode for stations you don't own.

## Limitations (honest)

- Uses **absolute world coordinates**. Correct for static grids (base + station). Do not use
  it to dock with a grid that moves.
- Docking requires the ship to have **gyros and thrusters** with authority on every axis
  (including against gravity at a planet base). The controller reproduces the recorded docked
  attitude and drives straight down the connector axis; the connector magnet completes the
  mate. If it fails to seat, the approach times out after 45 s and the shuttle **faults**
  rather than grinding on the dock — widen `approachDist`, lower `dockSpeed`, or check that
  the recorded connector axis is clear of obstacles.
- **Dock-clearance is camera-based and identity-aware.** On final approach the shuttle raycasts
  the corridor to the dock and holds off if a *foreign* grid is in the way, auto-resuming when
  clear. It knows the base from an intruder because `RECORD HOME`/`RECORD DEST` capture the base's
  grid id — so the base's own connector and off-axis neighbours never false-trigger; only a ship
  genuinely in the approach path holds it. It needs **a camera that can see the dock**: mount one
  facing the connector's approach axis (tag it `cameraTag`, default `[SF:CAM]`, or leave all
  cameras untagged and the shuttle picks whichever faces the dock). With no camera that can see the
  corridor it degrades gracefully — it simply docks as before (no false holds). A blocked corridor
  **waits forever by default** (`dockBlockSec = 0`); set `dockBlockSec` to fault after N seconds if
  you'd rather it give up. An **imported route that predates base-grid-id capture** falls back to a
  coarser distance rule — re-record it to enable the identity-aware check. Disable the whole check
  with `dockClearCheck = false`.
- Control gains are tuned conservatively but every ship is different. The attitude gains
  `gyroGain` (turn snappiness) and `gyroDamp` (wobble damping) are **live-tunable in Custom
  Data**. The flight controller now runs at 60 Hz while flying, so the heading holds steady and
  raising `gyroDamp` no longer makes turns sluggish — raise it only if a specific hull still
  hunts onto heading, or lower toward `2` for snappier turns. `VEL_GAIN` and `APPROACH_KP`
  remain script constants. Cruise behaviour is likewise tunable from Custom Data:
  `brakeFrac` (how early/gently it brakes), `cornerLen` (corner tightness), `gyroRpmCap`
  (max rotation rate — lower it to calm big-angle turn overshoot), and `cruiseAttitude`
  (`auto`/`level`/`nose` — how the ship orients while climbing/descending in gravity;
  `auto` flies level on lift-heavy hulls so the strong down-thrusters do the climbing).
- **The script controls your dampeners while flying.** To coast without fuel in space it turns
  dampeners **off** during undock/cruise/dock and restores them **on** when it stops, docks,
  faults, or is recompiled — so a parked or hand-flown ship always holds position. Gravity legs
  keep thrusting (hover compensation), so the ship never sags at the planet base. If you take
  manual control mid-flight, re-enable dampeners yourself (the ship's Z / dampener toggle).
- The controller obeys the world's speed cap (100 m/s by default). Setting `cruiseSpeed` above
  the world cap won't make the ship go faster — the game clamps it.
- An **imported route that stored position only** (from a version predating orientation capture)
  still loads, with orientation synthesised as a nose-first approach. Re-record it to capture the
  true docked orientation.
- The **fuel/battery departure gate learns by flying.** Its per-leg estimate is measured from the
  last completed leg in each direction, so the *first* departure after a fresh compile (or after
  re-recording the route) is gated only by the `minHydrogenPct` / `minBatteryPct` floors — set
  those high enough to cover a leg if you don't want to rely on the first measurement. A ship with
  no hydrogen tanks or no batteries skips that half of the check entirely.
- This is an in-game PB script; it cannot be unit-tested outside Space Engineers. Validation
  is in-world (see [roadmap.md](roadmap.md)).

## Building

The scripts are built with the [MDK2](https://github.com/malforge/mdk2) toolchain — you only need
this if you're changing the code; to *use* the scripts just paste the committed `.min.cs` files.

**Prerequisites:** the .NET SDK (9.0+), Space Engineers installed (MDK2 references the game's assemblies),
and the MDK2 templates (`dotnet new install Mal.Mdk2.ScriptTemplates`). On first checkout, copy
`mdk.local.ini.example`-style settings into each project's `mdk.local.ini` (git-ignored) pointing
`binarypath` and `output` at your machine — `auto` works for both if SE is installed in the default
location.

**Build both scripts:**

```
dotnet build SkippyFleet.sln -c Release
```

For each project this: (1) compiles with Roslyn and the `Mal.Mdk2.PbAnalyzers` PB-whitelist analyzer —
a real compile check, not just a brace count; (2) packs with **full minification** (whitespace stripped,
identifiers renamed to short Unicode names) straight into the game's local script folder
(`%AppData%\SpaceEngineers\IngameScripts\local\<name>\script.cs`) so it's instantly available in-game;
and (3) copies that packed output back into the repo as `SkippyFlight.min.cs` / `SkippyTower.min.cs` —
the committed, diffable deploy artifact.

**Editing:** the readable source of truth is [`SkippyFlight/Program.cs`](SkippyFlight/Program.cs) and
[`SkippyTower/Program.cs`](SkippyTower/Program.cs). Keep comments and formatting — the minifier removes
them at pack time, so documentation is free. The `.min.cs` files are generated; never hand-edit them.
The minifier renames identifiers but never touches string literals, so wire tokens (`CMD|…`), Custom
Data keys, and screen text survive verbatim.
