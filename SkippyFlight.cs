/*//////////////////////////////////////////////////////////////////////////////
 * SkippyFlight - Autonomous two-connector delivery shuttle for Space Engineers.
 * For pure ferry duty: cargo between two docks (e.g. a planet
 * base and an orbital station). Full docs in README.md / CHANGELOG.md.
 *
 * ONE script, TWO roles - paste into both PBs; role is set in Custom Data:
 *   role = shuttle : flies the route and manages cargo.
 *   role = base    : renders every shuttle's status + ETA to tagged LCDs.
 *                    ("station" is accepted as an alias for base.)
 *
 * QUICK START (ship): recompile once (writes a Custom Data template, edit + recompile
 * again) -> dock at home, RECORD HOME -> fly to the destination by hand -> dock there,
 * RECORD DEST (route saved under [route]; copy that section to clone it) -> START.
 *
 * COMMANDS (run-arg on the ship PB): RECORD HOME | RECORD DEST | START/GO | STOP |
 *   HOME | DEPART (release the current dock now; on a base PB broadcasts DEPART, or
 *   DEPART <shipName>, to the fleet) | MODE CONTINUOUS|ONETRIP|ONEWAY | RESUME |
 *   CLEARROUTE | UP | DOWN | APPLY | BACK (the last four drive the on-screen menu).
 *
 * CONFIG: the [sf] Custom Data section (auto-generated; every key optional
 * except role). See README.md for the full key table and semantics.
 *
 * SCREEN VIEWS: each ship screen shows ONE view so a multi-screen cockpit can split
 * the display - full (header+menu, the default), menu, status, trip. Assign via a
 * tagged LCD name ([SF] = full, [SF:trip], [SF:menu:1.2] to pin the
 * font, [SF:status:1.4:6] to also pin 6% padding) or an [sf-screens] section
 * in a cockpit/PB's own Custom Data mapping surface index -> view (e.g.
 * "2 = status@1.4/6" = view@font/pad). Each screen sizes to its OWN content, so a
 * small screen no longer shrinks a big wall LCD; an untagged rig is unchanged (full
 * view). See ParseScreenTag / Discover.
 *
 * NOTES: absolute world coordinates - static grids only, never a moving grid.
 * Docking is orientation-matched (RECORD captures the full docked pose), so it works
 * for a connector facing any direction on any ship with gyros + thrusters. One custom
 * gyro+thruster controller flies the whole route on a velocity profile (no stock
 * autopilot weaving); at 60 Hz while flying it holds heading, turns DAMPENERS OFF and
 * coasts thrust-free in space (restored on stop/dock/fault/recompile). In gravity it
 * flies level (belly-down VTOL climb) when the hull is lift-heavy so the strong down-
 * thrusters do the climbing, else nose-to-path; forced with cruiseAttitude in Custom
 * Data (auto|level|nose). On final approach it raycasts the docking corridor with a
 * camera and HOLDS off if another grid is parked on / crossing the connector (anti-
 * collision), auto-resuming when clear; see DockCorridorBlocked. Sorters are
 * only toggled on/off (filters/Drain-All untouched); tag match is case-insensitive
 * anywhere in the name. Version tracked in CHANGELOG.md. Semver.
 *//////////////////////////////////////////////////////////////////////////////

const string VERSION = "0.9.0";

// ---- Roles / states --------------------------------------------------------
enum Role { Shuttle, Base }
// RunMode is the TRIP CYCLE only. Continuous/OneTrip do a full round trip (home ->
// dest -> home); OneWay runs a single leg to the OPPOSITE end and holds there, the
// next START sending it back. Which way OneWay goes is decided by which END it's
// physically parked at (pose proximity). The old WaitFull mode was folded into
// Continuous + homeTrigger = Cargo (see DepartTrigger); a config that still says
// WAITFULL loads as exactly that.
enum RunMode { Continuous, OneTrip, OneWay }

// DepartTrigger is a SEPARATE, PER-CONNECTOR setting (PAM-style): what releases the
// shuttle from a dock to the next leg. Each end has its own.
//   Auto   - leave as soon as the cargo op finishes (loaded at home / emptied at the
//            dest, with the drain safety timeout) - the original behaviour.
//   Cargo  - wait for the hold to be full at home / empty at the destination.
//   Timer  - run the sorters for dwellSec, then leave regardless of fill.
//   Manual - hold until a DEPART command arrives (ship button or station over IGC).
// Departure is additionally gated on fuel/charge (see DepartFuelOk): the shuttle
// won't leave a dock without enough hydrogen and battery to reach the next one.
enum DepartTrigger { Auto, Cargo, Timer, Manual }

// A full docked pose: where the Remote Control sat, which way the ship faced,
// and the bound connector's mating axis. Capturing all four lets the shuttle
// reproduce the exact orientation it was docked in - on ANY ship, for a
// connector facing ANY direction, not just a nose-mounted one.
struct DockPose
{
    public Vector3D Pos;      // Remote Control world position while docked
    public Vector3D Fwd;      // Remote Control world forward while docked
    public Vector3D Up;       // Remote Control world up while docked
    public Vector3D ConnFwd;  // bound connector's world forward (points into the dock)
    public long BaseGridId;   // EntityId of the static grid this dock belongs to; lets the clearance raycast tell the base from a foreign ship parked on the connector (0 = unknown / pre-0.15 route)
    public double HoldDist;   // per-dock override [m] for the outer staging/holding fix distance; 0 = use the global holdDist. Set by hand in the route section for docks where the global stand-off isn't clear of the geometry.
    public double Grav;       // natural-gravity magnitude [m/s^2] captured at record time; classifies the dock as in-gravity (> GRAV_EPS) or space. 0 on pre-0.7 routes -> classified as space (harmless: SpaceLocal = today's single-Cruise behavior).
}
// A flight phase, decoupled from direction. What used to be the direction-baked
// State enum (UndockHome/UndockDest, CruiseToDest/CruiseToHome, ...) is now one
// direction-free phase per behavior; the direction lives in `Leg.Outbound`. This
// is the first of the three axes described in roadmap.md (phase / leg / scenario).
// A phase-object controller drives the loop; see FlightPhase below.
enum PhaseId
{
    Idle,          // parked, waiting for a command / next cycle
    Recording,     // teaching a route (path breadcrumbs are being captured)
    Loading,       // at home, load sorter on, filling to threshold (always outbound)
    Undock,        // released the current connector, backing off to the inner stand-off
    DepartStaging, // flown out to the departure staging fix; rotates to the route heading there
    Cruise,        // controller flying the (possibly reversed) recorded path
    Climb,         // cruise-family: ascending out of a gravity well (own speed governor + "Climbing" status)
    Descent,       // cruise-family: descending into a gravity well (own speed governor + "Descending" status)
    Holding,       // station-keeping at the arrival holding fix; reorients to the dock attitude
    Taxi,          // the cleared final move: from the holding fix down the connector axis
    Approach,      // legacy alias for Holding (kept so an in-flight ship resumes across the swap)
    Unloading,     // at destination, unload sorter on, draining (always inbound)
    Faulted        // something went wrong; needs operator attention
    // The anti-dive guarantee (roadmap Slice b): every dock is bracketed by a staging
    // fix on departure (DepartStaging) and a holding fix on arrival (Holding); Taxi is
    // the ONLY phase that moves the ship onto the connector. Climb/Descent (Slice c) are
    // the same cruise flight law under a different speed governor, selected per Scenario.
    // Adding phases here does NOT double the enum - direction is Leg.
}

// The current traversal context: which way we're flying and (by extension) which
// dock is the origin and which is the target. Replaces the duplicated *ToDest /
// *ToHome states and the `bool toDest` / `bool fromHome` params threaded through
// the flight ticks. Outbound = home -> dest; inbound = dest -> home.
struct Leg
{
    public bool Outbound;
}

// The third axis (roadmap.md: phase / leg / scenario). Classified once per leg from the
// two docks' recorded natural-gravity magnitudes; picks which cruise-family phases run and
// in what order. Direction is folded in by ClassifyLeg (it passes from/to gravity in leg
// order), so an outbound Ascent is automatically an inbound Descent - no swap table.
enum Scenario
{
    PlanetLocal,   // both docks in gravity: Climb -> Cruise -> Descent
    Ascent,        // gravity -> space:      Climb -> Cruise
    Descent,       // space -> gravity:      Cruise -> Descent
    SpaceLocal     // both docks in space:   Cruise (identical to pre-0.7 behavior)
}

// ============================================================================
//  Phase-object base controller
// ============================================================================
// Each flight phase is a lightweight object nested in Program, so it has full
// access to Program's private state and methods through the passed `p`. Phases
// are thin dispatchers to the existing Tick* bodies - no flight logic is copied
// here - and they expose the two facts the parallel switches used to hand-encode:
// whether the phase drives the fast (60 Hz) control loop, and its display label.
// One instance per phase is built once into `phases` (no per-tick allocation).
// Transitions still live inside the Tick* bodies for this slice; a later slice
// can lift the phase sequence into a data-driven per-scenario flight plan.
abstract class FlightPhase
{
    public abstract PhaseId Id { get; }
    public abstract bool IsFlightControl { get; }   // replaces IsFlightControlState()
    public abstract string Label { get; }            // direction-free short label
    public virtual void Enter(Program p) { }
    public abstract void Tick(Program p);
    public virtual void Exit(Program p) { }
}

class IdlePhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Idle; } }
    public override bool IsFlightControl { get { return false; } }
    public override string Label { get { return "Idle"; } }
    public override void Tick(Program p) { p.TickIdle(); }
}

class RecordingPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Recording; } }
    public override bool IsFlightControl { get { return false; } }
    public override string Label { get { return "Recording"; } }
    public override void Tick(Program p) { p.TickRecording(); }
}

class LoadingPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Loading; } }
    public override bool IsFlightControl { get { return false; } }
    public override string Label { get { return "Loading"; } }
    // Loading only ever precedes an outbound (home -> dest) leg.
    public override void Enter(Program p) { p.leg.Outbound = true; }
    public override void Tick(Program p) { p.TickLoading(); }
}

class UndockPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Undock; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Undock"; } }
    public override void Tick(Program p) { p.TickUndock(); }
}

class DepartStagingPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.DepartStaging; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Staging"; } }
    public override void Enter(Program p) { p.stageStableFor = 0; p.stagingAtFix = false; }
    public override void Tick(Program p) { p.TickDepartStaging(); }
}

class CruisePhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Cruise; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Cruise"; } }
    public override void Enter(Program p) { p.boundaryFor = 0; }   // fresh dwell for the Cruise->Descent boundary (shared accumulator)
    public override void Tick(Program p) { p.TickCruise(); }
}

// Climb / Descent are the SAME cruise flight law (RunCruiseControl over the same legWps)
// under a different top-speed governor (CruiseCap) and status label. Selected per Scenario
// and advanced by the gravity/distance boundary inside RunCruiseFamily; they exist so the
// operator sees the flight stage and can cap climb/descent speed independently of cruise.
class ClimbPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Climb; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Climbing"; } }
    public override void Enter(Program p) { p.boundaryFor = 0; }
    public override void Tick(Program p) { p.TickClimb(); }
}

class DescentPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Descent; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Descending"; } }
    public override void Enter(Program p) { p.boundaryFor = 0; }
    public override void Tick(Program p) { p.TickDescent(); }
}

class HoldingPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Holding; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Holding"; } }
    // Fresh clearance accounting each time we arrive at the holding fix.
    public override void Enter(Program p) { p.dockBlockTimer = 0; p.dockClearFor = 0; }
    public override void Tick(Program p) { p.TickHolding(); }
}

class TaxiPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Taxi; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Taxi"; } }
    public override void Tick(Program p) { p.TickTaxi(); }
}

// Legacy alias: normal flow no longer enters Approach, but a ship whose [state] was
// written by a pre-0.5.0 script (phase "Approach") still resumes here after the swap.
// It runs the holding logic - decelerate, reorient, clear, then Taxi - so an in-flight
// upgrade converges instead of stranding. The IGC wire name stays "Approach*".
class ApproachPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Approach; } }
    public override bool IsFlightControl { get { return true; } }
    public override string Label { get { return "Holding"; } }
    public override void Enter(Program p) { p.dockBlockTimer = 0; p.dockClearFor = 0; }
    public override void Tick(Program p) { p.TickHolding(); }
}

class UnloadingPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Unloading; } }
    public override bool IsFlightControl { get { return false; } }
    public override string Label { get { return "Unloading"; } }
    // Unloading only ever precedes an inbound (dest -> home) leg.
    public override void Enter(Program p) { p.leg.Outbound = false; }
    public override void Tick(Program p) { p.TickUnloading(); }
}

class FaultedPhase : FlightPhase
{
    public override PhaseId Id { get { return PhaseId.Faulted; } }
    public override bool IsFlightControl { get { return false; } }
    public override string Label { get { return "FAULT"; } }
    public override void Tick(Program p) { p.TickFaulted(); }
}


// ---- Configuration (loaded from Custom Data) -------------------------------
Role role = Role.Shuttle;
RunMode runMode = RunMode.Continuous;
DepartTrigger homeTrigger = DepartTrigger.Auto;   // what releases the shuttle from HOME
DepartTrigger destTrigger = DepartTrigger.Auto;   // what releases it from the DESTINATION
string shipName = "Skippy";
string channel = "SkippyShuttleNet";
bool useTower = false;        // opt-in tower clearance; Off (default) = fly independently, byte-identical to pre-tower behavior
string remoteName = "";
string loadTag = "[SF:LOAD]";
string unloadTag = "[SF:UNLOAD]";
string lcdTag = "[SF]";
float cruiseSpeed = 100f;
float climbSpeed = 100f;          // [m/s] top-speed cap while Climbing; clamped to (5, cruiseSpeed]. Default = cruiseSpeed (no-op) - lower it for a gentler climb.
float descentSpeed = 100f;        // [m/s] top-speed cap while Descending; clamped to (5, cruiseSpeed]. Default = cruiseSpeed (no-op) - lower it for a gentler descent into a planet.
float dockSpeed = 5f;
double maxMassKg = 0;
double departFill = 95;
double unloadDrainSec = 30;
double dwellSec = 30;             // [s] Timer-trigger dwell at a dock before departing
double minHydrogenPct = 10;       // [%] refuse to depart below this hydrogen level (ignored if no H2 tanks)
double minBatteryPct = 10;        // [%] refuse to depart below this battery charge (ignored if no batteries)
double fuelMarginPct = 25;        // [%] safety headroom added to the measured per-leg fuel/charge estimate
double segMeters = 250;
double turnDegrees = 12;
double simplifyMeters = 15;       // [m] max deviation from a straight chord before a waypoint is kept; collapses straight runs so they don't burn the MAX_PATH budget (0 = off, dense recording)
double approachDist = 15;         // [m] inner on-axis stand-off; the Taxi start point, where the ship commits down the connector axis
double holdDist = 40;             // [m] outer on-axis stand-off; the departure staging fix / arrival holding fix. Always forced >= approachDist+5 so it sits clear outside the inner stand-off. Per-dock override via homeHoldDist/destHoldDist route keys.
float gyroRpmCap = 0f;            // gyro rate cap [rpm]; 0 = auto (15 small grid / 5 large) - PAM's gentle-rotation values
double brakeFrac = 0.6;           // fraction of the weakest-axis thrust reserved for braking/cornering (headroom for gravity + saturation)
double cornerLen = 30;            // [m] corner-rounding length; also the look-ahead blend distance
double gyroGain = 4.0;            // attitude controller P gain (rotate toward the target attitude)
double gyroDamp = 3.0;            // attitude controller damping on angular velocity; raise if the ship wobbles/overshoots/jiggles
string cruiseAttitude = "auto";   // gravity-leg attitude: "auto" (level if lift-heavy, else nose), "level" (VTOL climb, belly down), "nose" (nose along path)
bool dockClearCheck = true;       // anti-collision: raycast the docking corridor on final approach and hold off if another grid is parked on / crossing the connector
string cameraTag = "[SF:CAM]"; // cameras that watch the dock; blank / no match = auto-use every camera and pick whichever faces the dock at check time
double dockBlockSec = 0;          // [s] fault if the corridor stays blocked this long. 0 = wait indefinitely (a blocked dock holds forever, never faults)

// ---- Route data ------------------------------------------------------------
// A route is two docked poses (home + dest) plus the breadcrumb path between
// them. The pose carries orientation, so docking reproduces the exact attitude
// the connector was recorded in - works for connectors facing any direction.
DockPose homePose, destPose;
string homeConn = "", destConn = "";
List<Vector3D> path = new List<Vector3D>();   // home -> dest breadcrumbs
bool haveRoute = false;
string activeRoute = "";        // name of the route currently loaded into homePose/destPose/path ("" = none)
string recordName = "";         // route name captured at RECORD HOME, consumed at RECORD DEST
List<string> routeNames = new List<string>();  // saved route names (cache; rebuilt on save/switch/delete/boot)

// ---- Runtime state ---------------------------------------------------------
PhaseId phase = PhaseId.Idle;    // current flight phase (direction-free; see enum PhaseId)
Leg leg;                         // current traversal context (Outbound = home->dest)
Scenario legScenario = Scenario.SpaceLocal;  // classified once per leg in ArmCruise; picks the cruise-family plan
Vector3D legStartPos;            // ship position when the current leg armed; PlanetLocal Climb->Cruise distance gate origin
public double boundaryFor = 0;   // s the current cruise-family gravity boundary has held (debounce accumulator; reset on phase Enter)
double prevSeaAlt = 0;           // last tick's sea-level altitude [m]; basis for the vRate finite difference
bool haveSeaAlt = false;         // false until the first valid sea-level reading this leg (skips the first-tick garbage rate; false in space)
double vRate = 0;                // sea-level climb(+)/sink(-) rate [m/s]; drives the PlanetLocal Climb->Cruise->Descent boundaries
Dictionary<PhaseId, FlightPhase> phases;   // one instance per phase, built in Program()
bool operating = false;          // set by START, cleared by STOP / OneTrip end
string statusMsg = "Idle";
double phaseTimer = 0;           // seconds spent in the current timed phase
double lastAlignErr = 0;         // last attitude error from AlignTo (rad-ish); surfaced on the telem view for stall diagnosis
bool departRequested = false;    // manual "Depart Now" latch (ship button / station IGC); consumed on departure

// ---- Fuel / charge gate ----------------------------------------------------
// Adaptive per-leg estimate: how much hydrogen and charge (in % points) the last
// completed leg burned, measured each direction and persisted. A departure needs
// current level >= estimate * (1 + fuelMarginPct/100), floored by minHydrogen/Battery.
double estHydroOut = 0, estBattOut = 0;    // home -> dest consumption [% points]
double estHydroHome = 0, estBattHome = 0;  // dest -> home consumption [% points]
double legStartH2 = -1, legStartBatt = -1; // level captured at departure; -1 = not measuring
bool legOutbound = true;                   // direction of the leg currently being measured

// ---- Cruise controller state -----------------------------------------------
// The custom cruise controller flies a flight-ordered list of waypoints, each
// with a precomputed max speed (the velocity profile). A cursor tracks which
// waypoint we're flying toward; the profile is rebuilt every leg (loaded vs
// empty mass differ).
List<Vector3D> legWps = new List<Vector3D>();   // flight-ordered leg waypoints (+ final on-axis stand-off)
List<double> legVmax = new List<double>();      // parallel: max speed [m/s] permitted AT legWps[i]
int cruiseIdx = 0;                              // index of the waypoint currently flown toward
double cruiseAccel = 1.0;                       // [m/s^2] decel/lateral accel cached for this leg (mass-dependent)
double cruiseProgTimer = 0;                     // seconds since the ship last closed on its target waypoint (stuck watchdog)
double cruiseBestDist = double.MaxValue;        // closest approach so far to the current waypoint; getting nearer resets the watchdog. A simplified straight is one waypoint tens of km away, so timing waypoint *arrivals* false-faults on a leg the ship is flying perfectly (v0.13.2)
bool cruiseFlyLevel = false;                    // latched decision (with hysteresis) for auto cruiseAttitude: true = fly belly-down/VTOL, false = nose-to-path. See UseLevelFlight
bool gyroResting = false;                        // latch: gyros held inert on-heading during coast-hold (see AlignTo). Wakes only on real heading drift, not angular-velocity noise from thruster torque - stops the gyros fighting the translation controller at cruise
double dockBlockTimer = 0;                        // s the docking corridor has read continuously blocked (see TickHolding/TickTaxi); drives the optional dockBlockSec give-up and the status readout
double dockClearFor = 0;                          // s the corridor has read clear since a block; must exceed CLEAR_CONFIRM_SEC before a held approach resumes, so we don't lurch forward at a ship still crossing
double stageStableFor = 0;                        // s the ship has held assembled+aligned at the departure staging fix; must exceed STAGE_CONFIRM_SEC before it commits to cruise (assemble-before-flying, not dive-off-the-pad)
bool stagingAtFix = false;                         // latched once the ship first reaches the staging fix; from then on it turns to the route heading and never reverts to the dock attitude on sub-metre drift (kills the 57<->113 deg target ping-pong in space). Reset on DepartStaging entry/exit.

// ---- Tower clearance handshake (ephemeral, never persisted; reset on every phase change) ----
double towerAge = 9999;                            // s since the last CMD|TOWER heartbeat; starts stale so TowerActive() reads false until a real tower is heard (a 0 default would falsely gate every ship)
bool cleared = false;                              // the tower granted the current gate (matched to reqAction)
bool clearanceRequested = false;                   // a CMD|REQ has been sent for the current gate wait
string reqAction = "";                             // "DEPART" or "LAND": which grant a CMD|CLEAR/HOLD must match to apply
string holdReason = "";                            // last CMD|HOLD reason, surfaced in the status line
double reqTimer = 0;                               // s since the last CMD|REQ send; drives REQ_RESEND

// ---- Display views (ship role) ---------------------------------------------
// Each ship screen shows ONE view, so a 3-screen cockpit can split the display
// (menu on one, trip info on another, a compact status on a third) instead of
// cramming everything onto every panel. "full" is the whole header+menu (the
// original layout) and stays the default, so any screen not explicitly assigned
// looks exactly as before. A screen picks its view by name tag ([SF:trip])
// or, for a multi-surface block like a cockpit, a [sf-screens] section in
// that block's Custom Data (see ParseScreenTag / Discover).
const string VIEW_FULL = "full", VIEW_MENU = "menu", VIEW_STATUS = "status", VIEW_TRIP = "trip", VIEW_TELEM = "telem";

