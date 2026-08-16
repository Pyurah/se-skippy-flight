using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript
{
    public partial class Program : MyGridProgram
    {
/*//////////////////////////////////////////////////////////////////////////////
 * SkippyTower - Active traffic-control tower for the SkippyFlight shuttle fleet.
 * A separate Programmable Block script (its OWN char budget) that a station PB runs
 * to serialize arrivals and departures: only one craft maneuvers at the station at a
 * time, so two shuttles never both undock into, or both taxi onto, the same corridor.
 *
 * It is a SUPERSET of the SkippyFlight base/board role - it renders the same status
 * board AND, in control mode, actively clears traffic. A third mode, teach, turns the
 * SAME script into a ship-side setup helper that records the holding zone / pads / interior
 * paths and streams them here (run it on a 2nd PB on the ship). Full docs in README.md.
 *
 * HOW IT PLUGS IN (no shuttle re-config beyond useTower=auto):
 *   - Ships already broadcast a status report and, since v0.9.0, ask a tower for
 *     clearance at their two commit points (undock, and taxi-onto-connector), holding
 *     until granted. See SkippyFlight.cs "Tower clearance" (useTower).
 *   - This tower announces itself with a periodic CMD|TOWER heartbeat. A ship that
 *     hears it switches from independent to controlled; if the heartbeat stops, the
 *     ship reverts to independent after TOWER_TIMEOUT (anti-stranding) - so a
 *     destroyed/unpowered tower never strands the fleet.
 *
 * WIRE PROTOCOL (all command messages CMD|-tagged; the 7-field status report is
 * consumed unchanged, so the tower knows every ship's phase/dock for free):
 *   receive  name|state|eta|dist|fill|mass|running   (status, unchanged)
 *   receive  CMD|REQ|<ship>|<DEPART|LAND>|<dock>|<grid>  (ship asks; grid = target dock's grid id, 0/absent = legacy)
 *   receive  CMD|PAD|<name>|<pos>|<fwd>|<up>|<connFwd>  (register a pad; vectors X:Y:Z)
 *   receive  CMD|ZONE|<center>|<fwd>|<up>|<ext>       (define the holding-zone box; vectors X:Y:Z)
 *   receive  CMD|PADPATH|<pad>|<seq>|<total>|<v>;<v>… (a chunk of a pad's interior path)
 *   emit     CMD|TOWER|<zone>|<gridId>[|<center>|<fwd>|<up>|<ext>]  (heartbeat; grid id scopes clearance, zone geometry appended when defined)
 *   emit     CMD|CLEAR|<ship>|DEPART                  (grant a departure - no pad payload)
 *   emit     CMD|CLEAR|<ship>|LAND|<pad>|<pos>|<fwd>|<up>|<connFwd>  (grant + assigned pad pose)
 *   emit     CMD|PATH|<ship>|<seq>|<total>|<v>;<v>…   (stream the granted pad's interior path, chunked)
 *   emit     CMD|HOLD|<ship>|<DEPART|LAND>|<reason>   (deny a queued ship; reason: traffic | no pad)
 * The tower must echo the EXACT action string it received (the ship matches on it).
 * All extensions append fields or add verbs; legacy ships/towers split unbounded and read
 * only the fields they know, so the protocol stays backward compatible.
 *
 * SERIALIZATION: one craft *maneuvers* at a time (a single global corridor slot) - the
 * anti-collision guarantee. A waiting LAND is served before a waiting DEPART (a landing
 * ship burns fuel holding, a docked ship can wait). The slot is held from grant until the
 * granted ship's status shows it cleared the corridor (departed to cruise / docked), with
 * a safety release if the ship is lost, faults, or overruns GRANT_MAX_SEC.
 *
 * PAD BANK (dynamic): the tower owns a pool of docking pads and assigns a *free* one to
 * each arriving ship, so ships PARK in parallel while MOVEMENT stays serialized. A pad's
 * dock pose is recorded once by ANY ship (REGPAD on the ship) and pushed here via CMD|PAD;
 * the tower stores it in a [pad.<name>] section and relays it in the LAND grant. This
 * assumes a geometrically UNIFORM drone fleet (a pose recorded by one ship docks another).
 * A pad is occupied from LAND-grant until its ship departs (or is lost); a LAND with no
 * free pad is held (reason "no pad"). If no pads are registered, LAND grants carry no pose
 * and ships fall back to their own recorded destination pose (pre-pad-bank behaviour).
 *
 * HOLDING ZONE + INTERIOR PATHS (station the ship does NOT own): for a hole-in-the-wall
 * station where a straight taxi would punch through a wall, the tower also owns a HOLDING
 * ZONE (an oriented box; taught via CMD|ZONE, advertised in the heartbeat) and, per pad, a
 * recorded INTERIOR PATH (holding-zone -> pad breadcrumbs; taught once by any ship via
 * CMD|PADPATH, stored in the pad's [pad.<name>] path key). On a LAND grant the tower streams
 * the assigned pad's path (CMD|PATH, chunked); the ship threads it in to dock. On DEPART it
 * re-streams the occupied pad's path; the ship reverses it back out to the zone. A pad with
 * no path is open-air: the ship uses its straight-line last mile (backward compatible).
 *
 * CONFIG: the [sf] Custom Data section (auto-generated on first compile):
 *   channel   = SkippyShuttleNet   (must match the fleet)
 *   zone      = Main               (heartbeat label; informational)
 *   lcdTag    = [SF]               (board renders to Me's surface + every matching LCD
 *                                    on this station's construct; a docked ship's own
 *                                    [SF] panels are excluded)
 *   towerMode = control | board | teach
 *                    control = active tower (heartbeat + serialize clearances).
 *                    board   = passive status board, NO heartbeat, fleet stays
 *                              independent - a drop-in for the old role=base.
 *                    teach   = ship-side SETUP helper (run this on a 2nd PB ON THE
 *                              SHIP, not the station): hand-fly to record the holding
 *                              zone, pad poses, and interior pad paths, streamed to the
 *                              station tower. No heartbeat, answers no clearance. See
 *                              the teach commands below.
 *   grant     = auto | manual    (control-mode sub-mode: auto clears the best waiting
 *                                    craft every tick; manual holds all traffic until the
 *                                    operator approves each one by hand. Persisted on toggle)
 *   remoteName= <name>           (teach only: exact Remote Control to read; blank = first on the grid)
 *   teachSeg  = 2.5              (teach only: interior-path breadcrumb spacing, metres)
 *   teachTurn = 12               (teach only: extra breadcrumb on this heading change, degrees)
 * Plus one [pad.<name>] section per registered pad (pos/fwd/up/connFwd, X:Y:Z, and an optional
 * ';'-joined interior path), written automatically on CMD|PAD / CMD|PADPATH, and a [zone] section
 * (center/fwd/up/ext) written on CMD|ZONE. Delete a section to retire a pad/zone; occupancy is
 * never persisted.
 *
 * OPERATOR COMMANDS (run the PB with an argument, or bind to a button; control mode only):
 *   MANUAL          take the controls - stop auto-granting; hold every request for your OK
 *   AUTO            hand back - resume auto-granting the best waiting craft
 *   CLEAR           approve the top of the queue (best: a LAND before a DEPART, then oldest)
 *   CLEAR <ship>    approve a specific waiting ship by name (queue-jump)
 *   RELEASE         force-free the current corridor slot now (deadlock breaker, no 180 s wait)
 *   PADFREE <name>  force-free a pad's occupancy (deadlock breaker for a stuck pad)
 * The heartbeat keeps beating in BOTH sub-modes, so a ship held for manual approval stays
 * controlled (never reverts to independent) while the operator deliberates.
 *
 * TEACH COMMANDS (towerMode=teach only; run the ship-side teach PB with an argument):
 *   REGZONE [w h d]   define the holding box at the ship's current pose (metres, default 20 20 20)
 *   REGPATH <pad>     start recording an interior path; fly the corridor in and dock to finalize
 *   REGPATH END       finalize the current recording without docking (path only, no pose)
 *   REGPATH CANCEL    abort the current recording
 *   REGPAD <pad>      register an open-air pad's dock pose (must be docked; no interior path)
 *
 * Version tracked in CHANGELOG.md (project semver; the shuttle script versions
 * independently over the same protocol).
 *//////////////////////////////////////////////////////////////////////////////

const string VERSION = "0.13.0";

// ---- Mode ------------------------------------------------------------------
// Control  - emit the heartbeat and serialize clearances (an active tower).
// Board    - render the status board only; NO heartbeat, so every ship stays
//            independent. Byte-for-byte the passive board behaviour of role=base.
// Teach    - ship-side SETUP helper (runs on a second PB ON THE SHIP, not the
//            station): records the holding zone, pad poses, and interior pad
//            paths by hand-flying, and streams them to the station tower via
//            CMD|ZONE / CMD|PAD / CMD|PADPATH. Emits NO heartbeat and answers NO
//            clearance request - it is a pure teaching tool, never a controller.
enum TowerMode { Control, Board, Teach }

// ---- Config (all live in the [sf] Custom Data section) ---------------------
string channel = "SkippyShuttleNet";   // IGC channel; must match the fleet's
string zone = "Main";                    // broadcast in the heartbeat; ships ignore the content, it is operator-facing only
string lcdTag = "[SF]";                  // board is written to every LCD on this construct whose name contains this tag (plus Me's own surface); a docked ship's panels are excluded
TowerMode mode = TowerMode.Control;      // active controller vs passive board
bool manual = false;                     // control sub-mode: false = auto-grant best each tick; true = operator approves each CLEAR. Toggled at runtime (MANUAL/AUTO), persisted to Custom Data. Ignored in board mode.

// ---- Teach-mode config (only meaningful when towerMode = teach) -------------
string remoteName = "";                  // optional exact Remote Control name on the ship; blank = first RC found on this construct
double teachSeg = 2.5;                   // interior-path breadcrumb spacing [m]; fine because station walls are close (cruise recording uses tens of metres, this must not)
double teachTurn = 12.0;                 // extra breadcrumb when heading changes by this many degrees over a short move, so tight corridor turns are captured

// ---- Timing constants ------------------------------------------------------
const double HEARTBEAT_SEC = 2.0;        // interval between CMD|TOWER beats; well under the ships' TOWER_TIMEOUT (6 s) so a beat or two can be lost
const double REQ_STALE_SEC = 6.0;        // drop a pending request not re-sent within this - the ship stopped waiting (cleared elsewhere, STOPped, or gone)
const double GRANT_MAX_SEC = 180.0;      // anti-deadlock: force-release a slot whose holder never reports clearing the resource
const double SIGNAL_STALE_SEC = 20.0;    // a fleet entry older than this reads as "NO SIGNAL" (matches the base board)
const double DT_FALLBACK = 1.0 / 6.0;    // assumed tick length when TimeSinceLastRun is unusable (first tick / long pause)
const int PATH_CHUNK = 18;               // interior-path points per CMD|PADPATH / CMD|PATH message (each Vec ~65 chars, well under IGC limits)

// ---- Runtime state ---------------------------------------------------------
IMyBroadcastListener listener;
double dt;                               // real seconds elapsed this tick
double hbTimer;                          // seconds since the last heartbeat
long seqCounter;                         // monotonic request sequence, for FIFO ordering
long myGrid;                             // EntityId of this tower's construct; advertised in the heartbeat and matched against a ship's target grid so a tower only governs ships bound for its own grid (0 until set in Program)

// One craft cleared into the shared corridor/pad at a time.
string activeShip = "";
string activeAction = "";                // "DEPART" or "LAND"
double grantAge;                         // seconds the current grant has been outstanding
string activePad = "";                   // pad reserved for the current LAND grant ("" for DEPART / no pad); the CLEAR re-confirm relays its pose

// Every ship heard on the channel, keyed by name (status board + phase source).
Dictionary<string, ShuttleReport> fleet = new Dictionary<string, ShuttleReport>();
// Ships waiting for the slot, keyed by name. The active ship is NOT kept here.
Dictionary<string, PendingReq> pending = new Dictionary<string, PendingReq>();
// The docking pads this tower owns, keyed by name. Populated by CMD|PAD (REGPAD on a
// ship) and persisted to [pad.<name>] Custom Data sections. Occupancy is live-only.
Dictionary<string, Pad> pads = new Dictionary<string, Pad>();

// The holding zone: an oriented box (center + fwd/up axes + half-extents) that station
// routes target instead of a pin-point pad. Ships loiter anywhere inside it, so a queue
// spreads out; it is also the seam every pad's interior path connects out to. Taught by a
// ship via CMD|ZONE, advertised in the heartbeat, persisted as the [zone] section.
bool haveZone;
Vector3D zoneCenter, zoneFwd, zoneUp, zoneExt;

// Reassembly buffers for chunked CMD|PADPATH transfers (ship -> tower), keyed by pad name.
// A partial trail lands here until its final chunk (seq==total-1) commits it to pads[pad].Path.
Dictionary<string, List<Vector3D>> padPathRx = new Dictionary<string, List<Vector3D>>();

// ---- Teach-mode runtime (this PB is on the SHIP; drives nothing, only reads pose) ----
const int MAX_TEACH_PATH = 400;          // hard cap on breadcrumbs per interior path (an interior run is <50 in practice)
IMyRemoteControl teachRc;                // the ship's Remote Control, for world position + attitude while teaching
List<IMyShipConnector> teachConns = new List<IMyShipConnector>();   // the ship's connectors, to detect a dock and capture the pad pose
bool recording;                          // mid REGPATH: accumulating an interior breadcrumb trail
string recPad = "";                      // pad the in-progress path (and its dock pose) will be filed under
List<Vector3D> recPath = new List<Vector3D>();   // the breadcrumbs captured so far (zone -> pad)
Vector3D recLast;                        // last committed breadcrumb, for spacing/turn tests
Vector3D recLastDir;                     // last committed heading, for the turn test (Zero until the first segment)
bool wasDocked;                          // previous-tick dock state, so a fresh dock auto-finalizes the path
string teachMsg = "Ready. REGZONE / REGPAD / REGPATH to begin.";   // operator-facing status line

class ShuttleReport
{
    public string Name, State;
    public int EtaSec, DistM, Fill;
    public double MassT;
    public bool Running;
    public double Age;   // seconds since the last update
}

class PendingReq
{
    public string Action;
    public double Age;   // seconds since the last CMD|REQ re-send
    public long Seq;      // request order (assigned once, on first sight) - lower is older
}

// A docking pad the tower can assign. Pos/Fwd/Up/ConnFwd is the absolute WORLD dock pose
// recorded once by any ship (a uniform-fleet assumption lets one ship's pose dock another),
// relayed verbatim in the LAND grant. OccupiedBy is the ship currently holding it ("" = free).
// Path is the recorded interior breadcrumb trail (holding-zone -> pad), taught once by any
// ship via CMD|PADPATH and streamed to the granted ship so it can thread walls to this pad;
// empty means an open-air pad the ship reaches by the straight-line fallback.
class Pad
{
    public string Name;
    public Vector3D Pos, Fwd, Up, ConnFwd;
    public string OccupiedBy = "";
    public List<Vector3D> Path = new List<Vector3D>();
}

// ============================================================================
//  Lifecycle
// ============================================================================
Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;   // ~6 Hz; ample for a 2 s heartbeat, 6 s timeouts, and pose sampling while teaching
    myGrid = Me.CubeGrid.EntityId;                         // this construct's identity; ships match it against their route's dock grid so only the right tower governs them
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();
    LoadConfig();
    if (mode == TowerMode.Teach) DiscoverTeach();          // this PB is on the ship; find its RC + connectors
    else listener = IGC.RegisterBroadcastListener(channel);// a teacher listens to nothing - it only broadcasts setup data
}

// ============================================================================
//  Main
// ============================================================================
void Main(string argument, UpdateType source)
{
    // Real elapsed time this tick. Guard the first post-compile tick (0) and long
    // save/exit pauses (huge delta) so the timers stay sane.
    dt = Runtime.TimeSinceLastRun.TotalSeconds;
    if (dt <= 0 || dt > 0.5) dt = DT_FALLBACK;

    // Teach mode is a wholly separate loop: no clearance queue, no heartbeat, no board.
    // It only reads the ship's pose and streams setup data to the station tower.
    if (mode == TowerMode.Teach)
    {
        RunTeach(string.IsNullOrEmpty(argument) ? "" : argument.Trim());
        RenderTeach();
        return;
    }

    if (!string.IsNullOrEmpty(argument)) HandleCommand(argument.Trim());

    DrainMessages();
    AgeTables();

    if (mode == TowerMode.Control)
    {
        ReleaseIfDone();              // auto slot-release stays ON in both sub-modes (anti-deadlock)
        ReleasePads();                // free a pad once its ship has departed / been lost (occupancy outlives the corridor slot)
        if (!manual) GrantNext();     // AUTO: grant the best waiting craft. MANUAL: only a CLEAR command grants.
        Heartbeat();                  // beats in both sub-modes, so a ship held for approval stays controlled
    }

    RenderBoard();
}

// Operator commands via the PB argument (terminal Run, or a button). Control mode only;
// in board mode there is no slot to grant so these are inert. Verb is case-insensitive;
// a ship name after CLEAR is kept raw (it may contain spaces).
void HandleCommand(string arg)
{
    string verb = arg.ToUpperInvariant();
    if (verb == "MANUAL")               { manual = true;  SaveGrantMode(); }
    else if (verb == "AUTO")            { manual = false; SaveGrantMode(); }
    else if (verb == "RELEASE")         ClearSlot();
    else if (verb == "CLEAR")           ManualGrant(null);
    else if (verb.StartsWith("CLEAR ")) ManualGrant(arg.Substring(6).Trim());
    else if (verb.StartsWith("PADFREE ")) FreePad(arg.Substring(8).Trim());
}

// Read every pending broadcast: status reports feed the fleet table; CMD|REQ feeds
// the clearance queue (control mode only). All other CMD| verbs - our own heartbeat/
// grants, another tower's, or a force DEPART - are ignored.
void DrainMessages()
{
    while (listener.HasPendingMessage)
    {
        var m = listener.AcceptMessage();
        var s = m.Data as string;
        if (s == null) continue;
        var f = s.Split('|');
        if (f.Length < 2) continue;

        if (f[0] == "CMD")
        {
            // CMD|REQ|<ship>|<action>|<dock>|<grid> - grid scopes the request to a tower; f[5] absent on legacy ships
            if (mode == TowerMode.Control && f[1] == "REQ" && f.Length >= 5)
                OnRequest(f[2], f[3], f.Length >= 6 ? ParseLong(f[5], 0) : 0);
            // CMD|PAD|<name>|<pos>|<fwd>|<up>|<connFwd> - register/update a pad definition
            else if (f[1] == "PAD" && f.Length >= 7)
                UpsertPad(f[2], f[3], f[4], f[5], f[6]);
            // CMD|ZONE|<center>|<fwd>|<up>|<ext> - define/replace the holding zone volume
            else if (f[1] == "ZONE" && f.Length >= 6)
                UpsertZone(f[2], f[3], f[4], f[5]);
            // CMD|PADPATH|<pad>|<seq>|<total>|<v>;<v>;... - a chunk of a pad's interior path
            else if (f[1] == "PADPATH" && f.Length >= 6)
                OnPadPathChunk(f[2], f[3], f[4], f[5]);
            continue;
        }

        // Status report: name|state|eta|dist|fill|mass|running
        if (f.Length < 7) continue;
        fleet[f[0]] = new ShuttleReport
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
    }
}

// A ship is asking to move. If it already holds the slot, re-confirm its grant (cheap
// redelivery in case a CMD|CLEAR was missed). Otherwise queue/refresh it and, if some
// OTHER ship holds the slot, tell it to keep holding with a reason. When the slot is
// free we stay silent and let GrantNext pick by priority within the same tick.
void OnRequest(string ship, string action, long grid)
{
    // Grid scoping: a ship names the grid it is arriving at / departing from. Ignore a request
    // meant for a different tower so we never reserve a slot/pad for a ship that isn't coming
    // here. grid 0 (legacy ship, or a zone route recorded before grid-scoping) -> accept as before.
    if (grid != 0 && myGrid != 0 && grid != myGrid) return;

    if (ship == activeShip)
    {
        Send(ClearMsg());        // re-confirm; relays the assigned pad pose for a LAND
        return;
    }

    PendingReq p;
    if (pending.TryGetValue(ship, out p))
    {
        p.Action = action;
        p.Age = 0;
    }
    else
    {
        pending[ship] = new PendingReq { Action = action, Age = 0, Seq = seqCounter++ };
    }

    // Tell the ship why it is still waiting. Busy corridor -> traffic. Corridor free but
    // every registered pad taken -> no pad (sent at the ship's REQ cadence, so not spammy).
    // With no pads registered at all we stay silent: that is pre-pad-bank fallback, and
    // GrantNext will clear it this tick to dock at its own recorded pose.
    if (activeShip.Length > 0)
        Send("CMD|HOLD|" + ship + "|" + action + "|traffic");
    else if (action == "LAND" && pads.Count > 0 && FirstFreePad() == null)
        Send("CMD|HOLD|" + ship + "|" + action + "|no pad");
}

// Age the fleet reports and pending requests; forget a request that has gone quiet
// (the ship stopped re-sending, so it is no longer waiting on us).
void AgeTables()
{
    foreach (var r in fleet.Values) r.Age += dt;

    if (pending.Count > 0)
    {
        var drop = new List<string>();
        foreach (var kv in pending)
        {
            kv.Value.Age += dt;
            if (kv.Value.Age > REQ_STALE_SEC) drop.Add(kv.Key);
        }
        foreach (var k in drop) pending.Remove(k);
    }
}

// Release the slot once its holder has cleared the shared resource - or as a safety
// net if the ship is lost, aborted, or overran. Then the next GrantNext can proceed.
void ReleaseIfDone()
{
    if (activeShip.Length == 0) return;
    grantAge += dt;

    bool done = false;
    ShuttleReport r;
    if (!fleet.TryGetValue(activeShip, out r) || r.Age > SIGNAL_STALE_SEC)
        done = true;                                         // lost the ship - free the slot rather than deadlock
    else if (r.State == "Faulted" || r.State == "Idle")
        done = true;                                         // aborted / stopped mid-move
    else if (activeAction == "DEPART" && IsCruiseState(r.State))
        done = true;                                         // departed and flying - corridor is clear (a still-docked state does NOT count: it hasn't left)
    else if (activeAction == "LAND" && IsDockedState(r.State))
        done = true;                                         // on the pad - corridor is clear
    else if (grantAge > GRANT_MAX_SEC)
        done = true;                                         // overran - assume complete so the pad never sticks

    if (done) ClearSlot();
}

// Grant the slot to the best waiting ship that CAN be served now: a LAND outranks a DEPART
// (a holding ship burns fuel; a docked one can wait), then oldest-first (lowest Seq). A LAND
// is only grantable when a pad is free - so when pads are full, a waiting DEPART is served
// instead (and departing frees a pad), which naturally clears the jam.
void GrantNext()
{
    if (activeShip.Length > 0 || pending.Count == 0) return;

    string bestShip = null;
    PendingReq best = null;
    foreach (var kv in pending)
    {
        if (!Grantable(kv.Value.Action)) continue;   // LAND with every pad taken - skip for now
        if (best == null || Better(kv.Value, best)) { best = kv.Value; bestShip = kv.Key; }
    }
    if (bestShip == null) return;

    Grant(bestShip, best.Action);
}

// True if request a should be served before request b.
bool Better(PendingReq a, PendingReq b)
{
    bool landA = a.Action == "LAND", landB = b.Action == "LAND";
    if (landA != landB) return landA;   // LAND beats DEPART
    return a.Seq < b.Seq;                // else FIFO (oldest first)
}

// Operator-approved grant for the CLEAR command. ship==null grants the best waiting craft
// (same priority as auto - reuses GrantNext); a name grants that specific waiting ship,
// jumping the queue. No-op if the slot is busy, the queue is empty, the name is unknown,
// or it is a LAND with no free pad (nothing to assign - the board shows the pads full).
void ManualGrant(string ship)
{
    if (activeShip.Length > 0 || pending.Count == 0) return;
    if (ship == null) { GrantNext(); return; }

    PendingReq p;
    if (!pending.TryGetValue(ship, out p)) return;
    if (!Grantable(p.Action)) return;
    Grant(ship, p.Action);
}

// Move a ship into the corridor slot and clear it. A LAND reserves a free pad (guaranteed
// present by the Grantable check) and its pose rides along in the CLEAR.
void Grant(string ship, string action)
{
    activeShip = ship;
    activeAction = action;
    grantAge = 0;
    activePad = "";
    pending.Remove(ship);
    if (action == "LAND")
    {
        activePad = FirstFreePad();
        if (activePad != null) pads[activePad].OccupiedBy = ship;
        else activePad = "";        // defensive; Grantable already guaranteed one
    }
    Send(ClearMsg());
    StreamGrantPath(ship, action);
}

// Stream the interior breadcrumb path for a grant so the ship can thread walls to/from its pad.
// LAND streams the just-assigned pad's path (ship follows it inbound); DEPART streams the path of
// whatever pad the ship still occupies (ship reverses it to thread back out to the zone). A pad
// with no recorded path (open-air dock) sends nothing - the ship keeps its straight-line fallback.
void StreamGrantPath(string ship, string action)
{
    Pad pd = null;
    if (action == "LAND" && activePad.Length > 0) pads.TryGetValue(activePad, out pd);
    else if (action == "DEPART")
    {
        foreach (var p in pads.Values) if (p.OccupiedBy == ship) { pd = p; break; }
    }
    if (pd == null || pd.Path.Count == 0) return;
    StreamPath(ship, pd.Path);
}

// Send a point list to a ship as chunked CMD|PATH messages: CMD|PATH|<ship>|<seq>|<total>|<v>;<v>;...
// The ship reassembles on seq/total. Chunks are small enough to clear the IGC per-message limit.
void StreamPath(string ship, List<Vector3D> path)
{
    int total = (path.Count + PATH_CHUNK - 1) / PATH_CHUNK;
    if (total == 0) return;
    for (int seq = 0; seq < total; seq++)
        Send("CMD|PATH|" + ship + "|" + seq + "|" + total + "|" + PathStr(path, seq * PATH_CHUNK, PATH_CHUNK));
}

// ';'-join a window of a point list into "X:Y:Z;X:Y:Z;..." for one chunk payload.
string PathStr(List<Vector3D> path, int start, int count)
{
    var sb = new StringBuilder();
    int end = Math.Min(start + count, path.Count);
    for (int i = start; i < end; i++)
    {
        if (i > start) sb.Append(';');
        sb.Append(Vec(path[i]));
    }
    return sb.ToString();
}

// Parse a ';'-joined "X:Y:Z" chunk payload, appending each valid point to buf.
void ParsePath(string payload, List<Vector3D> buf)
{
    if (string.IsNullOrEmpty(payload)) return;
    var parts = payload.Split(';');
    Vector3D v;
    foreach (var pt in parts) if (TryVec(pt, out v)) buf.Add(v);
}

// A LAND can only be granted when a pad is free (or none are registered at all - then the
// ship falls back to its own recorded pose, pre-pad-bank behaviour). DEPART always can.
bool Grantable(string action)
{
    return action != "LAND" || pads.Count == 0 || FirstFreePad() != null;
}

// The clearance message for the current slot holder. A LAND with a reserved pad appends the
// pad name and its world dock pose; a DEPART (or a pad-less fallback LAND) is the bare grant.
string ClearMsg()
{
    string m = "CMD|CLEAR|" + activeShip + "|" + activeAction;
    if (activeAction == "LAND" && activePad.Length > 0)
    {
        var pd = pads[activePad];
        m += "|" + activePad + "|" + Vec(pd.Pos) + "|" + Vec(pd.Fwd) + "|" + Vec(pd.Up) + "|" + Vec(pd.ConnFwd);
    }
    return m;
}

// Name of the first unoccupied pad, or null if none exist / all are taken.
string FirstFreePad()
{
    foreach (var pd in pads.Values)
        if (pd.OccupiedBy.Length == 0) return pd.Name;
    return null;
}

// Free a pad whose occupant has departed the station or gone away - occupancy outlives the
// corridor slot (the pad is held from LAND-grant, through docked/parked, until the ship
// leaves), so this runs independently of ReleaseIfDone. Anti-deadlock: lost/faulted/idle frees.
void ReleasePads()
{
    foreach (var pd in pads.Values)
    {
        if (pd.OccupiedBy.Length == 0) continue;
        ShuttleReport r;
        if (!fleet.TryGetValue(pd.OccupiedBy, out r) || r.Age > SIGNAL_STALE_SEC) pd.OccupiedBy = "";   // lost the ship
        else if (r.State == "Faulted" || r.State == "Idle") pd.OccupiedBy = "";                          // aborted / stopped
        else if (IsCruiseState(r.State)) pd.OccupiedBy = "";                                             // departed and flying - pad is clear
    }
}

// Register or update a pad from a CMD|PAD broadcast, preserving live occupancy, then persist.
void UpsertPad(string name, string pos, string fwd, string up, string cf)
{
    Vector3D p, fw, u, c;
    if (!(TryVec(pos, out p) & TryVec(fwd, out fw) & TryVec(up, out u) & TryVec(cf, out c))) return;
    Pad pd;
    if (!pads.TryGetValue(name, out pd)) { pd = new Pad { Name = name }; pads[name] = pd; }
    pd.Pos = p; pd.Fwd = fw; pd.Up = u; pd.ConnFwd = c;
    SavePads();
}

// Define/replace the holding zone from a CMD|ZONE broadcast (four "X:Y:Z" vectors), then persist.
void UpsertZone(string center, string fwd, string up, string ext)
{
    Vector3D c, fw, u, e;
    if (!(TryVec(center, out c) & TryVec(fwd, out fw) & TryVec(up, out u) & TryVec(ext, out e))) return;
    zoneCenter = c; zoneFwd = fw; zoneUp = u; zoneExt = e;
    haveZone = true;
    SaveZone();
}

// Accumulate one chunk of a pad's interior path (CMD|PADPATH). Points are ';'-joined "X:Y:Z".
// The first chunk (seq 0) starts a fresh buffer; the final chunk (seq==total-1) commits the
// assembled trail to the pad and persists. An unknown pad is created pose-less (a later CMD|PAD
// fills the pose) so path and pose can be taught in either order.
void OnPadPathChunk(string pad, string seqS, string totalS, string payload)
{
    int seq = ParseInt(seqS, -1), total = ParseInt(totalS, 0);
    if (seq < 0 || total <= 0) return;

    List<Vector3D> buf;
    if (seq == 0 || !padPathRx.TryGetValue(pad, out buf)) { buf = new List<Vector3D>(); padPathRx[pad] = buf; }
    ParsePath(payload, buf);

    if (seq == total - 1)
    {
        Pad pd;
        if (!pads.TryGetValue(pad, out pd)) { pd = new Pad { Name = pad }; pads[pad] = pd; }
        pd.Path = buf;
        padPathRx.Remove(pad);
        SavePads();
    }
}

// Force-free a pad's occupancy (operator deadlock breaker). No-op on an unknown name.
void FreePad(string name)
{
    Pad pd;
    if (pads.TryGetValue(name, out pd)) pd.OccupiedBy = "";
}

void ClearSlot()
{
    activeShip = "";
    activeAction = "";
    grantAge = 0;
    activePad = "";      // drop the reservation pointer; the pad itself stays OccupiedBy until the ship departs (ReleasePads)
}

void Heartbeat()
{
    hbTimer += dt;
    if (hbTimer < HEARTBEAT_SEC) return;
    hbTimer = 0;
    // CMD|TOWER|<zone>|<gridId>[|<center>|<fwd>|<up>|<ext>]. The grid id lets a ship match this
    // tower against its route's dock grid, so a tower only governs ships bound for its own grid.
    // Zone geometry is appended when defined so zone-routed ships can loiter inside the box.
    // Legacy ships read only f[2] (zone); the appended fields are ignored by their positional parse.
    string m = "CMD|TOWER|" + zone + "|" + myGrid;
    if (haveZone)
        m += "|" + Vec(zoneCenter) + "|" + Vec(zoneFwd) + "|" + Vec(zoneUp) + "|" + Vec(zoneExt);
    Send(m);
}

bool IsCruiseState(string s) { return s == "CruiseToDest" || s == "CruiseToHome"; }
bool IsDockedState(string s) { return s == "Loading" || s == "Unloading"; }

void Send(string msg) { IGC.SendBroadcastMessage(channel, msg); }

// ============================================================================
//  Board render
// ============================================================================
void RenderBoard()
{
    var sb = new StringBuilder();
    sb.Append("== Skippy Tower v").Append(VERSION).Append(" ==\n");
    if (mode == TowerMode.Control)
        sb.Append("CONTROL/").Append(manual ? "MANUAL" : "AUTO").Append(" - zone ").Append(zone).Append('\n');
    else
        sb.Append("BOARD (passive)\n");
    sb.Append('\n');

    if (fleet.Count == 0) sb.Append("Waiting for shuttle signal...\n");
    foreach (var r in fleet.Values)
    {
        if (r.Age > SIGNAL_STALE_SEC)
        {
            sb.Append(r.Name).Append(": NO SIGNAL (").Append((int)r.Age).Append("s)\n\n");
            continue;
        }
        sb.Append(r.Name).Append(": ").Append(PrettyState(r.State)).Append('\n');
        if (r.EtaSec >= 0)
            sb.Append("   ETA ").Append((r.EtaSec / 60).ToString("00")).Append(':').Append((r.EtaSec % 60).ToString("00"))
              .Append("   ").Append((r.DistM / 1000.0).ToString("0.0")).Append(" km\n");
        sb.Append("   Cargo ").Append(r.Fill).Append("%   ").Append(r.MassT.ToString("0.0")).Append("t\n");
        if (mode == TowerMode.Control)
        {
            if (r.Name == activeShip) sb.Append("   > CLEARED: ").Append(activeAction).Append('\n');
            else if (pending.ContainsKey(r.Name)) sb.Append(manual ? "   || WAITING (your OK)\n" : "   || HOLD (traffic)\n");
        }
        sb.Append('\n');
    }

    if (mode == TowerMode.Control)
    {
        if (activeShip.Length > 0)
            sb.Append("Slot: ").Append(activeShip).Append(" (").Append(activeAction).Append(")\n");
        else if (manual && pending.Count > 0)
        {
            // Manual, slot free, queue waiting: show what a bare CLEAR would approve next.
            string ns = null; PendingReq nb = null;
            foreach (var kv in pending)
                if (nb == null || Better(kv.Value, nb)) { nb = kv.Value; ns = kv.Key; }
            sb.Append("Next: ").Append(ns).Append(" (").Append(nb.Action).Append(") - run CLEAR\n");
        }
        else
            sb.Append("Slot: free\n");

        if (haveZone)
            sb.Append("Zone: ").Append((zoneExt.X * 2).ToString("0")).Append('x')
              .Append((zoneExt.Y * 2).ToString("0")).Append('x').Append((zoneExt.Z * 2).ToString("0")).Append("m\n");

        if (pads.Count > 0)
        {
            sb.Append("-- Pads --\n");
            foreach (var pd in pads.Values)
            {
                sb.Append(pd.Name).Append(": ").Append(pd.OccupiedBy.Length > 0 ? pd.OccupiedBy : "free");
                if (pd.Path.Count > 0) sb.Append(" (").Append(pd.Path.Count).Append("wp)");
                sb.Append('\n');
            }
        }
    }

    var text = sb.ToString();
    Echo(text);
    var panels = new List<IMyTextPanel>();
    // Scope to this station's own construct. IsSameConstructAs counts mechanically
    // linked subgrids (rotor/piston/hinge display arms) but treats a connector-docked
    // ship as a separate construct - so a visiting shuttle's [SF] panels are NOT hijacked.
    GridTerminalSystem.GetBlocksOfType(panels, b => b.CubeGrid.IsSameConstructAs(Me.CubeGrid) && b.CustomName.Contains(lcdTag));
    foreach (var p in panels) { p.ContentType = ContentType.TEXT_AND_IMAGE; p.WriteText(text); }
    Me.GetSurface(0).ContentType = ContentType.TEXT_AND_IMAGE;
    Me.GetSurface(0).WriteText(text);
}

// Turn the wire state name (shared with the base board) into an operator-facing line.
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
//  Teach mode (ship-side setup helper)
// ============================================================================
// This block only runs when towerMode = teach, on a Programmable Block ON THE SHIP.
// It captures the three things a station tower needs - the holding zone, each pad's
// dock pose, and each pad's interior breadcrumb path - by letting the operator hand-fly
// the ship, then streams them to the station tower over the same wire protocol the
// station uses. It never drives a block, never answers a clearance request, and never
// beats a heartbeat, so it can share the fleet channel with a live tower safely.
//
// WORKFLOW at the target station:
//   1. Fly to the spot outside the entrance where ships should loiter; run
//      REGZONE [width height depth]  (metres; default 20 20 20) - defines the box.
//   2. For each pad: from inside that zone run  REGPATH <padName>, hand-fly the corridor
//      in, and dock. Docking auto-finalizes: the breadcrumb path AND the pad's dock pose
//      are streamed to the tower. (REGPATH END finalizes without a dock; REGPATH CANCEL aborts.)
//   3. An open-air pad with no walls needs no path: just dock and run REGPAD <padName>.
void RunTeach(string arg)
{
    if (teachRc == null || teachConns.Count == 0) DiscoverTeach();
    if (teachRc == null) { teachMsg = "! No Remote Control on this grid - teach needs one."; return; }

    if (arg.Length > 0) HandleTeachCommand(arg);

    if (recording)
    {
        if (recPath.Count < MAX_TEACH_PATH) TickTeachRecord();
        // A fresh dock (edge from undocked to connected) auto-finalizes the path + pad pose.
        bool docked = ConnectedTeachConn() != null;
        if (docked && !wasDocked) FinalizePath();
        wasDocked = docked;
    }
}

// Verb dispatch for the teach PB argument. REGPATH is overloaded: a bare name starts a
// recording; the reserved words END / CANCEL finish or abort the one in progress.
void HandleTeachCommand(string arg)
{
    int sp = arg.IndexOf(' ');
    string verb = (sp < 0 ? arg : arg.Substring(0, sp)).ToUpperInvariant();
    string rest = sp < 0 ? "" : arg.Substring(sp + 1).Trim();

    if (verb == "REGZONE") RegZone(rest);
    else if (verb == "REGPAD")
    {
        if (rest.Length == 0) { teachMsg = "REGPAD needs a pad name."; return; }
        RegPad(rest);
    }
    else if (verb == "REGPATH")
    {
        string up = rest.ToUpperInvariant();
        if (up == "END") { if (recording) FinalizePath(); else teachMsg = "REGPATH END: nothing recording."; }
        else if (up == "CANCEL") { recording = false; recPad = ""; recPath.Clear(); teachMsg = "Path recording cancelled."; }
        else if (rest.Length == 0) teachMsg = "REGPATH needs a pad name (or END / CANCEL).";
        else StartPath(rest);
    }
}

// REGZONE [w h d]: snapshot the ship's current pose as the holding-box centre + axes, with
// half-extents from the operator's full-dimension args (default 20 m cube). Stores it locally
// (so the board can show it) and broadcasts CMD|ZONE. Axes: X = right, Y = up, Z = forward,
// matching the ship's InZone test.
void RegZone(string arg)
{
    double w = 20, h = 20, d = 20;
    var t = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (t.Length >= 1) w = ParseDouble(t[0], w);
    if (t.Length >= 2) h = ParseDouble(t[1], h);
    if (t.Length >= 3) d = ParseDouble(t[2], d);

    zoneCenter = teachRc.GetPosition();
    zoneFwd = teachRc.WorldMatrix.Forward;
    zoneUp = teachRc.WorldMatrix.Up;
    zoneExt = new Vector3D(w / 2, h / 2, d / 2);
    haveZone = true;
    Send("CMD|ZONE|" + Vec(zoneCenter) + "|" + Vec(zoneFwd) + "|" + Vec(zoneUp) + "|" + Vec(zoneExt));
    teachMsg = "Zone sent: " + w.ToString("0") + "x" + h.ToString("0") + "x" + d.ToString("0") + "m.";
}

// REGPAD <name>: register an open-air pad (no interior path). Must be docked at it; captures the
// live dock pose exactly as the ship's CapturePose does and broadcasts CMD|PAD.
void RegPad(string name)
{
    var c = ConnectedTeachConn();
    if (c == null) { teachMsg = "REGPAD: dock at pad '" + name + "' first."; return; }
    SendPad(name, c);
    teachMsg = "Pad '" + name + "' pose sent (open-air).";
}

// REGPATH <name>: begin an interior breadcrumb recording. Seed the first crumb at the current
// position (the zone-side start), then the operator flies the corridor and docks.
void StartPath(string name)
{
    recording = true;
    recPad = name;
    recPath.Clear();
    recLast = teachRc.GetPosition();
    recPath.Add(recLast);
    recLastDir = Vector3D.Zero;
    wasDocked = ConnectedTeachConn() != null;
    teachMsg = "Recording path to '" + name + "' - fly the corridor in and dock.";
}

// One breadcrumb sampling tick. Add a point on enough travel, or on a heading change over a
// short move (captures tight corridor turns). Mirrors the ship's recorder but at a fine spacing.
void TickTeachRecord()
{
    Vector3D p = teachRc.GetPosition();
    double moved = Vector3D.Distance(p, recLast);
    if (moved < 0.5) return;                          // parked jitter
    Vector3D dir = Vector3D.Normalize(p - recLast);
    double turn = recLastDir == Vector3D.Zero ? 0
                : Math.Acos(MathHelper.Clamp(dir.Dot(recLastDir), -1, 1)) * 180.0 / Math.PI;
    if (moved >= teachSeg || (moved >= 1.0 && turn >= teachTurn))
    {
        recPath.Add(p);
        recLast = p;
        recLastDir = dir;
    }
}

// Commit the in-progress path: stream the breadcrumbs (CMD|PADPATH, chunked) and, if docked, the
// pad's dock pose (CMD|PAD) so a single REGPATH teaches both. The ship appends the exact pad point
// itself from the pose, so the trail need not reach it. Clears the recording state.
void FinalizePath()
{
    var c = ConnectedTeachConn();
    SendPath(recPad, recPath);
    if (c != null)
    {
        SendPad(recPad, c);
        teachMsg = "Path '" + recPad + "' sent (" + recPath.Count + "wp) + pad pose.";
    }
    else
    {
        teachMsg = "Path '" + recPad + "' sent (" + recPath.Count + "wp); not docked, no pose.";
    }
    recording = false;
    recPad = "";
    recPath.Clear();
}

// Capture and broadcast a pad's world dock pose, byte-identical to the ship's CapturePose so the
// pose round-trips: RC position/forward/up + the connector's forward (points into the dock).
void SendPad(string name, IMyShipConnector c)
{
    Send("CMD|PAD|" + name + "|" + Vec(teachRc.GetPosition()) + "|" + Vec(teachRc.WorldMatrix.Forward)
       + "|" + Vec(teachRc.WorldMatrix.Up) + "|" + Vec(c.WorldMatrix.Forward));
}

// Stream a breadcrumb list to the tower as chunked CMD|PADPATH messages (reuses PathStr).
void SendPath(string pad, List<Vector3D> path)
{
    int total = (path.Count + PATH_CHUNK - 1) / PATH_CHUNK;
    if (total == 0) return;
    for (int seq = 0; seq < total; seq++)
        Send("CMD|PADPATH|" + pad + "|" + seq + "|" + total + "|" + PathStr(path, seq * PATH_CHUNK, PATH_CHUNK));
}

// The ship's first connected connector, or null. Used to detect a dock and capture the pad pose.
IMyShipConnector ConnectedTeachConn()
{
    foreach (var c in teachConns) if (c.Status == MyShipConnectorStatus.Connected) return c;
    return null;
}

// Find the ship's Remote Control (config override or first on this grid) and its connectors.
// Re-runs lazily if the RC/connectors weren't built at compile time.
void DiscoverTeach()
{
    var grid = Me.CubeGrid;
    if (!string.IsNullOrEmpty(remoteName))
        teachRc = GridTerminalSystem.GetBlockWithName(remoteName) as IMyRemoteControl;
    if (teachRc == null)
    {
        var rcs = new List<IMyRemoteControl>();
        GridTerminalSystem.GetBlocksOfType(rcs, b => b.CubeGrid == grid);
        teachRc = rcs.Count > 0 ? rcs[0] : null;
    }
    teachConns.Clear();
    GridTerminalSystem.GetBlocksOfType(teachConns, b => b.CubeGrid == grid);
}

// Teach-mode status board: shows what's been captured and the live pose, on Me's surface and
// every tagged panel on this ship (same panel scan as the tower board).
void RenderTeach()
{
    var sb = new StringBuilder();
    sb.Append("== Skippy Teach v").Append(VERSION).Append(" ==\n");
    sb.Append("Ship setup helper (channel ").Append(channel).Append(")\n\n");

    if (teachRc == null) sb.Append("! No Remote Control found on this grid.\n");
    else
    {
        bool docked = ConnectedTeachConn() != null;
        sb.Append("Zone: ").Append(haveZone
            ? (zoneExt.X * 2).ToString("0") + "x" + (zoneExt.Y * 2).ToString("0") + "x" + (zoneExt.Z * 2).ToString("0") + "m defined"
            : "not set - run REGZONE").Append('\n');
        sb.Append("Dock: ").Append(docked ? "CONNECTED" : "free").Append('\n');
        if (recording)
            sb.Append("REC '").Append(recPad).Append("': ").Append(recPath.Count).Append(" wp (fly in + dock)\n");
        sb.Append('\n');
    }

    sb.Append(teachMsg).Append('\n');
    sb.Append("\nCommands: REGZONE [w h d] | REGPATH <pad> | REGPATH END/CANCEL | REGPAD <pad>\n");

    var text = sb.ToString();
    Echo(text);
    var panels = new List<IMyTextPanel>();
    GridTerminalSystem.GetBlocksOfType(panels, b => b.CubeGrid.IsSameConstructAs(Me.CubeGrid) && b.CustomName.Contains(lcdTag));
    foreach (var p in panels) { p.ContentType = ContentType.TEXT_AND_IMAGE; p.WriteText(text); }
    Me.GetSurface(0).ContentType = ContentType.TEXT_AND_IMAGE;
    Me.GetSurface(0).WriteText(text);
}

// ============================================================================
//  Config
// ============================================================================
void WriteConfigTemplate()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    WriteSection(ini);
    Me.CustomData = ini.ToString();
}

