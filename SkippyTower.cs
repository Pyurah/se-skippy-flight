/*//////////////////////////////////////////////////////////////////////////////
 * SkippyTower - Active traffic-control tower for the SkippyFlight shuttle fleet.
 * A separate Programmable Block script (its OWN char budget) that a station PB runs
 * to serialize arrivals and departures: only one craft maneuvers at the station at a
 * time, so two shuttles never both undock into, or both taxi onto, the same corridor.
 *
 * It is a SUPERSET of the SkippyFlight base/board role - it renders the same status
 * board AND, in control mode, actively clears traffic. Full docs in README.md.
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
 *   receive  CMD|REQ|<ship>|<DEPART|LAND>|<dock>      (ship asks; re-sent while waiting)
 *   emit     CMD|TOWER|<zone>                         (heartbeat; control mode only)
 *   emit     CMD|CLEAR|<ship>|<DEPART|LAND>           (grant to the current slot holder)
 *   emit     CMD|HOLD|<ship>|<DEPART|LAND>|traffic    (deny a queued ship, with reason)
 * The tower must echo the EXACT action string it received (the ship matches on it).
 *
 * SERIALIZATION (minimal core): a single global slot. One craft is cleared at a time;
 * a waiting LAND is served before a waiting DEPART (a landing ship burns fuel holding,
 * a docked ship can wait). The slot is held from grant until the granted ship's own
 * status report shows it has cleared the shared resource (departed to cruise / docked),
 * with a safety release if the ship is lost, faults, or overruns GRANT_MAX_SEC.
 *
 * CONFIG: the [sf] Custom Data section (auto-generated on first compile):
 *   channel   = SkippyShuttleNet   (must match the fleet)
 *   zone      = Main               (heartbeat label; informational)
 *   lcdTag    = [SF]               (board renders to Me's surface + every matching LCD
 *                                    on this station's construct; a docked ship's own
 *                                    [SF] panels are excluded)
 *   towerMode = control | board    (board = passive status board, NO heartbeat, so the
 *                                    fleet stays independent - a drop-in for role=base)
 *   grant     = auto | manual      (control-mode sub-mode: auto clears the best waiting
 *                                    craft every tick; manual holds all traffic until the
 *                                    operator approves each one by hand. Persisted on toggle)
 *
 * OPERATOR COMMANDS (run the PB with an argument, or bind to a button; control mode only):
 *   MANUAL          take the controls - stop auto-granting; hold every request for your OK
 *   AUTO            hand back - resume auto-granting the best waiting craft
 *   CLEAR           approve the top of the queue (best: a LAND before a DEPART, then oldest)
 *   CLEAR <ship>    approve a specific waiting ship by name (queue-jump)
 *   RELEASE         force-free the current slot now (manual deadlock breaker, no 180 s wait)
 * The heartbeat keeps beating in BOTH sub-modes, so a ship held for manual approval stays
 * controlled (never reverts to independent) while the operator deliberates.
 *
 * NOTES: pose-based pad assignment (a shared pad bank) is intentionally NOT in this
 * build - CMD|CLEAR omits the reserved pose field. Version tracked in CHANGELOG.md
 * (project semver; the shuttle script versions independently over the same protocol).
 *//////////////////////////////////////////////////////////////////////////////

const string VERSION = "0.11.0";

// ---- Mode ------------------------------------------------------------------
// Control  - emit the heartbeat and serialize clearances (an active tower).
// Board    - render the status board only; NO heartbeat, so every ship stays
//            independent. Byte-for-byte the passive board behaviour of role=base.
enum TowerMode { Control, Board }

// ---- Config (all live in the [sf] Custom Data section) ---------------------
string channel = "SkippyShuttleNet";   // IGC channel; must match the fleet's
string zone = "Main";                    // broadcast in the heartbeat; ships ignore the content, it is operator-facing only
string lcdTag = "[SF]";                  // board is written to every LCD on this construct whose name contains this tag (plus Me's own surface); a docked ship's panels are excluded
TowerMode mode = TowerMode.Control;      // active controller vs passive board
bool manual = false;                     // control sub-mode: false = auto-grant best each tick; true = operator approves each CLEAR. Toggled at runtime (MANUAL/AUTO), persisted to Custom Data. Ignored in board mode.

// ---- Timing constants ------------------------------------------------------
const double HEARTBEAT_SEC = 2.0;        // interval between CMD|TOWER beats; well under the ships' TOWER_TIMEOUT (6 s) so a beat or two can be lost
const double REQ_STALE_SEC = 6.0;        // drop a pending request not re-sent within this - the ship stopped waiting (cleared elsewhere, STOPped, or gone)
const double GRANT_MAX_SEC = 180.0;      // anti-deadlock: force-release a slot whose holder never reports clearing the resource
const double SIGNAL_STALE_SEC = 20.0;    // a fleet entry older than this reads as "NO SIGNAL" (matches the base board)
const double DT_FALLBACK = 1.0 / 6.0;    // assumed tick length when TimeSinceLastRun is unusable (first tick / long pause)