// ---- LCD menu (ship role) --------------------------------------------------
const int PAGE_MAIN = 0, PAGE_RECORD = 1, PAGE_SETTINGS = 2, PAGE_DEPART = 3, PAGE_ROUTES = 4;
int menuPage = PAGE_MAIN;
int menuIndex = 0;               // cursor position within the current page
bool editing = false;            // true while adjusting a value item
double editValue = 0;            // working value during an edit

// ---- Recording scratch -----------------------------------------------------
Vector3D lastCrumb;
Vector3D lastDir = Vector3D.Zero;

// ---- Blocks ----------------------------------------------------------------
IMyRemoteControl rc;
List<IMyShipConnector> connectors = new List<IMyShipConnector>();
List<IMyConveyorSorter> loadSorters = new List<IMyConveyorSorter>();
List<IMyConveyorSorter> unloadSorters = new List<IMyConveyorSorter>();
List<IMyCargoContainer> cargo = new List<IMyCargoContainer>();
// A ship screen and the view it renders. FixedSize <= 0 = auto-fit to this surface;
// > 0 pins that font size (for operators who don't want auto-resize). Pad is the
// TextPadding (% per side); the auto-fit subtracts it so text still fits.
struct ScreenTarget { public IMyTextSurface Surface; public string View; public float FixedSize; public float Pad; }
List<ScreenTarget> shipScreens = new List<ScreenTarget>();
IMyTextSurface pbSurface;                            // the PB's own screen (fallback full view when no [sf-screens] on it)
List<IMyGyro> gyros = new List<IMyGyro>();          // final-approach attitude control
List<IMyThrust> thrusters = new List<IMyThrust>();  // final-approach translation control
List<IMyGasTank> h2Tanks = new List<IMyGasTank>();  // hydrogen tanks (fuel-gate reading)
List<IMyBatteryBlock> batteries = new List<IMyBatteryBlock>();  // batteries (charge-gate reading)
List<IMyCameraBlock> cameras = new List<IMyCameraBlock>();      // dock-clearance raycast (anti-collision on final approach)
IMyBroadcastListener listener;

// ---- Base-role state -------------------------------------------------------
Dictionary<string, ShuttleReport> fleet = new Dictionary<string, ShuttleReport>();

const double DT_FALLBACK = 1.0 / 6.0;   // seconds/tick fallback (first tick / long pause)
double dt = DT_FALLBACK;                 // real elapsed time this tick; timers use this, not a fixed rate
double sinceRender = 0;                  // s since the last LCD render + broadcast (throttle at 60 Hz)
const double APPROACH_TIMEOUT = 45;   // s to abort a stuck docking approach
const int MAX_PATH = 250;
const int WRAP_COLS = 26;             // ship LCD word-wrap width; keeps any one line from blowing out a screen's auto-fit font

// ---- Docking controller tuning ---------------------------------------------
const double APPROACH_KP = 0.5;     // desired approach speed = distance * this (capped at dockSpeed)
const double VEL_GAIN = 2.0;        // how hard to correct a velocity error into thrust
const double ALIGN_TOL = 0.03;      // ~2 deg: considered fully aligned / docked-attitude reached
const double ALIGN_MOVE_TOL = 0.20; // ~12 deg: align this close before translating on-axis
const double ARRIVE_SPEED = 1.0;    // m/s below which a stand-off point counts as "reached"

// ---- Cruise controller tuning ----------------------------------------------
const double WP_ARRIVE_MIN = 8.0;         // m, floor for the speed-scaled waypoint arrive radius
const double MIN_ACCEL = 0.5;             // m/s^2, floors the profile accel so it can't blow up (near-zero thrust axis)
const double CORNER_STRAIGHT_TOL = 0.10;  // rad (~6 deg): below this deflection, no corner speed limit
const double ALIGN_SLOW_TOL = 0.5;        // attitude error at which the forward-speed factor hits its floor
const double ALIGN_MIN_FAC = 0.15;        // never fully stall forward speed while re-aiming (keeps creeping to re-align)
const double VEL_MIN_FAC = 0.30;          // floor on the sideways-velocity speed cut
const double CRUISE_STUCK_TIMEOUT = 60.0; // s without closing on the target waypoint -> Faulted
const double ALIGN_DEADBAND = 0.01;       // ~0.6 deg: below this the gyros rest instead of hunting the target
const double GYRO_REST_ATT = 0.02;        // rad (~1.1 deg): on-heading tolerance for the rest deadband
const double GYRO_REST_RATE = 0.02;       // rad/s (~1.1 deg/s): below this spin, hold gyros inert instead of nulling AngularVelocity noise
const double COAST_HOLD_ENTER = 0.05;     // rad (~2.9 deg): cruise heading within this of the path latches the gyros inert (nose direction doesn't steer - thrust is world-space omni - so a small steady nose/path offset is harmless and must NOT be chased)
const double COAST_HOLD_WAKE = 0.10;      // rad (~5.7 deg): only a heading drift past this (or a corner) re-engages the cruise gyros. Wide gap from ENTER = strong hysteresis so the nose settles instead of hunting the ever-moving path vector
const double COAST_TOL = 0.5;             // m/s velocity error below which the ship coasts (thrust off) in space
const double CRUISE_COAST_BAND = 5.0;     // m/s along-track overshoot tolerated without reverse-thrust (kills speed-cap pulsing)
const double VEL_DEADBAND = 0.4;          // m/s: velocity-tracking error below this is not corrected (hover kept) - kills the vertical/cross-track thrust chatter, worst in low gravity

// ---- Scenario / cruise-family boundary tuning (Slice c) --------------------
// The gravity magnitude that separates "in a planet's gravity well" from "space".
// Reused for BOTH scenario classification and the Ascent/Descent phase boundary; it
// is exactly where RunCruiseControl's space-coast law engages (grav.LengthSquared() < 1e-3),
// so Climb->Cruise lands precisely where coasting becomes available.
const double GRAV_EPS = 1e-3;             // m/s^2: below this natural-gravity magnitude counts as space
const double BOUNDARY_CONFIRM_SEC = 2.0;  // s the gravity boundary must hold before advancing (debounces sensor noise; the signal is monotonic across the boundary so no hysteresis band is needed)
// PlanetLocal (grav<->grav same-planet) hops never cross GRAV_EPS - natural gravity barely changes
// across the altitudes a shuttle flies - so their Climb->Cruise->Descent boundaries read the ship's
// real sea-level altitude trend instead (the recorded waypoints ARE the altitude plan): Climb hands
// to Cruise when the climb levels off; Cruise hands to Descent on a sustained sink to the dock.
const double CLIMB_MIN_DIST = 100;        // m from the leg start before a "leveled off" reading can end a PlanetLocal Climb (guards the initial horizontal accel; also lets a flat hop hand straight to Cruise)
const double LEVEL_RATE = 0.75;           // m/s: |sea-level climb rate| below this counts as leveled off (Climb -> Cruise)
const double DESCENT_RATE = 1.5;          // m/s: sea-level sink rate beyond this counts as descending to the dock (Cruise -> Descent)

// ---- Route-end matching ----------------------------------------------------
// A start/depart is only dispatched when the ship is parked AT a recorded route
// dock (home or dest), within this distance of that pose. Prevents a beeline
// across the map when the ship happens to be docked at some unrelated connector.
const double DOCK_MATCH_DIST = 10.0;      // m: how close to a recorded docked pose counts as "at that end"

// ---- Dock-clearance (anti-collision) tuning --------------------------------
const double CLEAR_CONE_DOT = 0.70;       // cos(~45 deg): a camera must face this close to the dock direction for its raycast to be trusted (an out-of-cone ray silently reads empty, i.e. falsely "clear")
const double CLEAR_CONFIRM_SEC = 1.5;     // s the corridor must read clear before a held approach resumes - debounces a ship briefly crossing the corridor so we don't lurch into its path
const double STAGE_CONFIRM_SEC = 1.5;     // s the ship must hold assembled+aligned at the departure staging fix before it commits to cruise (assemble-before-flying dwell)
const double CLEAR_RANGE_PAD = 5.0;       // m added to the dock distance when checking the camera has charged enough scan range to reach past the mating plane
const double CLEAR_LEGACY_MARGIN = 5.0;   // m: pre-0.15 routes store no base grid id, so identity can't be checked - treat only a hit this much closer than the dock point as an obstruction (stay conservative; re-record to enable identity checks)

// ---- Tower clearance (optional fleet coordination) -------------------------
// An optional overlay on the two existing local clearance gates: a ship asks a tower
// (a separate SkippyTower PB) for clearance before it undocks or taxis onto a connector.
// The tower announces itself with a periodic CMD|TOWER heartbeat; if none arrives within
// TOWER_TIMEOUT the ship treats the tower as absent and flies independently (anti-strand).
const double TOWER_TIMEOUT = 6.0;         // s without a CMD|TOWER heartbeat before the tower counts as offline (a few missed beats) - then the ship proceeds on its local gate alone
const double REQ_RESEND = 2.0;            // s between CMD|REQ resends while a ship waits at a gate for a grant

// ============================================================================
//  Lifecycle
// ============================================================================
Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    phases = new Dictionary<PhaseId, FlightPhase>
    {
        { PhaseId.Idle,          new IdlePhase() },
        { PhaseId.Recording,     new RecordingPhase() },
        { PhaseId.Loading,       new LoadingPhase() },
        { PhaseId.Undock,        new UndockPhase() },
        { PhaseId.DepartStaging, new DepartStagingPhase() },
        { PhaseId.Cruise,        new CruisePhase() },
        { PhaseId.Climb,         new ClimbPhase() },
        { PhaseId.Descent,       new DescentPhase() },
        { PhaseId.Holding,       new HoldingPhase() },
        { PhaseId.Taxi,          new TaxiPhase() },
        { PhaseId.Approach,      new ApproachPhase() },
        { PhaseId.Unloading,     new UnloadingPhase() },
        { PhaseId.Faulted,       new FaultedPhase() }
    };
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();
    LoadConfig();
    if (role == Role.Shuttle) BackfillConfig();   // add keys introduced by a newer version, keeping the route/state
    else TrimBaseConfig();                         // a board ignores the flight/cargo keys - keep its config clean
    Discover();
    LoadRoute();
    LoadState();
    // Both roles listen on the channel: the base for status reports, the ship for
    // remote DEPART commands sent by a station PB.
    listener = IGC.RegisterBroadcastListener(channel);
    if (role == Role.Shuttle)
    {
        dampenersOwned = true;   // one-time safety restore below: a mid-flight recompile never leaves the ship adrift
        ReleaseControl();         // clear any thruster/gyro overrides left by a previous compile
    }
}

void Save()
{
    // Persist enough to resume a cycle across a recompile.
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("state", "phase", phase.ToString());
    ini.Set("state", "outbound", leg.Outbound);
    ini.Set("state", "operating", operating);
    ini.Set("state", "phaseTimer", phaseTimer);
    ini.Set("state", "estHydroOut", estHydroOut);
    ini.Set("state", "estBattOut", estBattOut);
    ini.Set("state", "estHydroHome", estHydroHome);
    ini.Set("state", "estBattHome", estBattHome);
    Me.CustomData = ini.ToString();
}

// ============================================================================
//  Main
// ============================================================================
void Main(string argument, UpdateType source)
{
    try
    {
        // Real elapsed time this tick, so every timer stays correct as the loop rate
        // switches between 60 Hz (flying) and 6 Hz (idle). Guard the first post-compile
        // tick (0) and long single-player save/exit pauses (huge delta).
        dt = Runtime.TimeSinceLastRun.TotalSeconds;
        if (dt <= 0 || dt > 0.5) dt = DT_FALLBACK;

        if (!string.IsNullOrEmpty(argument)) HandleCommand(argument.Trim());

        if (role == Role.Base) { RunBase(); return; }

        // Ship role - re-discover cheaply if the remote vanished (regrind etc.)
        if (rc == null) { Discover(); if (rc == null) { statusMsg = "No Remote Control found"; RenderShip(); return; } }

        DrainIgc();   // accept remote DEPART commands + tower heartbeat/grants from other PBs
        towerAge += dt; reqTimer += dt;   // age the tower heartbeat and the clearance-request resend clock

        // One dispatch, no hand-maintained switch: the current phase object drives
        // this tick and (via the Tick* body it wraps) may advance `phase` for the next.
        phases[phase].Tick(this);

        // Fly the attitude/translation control at 60 Hz so it holds heading cleanly
        // (a 6 Hz loop overshoots and hunts); drop back to 6 Hz when parked. Applies
        // next tick, so it tracks the phase the Tick above just moved us into.
        Runtime.UpdateFrequency = phases[phase].IsFlightControl ? UpdateFrequency.Update1 : UpdateFrequency.Update10;

        // Rendering (MeasureStringInPixels per panel) + broadcast are the expensive
        // work; throttle them to ~6-7 Hz so 60 Hz flight stays cheap. Render at once
        // on any command tick so the menu/UI stays instant.
        sinceRender += dt;
        if (sinceRender >= 0.15 || !string.IsNullOrEmpty(argument))
        {
            RenderShip();
            Broadcast();
            sinceRender = 0;
        }
    }
    catch (Exception e)
    {
        phase = PhaseId.Faulted;
        statusMsg = "ERROR: " + e.Message;
        Echo(statusMsg);
    }
}

// Move to a new phase: run the old phase's Exit, switch, run the new phase's Enter.
// The Tick* bodies call this in place of the old inline `state = State.X`, so the
// phase objects get their lifecycle hooks (Enter sets leg direction for Load/Unload;
// future slices arm cruise, capture staging fixes, etc.). phaseTimer stays under the
// tick bodies' own control, exactly as before, so timing is unchanged.
void SwitchPhase(PhaseId next)
{
    if (next == phase) return;
    phases[phase].Exit(this);
    phase = next;
    phases[phase].Enter(this);
    // Tower clearance is per-gate: dropping it on every phase change makes the next gate
    // request fresh, so a DEPART grant can't satisfy a later LAND (nor a home-depart grant
    // a dest-depart). reqTimer primed at the resend threshold so the first check sends now.
    cleared = false; clearanceRequested = false; holdReason = ""; reqTimer = REQ_RESEND;
}

// The Faulted phase's behavior: stop driving, drop control, kill the sorters. Was an
// inline case in Main's switch; now a body FaultedPhase dispatches to.
void TickFaulted()
{
    AbortAutopilot();
    ReleaseControl();
    SetSorters(loadSorters, false);
    SetSorters(unloadSorters, false);
}

// Map the direction-free (phase, Outbound) back to the pre-0.2.0 State name. Kept
// for the IGC report wire (a Skippy-Shuttle base board decodes these names) and for
// the RESUME echo, so cross-version interop and the on-screen text are unchanged.
string LegacyStateName()
{
    switch (phase)
    {
        case PhaseId.Loading:       return "Loading";
        case PhaseId.Undock:        return leg.Outbound ? "UndockHome"   : "UndockDest";
        case PhaseId.DepartStaging: return leg.Outbound ? "UndockHome"   : "UndockDest";
        case PhaseId.Cruise:        return leg.Outbound ? "CruiseToDest" : "CruiseToHome";
        case PhaseId.Climb:         return leg.Outbound ? "CruiseToDest" : "CruiseToHome";
        case PhaseId.Descent:       return leg.Outbound ? "CruiseToDest" : "CruiseToHome";
        case PhaseId.Holding:       return leg.Outbound ? "ApproachDest" : "ApproachHome";
        case PhaseId.Taxi:          return leg.Outbound ? "ApproachDest" : "ApproachHome";
        case PhaseId.Approach:      return leg.Outbound ? "ApproachDest" : "ApproachHome";
        case PhaseId.Unloading:     return "Unloading";
        case PhaseId.Recording:     return "Recording";
        case PhaseId.Faulted:       return "Faulted";
        default:                    return "Idle";
    }
}

// Reverse of LegacyStateName: decode a pre-0.2.0 [state] value into (phase, Outbound)
// so an existing ship whose Custom Data still holds an old state name resumes on the
// correct phase and direction after this script is pasted in over the old one.
void ApplyLegacyState(string name)
{
    switch (name)
    {
        case "Loading":      phase = PhaseId.Loading;   leg.Outbound = true;  break;
        case "UndockHome":   phase = PhaseId.Undock;    leg.Outbound = true;  break;
        case "CruiseToDest": phase = PhaseId.Cruise;    leg.Outbound = true;  break;
        case "ApproachDest": phase = PhaseId.Holding;   leg.Outbound = true;  break;
        case "Unloading":    phase = PhaseId.Unloading; leg.Outbound = false; break;
        case "UndockDest":   phase = PhaseId.Undock;    leg.Outbound = false; break;
        case "CruiseToHome": phase = PhaseId.Cruise;    leg.Outbound = false; break;
        case "ApproachHome": phase = PhaseId.Holding;   leg.Outbound = false; break;
        case "Recording":    phase = PhaseId.Recording; break;
        case "Faulted":      phase = PhaseId.Faulted;   break;
        default:             phase = PhaseId.Idle;      break;
    }
}

// ============================================================================
//  Commands
// ============================================================================
void HandleCommand(string arg)
{
    // raw keeps original case (route names are case-sensitive); parts is upper-cased for
    // command/keyword matching. Same tokenisation, so raw[i] pairs with parts[i].
    var raw = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    var parts = arg.ToUpperInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;

    switch (parts[0])
    {
        case "RECORD":
            if (parts.Length > 1 && parts[1] == "HOME") RecordHome(parts.Length > 2 ? raw[2] : "");
            else if (parts.Length > 1 && parts[1] == "DEST") RecordDest(parts.Length > 2 ? raw[2] : "");
            else statusMsg = "Usage: RECORD HOME [name] | RECORD DEST";
            break;

        case "START":
        case "GO":
            if (!haveRoute) { statusMsg = "No route - RECORD HOME/DEST first"; break; }
            operating = true;
            // Kick off from wherever we sensibly can. ONEWAY runs a single leg to the
            // OPPOSITE end and holds there, so its direction is decided purely by which
            // END we're physically docked at (by pose proximity, not connector name -
            // this ship docks both ends with the same connector): at home -> load and
            // head to dest; at dest -> depart straight for home (no re-unload). The
            // other modes cycle a full round trip.
            if (phase == PhaseId.Idle || phase == PhaseId.Faulted)
            {
                bool docked = DockedNow();
                if (docked && !AtRouteEnd())
                {
                    operating = false;   // don't let TickIdle dispatch from a non-route dock
                    statusMsg = "START: not at a route dock - move to home/dest first";
                    break;
                }
                bool atHome = AtHomeEnd();
                if (runMode == RunMode.OneWay)
                {
                    if (docked && atHome) SwitchPhase(PhaseId.Loading);         // load, head to dest
                    else if (docked)      { leg.Outbound = false; SwitchPhase(PhaseId.Undock); }   // at dest -> straight home
                    else if (atHome)      { leg.Outbound = true;  SwitchPhase(PhaseId.Cruise); }
                    else                  { leg.Outbound = false; SwitchPhase(PhaseId.Cruise); }
                }
                else
                {
                    if (docked && atHome) SwitchPhase(PhaseId.Loading);
                    else if (docked)      SwitchPhase(PhaseId.Unloading);
                    else                  { leg.Outbound = false; SwitchPhase(PhaseId.Cruise); }
                }
                phaseTimer = 0;          // begin a fresh load/unload dwell
                departRequested = false; // drop any stale manual-depart latch
            }
            statusMsg = "Started (" + runMode + ")";
            break;

        case "STOP":
            operating = false;
            departRequested = false;
            AbortAutopilot();
            ReleaseControl();
            SetSorters(loadSorters, false);
            SetSorters(unloadSorters, false);
            SwitchPhase(PhaseId.Idle);
            statusMsg = "Stopped";
            break;

        case "HOME":
            if (!haveRoute) { statusMsg = "No route - RECORD HOME/DEST first"; break; }
            AbortAutopilot();
            if (DockedNow() && AtHomeEnd())
            {
                operating = false;
                SwitchPhase(PhaseId.Idle);
                statusMsg = "Already home";
            }
            else
            {
                operating = true;
                leg.Outbound = false;   // heading home either way
                SwitchPhase(DockedNow() ? PhaseId.Undock : PhaseId.Cruise);
                statusMsg = "Returning home";
            }
            break;

        case "MODE":
            if (parts.Length > 1) SetMode(parts[1]);
            else statusMsg = "Mode: " + runMode;
            break;

        case "DEPART":
            // Ship: release the current dock now (manual trigger / override). Base:
            // broadcast the request to the shuttle(s) - "DEPART" for all, or
            // "DEPART <shipName>" to target one.
            if (role == Role.Base)
            {
                string who = parts.Length > 1 ? parts[1] : "*";
                IGC.SendBroadcastMessage(channel, "CMD|DEPART|" + who);
                statusMsg = "Sent DEPART to " + (who == "*" ? "all shuttles" : who);
            }
            else RequestDepart();
            break;

        case "RESUME":
            LoadState();
            statusMsg = "Resumed: " + LegacyStateName();
            break;

        case "CLEARROUTE":
            ClearRoute();
            statusMsg = "Route cleared";
            break;

        case "ROUTE":
            // Switch the active route. Blocked mid-flight (would swap the target under a live leg).
            if (parts.Length > 1)
            {
                if (operating) statusMsg = "STOP before switching routes";
                else SwitchActiveRoute(raw[1]);
            }
            else statusMsg = "Active: " + (activeRoute == "" ? "none" : activeRoute)
                             + " (" + routeNames.Count + " saved)";
            break;

        case "DELROUTE":
            if (parts.Length > 1) DeleteRoute(raw[1]);
            else statusMsg = "Usage: DELROUTE <name>";
            break;

        // ---- LCD menu navigation (bind these to cockpit toolbar buttons) ----
        case "UP":    MenuMove(-1); break;
        case "DOWN":  MenuMove(+1); break;
        case "APPLY": MenuApply();  break;
        case "BACK":  MenuBack();   break;

        default:
            statusMsg = "Unknown command: " + parts[0];
            break;
    }
}