void WriteSection(MyIni ini)
{
    ini.Set("sf", "channel", channel);
    ini.Set("sf", "zone", zone);
    ini.Set("sf", "lcdTag", lcdTag);
    ini.Set("sf", "towerMode", mode == TowerMode.Board ? "board" : mode == TowerMode.Teach ? "teach" : "control");
    ini.Set("sf", "grant", manual ? "manual" : "auto");
    // Teach-mode keys (ignored in control/board): which RC to read, and breadcrumb tuning.
    ini.Set("sf", "remoteName", remoteName);
    ini.Set("sf", "teachSeg", teachSeg);
    ini.Set("sf", "teachTurn", teachTurn);
}

// Persist just the grant sub-mode on a runtime MANUAL/AUTO toggle, so it survives a
// recompile. Re-parse first to preserve every other key the operator may have set.
void SaveGrantMode()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("sf", "grant", manual ? "manual" : "auto");
    Me.CustomData = ini.ToString();
}

// Persist the pad registry as one [pad.<name>] section each (pose + interior path; occupancy is
// live). Re-parse first to keep [sf], [zone], and any operator keys intact. Runs on every upsert.
void SavePads()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    foreach (var pd in pads.Values)
    {
        string s = "pad." + pd.Name;
        ini.Set(s, "pos", Vec(pd.Pos));
        ini.Set(s, "fwd", Vec(pd.Fwd));
        ini.Set(s, "up", Vec(pd.Up));
        ini.Set(s, "connFwd", Vec(pd.ConnFwd));
        ini.Set(s, "path", JoinPath(pd.Path));
    }
    Me.CustomData = ini.ToString();
}

