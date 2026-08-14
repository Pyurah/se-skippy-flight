const string VERSION = "0.11.0";
enum TowerMode { Control, Board }
string channel = "SkippyShuttleNet";
string zone = "Main";
string lcdTag = "[SF]";
TowerMode mode = TowerMode.Control;
bool manual = false;
const double HEARTBEAT_SEC = 2.0;
const double REQ_STALE_SEC = 6.0;
const double GRANT_MAX_SEC = 180.0;
const double SIGNAL_STALE_SEC = 20.0;
const double DT_FALLBACK = 1.0 / 6.0;
IMyBroadcastListener listener;
double dt;
double hbTimer;
long seqCounter;
string activeShip = "";
string activeAction = "";
double grantAge;
Dictionary<string, ShuttleReport> fleet = new Dictionary<string, ShuttleReport>();
Dictionary<string, PendingReq> pending = new Dictionary<string, PendingReq>();
class ShuttleReport
{
    public string Name, State;
    public int EtaSec, DistM, Fill;
    public double MassT;
    public bool Running;
    public double Age;
}
class PendingReq
{
    public string Action;
    public double Age;
    public long Seq;
}
Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();
    LoadConfig();
    listener = IGC.RegisterBroadcastListener(channel);
}
void Main(string argument, UpdateType source)
{
    dt = Runtime.TimeSinceLastRun.TotalSeconds;
    if (dt <= 0 || dt > 0.5) dt = DT_FALLBACK;
    if (!string.IsNullOrEmpty(argument)) HandleCommand(argument.Trim());
    DrainMessages();
    AgeTables();
    if (mode == TowerMode.Control)
    {
        ReleaseIfDone();
        if (!manual) GrantNext();
        Heartbeat();
    }
    RenderBoard();
}
void HandleCommand(string arg)
{
    string verb = arg.ToUpperInvariant();
    if (verb == "MANUAL")               { manual = true;  SaveGrantMode(); }
    else if (verb == "AUTO")            { manual = false; SaveGrantMode(); }
    else if (verb == "RELEASE")         ClearSlot();
    else if (verb == "CLEAR")           ManualGrant(null);
    else if (verb.StartsWith("CLEAR ")) ManualGrant(arg.Substring(6).Trim());
}
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
            if (mode == TowerMode.Control && f[1] == "REQ" && f.Length >= 5)
                OnRequest(f[2], f[3]);
            continue;
        }
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
void ReleaseIfDone()
{
    if (activeShip.Length == 0) return;
    grantAge += dt;
    bool done = false;
    ShuttleReport r;
    if (!fleet.TryGetValue(activeShip, out r) || r.Age > SIGNAL_STALE_SEC)
        done = true;
    else if (r.State == "Faulted" || r.State == "Idle")
        done = true;
    else if (activeAction == "DEPART" && IsCruiseState(r.State))
        done = true;
    else if (activeAction == "LAND" && IsDockedState(r.State))
        done = true;
    else if (grantAge > GRANT_MAX_SEC)
        done = true;
    if (done) ClearSlot();
}
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
bool Better(PendingReq a, PendingReq b)
{
    bool landA = a.Action == "LAND", landB = b.Action == "LAND";
    if (landA != landB) return landA;
    return a.Seq < b.Seq;
}
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
    GridTerminalSystem.GetBlocksOfType(panels, b => b.CubeGrid.IsSameConstructAs(Me.CubeGrid) && b.CustomName.Contains(lcdTag));
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
int ParseInt(string s, int def) { int r; return int.TryParse(s, out r) ? r : def; }
double ParseDouble(string s, double def) { double r; return double.TryParse(s, out r) ? r : def; }