void SetMode(string m)
{
    switch (m)
    {
        case "CONTINUOUS": runMode = RunMode.Continuous; break;
        case "ONETRIP":    runMode = RunMode.OneTrip;    break;
        case "ONEWAY":     runMode = RunMode.OneWay;     break;
        case "WAITFULL":   // legacy: fold into Continuous + a Cargo home trigger
            runMode = RunMode.Continuous;
            homeTrigger = DepartTrigger.Cargo;
            SaveCfg("runMode", "CONTINUOUS");
            SaveCfg("homeTrigger", "Cargo");
            statusMsg = "WaitFull -> Continuous + Home trigger = Cargo";
            return;
        default: statusMsg = "Mode must be CONTINUOUS|ONETRIP|ONEWAY"; return;
    }
    var ini = new MyIni(); ini.TryParse(Me.CustomData);
    ini.Set("sf", "runMode", m);
    Me.CustomData = ini.ToString();
    statusMsg = "Mode = " + runMode;
}

// ============================================================================
//  Route recording
// ============================================================================
void RecordHome(string name = "")
{
    var c = ConnectedConnector();
    if (c == null) { statusMsg = "RECORD HOME: dock at the home connector first"; return; }
    // Name the route being recorded. Explicit name wins; else re-record the active route;
    // else fall back to "Main" (the default single route / legacy migration target).
    string n = SanitizeName(name);
    recordName = n != "" ? n : (activeRoute != "" ? activeRoute : "Main");
    homePose = CapturePose(c);
    homeConn = c.CustomName;
    path.Clear();
    lastCrumb = homePose.Pos;
    lastDir = Vector3D.Zero;
    SwitchPhase(PhaseId.Recording);
    operating = false;
    statusMsg = "Recording '" + recordName + "' from " + homeConn + ". Fly to destination.";
}

void RecordDest(string name = "")
{
    if (phase != PhaseId.Recording) { statusMsg = "RECORD DEST: run RECORD HOME first"; return; }
    var c = ConnectedConnector();
    if (c == null) { statusMsg = "RECORD DEST: dock at the destination connector first"; return; }
    string n = SanitizeName(name);
    if (n != "") recordName = n;               // allow a late rename at DEST time
    destPose = CapturePose(c);
    destConn = c.CustomName;
    // Ensure the final approach point is captured.
    if (path.Count == 0 || Vector3D.Distance(path[path.Count - 1], destPose.Pos) > 5)
        AddCrumb(rc.GetPosition());
    haveRoute = true;
    activeRoute = recordName != "" ? recordName : "Main";   // the just-recorded route is now active
    SwitchPhase(PhaseId.Idle);
    SaveRoute();
    statusMsg = "Saved '" + activeRoute + "': " + homeConn + " -> " + destConn + " (" + path.Count + "wp)";
}

// Snapshot the exact docked attitude so it can be reproduced later, on any ship.
DockPose CapturePose(IMyShipConnector c)
{
    return new DockPose
    {
        Pos     = rc.GetPosition(),
        Fwd     = rc.WorldMatrix.Forward,
        Up      = rc.WorldMatrix.Up,
        ConnFwd = c.WorldMatrix.Forward,   // points out of the connector face, into the dock
        // Docked while recording, so OtherConnector is the base's mating connector - its
        // grid is the static dock. Stored so the approach raycast can tell "the base
        // itself" from "someone else's ship parked on my connector".
        BaseGridId = (c.Status == MyShipConnectorStatus.Connected && c.OtherConnector != null)
                     ? c.OtherConnector.CubeGrid.EntityId : 0,
        // Natural-gravity magnitude at the dock, for leg-scenario classification (Slice c).
        Grav = rc.GetNaturalGravity().Length()
    };
}

void TickRecording()
{
    Vector3D p = rc.GetPosition();
    double moved = Vector3D.Distance(p, lastCrumb);
    if (moved < 20) return;                      // ignore jitter while parked

    Vector3D dir = Vector3D.Normalize(p - lastCrumb);
    double turn = lastDir == Vector3D.Zero ? 0
                : Math.Acos(MathHelper.Clamp(dir.Dot(lastDir), -1, 1)) * 180.0 / Math.PI;

    if (moved >= segMeters || (moved >= 30 && turn >= turnDegrees))
        AddCrumb(p);
}

void AddCrumb(Vector3D p)
{
    // Collinear-run simplification: if this new point continues the straight line from the
    // vertex *before* the current tip, the tip is a redundant midpoint - slide it forward to
    // p instead of spending a new waypoint. A real corner leaves the tip well off the new
    // chord (perp > simplifyMeters) so it's kept and p is appended, starting a fresh segment.
    // Long straights collapse to their two endpoints, so the MAX_PATH budget is spent on
    // turns, not spacing. simplifyMeters <= 0 disables it (dense every-segMeters recording).
    if (simplifyMeters > 0 && path.Count >= 2)
    {
        Vector3D a = path[path.Count - 2];          // anchor: start of the current straight
        Vector3D chord = p - a;
        double chordLen = chord.Length();
        if (chordLen > 1e-3)
        {
            Vector3D u = chord / chordLen;
            Vector3D at = path[path.Count - 1] - a; // anchor -> current tip
            double proj = at.Dot(u);
            double perp = (at - proj * u).Length(); // tip's distance from the a->p chord
            if (perp <= simplifyMeters && proj >= 0 && proj <= chordLen)
            {
                path[path.Count - 1] = p;           // extend the straight; it stays two points
                lastCrumb = p;
                lastDir = u;
                return;
            }
        }
    }

    if (path.Count >= MAX_PATH) { statusMsg = "Path full (" + MAX_PATH + " wp) - raise segMeters/simplifyMeters"; return; }
    if (path.Count > 0) lastDir = Vector3D.Normalize(p - lastCrumb);
    path.Add(p);
    lastCrumb = p;
}

// ============================================================================
//  Flight state machine (ship)
// ============================================================================
void TickIdle()
{
    AbortAutopilot();
    ReleaseControl();
    if (!operating) return;
    phaseTimer = 0;   // fresh load/unload dwell when we pick a dock phase back up
    if (DockedNow())
    {
        // Parked at an unrelated connector - hold rather than beeline to a recorded dock.
        if (!AtRouteEnd()) { statusMsg = "Idle: docked away from route - undock or move to home/dest"; return; }
        SwitchPhase(AtHomeEnd() ? PhaseId.Loading : PhaseId.Unloading);
    }
    else { leg.Outbound = false; SwitchPhase(PhaseId.Cruise); }
}

void TickLoading()
{
    SetSorters(unloadSorters, false);
    phaseTimer += dt;
    double mass = ShipMassKg();
    double fill = CargoFillPct();

    bool massGate = maxMassKg > 0 && mass >= maxMassKg * 0.98;
    bool cargoReady = fill >= departFill || massGate;

    // Keep loading until the hold is full (or the mass gate trips); then stop the
    // sorters even while we're still waiting on a Manual/Timer trigger, so a shuttle
    // that dwells or waits for a button never keeps cramming past full/overweight.
    SetSorters(loadSorters, !cargoReady);

    if (DepartureAllowed(true, cargoReady))
    {
        string why;
        if (!DepartFuelOk(true, out why)) { SetSorters(loadSorters, false); statusMsg = why; return; }
        // Tower gate: stay docked (out of the corridor) until cleared. A manual/remote
        // DEPART (departRequested) is an explicit override and bypasses the tower.
        if (!departRequested && !ClearedToProceed("DEPART", homeConn))
        { SetSorters(loadSorters, false); statusMsg = TowerWait("DEPART"); return; }
        SetSorters(loadSorters, false);
        departRequested = false;
        BeginLegMeasure(true);
        statusMsg = "Loaded (" + fill.ToString("0") + "%, " + (mass / 1000.0).ToString("0.0") + "t) - departing";
        SwitchPhase(PhaseId.Undock);
        phaseTimer = 0;
        return;
    }

    statusMsg = DepartStatus(true, fill);
}

void TickUnloading()
{
    SetSorters(loadSorters, false);
    phaseTimer += dt;
    double fill = CargoFillPct();

    // Auto keeps its original drain-timeout safety net; the explicit Cargo trigger
    // waits for a truly empty hold. Both stop the sorters once empty.
    bool cargoReady = fill <= 1.0;
    SetSorters(unloadSorters, !cargoReady);

    if (DepartureAllowed(false, cargoReady))
    {
        SetSorters(unloadSorters, false);

        if (runMode == RunMode.OneWay)   // delivered - hold at the destination, don't return
        {
            departRequested = false;
            phaseTimer = 0;
            operating = false;
            SwitchPhase(PhaseId.Idle);
            statusMsg = "Delivered - holding at destination";
            return;
        }

        // Round trip: don't leave for home without the fuel to get there.
        string why;
        if (!DepartFuelOk(false, out why)) { statusMsg = why; return; }
        // Tower gate (same overlay as home): hold at the dock until cleared; DEPART overrides.
        if (!departRequested && !ClearedToProceed("DEPART", destConn))
        { statusMsg = TowerWait("DEPART"); return; }
        departRequested = false;
        BeginLegMeasure(false);
        phaseTimer = 0;
        SwitchPhase(PhaseId.Undock);   // return leg; OneTrip stops after docking home
        return;
    }

    statusMsg = DepartStatus(false, fill);
}

// Should the shuttle leave this dock now? A manual DEPART overrides any trigger;
// otherwise the end's configured trigger decides. `atHome` selects the trigger and
// `cargoReady` is that end's cargo condition (full at home / empty at the dest).
bool DepartureAllowed(bool atHome, bool cargoReady)
{
    if (departRequested) return true;   // manual "Depart Now" (ship button or station)
    DepartTrigger trig = atHome ? homeTrigger : destTrigger;
    switch (trig)
    {
        case DepartTrigger.Manual: return false;                    // wait for DEPART
        case DepartTrigger.Timer:  return phaseTimer >= dwellSec;    // dwell, then go
        case DepartTrigger.Cargo:  return cargoReady;               // full / empty
        default:                                                    // Auto: cargo-ready, with the drain safety net at the dest
            return cargoReady || (!atHome && phaseTimer >= unloadDrainSec);
    }
}

// The holding status line while waiting on a trigger.
string DepartStatus(bool atHome, double fill)
{
    string act = (atHome ? "Loading " : "Unloading ") + fill.ToString("0") + "%";
    DepartTrigger trig = atHome ? homeTrigger : destTrigger;
    if (trig == DepartTrigger.Manual) return act + " - waiting DEPART";
    if (trig == DepartTrigger.Timer)  return act + " - dwell " + phaseTimer.ToString("0") + "/" + dwellSec.ToString("0") + "s";
    return act;
}

// Outbound leg => currently at HOME, undocking to go to DEST.
// Inbound leg  => currently at DEST, undocking to go HOME.
void TickUndock()
{
    bool fromHome = leg.Outbound;
    var c = GetConnector(fromHome ? homeConn : destConn);
    DockPose p = fromHome ? homePose : destPose;

    if (c != null && c.Status == MyShipConnectorStatus.Connected)
    {
        c.Disconnect();
        phaseTimer = 0;
        statusMsg = "Undocking";
        return;
    }

    // Clear the connector ONLY: back straight out to the inner stand-off holding the
    // recorded docked attitude - no rotation here. The route-heading turn happens later
    // at the staging fix (DepartStaging), stationary and clear of the structure, so the
    // ship never pitches while still nose-in on the dock. This split is the departure half
    // of the anti-dive guarantee: Undock only ever backs off; it never flies the route.
    bool clear = FlyToPose(ApproachPoint(p), p.Fwd, p.Up, 1.0);
    phaseTimer += dt;
    statusMsg = fromHome ? "Clearing home dock" : "Clearing station dock";

    if (clear || phaseTimer >= APPROACH_TIMEOUT)
    {
        ReleaseControl();
        phaseTimer = 0;
        SwitchPhase(PhaseId.DepartStaging);
    }
}

// Departure staging fix: fly OUT to the outer stand-off (clear of the structure and
// traffic), THEN rotate in place to the route heading and hold a short confirm dwell
// before handing to cruise. Two reasons the turn lives here, not in Undock:
//   1. Anti-dive: the ship assembles at a legitimate staging point instead of pitching
//      the moment it unseats from the connector.
//   2. Seamless handoff: it hands cruise a ship already pointed down the route (matching
//      the EXACT attitude RunCruiseControl will hold), so cruise engages without the
//      spin-and-slide it would otherwise do.
// The confirm dwell (stageStableFor >= STAGE_CONFIRM_SEC) is the local assemble-before-
// flying gate. Tower clearance is NOT gated here: it's applied one step earlier, at the
// Loading/Unloading -> Undock commit, so a ship waiting on the tower stays on its connector
// (out of the corridor) rather than undocking into contested airspace first.
void TickDepartStaging()
{
    bool fromHome = leg.Outbound;
    DockPose p = fromHome ? homePose : destPose;
    Vector3D staging = HoldPoint(p);
    double distToFix = Vector3D.Distance(rc.GetPosition(), staging);

    // Latch "reached the staging fix" with hysteresis: arm at <3 m and, once armed, only
    // disarm on a real drift back out (>8 m). Without this, a bare 3 m threshold toggled on
    // sub-metre drift and flipped the attitude target between the dock pose (atFix false) and
    // the route heading (atFix true) every few ticks - the gyros ping-ponged between two
    // targets ~57 deg apart and the ship swung 57<->113 deg for the full watchdog in space
    // (0 g, 0 m/s) before timing out into cruise. Once we've reached the fix we commit to the
    // route heading and never revert to the dock attitude on small drift.
    if (!stagingAtFix && distToFix < 3.0) stagingAtFix = true;
    else if (stagingAtFix && distToFix > 8.0) stagingAtFix = false;

    Vector3D faceFwd = p.Fwd, faceUp = p.Up;
    if (stagingAtFix)
    {
        Vector3D toTarget = FirstCruiseTarget(fromHome) - staging;
        if (toTarget.LengthSquared() > 1)
        {
            Vector3D dir = Vector3D.Normalize(toTarget);
            Vector3D grav = rc.GetNaturalGravity();
            if (grav.LengthSquared() > 1e-3 && UseLevelFlight())
            {
                // Level-flight cruise attitude: nose on the HORIZONTAL heading, up away
                // from gravity. Matching it here removes the pitch-up/level-off flip a
                // climbing first waypoint would otherwise cause at cruise engage.
                Vector3D upWorld = Vector3D.Normalize(-grav);
                Vector3D horiz = dir - dir.Dot(upWorld) * upWorld;
                if (horiz.LengthSquared() > 1e-6) faceFwd = Vector3D.Normalize(horiz);
                faceUp = upWorld;
            }
            else
            {
                // Nose-forward cruise attitude (space, or up-thrust-poor craft). In space
                // cruise is roll-agnostic (holds current up), so demand no roll - aiming at
                // a space station's arbitrary recorded up made the gyros hunt forever. Use
                // gravity-up orthogonalised to the heading for an up-thrust-poor craft still
                // in air.
                faceFwd = dir;
                if (grav.LengthSquared() > 1e-3)
                {
                    Vector3D up = -grav;
                    Vector3D perp = up - up.Dot(dir) * dir;
                    faceUp = perp.LengthSquared() > 1e-6 ? Vector3D.Normalize(perp) : rc.WorldMatrix.Up;
                }
                else
                {
                    faceUp = rc.WorldMatrix.Up;
                }
            }
        }
    }

    if (stagingAtFix)
    {
        // At the fix: rotate to the route heading with the SAME coast-hold law cruise uses,
        // and station-keep position INDEPENDENTLY of alignment. Two reasons this doesn't go
        // through FlyToPose here:
        //   1. Precision alignment (ALIGN_TOL, ~1.7 deg) is unattainable in space - the nose
        //      target inches around and the gyros hunt it forever, so `posed` never latched
        //      and the ship never confirmed the dwell. Coast-hold (COAST_HOLD_ENTER ~2.9 deg,
        //      wide hysteresis) is exactly what cruise holds, so match it.
        //   2. FlyToPose withholds translation while align >= ALIGN_MOVE_TOL (~12 deg). During
        //      a 57 deg turn that means NO station-keeping - the ship coasts off the fix
        //      (dampeners are off in flight), which is what toggled the old atFix flag.
        //      StationKeep nulls the drift regardless of attitude, so the fix holds while we turn.
        double align = AlignTo(faceFwd, faceUp, true);
        StationKeep(staging);
        bool posed = align < COAST_HOLD_WAKE && distToFix < 3.0;
        if (posed) stageStableFor += dt; else stageStableFor = 0;
        statusMsg = "Staging - aligning for cruise";
    }
    else
    {
        // Still flying out to the fix: hold the recorded docked attitude, no rotation, so we
        // don't pitch while still close to the structure (the departure anti-dive guarantee).
        FlyToPose(staging, faceFwd, faceUp, 1.0);
        stageStableFor = 0;
        statusMsg = fromHome ? "Departing home" : "Departing station";
    }

    phaseTimer += dt;

    if (stageStableFor >= STAGE_CONFIRM_SEC || phaseTimer >= APPROACH_TIMEOUT)
    {
        ReleaseControl();
        phaseTimer = 0;
        stagingAtFix = false;
        SwitchPhase(FirstCruisePhase());   // Climb / Cruise per the leg's Scenario
    }
}

// The first point the cruise controller will actually fly to. Used to pre-aim
// the ship during undock so it engages cruise already facing its heading.
// BuildLeg always appends the final stand-off, so legWps[0] is never empty.
Vector3D FirstCruiseTarget(bool toDest)
{
    BuildLeg(toDest);
    return legWps[0];
}

// Cruise-family entry points. Cruise/Climb/Descent all run the SAME leg via the shared
// core below; the phase only selects the speed governor (CruiseCap) and status label, and
// which boundary advances to the next cruise-family phase.
void TickCruise()   { RunCruiseFamily(); }
void TickClimb()    { RunCruiseFamily(); }
void TickDescent()  { RunCruiseFamily(); }

void RunCruiseFamily()
{
    bool toDest = leg.Outbound;
    if (!CruiseArmed(toDest)) { ArmCruise(toDest); return; }

    cruiseProgTimer += dt;
    bool done = RunCruiseControl();
    statusMsg = CruiseStatus();

    if (done)
    {
        // Reached the arrival holding fix -> hand to the holding/clearance controller.
        cruiseArmed = false;
        ReleaseControl();
        SwitchPhase(PhaseId.Holding);
        phaseTimer = 0;
        return;
    }
    if (cruiseProgTimer >= CRUISE_STUCK_TIMEOUT)
    {
        cruiseArmed = false;
        ReleaseControl();
        SwitchPhase(PhaseId.Faulted);
        statusMsg = "Cruise stuck - check thrust/gyro/geometry";
        return;
    }
    // Still en route: advance Climb->Cruise->Descent when this leg's boundary is crossed.
    // Mid-leg only - NO ReleaseControl and NO cruiseArmed clear; cruiseIdx must survive.
    if (BoundaryReady()) SwitchPhase(NextCruisePhase());
}

// Per-phase governor cap fed to RunCruiseControl. Keyed off `phase` (not a stored field)
// so it stays correct after a mid-flight recompile - LoadState assigns `phase` directly and
// never calls Enter. Caps are clamped <= cruiseSpeed at load, so the profile (built at the
// cruiseSpeed ceiling) always has enough braking margin - no profile rebuild on transition.
double CruiseCap()
{
    if (phase == PhaseId.Climb) return climbSpeed;
    if (phase == PhaseId.Descent) return descentSpeed;
    return cruiseSpeed;
}

// True in any cruise-family phase (Cruise/Climb/Descent) - they share the leg, so ETA,
// remaining-distance and the IGC report apply to all three.
bool InCruiseFamily()
{
    return phase == PhaseId.Cruise || phase == PhaseId.Climb || phase == PhaseId.Descent;
}

string CruiseStatus()
{
    bool toDest = leg.Outbound;
    string verb = phase == PhaseId.Climb ? "Climbing"
                : phase == PhaseId.Descent ? "Descending" : "Cruising";
    // Danger-zone marker: powered climb/descent while still inside a gravity well - the
    // atmosphere/gravity thrust-handoff region. Status-only, no control change.
    string xfer = InTransition() ? " !xfer" : "";
    return verb + (toDest ? " to destination" : " home") + xfer;
}

// True in the atmosphere/gravity handoff: in a well (thrusters fighting gravity) and in a
// powered Climb/Descent phase. Derived, not stored, so it's correct after a recompile/resume.
bool InTransition()
{
    return rc != null && rc.GetNaturalGravity().Length() > GRAV_EPS
           && (phase == PhaseId.Climb || phase == PhaseId.Descent);
}

// Scenario from the two ends' recorded gravity. > GRAV_EPS = in a gravity well.
Scenario Classify(double fromG, double toG)
{
    bool f = fromG > GRAV_EPS, t = toG > GRAV_EPS;
    if (f && t) return Scenario.PlanetLocal;
    if (f && !t) return Scenario.Ascent;
    if (!f && t) return Scenario.Descent;
    return Scenario.SpaceLocal;
}
// Classify in leg order (from -> to), so an outbound Ascent is an inbound Descent for free.
Scenario ClassifyLeg()
{
    double fromG = leg.Outbound ? homePose.Grav : destPose.Grav;
    double toG   = leg.Outbound ? destPose.Grav : homePose.Grav;
    return Classify(fromG, toG);
}

// The cruise-family phase to enter from DepartStaging: Climb if the leg departs a gravity
// well (Ascent/PlanetLocal), else straight to Cruise. Sets legScenario so the plan is known
// before ArmCruise runs on the first cruise tick (ArmCruise re-classifies; idempotent).
PhaseId FirstCruisePhase()
{
    legScenario = ClassifyLeg();
    return (legScenario == Scenario.PlanetLocal || legScenario == Scenario.Ascent)
           ? PhaseId.Climb : PhaseId.Cruise;
}

// Successor in the Climb -> Cruise -> Descent chain. BoundaryReady gates whether the
// advance actually fires, so a terminal phase never reaches here with a real trigger.
PhaseId NextCruisePhase()
{
    if (phase == PhaseId.Climb) return PhaseId.Cruise;
    if (phase == PhaseId.Cruise) return PhaseId.Descent;
    return phase;
}