// ---- Runtime state ---------------------------------------------------------
IMyBroadcastListener listener;
double dt;                               // real seconds elapsed this tick
double hbTimer;                          // seconds since the last heartbeat
long seqCounter;                         // monotonic request sequence, for FIFO ordering

// One craft cleared into the shared corridor/pad at a time.
string activeShip = "";
string activeAction = "";                // "DEPART" or "LAND"
double grantAge;                         // seconds the current grant has been outstanding

// Every ship heard on the channel, keyed by name (status board + phase source).
Dictionary<string, ShuttleReport> fleet = new Dictionary<string, ShuttleReport>();
// Ships waiting for the slot, keyed by name. The active ship is NOT kept here.
Dictionary<string, PendingReq> pending = new Dictionary<string, PendingReq>();

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

// ============================================================================
//  Lifecycle
// ============================================================================
Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;   // ~6 Hz; ample for a 2 s heartbeat and 6 s timeouts
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();
    LoadConfig();
    listener = IGC.RegisterBroadcastListener(channel);
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

    if (!string.IsNullOrEmpty(argument)) HandleCommand(argument.Trim());

    DrainMessages();
    AgeTables();

    if (mode == TowerMode.Control)
    {
        ReleaseIfDone();              // auto slot-release stays ON in both sub-modes (anti-deadlock)
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
            // CMD|REQ|<ship>|<action>|<dock>
            if (mode == TowerMode.Control && f[1] == "REQ" && f.Length >= 5)
                OnRequest(f[2], f[3]);
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
void OnRequest(string ship, string action)
{
    if (ship == activeShip)
    {
        Send("CMD|CLEAR|" + ship + "|" + activeAction);
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

    if (activeShip.Length > 0)
        Send("CMD|HOLD|" + ship + "|" + action + "|traffic");
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

// Grant the slot to the best waiting ship: a LAND outranks a DEPART (a holding ship
// burns fuel; a docked one can wait), then oldest-first (lowest Seq) within a class.
void GrantNext()
{
    if (activeShip.Length > 0 || pending.Count == 0) return;

    string bestShip = null;
    PendingReq best = null;
    foreach (var kv in pending)
    {
        if (best == null || Better(kv.Value, best)) { best = kv.Value; bestShip = kv.Key; }
    }
    if (bestShip == null) return;

    activeShip = bestShip;
    activeAction = best.Action;
    grantAge = 0;
    pending.Remove(bestShip);
    Send("CMD|CLEAR|" + activeShip + "|" + activeAction);
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
// jumping the queue. No-op if the slot is busy, the queue is empty, or the name is unknown.
void ManualGrant(string ship)
{
    if (activeShip.Length > 0 || pending.Count == 0) return;
    if (ship == null) { GrantNext(); return; }

    PendingReq p;
    if (!pending.TryGetValue(ship, out p)) return;
    activeShip = ship;
    activeAction = p.Action;
    grantAge = 0;
    pending.Remove(ship);
    Send("CMD|CLEAR|" + activeShip + "|" + activeAction);
}

void ClearSlot()
{
    activeShip = "";
    activeAction = "";
    grantAge = 0;
}

void Heartbeat()
{
    hbTimer += dt;
    if (hbTimer >= HEARTBEAT_SEC) { Send("CMD|TOWER|" + zone); hbTimer = 0; }
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
    ini.Set("sf", "towerMode", mode == TowerMode.Board ? "board" : "control");
    ini.Set("sf", "grant", manual ? "manual" : "auto");
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

void LoadConfig()
{
    var ini = new MyIni();
    if (!ini.TryParse(Me.CustomData)) return;
    channel = ini.Get("sf", "channel").ToString(channel);
    zone = ini.Get("sf", "zone").ToString(zone);
    lcdTag = ini.Get("sf", "lcdTag").ToString(lcdTag);
    mode = ini.Get("sf", "towerMode").ToString("control").Trim().ToLowerInvariant() == "board"
         ? TowerMode.Board : TowerMode.Control;
    manual = ini.Get("sf", "grant").ToString("auto").Trim().ToLowerInvariant() == "manual";
}

// ============================================================================
//  Small parse helpers (mirrors SkippyFlight)
// ============================================================================
int ParseInt(string s, int def) { int r; return int.TryParse(s, out r) ? r : def; }
double ParseDouble(string s, double def) { double r; return double.TryParse(s, out r) ? r : def; }