// Persist the holding zone as the [zone] section (center/fwd/up/ext). Re-parse first to keep
// [sf] and every [pad.<name>] section intact. Runs on every CMD|ZONE upsert.
void SaveZone()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("zone", "center", Vec(zoneCenter));
    ini.Set("zone", "fwd", Vec(zoneFwd));
    ini.Set("zone", "up", Vec(zoneUp));
    ini.Set("zone", "ext", Vec(zoneExt));
    Me.CustomData = ini.ToString();
}

// ';'-join a full point list for the [pad.*] path key (empty string for a path-less pad).
string JoinPath(List<Vector3D> path) { return path.Count == 0 ? "" : PathStr(path, 0, path.Count); }

void LoadConfig()
{
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData)) return;
    channel = ini.Get("sf", "channel").ToString(channel);
    zone = ini.Get("sf", "zone").ToString(zone);
    lcdTag = ini.Get("sf", "lcdTag").ToString(lcdTag);
    string tm = ini.Get("sf", "towerMode").ToString("control").Trim().ToLowerInvariant();
    mode = tm == "board" ? TowerMode.Board : tm == "teach" ? TowerMode.Teach : TowerMode.Control;
    manual = ini.Get("sf", "grant").ToString("auto").Trim().ToLowerInvariant() == "manual";
    remoteName = ini.Get("sf", "remoteName").ToString(remoteName);
    teachSeg = ini.Get("sf", "teachSeg").ToDouble(teachSeg);
    teachTurn = ini.Get("sf", "teachTurn").ToDouble(teachTurn);

    // Restore the holding zone if a [zone] section is present (all four vectors must parse).
    haveZone = false;
    Vector3D zc, zf, zu, ze;
    if (TryVec(ini.Get("zone", "center").ToString(), out zc)
      & TryVec(ini.Get("zone", "fwd").ToString(), out zf)
      & TryVec(ini.Get("zone", "up").ToString(), out zu)
      & TryVec(ini.Get("zone", "ext").ToString(), out ze))
    {
        zoneCenter = zc; zoneFwd = zf; zoneUp = zu; zoneExt = ze; haveZone = true;
    }

    // Restore every [pad.<name>] section into the registry (all free at boot).
    pads.Clear();
    var secs = new List<string>();
    ini.GetSections(secs);
    foreach (var s in secs)
    {
        if (!s.StartsWith("pad.")) continue;
        string name = s.Substring(4);
        Vector3D p, fw, u, c;
        if (TryVec(ini.Get(s, "pos").ToString(), out p)
          & TryVec(ini.Get(s, "fwd").ToString(), out fw)
          & TryVec(ini.Get(s, "up").ToString(), out u)
          & TryVec(ini.Get(s, "connFwd").ToString(), out c))
        {
            var pd = new Pad { Name = name, Pos = p, Fwd = fw, Up = u, ConnFwd = c };
            ParsePath(ini.Get(s, "path").ToString(), pd.Path);
            pads[name] = pd;
        }
    }
}

// ============================================================================
//  Small parse helpers (mirrors SkippyFlight)
// ============================================================================
int ParseInt(string s, int def) { int r; return int.TryParse(s, out r) ? r : def; }
long ParseLong(string s, long def) { long r; return long.TryParse(s, out r) ? r : def; }
double ParseDouble(string s, double def) { double r; return double.TryParse(s, out r) ? r : def; }

// Vector <-> "X:Y:Z" string, matching SkippyFlight's Vec/TryVec so a pose recorded on a
// ship round-trips through this tower's Custom Data and back to a ship byte-for-byte.
string Vec(Vector3D v) { return v.X.ToString("R") + ":" + v.Y.ToString("R") + ":" + v.Z.ToString("R"); }
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

    }
}