// Sea-level altitude [m] where a planet is beneath us; false (a=0) in space or with no controller.
// Sea-level (not Surface/AGL) so flying level over rising terrain doesn't read as a descent.
bool TrySeaAlt(out double a)
{
    a = 0;
    return rc != null && rc.TryGetPlanetElevation(MyPlanetElevation.Sealevel, out a);
}

// Is the current cruise-family phase's advance trigger satisfied? Gravity crossings
// (Ascent/Descent) use a confirm dwell on GRAV_EPS. PlanetLocal never crosses GRAV_EPS
// within one planet, so it reads the sea-level altitude trend the ship is flying (the
// recorded waypoints ARE the altitude plan): Climb -> Cruise when the climb levels off,
// Cruise -> Descent on a sustained sink toward the dock.
bool BoundaryReady()
{
    if (rc == null) return false;
    double gMag = rc.GetNaturalGravity().Length();

    // Sea-level climb(+)/sink(-) rate, refreshed every en-route tick so it's current wherever it's used.
    double seaAlt;
    bool onPlanet = TrySeaAlt(out seaAlt);
    vRate = (onPlanet && haveSeaAlt && dt > 0) ? (seaAlt - prevSeaAlt) / dt : 0;
    prevSeaAlt = seaAlt;
    haveSeaAlt = onPlanet;

    if (phase == PhaseId.Climb)   // -> Cruise (Ascent, PlanetLocal)
    {
        if (legScenario == Scenario.PlanetLocal)
        {
            // Leveled off: clear of the initial horizontal accel and no longer climbing. The
            // distance guard also lets a flat hop (no real climb) hand straight to Cruise.
            bool level = Vector3D.Distance(rc.GetPosition(), legStartPos) > CLIMB_MIN_DIST
                         && vRate < LEVEL_RATE;
            boundaryFor = level ? boundaryFor + dt : 0;
            return boundaryFor >= BOUNDARY_CONFIRM_SEC;
        }
        boundaryFor = gMag < GRAV_EPS ? boundaryFor + dt : 0;   // Ascent: left the well
        return boundaryFor >= BOUNDARY_CONFIRM_SEC;
    }
    if (phase == PhaseId.Cruise)  // -> Descent (Descent, PlanetLocal only)
    {
        if (legScenario == Scenario.PlanetLocal)
        {
            boundaryFor = vRate < -DESCENT_RATE ? boundaryFor + dt : 0;   // sustained sink to the dock
            return boundaryFor >= BOUNDARY_CONFIRM_SEC;
        }
        if (legScenario == Scenario.Descent)
        {
            boundaryFor = gMag > GRAV_EPS ? boundaryFor + dt : 0;   // entered the well
            return boundaryFor >= BOUNDARY_CONFIRM_SEC;
        }
        return false;   // Ascent / SpaceLocal: Cruise is terminal
    }
    return false;       // Descent is terminal
}

bool cruiseArmedToDest = false;
bool cruiseArmed = false;
bool CruiseArmed(bool toDest) { return cruiseArmed && cruiseArmedToDest == toDest; }

// The stand-off point sits on the connector's mating axis, approachDist metres
// clear of the dock. ConnFwd points into the dock, so we back off along -ConnFwd.
// This is the INNER stand-off - the Taxi start, where the ship commits down the axis.
Vector3D ApproachPoint(DockPose p) { return p.Pos - p.ConnFwd * approachDist; }

// The OUTER stand-off on the same axis: the departure staging fix / arrival holding
// fix. Sits holdDist metres off the dock (per-dock override wins), and is always forced
// clear outside the inner stand-off so the ship never holds on top of the taxi point.
double EffHoldDist(DockPose p)
{
    double d = p.HoldDist > 0 ? p.HoldDist : holdDist;
    return Math.Max(d, approachDist + 5);
}
Vector3D HoldPoint(DockPose p) { return p.Pos - p.ConnFwd * EffHoldDist(p); }

void ArmCruise(bool toDest)
{
    BuildLeg(toDest);
    if (legWps.Count == 0)   // defensive: BuildLeg always appends the stand-off, so this shouldn't happen
    {
        SwitchPhase(PhaseId.Faulted);
        statusMsg = "Cruise: empty path - re-record route";
        return;
    }
    BuildVelocityProfile();
    legScenario = ClassifyLeg();          // pick the cruise-family plan for this leg/direction
    legStartPos = rc.GetPosition();       // origin for the PlanetLocal Climb->Cruise distance guard
    boundaryFor = 0;
    prevSeaAlt = 0; haveSeaAlt = false; vRate = 0;   // fresh altitude trend for the PlanetLocal boundaries
    cruiseIdx = 0;
    cruiseProgTimer = 0;
    cruiseBestDist = double.MaxValue;
    dockBlockTimer = 0; dockClearFor = 0;   // fresh leg: clear any stale dock-clearance hold state
    cruiseArmed = true;
    cruiseArmedToDest = toDest;
    statusMsg = toDest ? "Cruising to destination" : "Cruising home";
}

// Build the flight-ordered waypoint list for a leg: the recorded crumbs (forward
// for the outbound leg, reversed for the return) minus any that sit inside either
// dock's holding-fix radius, then the arrival HOLDING FIX as the final target.
// Cruise now hands off to Holding at the outer fix, never onto the connector.
void BuildLeg(bool toDest)
{
    legWps.Clear();
    DockPose from = toDest ? homePose : destPose;
    DockPose to   = toDest ? destPose : homePose;
    double skipFrom = EffHoldDist(from) + 3;   // drop crumbs inside either holding fix
    double skipTo   = EffHoldDist(to) + 3;

    if (toDest)
    {
        for (int i = 0; i < path.Count; i++)
            if (Vector3D.Distance(path[i], from.Pos) > skipFrom && Vector3D.Distance(path[i], to.Pos) > skipTo)
                legWps.Add(path[i]);
    }
    else
    {
        for (int i = path.Count - 1; i >= 0; i--)
            if (Vector3D.Distance(path[i], from.Pos) > skipFrom && Vector3D.Distance(path[i], to.Pos) > skipTo)
                legWps.Add(path[i]);
    }

    legWps.Add(HoldPoint(to));   // final target: the arrival holding fix (NOT the connector)
}

// Precompute a max speed for each leg waypoint (PAM-style velocity profile): slow
// into sharp corners, full speed on straights, and always able to brake to the
// next point. Recomputed every arm because loaded vs empty mass differ.
void BuildVelocityProfile()
{
    int n = legWps.Count;
    legVmax.Clear();
    for (int i = 0; i < n; i++) legVmax.Add(cruiseSpeed);
    if (n == 0) return;

    // Conservative isotropic accel = weakest thrust axis / mass, with headroom.
    // The leg turns, so no single direction is right; the weakest axis is safe.
    double[] cap; MatrixD toLocal;
    AxisThrust(out cap, out toLocal);
    double minAxis = cap[0];
    for (int i = 1; i < 6; i++) minAxis = Math.Min(minAxis, cap[i]);
    double mass = rc.CalculateShipMass().PhysicalMass;
    cruiseAccel = Math.Max(MIN_ACCEL, brakeFrac * minAxis / Math.Max(mass, 1.0));

    // Corner speed from the deflection angle at each interior waypoint. Round the
    // corner within cornerLen metres -> arc radius R = L/tan(theta/2), and the
    // centripetal limit vCorner = sqrt(aLat * R).
    for (int i = 1; i < n - 1; i++)
    {
        Vector3D inDir = legWps[i] - legWps[i - 1];
        Vector3D outDir = legWps[i + 1] - legWps[i];
        if (inDir.LengthSquared() < 1e-6 || outDir.LengthSquared() < 1e-6) continue;
        inDir = Vector3D.Normalize(inDir);
        outDir = Vector3D.Normalize(outDir);
        double theta = Math.Acos(MathHelper.Clamp(inDir.Dot(outDir), -1, 1));
        if (theta < CORNER_STRAIGHT_TOL) continue;   // ~straight, keep full cruise
        double R = cornerLen / Math.Max(Math.Tan(theta * 0.5), 1e-3);
        double corner = Math.Sqrt(cruiseAccel * R);
        legVmax[i] = Math.Min(legVmax[i], Math.Min(corner, cruiseSpeed));
    }

    // Final target: arrive slow so the docking controller takes over cleanly.
    legVmax[n - 1] = ARRIVE_SPEED;

    // Backward pass: guarantee we can always decelerate into each point's limit.
    for (int i = n - 2; i >= 0; i--)
    {
        double segLen = Vector3D.Distance(legWps[i], legWps[i + 1]);
        double reachable = Math.Sqrt(legVmax[i + 1] * legVmax[i + 1] + 2.0 * cruiseAccel * segLen);
        legVmax[i] = Math.Min(legVmax[i], Math.Min(reachable, cruiseSpeed));
    }
}

// Speed-scaled radius at which a waypoint counts as reached: at least WP_ARRIVE_MIN,
// but a couple of ticks of travel at high speed so a fast fly-by doesn't stall.
double WpArriveRadius()
{
    return Math.Max(WP_ARRIVE_MIN, rc.GetShipSpeed() * dt * 2.0);
}

// Advance the waypoint cursor: step to the next point when we're within the arrive
// radius OR our projection along the leg has passed the waypoint plane. The plane
// test commits to the next point on a high-speed fly-by, which stops the ship
// orbiting a waypoint it never quite touched (the stock-autopilot circling).
void AdvanceCursor(Vector3D pos)
{
    while (cruiseIdx < legWps.Count - 1)
    {
        Vector3D cur = legWps[cruiseIdx];
        Vector3D next = legWps[cruiseIdx + 1];
        bool arrived = Vector3D.Distance(pos, cur) < WpArriveRadius();
        Vector3D seg = next - cur;
        bool passed = seg.LengthSquared() > 1e-6 &&
                      (pos - cur).Dot(Vector3D.Normalize(seg)) > 0;
        if (arrived || passed) { cruiseIdx++; cruiseProgTimer = 0; cruiseBestDist = double.MaxValue; }
        else break;
    }
}

// Per-tick cruise control law. Faces the ship along the path, picks a desired
// speed from the velocity profile + a sqrt(2*a*d) braking curve, scales it down
// when mis-aimed or drifting sideways, and drives it with the shared thrust/gyro
// primitives (same force law as FlyToPose). Returns true at the stand-off.
bool RunCruiseControl()
{
    SetDampeners(false);   // controller owns thrust all leg; off = coast in space, no auto-braking to fight

    Vector3D pos = rc.GetPosition();
    Vector3D vel = rc.GetShipVelocities().LinearVelocity;
    Vector3D grav = rc.GetNaturalGravity();
    double mass = rc.CalculateShipMass().PhysicalMass;

    AdvanceCursor(pos);

    Vector3D target = legWps[cruiseIdx];
    Vector3D toWp = target - pos;
    double dist = toWp.Length();
    Vector3D pathDir = dist > 1e-3 ? toWp / dist : rc.WorldMatrix.Forward;

    // Stuck watchdog: reset the timer whenever we get meaningfully closer to the
    // current waypoint. Timing waypoint *arrivals* (the pre-0.13.2 scheme) false-faults
    // now that a simplified straight is a single waypoint tens of km away - the ship
    // flies it perfectly for minutes without ever "advancing". Progress toward the
    // target, not arrival at it, is what proves the ship isn't truly stuck.
    if (dist < cruiseBestDist - 1.0) { cruiseBestDist = dist; cruiseProgTimer = 0; }

    // Ease toward the next segment's direction as we near a corner.
    if (cruiseIdx < legWps.Count - 1 && dist < cornerLen)
    {
        Vector3D nextSeg = legWps[cruiseIdx + 1] - target;
        if (nextSeg.LengthSquared() > 1e-6)
        {
            Vector3D nextDir = Vector3D.Normalize(nextSeg);
            double b = 1.0 - dist / cornerLen;   // 0 far from the vertex, 1 at it
            Vector3D blended = Vector3D.Lerp(pathDir, nextDir, b);
            if (blended.LengthSquared() > 1e-6) pathDir = Vector3D.Normalize(blended);
        }
    }

    // Braking curve toward this waypoint's profiled speed, capped at the active governor
    // (Cruise/Climb/Descent). CruiseCap() is always <= cruiseSpeed, so the profile's
    // braking margin (built at the cruiseSpeed ceiling) stays valid.
    double vmax = legVmax[cruiseIdx];
    double vBrake = Math.Sqrt(vmax * vmax + 2.0 * cruiseAccel * dist);
    double speed = Math.Min(CruiseCap(), vBrake);

    // Attitude target. fwdTarget is what the *nose* aims at (and what the heading throttle
    // is measured against); upTarget is where the top of the ship points. In space we just
    // nose along the path. In gravity we choose between two strategies (see UseLevelFlight):
    //   nose  - nose along the path, belly to the planet. Uses the component of anti-gravity
    //           PERPENDICULAR to the path (Gram-Schmidt): Forward=pathDir and Up=-gravity are
    //           only jointly satisfiable when orthogonal, so on a climbing/descending leg a
    //           rigid up=-gravity left a standing ~45 deg heading error that pinned cruise at
    //           ~30 m/s. Orthogonalising lets the gyro hit the heading exactly. Best for
    //           rocket-style hulls whose strongest thrust pushes the ship forward.
    //   level - hold the belly straight down (lift bank pointing at the planet) and yaw the
    //           nose to the *horizontal* bearing of the path: a VTOL climb/descent. Best for
    //           lift-heavy hulls - the strong down-thrusters then do the climbing and the
    //           descent-braking instead of the weak rear bank. Nothing in the force law
    //           changes: ApplyForce still drives desiredVel = pathDir*speed, just spreading
    //           the vertical part onto the lift thrusters this attitude puts under gravity.
    // In space (no gravity) hold the current up so we don't roll.
    Vector3D fwdTarget, upTarget;
    bool inGrav = grav.LengthSquared() > 1e-3;
    if (inGrav && UseLevelFlight())
    {
        Vector3D upWorld = Vector3D.Normalize(-grav);
        Vector3D horiz = pathDir - pathDir.Dot(upWorld) * upWorld;   // path with the vertical part removed
        fwdTarget = horiz.LengthSquared() > 1e-6 ? Vector3D.Normalize(horiz) : rc.WorldMatrix.Forward;
        upTarget = upWorld;
    }
    else if (inGrav)
    {
        Vector3D up = -grav;
        Vector3D perp = up - up.Dot(pathDir) * pathDir;
        fwdTarget = pathDir;
        upTarget = perp.LengthSquared() > 1e-6 ? Vector3D.Normalize(perp) : rc.WorldMatrix.Up;
    }
    else { fwdTarget = pathDir; upTarget = rc.WorldMatrix.Up; }
    double align = AlignTo(fwdTarget, upTarget, true);   // coast-hold: latch gyros inert on heading, don't fight thrust-torque noise

    // Turn before accelerating; don't fly fast sideways. Both factors are floored so the
    // ship can still creep and re-align rather than dead-stall. The heading throttle reads
    // the *nose target* (Forward x fwdTarget), not the raw path: being rolled off level, or
    // (in level flight) deliberately not pointing up a steep climb, doesn't affect the
    // thrust that actually moves the ship, so it must not cut cruise speed. Throttling on
    // the vertical miss is exactly the old ~30 m/s trap.
    double headErr = rc.WorldMatrix.Forward.Cross(fwdTarget).Length();
    double alignFac = Clamp(1.0 - headErr / ALIGN_SLOW_TOL, ALIGN_MIN_FAC, 1.0);
    double vmag = vel.Length();
    double velFac = vmag < 1.0 ? 1.0 : Clamp((vel / vmag).Dot(pathDir), VEL_MIN_FAC, 1.0);
    speed *= alignFac * velFac;

    Vector3D desiredVel = pathDir * speed;
    Vector3D dv = desiredVel - vel;

    // Coast in space once we're aligned and already at the target velocity: holding a
    // velocity in zero-g needs zero net force, so cut thrust entirely rather than
    // micro-trimming it every tick (the continuous pulsing is what drains fuel). In
    // gravity we always thrust - ApplyForce keeps its -grav*mass hover compensation,
    // which is exactly why running with dampeners off is safe on the planetary leg.
    bool inSpace = grav.LengthSquared() < 1e-3;
    if (inSpace && align < ALIGN_MOVE_TOL && dv.Length() < COAST_TOL)
        ZeroThrusters();
    else
    {
        // Don't reverse-thrust just to shave a small along-track overshoot of the speed
        // cap. Holding a hard cap with a pure velocity P-controller makes the engines
        // pulse on/off at 60 Hz: tick a hair over the cap -> dv flips negative -> brake;
        // next tick you're under -> accelerate. That limit cycle is the shaking/throttle
        // chatter felt at the cap. If we're only a few m/s fast *along the path*, null
        // just that component so the ship coasts back down to the cap instead of fighting
        // itself. Cross-track/vertical correction and the -grav*mass hover term stay live,
        // and a genuine slowdown (corner or arrival) drives the along-track error strongly
        // negative (past the band) so real braking still fires hard.
        double along = dv.Dot(pathDir);
        if (along < 0.0 && along > -CRUISE_COAST_BAND) dv -= along * pathDir;

        // Don't chase sub-threshold velocity error. What's left after the along-track
        // coast is vertical/cross-track chatter: in low gravity the -grav*mass hover bias
        // is tiny, so a velocity error of only ~g/VEL_GAIN flips the *net* vertical force
        // sign and swaps the up-thruster bank for the down-thruster bank every 60 Hz frame
        // (the visible up/down shake). Deadbanding the correction - while always keeping
        // the hover term - lets the ship ride through that noise; path position still
        // self-corrects because desiredVel always points at the target waypoint.
        if (dv.Length() < VEL_DEADBAND) dv = Vector3D.Zero;
        ApplyForce(dv * mass * VEL_GAIN - grav * mass);   // identical law to FlyToPose
    }

    bool atEnd = cruiseIdx == legWps.Count - 1;
    return atEnd && dist < WpArriveRadius() && vmag < ARRIVE_SPEED;
}

// Arrival holding fix: station-keep at the OUTER stand-off (not the connector) until the
// corridor is clear and confirmed, then hand to Taxi for the final commit. Two jobs:
//
//   * Clearance gate. A craft NEVER flies from cruise straight onto a connector - it waits
//     here until DockCorridorBlocked reads clear for CLEAR_CONFIRM_SEC and, when a tower is
//     live, until it grants the landing. Taxi is the only phase that touches the connector.
//   * Gravity-gated reorient. Rotating to the dock attitude swings the ship's strong thrust
//     axis off anti-gravity, gutting braking authority - dangerous if done while still
//     descending in gravity. So in gravity we hold a level, belly-down attitude (lift bank
//     fighting gravity) until the ship has actually stopped (vmag < ARRIVE_SPEED), and only
//     then rotate to the dock pose. In space there is no braking to lose, so we blend
//     straight to the dock attitude on arrival. (Cruise already arrives here at < ARRIVE_SPEED,
//     so the common path reorients immediately; the gate protects the moving edge cases.)
void TickHolding()
{
    bool toDest = leg.Outbound;
    DockPose p = toDest ? destPose : homePose;
    Vector3D fix = HoldPoint(p);
    Vector3D grav = rc.GetNaturalGravity();
    bool inGrav = grav.LengthSquared() > 1e-3;
    double vmag = rc.GetShipVelocities().LinearVelocity.Length();

    // Reorient gate (design: "stop only in gravity").
    Vector3D faceFwd, faceUp;
    if (inGrav && vmag >= ARRIVE_SPEED)
    {
        Vector3D upWorld = Vector3D.Normalize(-grav);
        Vector3D fwd = rc.WorldMatrix.Forward;
        Vector3D horiz = fwd - fwd.Dot(upWorld) * upWorld;   // keep current heading, belly down for braking
        faceFwd = horiz.LengthSquared() > 1e-6 ? Vector3D.Normalize(horiz) : fwd;
        faceUp = upWorld;
    }
    else { faceFwd = p.Fwd; faceUp = p.Up; }   // stopped, or in space: take the dock attitude

    // Anti-collision: hold at the fix while the corridor is fouled. A false positive only
    // costs time (we hold and auto-resume) - it never faults unless dockBlockSec is set.
    if (DockCorridorBlocked(p))
    {
        dockClearFor = 0;
        dockBlockTimer += dt;
        FlyToPose(fix, faceFwd, faceUp, 1.0);
        statusMsg = (toDest ? "Holding at destination" : "Holding at home")
                  + " - dock blocked (" + dockBlockTimer.ToString("0") + "s)";
        if (dockBlockSec > 0 && dockBlockTimer >= dockBlockSec)
        {
            ReleaseControl();
            SwitchPhase(PhaseId.Faulted);
            statusMsg = "Dock blocked - gave up after " + dockBlockSec.ToString("0") + "s";
        }
        return;
    }

    bool posed = FlyToPose(fix, faceFwd, faceUp, 1.0);

    // Corridor reads clear. After a block, require CLEAR_CONFIRM_SEC of continuous clear
    // before proceeding, so a ship still crossing doesn't get us moving into its path.
    if (dockBlockTimer > 0)
    {
        dockClearFor += dt;
        if (dockClearFor < CLEAR_CONFIRM_SEC)
        {
            statusMsg = (toDest ? "Dock clearing at destination" : "Dock clearing at home") + " - confirming";
            return;
        }
        dockBlockTimer = 0;   // confirmed clear
    }

    // Local corridor clear. Commit to Taxi only once settled at the fix in the dock attitude
    // (posed = arrived, aligned to the dock pose, and stopped) - i.e. reoriented and no longer
    // moving - AND, if a tower is live, once it has granted the landing.
    statusMsg = (toDest ? "Holding at destination" : "Holding at home") + " - cleared";
    if (posed)
    {
        if (!ClearedToProceed("LAND", toDest ? destConn : homeConn))
        { statusMsg = (toDest ? "Holding at destination" : "Holding at home") + " - " + TowerWait("LAND"); return; }
        phaseTimer = 0;
        SwitchPhase(PhaseId.Taxi);
    }
}

// Taxi: the cleared final move. Hold the recorded dock attitude and translate straight
// down the connector axis from the holding fix onto the connector, then connect. If the
// corridor fouls mid-taxi, abandon the commit and fall back to Holding rather than pressing
// into it - the clearance gate re-arms and we only re-taxi once clear.
void TickTaxi()
{
    bool toDest = leg.Outbound;
    var c = GetConnector(toDest ? destConn : homeConn);
    DockPose p = toDest ? destPose : homePose;

    if (c != null && c.Status == MyShipConnectorStatus.Connected)
    {
        AbortAutopilot();
        ReleaseControl();
        c.Connect();
        phaseTimer = 0;
        OnDocked();
        return;
    }
    if (c != null && c.Status == MyShipConnectorStatus.Connectable)
        c.Connect();   // magnet range reached; keep steering until Connected confirms next tick

    if (rc.IsAutoPilotEnabled) AbortAutopilot();

    if (DockCorridorBlocked(p))
    {
        dockBlockTimer = 0; dockClearFor = 0;
        phaseTimer = 0;
        SwitchPhase(PhaseId.Holding);
        statusMsg = toDest ? "Taxi aborted - corridor blocked at destination"
                           : "Taxi aborted - corridor blocked at home";
        return;
    }

    // Cleared: orientation-matched final translation straight down the connector axis.
    FlyToPose(p.Pos, p.Fwd, p.Up, 0.3);

    phaseTimer += dt;
    statusMsg = (toDest ? "Docking at destination" : "Docking at home")
              + " (" + Vector3D.Distance(rc.GetPosition(), p.Pos).ToString("0") + "m)";

    if (phaseTimer >= APPROACH_TIMEOUT)
    {
        AbortAutopilot();
        ReleaseControl();
        SwitchPhase(PhaseId.Faulted);
        statusMsg = "Docking timed out - check approach geometry";
    }
}

// ============================================================================
//  Docking controller (orientation-matched, works on any ship / any connector)
// ============================================================================
// Aligns the ship to a target attitude with the gyros and drives the Remote
// Control to a target position with the thrusters. Returns true once the ship
// has reached the point, matched the attitude, and slowed below ARRIVE_SPEED.
bool FlyToPose(Vector3D pos, Vector3D fwd, Vector3D up, double arriveDist)
{
    SetDampeners(false);   // controller drives translation; ApplyForce handles hover + stopping
    double align = AlignTo(fwd, up);

    Vector3D toTarget = pos - rc.GetPosition();
    double dist = toTarget.Length();
    Vector3D grav = rc.GetNaturalGravity();
    double mass = rc.CalculateShipMass().PhysicalMass;

    // Only translate once roughly aligned, so we never thrust off-axis into the dock.
    Vector3D desiredVel = Vector3D.Zero;
    if (align < ALIGN_MOVE_TOL && dist > 0.05)
    {
        double speedCap = Math.Min((double)dockSpeed, dist * APPROACH_KP);
        desiredVel = toTarget / dist * speedCap;
    }

    Vector3D vel = rc.GetShipVelocities().LinearVelocity;
    Vector3D force = (desiredVel - vel) * mass * VEL_GAIN - grav * mass;
    ApplyForce(force);

    return dist <= arriveDist && align < ALIGN_TOL && vel.Length() < ARRIVE_SPEED;
}

// Hold position at a fixed point with the thrusters (null residual velocity + cancel
// gravity), INDEPENDENT of attitude. Unlike FlyToPose, it does not gate translation on
// alignment - so DepartStaging can keep the ship parked on its staging fix WHILE the gyros
// turn it to the route heading, instead of coasting off the fix during the turn (dampeners
// are off in flight). Same velocity->thrust law as FlyToPose's translation block.
void StationKeep(Vector3D pos)
{
    SetDampeners(false);
    Vector3D toTarget = pos - rc.GetPosition();
    double dist = toTarget.Length();
    Vector3D grav = rc.GetNaturalGravity();
    double mass = rc.CalculateShipMass().PhysicalMass;

    Vector3D desiredVel = Vector3D.Zero;
    if (dist > 0.05)
    {
        double speedCap = Math.Min((double)dockSpeed, dist * APPROACH_KP);
        desiredVel = toTarget / dist * speedCap;
    }

    Vector3D vel = rc.GetShipVelocities().LinearVelocity;
    Vector3D force = (desiredVel - vel) * mass * VEL_GAIN - grav * mass;
    ApplyForce(force);
}

// PD cross-product attitude controller. The P term rotates toward the target
// attitude; the D term (actual angular velocity) damps the rotation so the ship
// settles cleanly instead of overshooting and wobbling. Gyro Pitch/Yaw/Roll are
// angular-velocity setpoints, so the command is desiredRate = Kp*err - Kd*angVel,
// clamped to a gentle max rate (PAM-style) that never hurts docking precision.
// Returns an error metric (~sin of the misalignment angle); near zero when
// forward AND up both match the target.
double AlignTo(Vector3D targetFwd, Vector3D targetUp) => AlignTo(targetFwd, targetUp, GyroCapRad(), false);
double AlignTo(Vector3D targetFwd, Vector3D targetUp, bool coastHold) => AlignTo(targetFwd, targetUp, GyroCapRad(), coastHold);

double AlignTo(Vector3D targetFwd, Vector3D targetUp, double maxRad, bool coastHold)
{
    Vector3D fwd = rc.WorldMatrix.Forward, up = rc.WorldMatrix.Up;
    Vector3D fErr = fwd.Cross(targetFwd);
    // The cross product is sin(theta)*axis: it SHRINKS toward zero as the misalignment
    // approaches 180 deg - exactly when the turn is largest - so a near-reversed target
    // (dock nose-in, then undock and head back out is a ~180 deg yaw) produces almost no
    // command and the ship stalls at the unstable equilibrium, waiting ~30 s for float
    // noise to tip it off. Past 90 deg (dot < 0) replace the shrinking term with a
    // full-strength unit turn about a valid axis, so the back half of the rotation drives
    // just as hard as the front. Also stops attErr reading ~0 (falsely "aligned") at 180.
    if (fwd.Dot(targetFwd) < 0.0)
    {
        double l = fErr.Length();
        fErr = l > 1e-6 ? fErr / l : Vector3D.Normalize(up);   // any axis perpendicular to forward if exactly reversed
    }
    Vector3D uErr = up.Cross(targetUp);
    if (up.Dot(targetUp) < 0.0)
    {
        double l = uErr.Length();
        uErr = l > 1e-6 ? uErr / l : Vector3D.Normalize(fwd);   // roll axis if the ship is exactly inverted
    }
    Vector3D err = fErr + uErr;   // combined world-space rotation axis * angle
    double attErr = fErr.Length() + uErr.Length();
    lastAlignErr = attErr;        // latch for the telem view (attitude-stall diagnosis)

    Vector3D angVel = rc.GetShipVelocities().AngularVelocity;   // world rad/s

    // Rest deadband: hold the gyros fully inert once on-heading so they stop feeding
    // AngularVelocity noise back through the damping term (a strong gyro chatters forever
    // chasing "motion" that's just float noise; capping RPM can't fix it - only stopping
    // the command does). Two modes:
    //   coastHold (cruise): LATCH inert on heading alone and ignore angular-velocity
    //     spikes. At cruise the nose target is the direction to a waypoint tens of km
    //     off; as the ship coasts (and drifts a hair cross-track) that vector inches
    //     around, and off-centre thruster corrections add little torque pulses - so the
    //     nose and the path never converge to the arc-minute rest band and the gyros hunt
    //     it every tick, fighting the translation controller (the jitter the pilot sees).
    //     Since the nose direction doesn't steer (thrust is world-space omni-directional),
    //     a few degrees of steady nose/path offset is cosmetic. So latch inert once the
    //     heading is roughly on the path (COAST_HOLD_ENTER, ~2.9 deg) and only re-engage
    //     on a real drift or corner (COAST_HOLD_WAKE, ~5.7 deg) - wide hysteresis lets the
    //     nose settle instead of chasing.
    //   precision (docking): strict - rest only when heading AND spin are both tiny, so a
    //     docked-attitude match stays exact and FlyToPose can still seat the connector.
    if (coastHold)
    {
        bool stay = gyroResting ? attErr < COAST_HOLD_WAKE
                                : (attErr < COAST_HOLD_ENTER && angVel.Length() < GYRO_REST_RATE * 2.0);
        if (stay) { gyroResting = true; HoldGyrosInert(); return attErr; }
        gyroResting = false;
    }
    else
    {
        gyroResting = false;
        if (attErr < GYRO_REST_ATT && angVel.Length() < GYRO_REST_RATE) { HoldGyrosInert(); return attErr; }
    }

    // Inside the deadband, stop chasing the target: drop the P term so the command is
    // pure damping (-angVel*gyroDamp), which nulls any residual spin and then holds
    // still. This kills the constant micro-hunt around a heading that otherwise never
    // rests (and keeps re-trimming thrust, burning fuel).
    if (err.Length() < ALIGN_DEADBAND) err = Vector3D.Zero;

    Vector3D cmd = err * gyroGain - angVel * gyroDamp;

    // Cap the commanded angular rate so rotation stays gentle (rad/s, frame-independent).
    double m = cmd.Length();
    if (m > maxRad && m > 1e-6) cmd *= maxRad / m;

    foreach (var g in gyros)
    {
        if (g == null || !g.IsWorking) continue;
        Vector3D local = Vector3D.TransformNormal(cmd, MatrixD.Transpose(g.WorldMatrix));
        g.GyroOverride = true;
        g.Pitch = (float)(-local.X);
        g.Yaw   = (float)(-local.Y);
        g.Roll  = (float)(-local.Z);
    }
    return attErr;
}

// Freeze every gyro (override on, zero rate) - the inert state of the rest deadband.
void HoldGyrosInert()
{
    foreach (var g in gyros)
        if (g != null && g.IsWorking) { g.GyroOverride = true; g.Pitch = 0f; g.Yaw = 0f; g.Roll = 0f; }
}

// Gyro angular-rate cap in rad/s. gyroRpmCap>0 uses that; otherwise PAM's gentle
// defaults by grid size (15 rpm small / 5 rpm large).
double GyroCapRad()
{
    double rpm = gyroRpmCap > 0 ? gyroRpmCap
               : (Me.CubeGrid.GridSizeEnum == MyCubeSize.Small ? 15.0 : 5.0);
    return rpm * 2.0 * Math.PI / 60.0;
}

// Distribute a desired world-space force across the thrusters. Each thruster
// pushes the ship along its own Backward axis; we bucket them into the six
// ship-local directions and split each axis's demand proportionally to thrust.
void ApplyForce(Vector3D worldForce)
{
    if (rc == null) return;
    // Never write a non-finite override to a thruster. A NaN/Infinity ThrustOverride
    // propagates straight into the physics solver and can destabilise or crash the
    // server - it is the one thing this script does that a client can't just shrug off.
    // If the force ever arrives non-finite (a degenerate path/velocity vector slipping
    // through upstream), cut thrust this tick rather than feed garbage to the engines.
    if (!IsFinite(worldForce)) { ZeroThrusters(); return; }
    double[] cap; MatrixD toLocal;
    AxisThrust(out cap, out toLocal);
    Vector3D lf = Vector3D.TransformNormal(worldForce, toLocal);

    // need[0..5] = demand along +X,-X,+Y,-Y,+Z,-Z (local), all >= 0
    double[] need = new double[6];
    need[0] = Math.Max(0, lf.X); need[1] = Math.Max(0, -lf.X);
    need[2] = Math.Max(0, lf.Y); need[3] = Math.Max(0, -lf.Y);
    need[4] = Math.Max(0, lf.Z); need[5] = Math.Max(0, -lf.Z);

    foreach (var t in thrusters)
    {
        if (t == null || !t.IsWorking) continue;
        int k = ThrustKey(t, toLocal);
        if (cap[k] <= 1e-3 || need[k] <= 1e-3) { t.ThrustOverride = 0f; continue; }
        double share = need[k] * (t.MaxEffectiveThrust / cap[k]);
        t.ThrustOverride = (float)Math.Min(share, t.MaxEffectiveThrust);
    }
}

// True only if every component is a real, finite number. Guards the thrust path
// against NaN/Infinity (see ApplyForce) - Math.Max(0, NaN) is NaN and NaN <= eps is
// false, so a bad force would otherwise sail past the per-thruster skip guard.
static bool IsFinite(Vector3D v)
{
    return !double.IsNaN(v.X) && !double.IsNaN(v.Y) && !double.IsNaN(v.Z) &&
           !double.IsInfinity(v.X) && !double.IsInfinity(v.Y) && !double.IsInfinity(v.Z);
}

// Sum each working thruster's MaxEffectiveThrust into its ship-local axis bucket
// (+X,-X,+Y,-Y,+Z,-Z). Shared by ApplyForce (allocation) and the velocity
// profile (available acceleration).
void AxisThrust(out double[] cap, out MatrixD toLocal)
{
    toLocal = MatrixD.Transpose(rc.WorldMatrix);
    cap = new double[6];
    foreach (var t in thrusters)
        if (t != null && t.IsWorking) cap[ThrustKey(t, toLocal)] += t.MaxEffectiveThrust;
}

// Which of the six ship-local directions this thruster pushes the ship.
int ThrustKey(IMyThrust t, MatrixD toLocal)
{
    Vector3D lp = Vector3D.TransformNormal(t.WorldMatrix.Backward, toLocal);
    double ax = Math.Abs(lp.X), ay = Math.Abs(lp.Y), az = Math.Abs(lp.Z);
    if (ax >= ay && ax >= az) return lp.X >= 0 ? 0 : 1;
    if (ay >= az)             return lp.Y >= 0 ? 2 : 3;
    return lp.Z >= 0 ? 4 : 5;
}

// Which gravity-leg attitude RunCruiseControl should fly (see the attitude block there).
//   "level" / "nose"  - forced by Custom Data.
//   "auto" (default)   - fly level (belly-down VTOL climb) when the hull's up-thrust
//                        outweighs its forward-thrust, i.e. the strong bank is the lift
//                        bank; otherwise nose along the path. cap[2] = +Y push (lift up),
//                        cap[5] = -Z push (forward). The 1.1x / 0.9x band is hysteresis so
//                        a hull that's roughly balanced doesn't flip attitude every tick.
bool UseLevelFlight()
{
    if (cruiseAttitude == "level") return true;
    if (cruiseAttitude == "nose") return false;
    double[] cap; MatrixD toLocal;
    AxisThrust(out cap, out toLocal);
    double up = cap[2], fwd = cap[5];
    if (!cruiseFlyLevel && up > fwd * 1.1) cruiseFlyLevel = true;
    else if (cruiseFlyLevel && up < fwd * 0.9) cruiseFlyLevel = false;
    return cruiseFlyLevel;
}

// Zero every thruster/gyro override so the autopilot (or the pilot) has control.
// Also restores dampeners, which the flight controller turns off so it can coast
// in space without the game braking the cruise. Runs on done/fault/stop/idle and on
// recompile, so the ship is never left adrift with dampeners disabled.
void ReleaseControl()
{
    foreach (var t in thrusters) if (t != null) t.ThrustOverride = 0f;
    foreach (var g in gyros)
        if (g != null) { g.GyroOverride = false; g.Pitch = 0f; g.Yaw = 0f; g.Roll = 0f; }
    foreach (var cam in cameras) if (cam != null) cam.EnableRaycast = false;   // stop charging the dock-clearance raycast
    dockBlockTimer = 0; dockClearFor = 0;
    SetDampeners(true);
}

// Zero only the thruster overrides (gyros keep holding attitude). With dampeners off,
// this is a true coast: the ship keeps its velocity and burns no fuel.
void ZeroThrusters()
{
    foreach (var t in thrusters) if (t != null) t.ThrustOverride = 0f;
}

// The controller owns the dampeners while flying: OFF so coasting in space costs no
// fuel and there's no automatic braking to fight; restored ON when control is released.
// But it only re-asserts dampeners it actually turned off (dampenersOwned) - so a parked
// or idle PB never fights a pilot hand-flying with dampeners deliberately switched off.
bool dampenersOwned = false;   // true while the controller owes a dampener restore
void SetDampeners(bool on)
{
    if (rc == null) return;
    if (on)
    {
        if (!dampenersOwned) return;   // we didn't turn them off; leave the pilot's switch alone
        rc.DampenersOverride = true;
        dampenersOwned = false;
    }
    else
    {
        rc.DampenersOverride = false;
        dampenersOwned = true;
    }
}

void OnDocked()
{
    bool atDest = leg.Outbound;
    FinishLegMeasure();   // learn what the leg just flown actually cost in fuel/charge
    if (atDest)
    {
        SwitchPhase(PhaseId.Unloading);
        phaseTimer = 0;
    }
    else
    {
        // Home again. OneTrip and OneWay stop and hold here; Continuous loads and
        // sets out again (subject to the home departure trigger).
        if (runMode == RunMode.OneTrip) { operating = false; SwitchPhase(PhaseId.Idle); statusMsg = "Trip complete"; }
        else if (runMode == RunMode.OneWay) { operating = false; SwitchPhase(PhaseId.Idle); statusMsg = "Holding at home"; }
        else { SwitchPhase(PhaseId.Loading); phaseTimer = 0; }
    }
}

// ============================================================================
//  Dock clearance (anti-collision on final approach)
// ============================================================================
// Raycast down the docking corridor to catch another grid parked on - or drifting
// across - the connector before we fly into it (a shuttle was destroyed doing exactly
// this when someone landed on its dock). Camera-primary: pick the camera that actually
// faces the dock, cast a thin ray to the docked-pose point, and read the first thing
// between us and the dock:
//   - hit nothing (open segment) ........... clear
//   - hit the base's own grid .............. clear   (identity match, distance-agnostic)
//   - hit any OTHER grid in the corridor ... BLOCKED
// The identity match is why this doesn't false-positive on the cases the operator worried
// about: the base's own connector reads clear (it IS the base), an off-axis neighbour the
// ray never touches reads clear, and a ship that undocked but lingers *beside* the corridor
// is never hit. Only a foreign grid genuinely in the approach path holds us off - and there
// waiting is exactly right, because flying into it is the crash we're preventing.
// Pre-0.15 routes stored no base grid id; those fall back to a conservative distance rule.
// Returns true = corridor blocked.
bool DockCorridorBlocked(DockPose p)
{
    if (!dockClearCheck || cameras.Count == 0 || rc == null) return false;

    Vector3D dock = p.Pos;
    // Choose the working camera that faces the dock within the trusted cone and is nearest
    // on-axis. An out-of-cone raycast silently returns empty (looks "clear"), so a camera
    // that isn't actually pointed at the dock must never be trusted to clear the corridor.
    IMyCameraBlock best = null;
    double bestDot = CLEAR_CONE_DOT, dockDist = 0;
    foreach (var cam in cameras)
    {
        if (cam == null || !cam.IsWorking) continue;
        Vector3D toDock = dock - cam.GetPosition();
        double d = toDock.Length();
        if (d < 1e-3) continue;
        double dot = cam.WorldMatrix.Forward.Dot(toDock / d);
        if (dot > bestDot) { bestDot = dot; best = cam; dockDist = d; }
    }
    if (best == null) return false;   // no camera can see the corridor -> can't judge; never false-block

    if (!best.EnableRaycast) best.EnableRaycast = true;
    if (!best.CanScan(dockDist + CLEAR_RANGE_PAD)) return false;   // still charging scan range; treat as clear until it can reach the dock

    MyDetectedEntityInfo hit = best.Raycast(dock);
    if (hit.IsEmpty()) return false;                          // open corridor
    if (hit.EntityId == p.BaseGridId) return false;           // the base itself
    if (hit.EntityId == Me.CubeGrid.EntityId) return false;   // our own hull occluding this camera - ignore it

    if (p.BaseGridId == 0)
    {
        // Legacy route: no base identity to compare against. Only treat a hit clearly IN
        // FRONT of the dock point as an obstruction; a hit at ~dock distance is assumed to
        // be the base structure. Re-record the route to capture the base id and get the
        // exact identity check above.
        double hitDist = Vector3D.Distance(best.GetPosition(), hit.HitPosition ?? dock);
        return hitDist < dockDist - CLEAR_LEGACY_MARGIN;
    }
    return true;   // a foreign grid is sitting in the docking corridor
}

// ============================================================================
//  Helpers - blocks & sensors
// ============================================================================
void Discover()
{
    connectors.Clear(); cargo.Clear(); shipScreens.Clear();
    var grid = Me.CubeGrid;

    // Remote Control
    if (!string.IsNullOrEmpty(remoteName))
        rc = GridTerminalSystem.GetBlockWithName(remoteName) as IMyRemoteControl;
    if (rc == null)
    {
        var rcs = new List<IMyRemoteControl>();
        GridTerminalSystem.GetBlocksOfType(rcs, b => b.CubeGrid == grid);
        rc = rcs.Count > 0 ? rcs[0] : null;
    }

    GridTerminalSystem.GetBlocksOfType(connectors, b => b.CubeGrid == grid);
    GridTerminalSystem.GetBlocksOfType(cargo, b => b.CubeGrid == grid);
    GridTerminalSystem.GetBlocksOfType(gyros, b => b.CubeGrid == grid);
    GridTerminalSystem.GetBlocksOfType(thrusters, b => b.CubeGrid == grid);
    GridTerminalSystem.GetBlocksOfType(batteries, b => b.CubeGrid == grid);

    // Dock-clearance cameras (anti-collision). Prefer cameras tagged cameraTag; if none
    // carry the tag, fall back to every camera on the grid and let the clearance check
    // pick whichever one actually faces the dock. Raycast is charged only during an
    // approach (see DockCorridorBlocked) and powered back down in ReleaseControl.
    var cams = new List<IMyCameraBlock>();
    GridTerminalSystem.GetBlocksOfType(cams, b => b.CubeGrid == grid);
    cameras.Clear();
    foreach (var cam in cams) if (HasTag(cam.CustomName, cameraTag)) cameras.Add(cam);
    if (cameras.Count == 0) cameras.AddRange(cams);   // untagged fallback: keep all, filter by aim at check time

    // Gas tanks whose subtype names them a Hydrogen tank feed the fuel gate; oxygen
    // tanks are ignored. Ships with no hydrogen tanks just skip the hydrogen check.
    var tanks = new List<IMyGasTank>();
    GridTerminalSystem.GetBlocksOfType(tanks, b => b.CubeGrid == grid);
    h2Tanks.Clear();
    foreach (var t in tanks)
        if (t.BlockDefinition.SubtypeName.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0)
            h2Tanks.Add(t);

    // Sorters are found by tag: any conveyor sorter whose name contains the
    // load/unload tag (case-insensitive). Name them anything - only the tag
    // has to appear somewhere in the name. Multiple per role is fine.
    var sorters = new List<IMyConveyorSorter>();
    GridTerminalSystem.GetBlocksOfType(sorters, b => b.CubeGrid == grid);
    loadSorters.Clear(); unloadSorters.Clear();
    foreach (var s in sorters)
    {
        if (HasTag(s.CustomName, loadTag)) loadSorters.Add(s);
        if (HasTag(s.CustomName, unloadTag)) unloadSorters.Add(s);
    }

    // Status surfaces. Two ways a screen picks its view:
    //   1. A tagged text panel: name contains lcdTag, optionally with a view/size,
    //      e.g. [SF] (full), [SF:trip], [SF:menu:1.2].
    //   2. A multi-surface block (cockpit, PB, ...) that OPTS IN via a
    //      [sf-screens] section in its Custom Data mapping surface index -> view
    //      (e.g. "0 = menu", "2 = status@1.4"). Opt-in, so it never hijacks an
    //      unrelated cockpit. Each screen is later sized to its OWN content.
    var panels = new List<IMyTextPanel>();
    GridTerminalSystem.GetBlocksOfType(panels, b => b.CubeGrid == grid && HasTag(b.CustomName, TagOpener()));
    foreach (var p in panels)
    {
        string view; float size, pad;
        ParseScreenTag(p.CustomName, out view, out size, out pad);
        AddScreen(p, view, size, pad);
    }

    // Multi-surface providers with a [sf-screens] config section (or the legacy
    // [shuttle-screens] name, still honoured so an existing cockpit map keeps working).
    var providers = new List<IMyTerminalBlock>();
    GridTerminalSystem.GetBlocksOfType(providers, b => b.CubeGrid == grid
        && b is IMyTextSurfaceProvider
        && (b.CustomData.IndexOf("sf-screens", StringComparison.OrdinalIgnoreCase) >= 0
            || b.CustomData.IndexOf("shuttle-screens", StringComparison.OrdinalIgnoreCase) >= 0));
    bool pbConfigured = false;
    foreach (var b in providers)
    {
        var prov = b as IMyTextSurfaceProvider;
        var ini = new MyIni();
        if (!ini.TryParse(b.CustomData)) continue;
        string sec = ini.ContainsSection("sf-screens") ? "sf-screens"
                   : ini.ContainsSection("shuttle-screens") ? "shuttle-screens" : null;
        if (sec == null) continue;
        var keys = new List<MyIniKey>();
        ini.GetKeys(sec, keys);
        foreach (var k in keys)
        {
            int idx;
            if (!int.TryParse(k.Name.Trim(), out idx) || idx < 0 || idx >= prov.SurfaceCount) continue;
            string view; float size, pad;
            ParseViewSpec(ini.Get(k).ToString(""), out view, out size, out pad);
            AddScreen(prov.GetSurface(idx), view, size, pad);
        }
        if (b == Me) pbConfigured = true;
    }

    // PB's own screen: full-view fallback unless the PB itself declared [sf-screens].
    pbSurface = Me.GetSurface(0);
    if (!pbConfigured) { PrepSurface(pbSurface); AddScreen(pbSurface, VIEW_FULL, 0f, 0f); }
}

// The tag "opener" is lcdTag without a trailing ']', so [SF] matches both the
// plain tag and the extended [SF:view] / [SF:view:size] forms.
string TagOpener()
{
    return lcdTag.EndsWith("]") ? lcdTag.Substring(0, lcdTag.Length - 1) : lcdTag;
}

// Prep + register a screen target, de-duplicating so the same surface isn't added
// twice (e.g. a panel tagged AND listed in a [sf-screens] section).
void AddScreen(IMyTextSurface s, string view, float size, float pad)
{
    if (s == null) return;
    for (int i = 0; i < shipScreens.Count; i++) if (shipScreens[i].Surface == s) return;
    PrepSurface(s);
    shipScreens.Add(new ScreenTarget { Surface = s, View = view, FixedSize = size, Pad = pad });
}

// Parse the view (+ optional size + optional padding) out of a tagged panel name.
// Accepts the tag opener then "]", ":view]", ":view:size]", or ":view:size:pad]".
// Defaults to the full view, auto-fit font, zero padding.
void ParseScreenTag(string name, out string view, out float size, out float pad)
{
    view = VIEW_FULL; size = 0f; pad = 0f;
    string opener = TagOpener();
    int i = name.IndexOf(opener, StringComparison.OrdinalIgnoreCase);
    if (i < 0) return;
    int start = i + opener.Length;
    int end = name.IndexOf(']', start);
    string inner = end > start ? name.Substring(start, end - start) : name.Substring(start);
    // inner is "" (plain tag) or ":view" / ":view:size" / ":view:size:pad"
    var parts = inner.Split(':');
    // parts[0] is empty (text before the first ':') for the plain/extended tag
    if (parts.Length >= 2 && parts[1].Trim().Length > 0) view = NormalizeView(parts[1]);
    if (parts.Length >= 3) { float f; if (float.TryParse(parts[2].Trim(), out f) && f > 0) size = f; }
    if (parts.Length >= 4) { float f; if (float.TryParse(parts[3].Trim(), out f) && f >= 0) pad = f; }
}

// Parse a "view", "view@size", "view@size/pad", or "view/pad" spec from a
// [sf-screens] value. '@' pins the font size, '/' the padding (% per side).
void ParseViewSpec(string spec, out string view, out float size, out float pad)
{
    view = VIEW_FULL; size = 0f; pad = 0f;
    if (string.IsNullOrEmpty(spec)) return;
    var pp = spec.Split('/');
    if (pp.Length >= 2) { float f; if (float.TryParse(pp[1].Trim(), out f) && f >= 0) pad = f; }
    var parts = pp[0].Split('@');
    view = NormalizeView(parts[0]);
    if (parts.Length >= 2) { float f; if (float.TryParse(parts[1].Trim(), out f) && f > 0) size = f; }
}

// Map a view name (case-insensitive) to a known view constant; unknown -> full.
string NormalizeView(string v)
{
    switch (v.Trim().ToLowerInvariant())
    {
        case VIEW_MENU:   return VIEW_MENU;
        case VIEW_STATUS: return VIEW_STATUS;
        case VIEW_TRIP:   return VIEW_TRIP;
        case VIEW_TELEM:  return VIEW_TELEM;
        default:          return VIEW_FULL;
    }
}

// Configure a surface for monospaced, left-aligned status text. The font size is
// set per-render by SizeAndWrite (each screen sized to its own content).
void PrepSurface(IMyTextSurface s)
{
    s.ContentType = ContentType.TEXT_AND_IMAGE;
    s.Font = "Monospace";
    s.Alignment = TextAlignment.LEFT;
    s.TextPadding = 0f;
}

IMyShipConnector ConnectedConnector()
{
    foreach (var c in connectors) if (c.Status == MyShipConnectorStatus.Connected) return c;
    return null;
}

IMyShipConnector GetConnector(string name)
{
    foreach (var c in connectors) if (c.CustomName == name) return c;
    return null;
}

// Am I physically connected to ANY connector right now? Name-independent, so it
// works even when the ship docks both ends with the same physical connector.
bool DockedNow() { return ConnectedConnector() != null; }

// Which recorded end is the ship physically at? Decided by proximity to the two
// recorded docked poses (distinct world coordinates ~78 km apart), NOT by the
// connector name - a shuttle that docks both ends with the SAME connector has
// homeConn == destConn, so a name match can't tell the ends apart. Assumes the
// two docked poses are separated by more than a ship length (true for any real
// home/station pair). Only meaningful when haveRoute.
bool AtHomeEnd()
{
    Vector3D p = rc.GetPosition();
    return Vector3D.DistanceSquared(p, homePose.Pos) <= Vector3D.DistanceSquared(p, destPose.Pos);
}

// Is the ship physically parked at a recorded route dock (home OR dest), within
// DOCK_MATCH_DIST of that pose? AtHomeEnd only picks the *nearer* end, so on its
// own it can't tell "docked at a route end" from "docked at some other connector
// that merely happens to be closer to one end" - which used to dispatch a leg and
// beeline across the map to the recorded dock. A start/depart is gated on this.
bool AtRouteEnd()
{
    if (rc == null || !haveRoute) return false;
    Vector3D p = rc.GetPosition();
    double tol = DOCK_MATCH_DIST * DOCK_MATCH_DIST;
    return Vector3D.DistanceSquared(p, homePose.Pos) <= tol
        || Vector3D.DistanceSquared(p, destPose.Pos) <= tol;
}

void SetSorters(List<IMyConveyorSorter> list, bool on)
{
    foreach (var s in list)
        if (s != null && s.Enabled != on) s.Enabled = on;
}

// Case-insensitive "does this block name contain the tag" test.
bool HasTag(string name, string tag)
{
    return !string.IsNullOrEmpty(tag) &&
           name.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0;
}

double ShipMassKg() { return rc != null ? rc.CalculateShipMass().PhysicalMass : 0; }

double CargoFillPct()
{
    double cur = 0, max = 0;
    foreach (var c in cargo)
    {
        var inv = c.GetInventory();
        cur += (double)inv.CurrentVolume;
        max += (double)inv.MaxVolume;
    }
    return max <= 0 ? 0 : cur / max * 100.0;
}

// ---- Tower clearance -------------------------------------------------------
// True only while an opted-in ship is hearing a live tower. No heartbeat within
// TOWER_TIMEOUT (or useTower off) => independent operation on the local gate alone.
bool TowerActive() { return useTower && towerAge < TOWER_TIMEOUT; }

// The single gate the flight loop consults before committing to a controlled move
// (undock, or taxi onto a connector). Returns true to proceed. When a tower is live
// but hasn't granted this action, it (re)sends CMD|REQ on the resend interval and
// returns false so the caller holds. `action` is "DEPART" or "LAND"; `dock` names
// the connector for the tower's benefit.
bool ClearedToProceed(string action, string dock)
{
    if (!TowerActive()) return true;                    // no tower / disabled -> proceed independently
    if (cleared && reqAction == action) return true;    // granted for this action
    if (!clearanceRequested || reqTimer >= REQ_RESEND)
    {
        IGC.SendBroadcastMessage(channel, "CMD|REQ|" + shipName + "|" + action + "|" + dock);
        // Fresh request => not yet granted for it. Clearing here (not just in SwitchPhase) drops
        // any stale grant left over when the action changes within a phase, so a leftover DEPART
        // clear can never satisfy a LAND gate.
        clearanceRequested = true; reqAction = action; reqTimer = 0; cleared = false;
    }
    return false;
}

// Status line while waiting at a tower gate (a HOLD reason wins over the generic wait).
string TowerWait(string action)
{
    return holdReason.Length > 0 ? "HOLD: " + holdReason : "Awaiting tower - " + action;
}

// ---- Remote / manual departure --------------------------------------------
// Accept DEPART commands broadcast by a station PB. Command messages are tagged
// "CMD|..." so they're never confused with the pipe-delimited status reports the
// ship itself broadcasts on the same channel (which the base consumes). Tower
// clearance rides the same CMD| family (TOWER heartbeat, CLEAR/HOLD grants).
void DrainIgc()
{
    if (listener == null) return;
    while (listener.HasPendingMessage)
    {
        var m = listener.AcceptMessage();
        var s = m.Data as string;
        if (string.IsNullOrEmpty(s) || !s.StartsWith("CMD|")) continue;   // ignore status broadcasts
        var f = s.Split('|');
        if (f.Length < 2) continue;
        if (f[1] == "DEPART")
        {
            string who = f.Length >= 3 ? f[2] : "*";
            if (who == "*" || who.Equals(shipName, StringComparison.OrdinalIgnoreCase))
                RequestDepart();
        }
        else if (f[1] == "TOWER") towerAge = 0;   // heartbeat: a tower is live on this channel
        else if (f[1] == "CLEAR" && f.Length >= 4 && f[2].Equals(shipName, StringComparison.OrdinalIgnoreCase) && f[3] == reqAction)
        { cleared = true; holdReason = ""; }
        else if (f[1] == "HOLD" && f.Length >= 4 && f[2].Equals(shipName, StringComparison.OrdinalIgnoreCase) && f[3] == reqAction)
        { cleared = false; holdReason = f.Length >= 5 ? f[4] : "hold"; }
    }
}

// Manual "Depart Now" (ship button / station IGC). Two cases:
//   - Mid-cycle, holding at a dock on a trigger (Loading/Unloading): latch the
//     override so the phase releases now (still subject to the fuel gate). The
//     latch is consumed when the shuttle actually leaves (cleared by STOP / START).
//   - Parked Idle at a dock: begin operating and dispatch the next leg immediately,
//     exactly the way START does (direction from which end we're physically at),
//     with the override latched so we don't sit waiting on the trigger.
void RequestDepart()
{
    if (phase == PhaseId.Loading || phase == PhaseId.Unloading)
    {
        departRequested = true;
        statusMsg = "Depart requested";
        return;
    }

    if (phase == PhaseId.Idle && haveRoute && DockedNow())
    {
        if (!AtRouteEnd()) { statusMsg = "DEPART: not at a route dock - GO HOME/DEST first"; return; }
        operating = true;
        bool atHome = AtHomeEnd();
        if (runMode == RunMode.OneWay)
        {
            if (atHome) SwitchPhase(PhaseId.Loading);
            else { leg.Outbound = false; SwitchPhase(PhaseId.Undock); }   // at dest -> straight home
        }
        else
            SwitchPhase(atHome ? PhaseId.Loading : PhaseId.Unloading);
        phaseTimer = 0;
        departRequested = true;
        statusMsg = "Departing now";
        return;
    }

    if (operating) statusMsg = "DEPART: already under way";
    else statusMsg = haveRoute ? "DEPART: dock first" : "DEPART: no route";
}

// ---- Fuel / charge gate ----------------------------------------------------
// Current hydrogen fill across working H2 tanks, as a %. -1 when the ship has no
// hydrogen tanks, so the hydrogen gate simply doesn't apply (battery/ion ships).
double HydrogenPct()
{
    double cur = 0, cap = 0;
    foreach (var t in h2Tanks)
        if (t != null && t.IsWorking) { cap += t.Capacity; cur += t.FilledRatio * t.Capacity; }
    return cap <= 0 ? -1 : cur / cap * 100.0;
}

// Current battery charge across working batteries, as a %. -1 when there are none.
double BatteryPct()
{
    double cur = 0, cap = 0;
    foreach (var b in batteries)
        if (b != null && b.IsWorking) { cap += b.MaxStoredPower; cur += b.CurrentStoredPower; }
    return cap <= 0 ? -1 : cur / cap * 100.0;
}

// May the shuttle depart on fuel grounds? Requires each resource (that the ship
// actually has) to sit at or above the greater of its configured floor and the
// last measured consumption for this leg direction plus the safety margin.
bool DepartFuelOk(bool outbound, out string msg)
{
    double h2 = HydrogenPct();
    double batt = BatteryPct();
    double m = 1.0 + fuelMarginPct / 100.0;

    double needH2 = minHydrogenPct;
    double estH2 = outbound ? estHydroOut : estHydroHome;
    if (estH2 > 0) needH2 = Math.Max(needH2, estH2 * m);

    double needBatt = minBatteryPct;
    double estB = outbound ? estBattOut : estBattHome;
    if (estB > 0) needBatt = Math.Max(needBatt, estB * m);

    if (h2 >= 0 && h2 < needH2)
    { msg = "Hold: H2 " + h2.ToString("0") + "% < " + needH2.ToString("0") + "% to depart"; return false; }
    if (batt >= 0 && batt < needBatt)
    { msg = "Hold: Batt " + batt.ToString("0") + "% < " + needBatt.ToString("0") + "% to depart"; return false; }
    msg = "";
    return true;
}

// Snapshot fuel/charge at the start of a leg so its consumption can be measured on
// arrival. `outbound` = home->dest; else dest->home.
void BeginLegMeasure(bool outbound)
{
    legOutbound = outbound;
    legStartH2 = HydrogenPct();
    legStartBatt = BatteryPct();
}

// On arrival, record how much this leg burned (per direction) and persist it, so
// the fuel gate learns the real cost of the run. Skipped if no leg was in progress
// (e.g. a recompile mid-flight lost the start snapshot) - the prior estimate holds.
void FinishLegMeasure()
{
    if (legStartH2 < 0 && legStartBatt < 0) return;
    double h2 = HydrogenPct(), batt = BatteryPct();
    if (legOutbound)
    {
        if (legStartH2 >= 0 && h2 >= 0) estHydroOut = Math.Max(0, legStartH2 - h2);
        if (legStartBatt >= 0 && batt >= 0) estBattOut = Math.Max(0, legStartBatt - batt);
    }
    else
    {
        if (legStartH2 >= 0 && h2 >= 0) estHydroHome = Math.Max(0, legStartH2 - h2);
        if (legStartBatt >= 0 && batt >= 0) estBattHome = Math.Max(0, legStartBatt - batt);
    }
    legStartH2 = -1; legStartBatt = -1;
    SaveEstimates();
}

void AbortAutopilot()
{
    if (rc == null) return;
    rc.SetAutoPilotEnabled(false);
    rc.ClearWaypoints();
    cruiseArmed = false;
}

// ============================================================================
//  ETA
// ============================================================================
string FormatEta()
{
    double dist = RemainingDistance();
    double spd = rc != null ? rc.GetShipSpeed() : 0;
    if (spd < 1) return "--:--";
    int sec = (int)(dist / spd);
    return (sec / 60).ToString("00") + ":" + (sec % 60).ToString("00");
}

// Remaining distance along the current leg: ship -> current waypoint, then each
// remaining leg segment through to the final stand-off. Drives the ETA and the
// base-board distance readout.
double RemainingDistance()
{
    if (rc == null || !cruiseArmed || legWps.Count == 0) return 0;
    if (cruiseIdx >= legWps.Count) return 0;
    double d = Vector3D.Distance(rc.GetPosition(), legWps[cruiseIdx]);
    for (int i = cruiseIdx; i < legWps.Count - 1; i++)
        d += Vector3D.Distance(legWps[i], legWps[i + 1]);
    return d;
}

// ============================================================================
//  LCD menu (ship role)
// ============================================================================
int MenuCount()
{
    switch (menuPage)
    {
        case PAGE_MAIN:     return 6;   // Start/Stop, Run Mode, Depart Now, Go Home, Record, Settings
        case PAGE_RECORD:   return 5;   // Home, Dest, Clear, Routes, Back
        case PAGE_SETTINGS: return 6;   // Cruise, Dock, MaxMass, DepartFill, Depart page, Back
        case PAGE_DEPART:   return 8;   // Home trig, Dest trig, Dwell, MinH2, MinBatt, Margin, Tower, Back
        case PAGE_ROUTES:   return routeNames.Count + 1;   // one item per saved route + Back
        default:            return 1;
    }
}

void MenuMove(int dir)
{
    if (editing) { AdjustEdit(dir); return; }
    int n = MenuCount();
    menuIndex = ((menuIndex + dir) % n + n) % n;
}

void MenuApply()
{
    if (editing) { CommitEdit(); editing = false; return; }

    if (menuPage == PAGE_MAIN)
    {
        switch (menuIndex)
        {
            case 0: HandleCommand(operating ? "STOP" : "START"); break;
            case 1: CycleMode(); break;
            case 2: HandleCommand("DEPART"); break;
            case 3: HandleCommand("HOME"); break;
            case 4: menuPage = PAGE_RECORD; menuIndex = 0; break;
            case 5: menuPage = PAGE_SETTINGS; menuIndex = 0; break;
        }
    }
    else if (menuPage == PAGE_RECORD)
    {
        switch (menuIndex)
        {
            case 0: RecordHome(); break;
            case 1: RecordDest(); break;
            case 2: ClearRoute(); statusMsg = "Route cleared"; break;
            case 3: menuPage = PAGE_ROUTES; menuIndex = 0; break;
            case 4: menuPage = PAGE_MAIN; menuIndex = 4; break;
        }
    }
    else if (menuPage == PAGE_ROUTES)
    {
        // Items 0..N-1 are saved routes (APPLY = make active); the last item is Back.
        if (menuIndex < routeNames.Count)
        {
            if (operating) statusMsg = "STOP before switching routes";
            else SwitchActiveRoute(routeNames[menuIndex]);
        }
        else { menuPage = PAGE_RECORD; menuIndex = 3; }
    }
    else if (menuPage == PAGE_SETTINGS)
    {
        switch (menuIndex)
        {
            case 0: BeginEdit(cruiseSpeed); break;
            case 1: BeginEdit(dockSpeed); break;
            case 2: BeginEdit(maxMassKg / 1000.0); break;   // edit in tonnes
            case 3: BeginEdit(departFill); break;
            case 4: menuPage = PAGE_DEPART; menuIndex = 0; break;
            case 5: menuPage = PAGE_MAIN; menuIndex = 5; break;
        }
    }
    else if (menuPage == PAGE_DEPART)
    {
        switch (menuIndex)
        {
            case 0: CycleTrigger(true); break;
            case 1: CycleTrigger(false); break;
            case 2: BeginEdit(dwellSec); break;
            case 3: BeginEdit(minHydrogenPct); break;
            case 4: BeginEdit(minBatteryPct); break;
            case 5: BeginEdit(fuelMarginPct); break;
            case 6: useTower = !useTower; SaveCfg("useTower", useTower ? "auto" : "off"); statusMsg = "Tower = " + (useTower ? "Auto" : "Off"); break;
            case 7: menuPage = PAGE_SETTINGS; menuIndex = 4; break;
        }
    }
}

void MenuBack()
{
    if (editing) { editing = false; statusMsg = "Edit cancelled"; return; }
    if (menuPage == PAGE_DEPART) { menuPage = PAGE_SETTINGS; menuIndex = 4; }
    else if (menuPage == PAGE_ROUTES) { menuPage = PAGE_RECORD; menuIndex = 3; }
    else if (menuPage != PAGE_MAIN) { menuPage = PAGE_MAIN; menuIndex = 0; }
}

void CycleMode()
{
    runMode = runMode == RunMode.Continuous ? RunMode.OneTrip
            : runMode == RunMode.OneTrip ? RunMode.OneWay
            : RunMode.Continuous;
    string s = runMode == RunMode.OneTrip ? "ONETRIP"
             : runMode == RunMode.OneWay ? "ONEWAY" : "CONTINUOUS";
    SaveCfg("runMode", s);
    statusMsg = "Mode = " + runMode;
}

// Cycle a per-connector departure trigger (APPLY on the Depart page), persisting it.
void CycleTrigger(bool home)
{
    DepartTrigger t = home ? homeTrigger : destTrigger;
    t = t == DepartTrigger.Auto ? DepartTrigger.Cargo
      : t == DepartTrigger.Cargo ? DepartTrigger.Timer
      : t == DepartTrigger.Timer ? DepartTrigger.Manual
      : DepartTrigger.Auto;
    if (home) homeTrigger = t; else destTrigger = t;
    SaveCfg(home ? "homeTrigger" : "destTrigger", t.ToString());
    statusMsg = (home ? "Home" : "Dest") + " trigger = " + t;
}

void BeginEdit(double v) { editing = true; editValue = v; }

double EditStep()
{
    if (menuPage == PAGE_SETTINGS)
        switch (menuIndex)
        {
            case 0: return 5;      // cruise m/s
            case 1: return 0.5;    // dock m/s
            case 2: return 1;      // max mass tonnes
            case 3: return 5;      // depart fill %
        }
    if (menuPage == PAGE_DEPART) return 5;   // dwell s / min H2 % / min batt % / margin %
    return 1;
}

void AdjustEdit(int dir) { editValue = Math.Round(editValue + dir * EditStep(), 2); }

void CommitEdit()
{
    if (menuPage == PAGE_SETTINGS)
        switch (menuIndex)
        {
            case 0: cruiseSpeed = (float)Clamp(editValue, 5, 1000); SaveCfg("cruiseSpeed", cruiseSpeed); break;
            case 1: dockSpeed   = (float)Clamp(editValue, 0.5, 20); SaveCfg("dockSpeed", dockSpeed); break;
            case 2: maxMassKg   = Clamp(editValue, 0, 100000) * 1000.0; SaveCfg("maxMassKg", maxMassKg); break;
            case 3: departFill  = Clamp(editValue, 0, 100); SaveCfg("departFill", departFill); break;
        }
    else if (menuPage == PAGE_DEPART)
        switch (menuIndex)
        {
            case 2: dwellSec       = Clamp(editValue, 0, 3600); SaveCfg("dwellSec", dwellSec); break;
            case 3: minHydrogenPct = Clamp(editValue, 0, 100); SaveCfg("minHydrogenPct", minHydrogenPct); break;
            case 4: minBatteryPct  = Clamp(editValue, 0, 100); SaveCfg("minBatteryPct", minBatteryPct); break;
            case 5: fuelMarginPct  = Clamp(editValue, 0, 200); SaveCfg("fuelMarginPct", fuelMarginPct); break;
        }
    statusMsg = "Saved";
}

double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

void SaveCfg(string key, object val)
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("sf", key, val.ToString());
    Me.CustomData = ini.ToString();
}

// Builds the labels for the current page; substitutes the working value while editing.
List<string> MenuLabels()
{
    var l = new List<string>();
    if (menuPage == PAGE_MAIN)
    {
        l.Add(operating ? "Stop" : "Start");
        l.Add("Mode: " + runMode);
        l.Add("Depart Now");
        l.Add("Go Home");
        l.Add("Record >>");
        l.Add("Settings >>");
    }
    else if (menuPage == PAGE_RECORD)
    {
        l.Add("Record Home");
        l.Add("Record Dest");
        l.Add("Clear Route");
        l.Add("Routes >>");
        l.Add("<< Back");
    }
    else if (menuPage == PAGE_ROUTES)
    {
        // Saved routes, active marked with '*'; type RECORD HOME <name> to add one.
        for (int i = 0; i < routeNames.Count; i++)
            l.Add((routeNames[i] == activeRoute ? "* " : "  ") + routeNames[i]);
        l.Add("<< Back");
    }
    else if (menuPage == PAGE_SETTINGS)
    {
        l.Add("Cruise: " + FmtSetting(0, cruiseSpeed) + " m/s");
        l.Add("Dock: " + FmtSetting(1, dockSpeed) + " m/s");
        l.Add("MaxMass: " + FmtSetting(2, maxMassKg / 1000.0) + "t" + (maxMassKg <= 0 ? " off" : ""));
        l.Add("Fill: " + FmtSetting(3, departFill) + " %");
        l.Add("Depart >>");
        l.Add("<< Back");
    }
    else if (menuPage == PAGE_DEPART)
    {
        l.Add("Home trig: " + homeTrigger);
        l.Add("Dest trig: " + destTrigger);
        l.Add("Dwell: " + FmtSetting(2, dwellSec) + " s");
        l.Add("Min H2: " + FmtSetting(3, minHydrogenPct) + " %");
        l.Add("Min Bat: " + FmtSetting(4, minBatteryPct) + " %");
        l.Add("Margin: " + FmtSetting(5, fuelMarginPct) + " %");
        l.Add("Tower: " + (useTower ? "Auto" : "Off"));
        l.Add("<< Back");
    }
    return l;
}

string FmtSetting(int idx, double current)
{
    bool active = editing && menuIndex == idx;
    double v = active ? editValue : current;
    string s = v.ToString("0.##");
    return active ? "[" + s + "]" : s;
}

string PageName()
{
    return menuPage == PAGE_RECORD ? "RECORD"
         : menuPage == PAGE_SETTINGS ? "SETTINGS"
         : menuPage == PAGE_DEPART ? "DEPART"
         : menuPage == PAGE_ROUTES ? "ROUTES" : "MAIN";
}

// ============================================================================
//  Displays (ship) - status header + interactive menu
// ============================================================================
void RenderShip()
{
    // Build each distinct view's text once, then write it to every screen showing
    // that view (sized to each screen on its own). Most rigs use only a couple of
    // views, so this caches the work instead of rebuilding per panel.
    var cache = new Dictionary<string, string>();
    foreach (var t in shipScreens)
    {
        string text;
        if (!cache.TryGetValue(t.View, out text))
        {
            text = WrapText(BuildView(t.View), WRAP_COLS);
            cache[t.View] = text;
        }
        SizeAndWrite(t, text);
    }
    // The PB terminal always echoes the full view for troubleshooting.
    Echo(WrapText(BuildView(VIEW_FULL), WRAP_COLS));
}

// Assemble the raw (pre-wrap) text for a named view. Views compose from the shared
// header/menu builders so they stay in lockstep with the live state.
string BuildView(string view)
{
    switch (view)
    {
        case VIEW_MENU:   return BuildMenu();
        case VIEW_STATUS: return BuildStatus();
        case VIEW_TRIP:   return BuildTrip();
        case VIEW_TELEM:  return BuildTelem();
        default:          return BuildHeader() + BuildMenu();   // full
    }
}

// Compact one-line header: ship + short state + run flag.
string BuildHeaderLine()
{
    return shipName + " " + ShipState() + (operating ? " [RUN]" : " [STOP]");
}

// The full multi-line status header (name/state, cargo, route, ETA, status line).
string BuildHeader()
{
    var sb = new StringBuilder();
    sb.Append(BuildHeaderLine()).Append('\n');
    sb.Append("Cargo ").Append(CargoFillPct().ToString("0")).Append("% ")
      .Append((ShipMassKg() / 1000.0).ToString("0")).Append("t ")
      .Append((rc != null ? rc.GetShipSpeed() : 0).ToString("0")).Append("m/s\n");
    sb.Append(haveRoute
        ? ("Route " + (activeRoute != "" ? activeRoute + " " : "") + path.Count + "wp")
        : "Route: none").Append('\n');
    if (InCruiseFamily())
        sb.Append("ETA ").Append(FormatEta()).Append(' ')
          .Append((RemainingDistance() / 1000.0).ToString("0.0")).Append("km\n");
    sb.Append(statusMsg).Append('\n');
    return sb.ToString();
}

// The interactive menu block (page title, cursored labels, control footer).
string BuildMenu()
{
    var sb = new StringBuilder();
    sb.Append("-- ").Append(PageName()).Append(" --\n");
    var labels = MenuLabels();
    for (int i = 0; i < labels.Count; i++)
        sb.Append(i == menuIndex ? "> " : "  ").Append(labels[i]).Append('\n');
    sb.Append(editing ? "UP/DN +/-  APPLY save" : "UP/DN  APPLY  BACK");
    return sb.ToString();
}

// Compact at-a-glance status: state + run flag, then cargo / mass / speed.
string BuildStatus()
{
    var sb = new StringBuilder();
    sb.Append("-- Status --\n");
    sb.Append(ShipState()).Append(operating ? " [RUN]" : " [STOP]").Append('\n');
    sb.Append('\n');
    sb.Append("-- Cargo --\n");
    sb.Append(CargoFillPct().ToString("0")).Append("%  ")
      .Append((ShipMassKg() / 1000.0).ToString("0")).Append("t  ")
      .Append((rc != null ? rc.GetShipSpeed() : 0).ToString("0")).Append("m/s");
    return sb.ToString();
}

// Trip view: route, current phase, ETA/distance while cruising, and the live
// status line (delivered / holding / fuel-hold messages surface here).
string BuildTrip()
{
    var sb = new StringBuilder();
    sb.Append("-- Trip --\n");
    sb.Append(haveRoute
        ? ("Route " + (activeRoute != "" ? activeRoute + " " : "") + path.Count + "wp")
        : "Route: none").Append('\n');
    sb.Append("Phase: ").Append(ShipState()).Append('\n');
    if (InCruiseFamily())
        sb.Append("ETA ").Append(FormatEta()).Append("  ")
          .Append((RemainingDistance() / 1000.0).ToString("0.0")).Append("km\n");
    sb.Append(statusMsg);
    return sb.ToString();
}

// Debug telemetry view: the flight-law signals that explain a bad climb, cruise, or
// descent - phase timer, speed vs the governor cap, vertical rate, altitude, the
// gravity magnitude that drives the atmo<->space handoff, waypoint progress, attitude
// error, and fuel. It is opt-in by assignment: point a dedicated surface at it (a panel
// named [SF:telem], or a cockpit's [sf-screens] "0 = telem") and it never
// touches the main info screen.
string BuildTelem()
{
    var sb = new StringBuilder();
    sb.Append("-- Telemetry --\n");
    if (rc == null) { sb.Append("no remote control"); return sb.ToString(); }

    Vector3D vel = rc.GetShipVelocities().LinearVelocity;
    Vector3D grav = rc.GetNaturalGravity();
    double gMag = grav.Length();
    double spd = rc.GetShipSpeed();

    // Phase + time-in-phase (with leg direction) and run flag.
    sb.Append(ShipState()).Append(operating ? " [RUN]" : " [STOP]")
      .Append("  t=").Append(phaseTimer.ToString("0.0")).Append("s\n");

    // Speed vs the governor's cap at the current waypoint (cap shown only while cruising).
    // The cap is the lower of the waypoint profile and the active Cruise/Climb/Descent governor.
    sb.Append("Spd ").Append(spd.ToString("0.0")).Append("m/s");
    if (cruiseArmed && cruiseIdx < legVmax.Count)
        sb.Append(" /").Append(Math.Min(CruiseCap(), legVmax[cruiseIdx]).ToString("0")).Append("cap");
    sb.Append('\n');

    // Vertical rate along gravity-up (climb +, descent -); blank in space.
    if (gMag > 1e-3)
    {
        Vector3D up = -grav / gMag;
        double vrate = vel.Dot(up);
        sb.Append("VS  ").Append(vrate >= 0 ? "+" : "").Append(vrate.ToString("0.0")).Append("m/s\n");
    }
    else sb.Append("VS  (space)\n");

    // Gravity magnitude - the atmo<->space boundary the flight law pivots on.
    sb.Append("Grav ").Append(gMag.ToString("0.00")).Append("m/s2 ")
      .Append((gMag / 9.81).ToString("0.00")).Append("g\n");

    // Surface altitude where a planet is beneath us.
    double surf;
    if (rc.TryGetPlanetElevation(MyPlanetElevation.Surface, out surf))
        sb.Append("Alt ").Append(surf.ToString("0")).Append("m\n");
    else
        sb.Append("Alt --\n");

    // Waypoint progress + straight-line remaining distance.
    if (cruiseArmed && legWps.Count > 0)
        sb.Append("WP ").Append(cruiseIdx + 1).Append('/').Append(legWps.Count)
          .Append("  ").Append((RemainingDistance() / 1000.0).ToString("0.00")).Append("km\n");
    else
        sb.Append("WP --\n");

    // Attitude error (approx deg) - a value stuck high flags an align stall.
    sb.Append("Att ").Append((lastAlignErr * 57.2958).ToString("0.0")).Append("deg\n");

    // Fuel reserves (n/a when the ship carries none of that resource).
    double h2 = HydrogenPct(), batt = BatteryPct();
    sb.Append("H2 ").Append(h2 < 0 ? "n/a" : h2.ToString("0") + "%")
      .Append("  Bat ").Append(batt < 0 ? "n/a" : batt.ToString("0") + "%");
    return sb.ToString();
}

// Short, single-word state label for the compact header. The direction-free phase
// label comes from the phase object; the directional flight phases append the leg
// arrow (> outbound to dest, < inbound home) exactly as the old per-state labels did.
string ShipState()
{
    string lbl = phases[phase].Label;
    if (phase == PhaseId.DepartStaging || phase == PhaseId.Cruise || phase == PhaseId.Climb
        || phase == PhaseId.Descent || phase == PhaseId.Holding
        || phase == PhaseId.Taxi || phase == PhaseId.Approach)
        return lbl + (leg.Outbound ? " >" : " <");
    return lbl;
}

// Write one screen's text, sized to THAT surface only (no cross-screen coupling):
// applies the screen's padding, then a fixed font size if it pinned one, otherwise
// the largest font that fits this surface's own (already-wrapped) content within the
// padded area, clamped to a sane range.
void SizeAndWrite(ScreenTarget t, string text)
{
    var s = t.Surface;
    if (s == null) return;
    float pad = (float)Clamp(t.Pad, 0, 40);   // % per side; clamp leaves usable area
    s.TextPadding = pad;
    if (t.FixedSize > 0)
    {
        s.FontSize = t.FixedSize;
    }
    else
    {
        var m = s.MeasureStringInPixels(new StringBuilder(text), s.Font, 1f);
        if (m.X >= 1 && m.Y >= 1)
        {
            float padScale = Math.Max(0.1f, 1f - 2f * pad / 100f);   // padding insets both sides
            Vector2 area = s.SurfaceSize * padScale;
            float fit = Math.Min(area.X / m.X, area.Y / m.Y) * 0.95f;
            s.FontSize = (float)Clamp(fit, 0.4, 3.0);
        }
    }
    s.WriteText(text);
}

// Word-wrap to a fixed column count. Monospace => columns == characters, so this
// guarantees no single (possibly long) status line dictates the shared font.
string WrapText(string text, int cols)
{
    var outSb = new StringBuilder();
    foreach (var line in text.Split('\n'))
    {
        if (line.Length <= cols) { outSb.Append(line).Append('\n'); continue; }
        int col = 0;
        foreach (var raw in line.Split(' '))
        {
            string w = raw;
            // Hard-break a single word longer than the budget (e.g. a long name).
            while (w.Length > cols)
            {
                if (col > 0) { outSb.Append('\n'); col = 0; }
                outSb.Append(w.Substring(0, cols)).Append('\n');
                w = w.Substring(cols);
            }
            if (col == 0) { outSb.Append(w); col = w.Length; }
            else if (col + 1 + w.Length <= cols) { outSb.Append(' ').Append(w); col += 1 + w.Length; }
            else { outSb.Append('\n').Append(w); col = w.Length; }
        }
        outSb.Append('\n');
    }
    if (outSb.Length > 0 && outSb[outSb.Length - 1] == '\n') outSb.Length--;
    return outSb.ToString();
}

// ============================================================================
//  Broadcast (ship -> base)
// ============================================================================
void Broadcast()
{
    // Pipe-delimited: name|state|etaSec|distM|fill|massT|running
    double distM = 0; int etaSec = -1;
    if (InCruiseFamily())
    {
        distM = RemainingDistance();
        double spd = rc.GetShipSpeed();
        if (spd >= 1) etaSec = (int)(distM / spd);
    }
    string msg = string.Join("|", new[]
    {
        shipName,
        LegacyStateName(),   // pre-0.2.0 State name, so a Skippy-Shuttle base board decodes it unchanged
        etaSec.ToString(),
        ((int)distM).ToString(),
        CargoFillPct().ToString("0"),
        (ShipMassKg() / 1000.0).ToString("0.0"),
        operating ? "1" : "0"
    });
    IGC.SendBroadcastMessage(channel, msg);
}

// ============================================================================
//  Base role - listen & render
// ============================================================================
class ShuttleReport
{
    public string Name, State;
    public int EtaSec, DistM, Fill;
    public double MassT;
    public bool Running;
    public double Age;   // seconds since last update
}

void RunBase()
{
    if (listener == null) listener = IGC.RegisterBroadcastListener(channel);

    while (listener.HasPendingMessage)
    {
        var m = listener.AcceptMessage();
        var s = m.Data as string;
        if (s == null) continue;
        var f = s.Split('|');
        if (f.Length < 7) continue;
        var r = new ShuttleReport
        {
            Name = f[0],
            State = f[1],
            EtaSec = ParseInt(f[2], -1),
            DistM = ParseInt(f[3], 0),
            Fill = ParseInt(f[4], 0),
            MassT = ParseDouble(f[5], 0),
            Running = f[6] == "1",
            Age = 0
        };
        fleet[r.Name] = r;
    }

    foreach (var r in fleet.Values) r.Age += dt;

    var sb = new StringBuilder();
    sb.Append("== Shuttle Board v").Append(VERSION).Append(" ==\n\n");
    if (fleet.Count == 0) sb.Append("Waiting for shuttle signal...\n");
    foreach (var r in fleet.Values)
    {
        if (r.Age > 20) { sb.Append(r.Name).Append(": NO SIGNAL (").Append((int)r.Age).Append("s)\n\n"); continue; }
        sb.Append(r.Name).Append(": ").Append(PrettyState(r.State)).Append('\n');
        if (r.EtaSec >= 0)
            sb.Append("   ETA ").Append((r.EtaSec / 60).ToString("00")).Append(':').Append((r.EtaSec % 60).ToString("00"))
              .Append("   ").Append((r.DistM / 1000.0).ToString("0.0")).Append(" km\n");
        sb.Append("   Cargo ").Append(r.Fill).Append("%   ").Append(r.MassT.ToString("0.0")).Append("t\n\n");
    }

    var text = sb.ToString();
    Echo(text);
    var panels = new List<IMyTextPanel>();
    GridTerminalSystem.GetBlocksOfType(panels, b => b.CustomName.Contains(lcdTag));
    foreach (var p in panels) { p.ContentType = ContentType.TEXT_AND_IMAGE; p.WriteText(text); }
    Me.GetSurface(0).ContentType = ContentType.TEXT_AND_IMAGE;
    Me.GetSurface(0).WriteText(text);
}

string PrettyState(string s)
{
    switch (s)
    {
        case "Loading":      return "Loading at home";
        case "CruiseToDest": return "En route to station";
        case "ApproachDest": return "Docking at station";
        case "Unloading":    return "Unloading at station";
        case "CruiseToHome": return "Returning home";
        case "ApproachHome": return "Docking at home";
        case "Idle":         return "Idle";
        case "Faulted":      return "FAULT - needs attention";
        default:             return s;
    }
}

// ============================================================================
//  Persistence & config
// ============================================================================
void WriteConfigTemplate()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);   // keep any existing [route]/[state] if present
    WriteShuttleSection(ini);
    Me.CustomData = ini.ToString();
}

// Ensure every known [sf] key exists in Custom Data, seeding any that a
// newer script version added with the value currently in effect (the loaded
// value, or the default if the key was absent). Runs on compile so upgrading the
// script surfaces its new tuning keys WITHOUT wiping the recorded [route]/[state].
void BackfillConfig()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    WriteShuttleSection(ini);
    Me.CustomData = ini.ToString();
}

// A base/station board only ever reads role, shipName, channel and lcdTag - the
// flight/cargo/fuel keys are meaningless to it. Rewrite the [sf] section with
// just those four so a board's Custom Data isn't cluttered with irrelevant tuning
// (e.g. a block first compiled as a shuttle, then switched to base). Other sections
// are left untouched. Runs on compile for the base role only.
void TrimBaseConfig()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.DeleteSection("sf");
    WriteBaseSection(ini);
    Me.CustomData = ini.ToString();
}

// Write the minimal base-role key set. Normalizes the role to "base" (so a
// "station" alias is rewritten cleanly).
void WriteBaseSection(MyIni ini)
{
    ini.Set("sf", "role", "base");
    ini.Set("sf", "shipName", shipName);
    ini.Set("sf", "channel", channel);
    ini.Set("sf", "lcdTag", lcdTag);
}

// Write the full [sf] key set from the current field values into an ini,
// leaving all other sections untouched. Shared by the first-run template and the
// on-compile backfill.
void WriteShuttleSection(MyIni ini)
{
    string modeStr = runMode == RunMode.OneTrip ? "ONETRIP"
                   : runMode == RunMode.OneWay ? "ONEWAY" : "CONTINUOUS";
    ini.Set("sf", "role", role == Role.Base ? "base" : "shuttle");
    ini.Set("sf", "shipName", shipName);
    ini.Set("sf", "channel", channel);
    ini.Set("sf", "useTower", useTower ? "auto" : "off");
    ini.Set("sf", "runMode", modeStr);
    ini.Set("sf", "homeTrigger", homeTrigger.ToString());
    ini.Set("sf", "destTrigger", destTrigger.ToString());
    ini.Set("sf", "remoteName", remoteName);
    ini.Set("sf", "loadTag", loadTag);
    ini.Set("sf", "unloadTag", unloadTag);
    ini.Set("sf", "lcdTag", lcdTag);
    ini.Set("sf", "cruiseSpeed", cruiseSpeed);
    ini.Set("sf", "climbSpeed", climbSpeed);
    ini.Set("sf", "descentSpeed", descentSpeed);
    ini.Set("sf", "dockSpeed", dockSpeed);
    ini.Set("sf", "maxMassKg", maxMassKg);
    ini.Set("sf", "departFill", departFill);
    ini.Set("sf", "unloadDrainSec", unloadDrainSec);
    ini.Set("sf", "dwellSec", dwellSec);
    ini.Set("sf", "minHydrogenPct", minHydrogenPct);
    ini.Set("sf", "minBatteryPct", minBatteryPct);
    ini.Set("sf", "fuelMarginPct", fuelMarginPct);
    ini.Set("sf", "segMeters", segMeters);
    ini.Set("sf", "turnDegrees", turnDegrees);
    ini.Set("sf", "simplifyMeters", simplifyMeters);
    ini.Set("sf", "approachDist", approachDist);
    ini.Set("sf", "holdDist", holdDist);
    ini.Set("sf", "gyroRpmCap", gyroRpmCap);
    ini.Set("sf", "brakeFrac", brakeFrac);
    ini.Set("sf", "cornerLen", cornerLen);
    ini.Set("sf", "gyroGain", gyroGain);
    ini.Set("sf", "gyroDamp", gyroDamp);
    ini.Set("sf", "cruiseAttitude", cruiseAttitude);
    ini.Set("sf", "dockClearCheck", dockClearCheck);
    ini.Set("sf", "cameraTag", cameraTag);
    ini.Set("sf", "dockBlockSec", dockBlockSec);
}

void LoadConfig()
{
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData)) return;
    MigrateLegacyConfig(ini);       // one-time [shuttle] -> [sf]
    // Role: "base" (or its alias "station") renders the board; anything else flies.
    string roleStr = ini.Get("sf", "role").ToString("shuttle").Trim().ToLowerInvariant();
    role = (roleStr == "base" || roleStr == "station") ? Role.Base : Role.Shuttle;
    shipName = ini.Get("sf", "shipName").ToString(shipName);
    channel = ini.Get("sf", "channel").ToString(channel);
    useTower = ini.Get("sf", "useTower").ToString(useTower ? "auto" : "off").Trim().ToLowerInvariant() == "auto";
    // Run mode. Legacy WAITFULL folds into Continuous + a Cargo home trigger, but
    // only supplies that default if no explicit homeTrigger key is present, so a new
    // config's homeTrigger always wins.
    string modeStr = ini.Get("sf", "runMode").ToString("CONTINUOUS").Trim().ToUpperInvariant();
    string defHome = "Auto";
    if (modeStr == "WAITFULL") { runMode = RunMode.Continuous; defHome = "Cargo"; }
    else SetModeSilent(modeStr);
    homeTrigger = TrigFromString(ini.Get("sf", "homeTrigger").ToString(defHome));
    destTrigger = TrigFromString(ini.Get("sf", "destTrigger").ToString("Auto"));
    remoteName = ini.Get("sf", "remoteName").ToString("");
    // Sorter tags; fall back to the legacy exact-name keys (a full name still
    // matches as a substring tag), else the defaults.
    loadTag = ini.Get("sf", "loadTag").ToString(ini.Get("sf", "loadSorter").ToString(loadTag));
    unloadTag = ini.Get("sf", "unloadTag").ToString(ini.Get("sf", "unloadSorter").ToString(unloadTag));
    lcdTag = ini.Get("sf", "lcdTag").ToString(lcdTag);
    cruiseSpeed = (float)ini.Get("sf", "cruiseSpeed").ToDouble(cruiseSpeed);
    // Climb/Descent governors: clamped to (5, cruiseSpeed] so a lower cap is always braking-safe
    // against the velocity profile (built at the cruiseSpeed ceiling). Default = cruiseSpeed (no-op).
    climbSpeed = (float)Clamp(ini.Get("sf", "climbSpeed").ToDouble(cruiseSpeed), 5, cruiseSpeed);
    descentSpeed = (float)Clamp(ini.Get("sf", "descentSpeed").ToDouble(cruiseSpeed), 5, cruiseSpeed);
    dockSpeed = (float)ini.Get("sf", "dockSpeed").ToDouble(dockSpeed);
    maxMassKg = ini.Get("sf", "maxMassKg").ToDouble(maxMassKg);
    departFill = ini.Get("sf", "departFill").ToDouble(departFill);
    unloadDrainSec = ini.Get("sf", "unloadDrainSec").ToDouble(unloadDrainSec);
    dwellSec = ini.Get("sf", "dwellSec").ToDouble(dwellSec);
    minHydrogenPct = Clamp(ini.Get("sf", "minHydrogenPct").ToDouble(minHydrogenPct), 0, 100);
    minBatteryPct = Clamp(ini.Get("sf", "minBatteryPct").ToDouble(minBatteryPct), 0, 100);
    fuelMarginPct = Math.Max(0, ini.Get("sf", "fuelMarginPct").ToDouble(fuelMarginPct));
    segMeters = ini.Get("sf", "segMeters").ToDouble(segMeters);
    turnDegrees = ini.Get("sf", "turnDegrees").ToDouble(turnDegrees);
    simplifyMeters = ini.Get("sf", "simplifyMeters").ToDouble(simplifyMeters);
    approachDist = ini.Get("sf", "approachDist").ToDouble(approachDist);
    holdDist = Math.Max(approachDist + 5, ini.Get("sf", "holdDist").ToDouble(holdDist));
    gyroRpmCap = (float)ini.Get("sf", "gyroRpmCap").ToDouble(gyroRpmCap);
    brakeFrac = Clamp(ini.Get("sf", "brakeFrac").ToDouble(brakeFrac), 0.1, 1.0);
    cornerLen = Math.Max(1.0, ini.Get("sf", "cornerLen").ToDouble(cornerLen));
    gyroGain = Math.Max(0.1, ini.Get("sf", "gyroGain").ToDouble(gyroGain));
    gyroDamp = Math.Max(0.0, ini.Get("sf", "gyroDamp").ToDouble(gyroDamp));
    // Gravity-leg attitude: only auto/level/nose are meaningful; anything else falls back to auto.
    string attStr = ini.Get("sf", "cruiseAttitude").ToString(cruiseAttitude).Trim().ToLowerInvariant();
    cruiseAttitude = (attStr == "level" || attStr == "nose") ? attStr : "auto";
    dockClearCheck = ini.Get("sf", "dockClearCheck").ToBoolean(dockClearCheck);
    cameraTag = ini.Get("sf", "cameraTag").ToString(cameraTag);
    dockBlockSec = Math.Max(0, ini.Get("sf", "dockBlockSec").ToDouble(dockBlockSec));
}

void SetModeSilent(string m)
{
    switch (m.Trim().ToUpperInvariant())
    {
        case "ONETRIP":  runMode = RunMode.OneTrip; break;
        case "ONEWAY":   runMode = RunMode.OneWay; break;
        default:         runMode = RunMode.Continuous; break;   // incl. legacy WAITFULL (home trigger set by LoadConfig)
    }
}

// Parse a DepartTrigger name (case-insensitive), defaulting to Auto.
DepartTrigger TrigFromString(string s)
{
    switch (s.Trim().ToUpperInvariant())
    {
        case "CARGO":  return DepartTrigger.Cargo;
        case "TIMER":  return DepartTrigger.Timer;
        case "MANUAL": return DepartTrigger.Manual;
        default:       return DepartTrigger.Auto;
    }
}

// Multiple named routes are stored one-per-section as [route.<name>]; the bare
// [route] section is the legacy single-route layout, migrated to [route.Main] on
// first load. [routes] active=<name> points at the route loaded into memory.
string RouteSec(string name) { return "route." + name; }

void SaveRoute()
{
    if (activeRoute == "") activeRoute = "Main";
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    WriteRoute(ini, activeRoute);
    ini.Set("routes", "active", activeRoute);
    Me.CustomData = ini.ToString();
    RefreshRouteNames();
}

// Serialize the in-memory route (poses + path) into [route.<name>].
void WriteRoute(MyIni ini, string name)
{
    string s = RouteSec(name);
    ini.Set(s, "homeConn", homeConn);
    ini.Set(s, "destConn", destConn);
    // Full docked pose (position + orientation + connector axis) at each end.
    ini.Set(s, "homePos", Vec(homePose.Pos));
    ini.Set(s, "homeFwd", Vec(homePose.Fwd));
    ini.Set(s, "homeUp", Vec(homePose.Up));
    ini.Set(s, "homeConnFwd", Vec(homePose.ConnFwd));
    ini.Set(s, "destPos", Vec(destPose.Pos));
    ini.Set(s, "destFwd", Vec(destPose.Fwd));
    ini.Set(s, "destUp", Vec(destPose.Up));
    ini.Set(s, "destConnFwd", Vec(destPose.ConnFwd));
    // Static grid each dock belongs to, for the approach clearance raycast (0 = unknown).
    ini.Set(s, "homeBaseId", homePose.BaseGridId);
    ini.Set(s, "destBaseId", destPose.BaseGridId);
    // Per-dock staging/holding-fix distance override (0 = use the global holdDist).
    ini.Set(s, "homeHoldDist", homePose.HoldDist);
    ini.Set(s, "destHoldDist", destPose.HoldDist);
    // Natural-gravity magnitude at each dock, for leg-scenario classification (0 = space).
    ini.Set(s, "homeG", homePose.Grav);
    ini.Set(s, "destG", destPose.Grav);
    var sb = new StringBuilder();
    for (int i = 0; i < path.Count; i++) { if (i > 0) sb.Append(';'); sb.Append(Vec(path[i])); }
    ini.Set(s, "path", sb.ToString());
}

void LoadRoute()
{
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData)) return;

    MigrateLegacyRoute(ini);        // one-time [route] -> [route.Main]
    RefreshRouteNames();

    // Resolve the active route: the saved pointer, else the first saved route, else none.
    string active = ini.Get("routes", "active").ToString("");
    if (active == "" || !routeNames.Contains(active))
        active = routeNames.Count > 0 ? routeNames[0] : "";
    if (active == "") { haveRoute = false; activeRoute = ""; return; }

    activeRoute = active;
    LoadRouteInto(ini, active);
}

// The [shuttle] config section was renamed to [sf] in 0.6.0. Copy its keys into [sf]
// the first time we see the old layout, then drop it, so an existing ship keeps all its
// tuning (and its persisted tag values) without re-entering anything.
void MigrateLegacyConfig(MyIni ini)
{
    if (!ini.ContainsSection("shuttle") || ini.ContainsSection("sf")) return;
    var keys = new List<MyIniKey>();
    ini.GetKeys("shuttle", keys);
    foreach (var k in keys) ini.Set("sf", k.Name, ini.Get(k).ToString(""));
    ini.DeleteSection("shuttle");
    Me.CustomData = ini.ToString();     // persist the migration
}

// Copy a legacy single [route] section into [route.Main] the first time we see it,
// then drop it. If named routes already exist the legacy section is stale - just remove it.
void MigrateLegacyRoute(MyIni ini)
{
    if (!ini.ContainsSection("route")) return;
    var secs = new List<string>();
    ini.GetSections(secs);
    bool haveNamed = false;
    foreach (var sec in secs) if (sec.StartsWith("route.")) { haveNamed = true; break; }
    if (!haveNamed)
    {
        string[] keys = { "homeConn", "destConn", "homePos", "homeFwd", "homeUp", "homeConnFwd",
                          "destPos", "destFwd", "destUp", "destConnFwd", "homeBaseId", "destBaseId",
                          "homeHoldDist", "destHoldDist", "homeG", "destG", "path", "homeDock", "destDock" };
        string dst = RouteSec("Main");
        foreach (var key in keys)
        {
            var v = ini.Get("route", key);
            if (!v.IsEmpty) ini.Set(dst, key, v.ToString(""));
        }
        if (!ini.ContainsKey("routes", "active")) ini.Set("routes", "active", "Main");
    }
    ini.DeleteSection("route");
    Me.CustomData = ini.ToString();     // persist the migration
}

// Read a named route's section into the in-memory pose/path fields.
void LoadRouteInto(MyIni ini, string name)
{
    string s = RouteSec(name);
    if (!ini.ContainsSection(s)) { haveRoute = false; return; }
    homeConn = ini.Get(s, "homeConn").ToString("");
    destConn = ini.Get(s, "destConn").ToString("");

    // Position: prefer new keys, fall back to the legacy homeDock/destDock keys.
    bool haveHP = LoadPos(ini, s, "homePos", "homeDock", out homePose.Pos);
    bool haveDP = LoadPos(ini, s, "destPos", "destDock", out destPose.Pos);

    // Orientation: present in v0.3.0+ routes. Older routes get poses synthesised
    // from the flight-path geometry (nose-first, which is all they supported).
    bool haveOri = TryVec(ini.Get(s, "homeFwd").ToString(""), out homePose.Fwd)
                 & TryVec(ini.Get(s, "homeUp").ToString(""), out homePose.Up)
                 & TryVec(ini.Get(s, "homeConnFwd").ToString(""), out homePose.ConnFwd)
                 & TryVec(ini.Get(s, "destFwd").ToString(""), out destPose.Fwd)
                 & TryVec(ini.Get(s, "destUp").ToString(""), out destPose.Up)
                 & TryVec(ini.Get(s, "destConnFwd").ToString(""), out destPose.ConnFwd);

    // Base grid ids for the clearance raycast; absent (0) on pre-0.15 routes -> distance fallback.
    homePose.BaseGridId = ini.Get(s, "homeBaseId").ToInt64(0);
    destPose.BaseGridId = ini.Get(s, "destBaseId").ToInt64(0);

    // Per-dock staging/holding-fix override; absent (0) on pre-0.5 routes -> global holdDist.
    homePose.HoldDist = ini.Get(s, "homeHoldDist").ToDouble(0);
    destPose.HoldDist = ini.Get(s, "destHoldDist").ToDouble(0);

    // Natural-gravity magnitude at each dock; absent (0) on pre-0.7 routes -> classified as
    // space, so an un-re-recorded route stays SpaceLocal (today's single-Cruise behavior).
    homePose.Grav = ini.Get(s, "homeG").ToDouble(0);
    destPose.Grav = ini.Get(s, "destG").ToDouble(0);

    path.Clear();
    var raw = ini.Get(s, "path").ToString("");
    if (!string.IsNullOrEmpty(raw))
        foreach (var token in raw.Split(';'))
        {
            Vector3D v;
            if (TryVec(token, out v)) path.Add(v);
        }

    if (!haveOri) SynthesizePoses();
    haveRoute = haveHP && haveDP && path.Count > 0 && homeConn != "" && destConn != "";
}

// Rebuild the saved-route-name cache by scanning [route.*] sections.
void RefreshRouteNames()
{
    routeNames.Clear();
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData)) return;
    var secs = new List<string>();
    ini.GetSections(secs);
    foreach (var sec in secs)
        if (sec.StartsWith("route.")) routeNames.Add(sec.Substring(6));
    routeNames.Sort();
}

// Switch the active route to a saved one (user-initiated; validates, never destroys on a typo).
void SwitchActiveRoute(string name)
{
    name = SanitizeName(name);
    var ini = new MyIni(); ini.TryParse(Me.CustomData);
    if (name == "" || !ini.ContainsSection(RouteSec(name))) { statusMsg = "No route '" + name + "'"; return; }
    activeRoute = name;
    LoadRouteInto(ini, name);
    ini.Set("routes", "active", name);
    Me.CustomData = ini.ToString();
    statusMsg = "Active route: " + name + " (" + path.Count + "wp)";
}

// Delete a saved route by name; if it was active, fall back to another (or none).
void DeleteRoute(string name)
{
    name = SanitizeName(name);
    if (name == "") { statusMsg = "Usage: DELROUTE <name>"; return; }
    var ini = new MyIni(); ini.TryParse(Me.CustomData);
    if (!ini.ContainsSection(RouteSec(name))) { statusMsg = "No route '" + name + "'"; return; }
    ini.DeleteSection(RouteSec(name));
    Me.CustomData = ini.ToString();
    bool wasActive = activeRoute == name;
    if (wasActive) ActivateFallback(); else RefreshRouteNames();
    statusMsg = "Deleted route '" + name + "'";
}

// After the active route is removed, load the first remaining route, or clear to none.
void ActivateFallback()
{
    RefreshRouteNames();
    var ini = new MyIni(); ini.TryParse(Me.CustomData);
    if (routeNames.Count > 0)
    {
        activeRoute = routeNames[0];
        LoadRouteInto(ini, activeRoute);
        ini.Set("routes", "active", activeRoute);
    }
    else
    {
        activeRoute = "";
        haveRoute = false; path.Clear(); homeConn = ""; destConn = "";
        homePose = new DockPose(); destPose = new DockPose();
        ini.Set("routes", "active", "");
    }
    Me.CustomData = ini.ToString();
}

// Keep only [A-Za-z0-9_-], cap length - safe as a MyIni section suffix and menu label.
string SanitizeName(string raw)
{
    if (string.IsNullOrEmpty(raw)) return "";
    var sb = new StringBuilder();
    foreach (char c in raw.Trim())
    {
        // Explicit ASCII test (no char.* statics) - safe as a MyIni section suffix.
        bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')
                  || (c >= 'a' && c <= 'z') || c == '_' || c == '-';
        if (ok) sb.Append(c);
        if (sb.Length >= 16) break;
    }
    return sb.ToString();
}

// Read a position, preferring the primary key and falling back to a legacy one.
bool LoadPos(MyIni ini, string section, string primary, string legacy, out Vector3D pos)
{
    if (TryVec(ini.Get(section, primary).ToString(""), out pos)) return true;
    return TryVec(ini.Get(section, legacy).ToString(""), out pos);
}

// Derive orientation for a legacy route (position-only) from its path geometry.
// Assumes the ship left home and arrived at the destination nose-first.
void SynthesizePoses()
{
    if (path.Count >= 2)
    {
        Vector3D outDir = Vector3D.Normalize(path[1] - path[0]);                          // departing home
        Vector3D inDir  = Vector3D.Normalize(path[path.Count - 1] - path[path.Count - 2]); // arriving dest
        homePose.Fwd = outDir; homePose.ConnFwd = -outDir;   // stand-off is out along departure dir
        destPose.Fwd = inDir;  destPose.ConnFwd = inDir;     // stand-off is behind along arrival dir
    }
    else
    {
        homePose.Fwd = rc != null ? rc.WorldMatrix.Forward : Vector3D.Forward;
        destPose.Fwd = homePose.Fwd;
        homePose.ConnFwd = homePose.Fwd;
        destPose.ConnFwd = destPose.Fwd;
    }
    homePose.Up = UpAt(homePose.Pos);
    destPose.Up = UpAt(destPose.Pos);
}

// Up = away from gravity where we are now (best available for a legacy route);
// falls back to the ship's current up in zero-g.
Vector3D UpAt(Vector3D pos)
{
    Vector3D g = rc != null ? rc.GetNaturalGravity() : Vector3D.Zero;
    return g.LengthSquared() > 1e-3 ? Vector3D.Normalize(-g)
         : rc != null ? rc.WorldMatrix.Up : Vector3D.Up;
}

void ClearRoute()
{
    // Remove the active route's saved section, then fall back to another saved route (or none).
    if (activeRoute != "")
    {
        var ini = new MyIni(); ini.TryParse(Me.CustomData);
        ini.DeleteSection(RouteSec(activeRoute));
        Me.CustomData = ini.ToString();
    }
    ActivateFallback();
}

void LoadState()
{
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData) || !ini.ContainsSection("state")) return;
    // Prefer the 0.2.0+ (phase, outbound) pair; fall back to a pre-0.2.0 `state`
    // name so a ship whose Custom Data was written by the old script (or by
    // Skippy-Shuttle, pasted over) resumes on the correct phase and direction.
    if (ini.ContainsKey("state", "phase"))
    {
        PhaseId p;
        if (!Enum.TryParse(ini.Get("state", "phase").ToString("Idle"), out p)) p = PhaseId.Idle;
        phase = p;
        leg.Outbound = ini.Get("state", "outbound").ToBoolean(true);
    }
    else
    {
        ApplyLegacyState(ini.Get("state", "state").ToString("Idle"));
    }
    operating = ini.Get("state", "operating").ToBoolean(false);
    phaseTimer = ini.Get("state", "phaseTimer").ToDouble(0);
    estHydroOut = ini.Get("state", "estHydroOut").ToDouble(0);
    estBattOut = ini.Get("state", "estBattOut").ToDouble(0);
    estHydroHome = ini.Get("state", "estHydroHome").ToDouble(0);
    estBattHome = ini.Get("state", "estBattHome").ToDouble(0);
    cruiseArmed = false;  // always re-arm autopilot after a recompile
}

// Persist just the learned per-leg fuel/charge estimates (called on arrival), so a
// recompile doesn't forget what the run costs.
void SaveEstimates()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("state", "estHydroOut", estHydroOut);
    ini.Set("state", "estBattOut", estBattOut);
    ini.Set("state", "estHydroHome", estHydroHome);
    ini.Set("state", "estBattHome", estBattHome);
    Me.CustomData = ini.ToString();
}

// ---- Vector <-> string -----------------------------------------------------
string Vec(Vector3D v)
{
    return v.X.ToString("R") + ":" + v.Y.ToString("R") + ":" + v.Z.ToString("R");
}
bool TryVec(string s, out Vector3D v)
{
    v = Vector3D.Zero;
    if (string.IsNullOrEmpty(s)) return false;
    var p = s.Split(':');
    if (p.Length != 3) return false;
    double x, y, z;
    if (!double.TryParse(p[0], out x) || !double.TryParse(p[1], out y) || !double.TryParse(p[2], out z)) return false;
    v = new Vector3D(x, y, z);
    return true;
}

int ParseInt(string s, int def) { int r; return int.TryParse(s, out r) ? r : def; }
double ParseDouble(string s, double def) { double r; return double.TryParse(s, out r) ? r : def; }
