const string VERSION = "0.13.0";
enum TowerMode { Control, Board, Teach }
string channel = "SkippyShuttleNet";
string zone = "Main";
string lcdTag = "[SF]";
TowerMode mode = TowerMode.Control;
bool manual = false;
string remoteName = "";
double teachSeg = 2.5;
double teachTurn = 12.0;
const double HEARTBEAT_SEC = 2.0;
const double REQ_STALE_SEC = 6.0;
const double GRANT_MAX_SEC = 180.0;
const double SIGNAL_STALE_SEC = 20.0;
const double DT_FALLBACK = 1.0 / 6.0;
const int PATH_CHUNK = 18;
IMyBroadcastListener listener;
double dt;
double hbTimer;
long seqCounter;
long myGrid;
string activeShip = "";
string activeAction = "";
double grantAge;
string activePad = "";
Dictionary<string, ShuttleReport> fleet = new Dictionary<string, ShuttleReport>();
Dictionary<string, PendingReq> pending = new Dictionary<string, PendingReq>();
Dictionary<string, Pad> pads = new Dictionary<string, Pad>();
bool haveZone;
Vector3D zoneCenter, zoneFwd, zoneUp, zoneExt;
Dictionary<string, List<Vector3D>> padPathRx = new Dictionary<string, List<Vector3D>>();
const int MAX_TEACH_PATH = 400;
IMyRemoteControl teachRc;
List<IMyShipConnector> teachConns = new List<IMyShipConnector>();
bool recording;
string recPad = "";
List<Vector3D> recPath = new List<Vector3D>();
Vector3D recLast;
Vector3D recLastDir;
bool wasDocked;
string teachMsg = "Ready. REGZONE / REGPAD / REGPATH to begin.";
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
class Pad
{
    public string Name;
    public Vector3D Pos, Fwd, Up, ConnFwd;
    public string OccupiedBy = "";
    public List<Vector3D> Path = new List<Vector3D>();
}
Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    myGrid = Me.CubeGrid.EntityId;
    if (string.IsNullOrWhiteSpace(Me.CustomData)) WriteConfigTemplate();
    LoadConfig();
    if (mode == TowerMode.Teach) DiscoverTeach();
    else listener = IGC.RegisterBroadcastListener(channel);
}
void Main(string argument, UpdateType source)
{
    dt = Runtime.TimeSinceLastRun.TotalSeconds;
    if (dt <= 0 || dt > 0.5) dt = DT_FALLBACK;
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
        ReleaseIfDone();
        ReleasePads();
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
    else if (verb.StartsWith("PADFREE ")) FreePad(arg.Substring(8).Trim());
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
                OnRequest(f[2], f[3], f.Length >= 6 ? ParseLong(f[5], 0) : 0);
            else if (f[1] == "PAD" && f.Length >= 7)
                UpsertPad(f[2], f[3], f[4], f[5], f[6]);
            else if (f[1] == "ZONE" && f.Length >= 6)
                UpsertZone(f[2], f[3], f[4], f[5]);
            else if (f[1] == "PADPATH" && f.Length >= 6)
                OnPadPathChunk(f[2], f[3], f[4], f[5]);
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
void OnRequest(string ship, string action, long grid)
{
    if (grid != 0 && myGrid != 0 && grid != myGrid) return;
    if (ship == activeShip)
    {
        Send(ClearMsg());
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
    else if (action == "LAND" && pads.Count > 0 && FirstFreePad() == null)
        Send("CMD|HOLD|" + ship + "|" + action + "|no pad");
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
        if (!Grantable(kv.Value.Action)) continue;
        if (best == null || Better(kv.Value, best)) { best = kv.Value; bestShip = kv.Key; }
    }
    if (bestShip == null) return;
    Grant(bestShip, best.Action);
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
    if (!Grantable(p.Action)) return;
    Grant(ship, p.Action);
}
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
        else activePad = "";
    }
    Send(ClearMsg());
    StreamGrantPath(ship, action);
}
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
void StreamPath(string ship, List<Vector3D> path)
{
    int total = (path.Count + PATH_CHUNK - 1) / PATH_CHUNK;
    if (total == 0) return;
    for (int seq = 0; seq < total; seq++)
        Send("CMD|PATH|" + ship + "|" + seq + "|" + total + "|" + PathStr(path, seq * PATH_CHUNK, PATH_CHUNK));
}
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
void ParsePath(string payload, List<Vector3D> buf)
{
    if (string.IsNullOrEmpty(payload)) return;
    var parts = payload.Split(';');
    Vector3D v;
    foreach (var pt in parts) if (TryVec(pt, out v)) buf.Add(v);
}
bool Grantable(string action)
{
    return action != "LAND" || pads.Count == 0 || FirstFreePad() != null;
}
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
string FirstFreePad()
{
    foreach (var pd in pads.Values)
        if (pd.OccupiedBy.Length == 0) return pd.Name;
    return null;
}
void ReleasePads()
{
    foreach (var pd in pads.Values)
    {
        if (pd.OccupiedBy.Length == 0) continue;
        ShuttleReport r;
        if (!fleet.TryGetValue(pd.OccupiedBy, out r) || r.Age > SIGNAL_STALE_SEC) pd.OccupiedBy = "";
        else if (r.State == "Faulted" || r.State == "Idle") pd.OccupiedBy = "";
        else if (IsCruiseState(r.State)) pd.OccupiedBy = "";
    }
}
void UpsertPad(string name, string pos, string fwd, string up, string cf)
{
    Vector3D p, fw, u, c;
    if (!(TryVec(pos, out p) & TryVec(fwd, out fw) & TryVec(up, out u) & TryVec(cf, out c))) return;
    Pad pd;
    if (!pads.TryGetValue(name, out pd)) { pd = new Pad { Name = name }; pads[name] = pd; }
    pd.Pos = p; pd.Fwd = fw; pd.Up = u; pd.ConnFwd = c;
    SavePads();
}
void UpsertZone(string center, string fwd, string up, string ext)
{
    Vector3D c, fw, u, e;
    if (!(TryVec(center, out c) & TryVec(fwd, out fw) & TryVec(up, out u) & TryVec(ext, out e))) return;
    zoneCenter = c; zoneFwd = fw; zoneUp = u; zoneExt = e;
    haveZone = true;
    SaveZone();
}
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
    activePad = "";
}
void Heartbeat()
{
    hbTimer += dt;
    if (hbTimer < HEARTBEAT_SEC) return;
    hbTimer = 0;
    string m = "CMD|TOWER|" + zone + "|" + myGrid;
    if (haveZone)
        m += "|" + Vec(zoneCenter) + "|" + Vec(zoneFwd) + "|" + Vec(zoneUp) + "|" + Vec(zoneExt);
    Send(m);
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
void RunTeach(string arg)
{
    if (teachRc == null || teachConns.Count == 0) DiscoverTeach();
    if (teachRc == null) { teachMsg = "! No Remote Control on this grid - teach needs one."; return; }
    if (arg.Length > 0) HandleTeachCommand(arg);
    if (recording)
    {
        if (recPath.Count < MAX_TEACH_PATH) TickTeachRecord();
        bool docked = ConnectedTeachConn() != null;
        if (docked && !wasDocked) FinalizePath();
        wasDocked = docked;
    }
}
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
void RegPad(string name)
{
    var c = ConnectedTeachConn();
    if (c == null) { teachMsg = "REGPAD: dock at pad '" + name + "' first."; return; }
    SendPad(name, c);
    teachMsg = "Pad '" + name + "' pose sent (open-air).";
}
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
void TickTeachRecord()
{
    Vector3D p = teachRc.GetPosition();
    double moved = Vector3D.Distance(p, recLast);
    if (moved < 0.5) return;
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
void SendPad(string name, IMyShipConnector c)
{
    Send("CMD|PAD|" + name + "|" + Vec(teachRc.GetPosition()) + "|" + Vec(teachRc.WorldMatrix.Forward)
       + "|" + Vec(teachRc.WorldMatrix.Up) + "|" + Vec(c.WorldMatrix.Forward));
}
void SendPath(string pad, List<Vector3D> path)
{
    int total = (path.Count + PATH_CHUNK - 1) / PATH_CHUNK;
    if (total == 0) return;
    for (int seq = 0; seq < total; seq++)
        Send("CMD|PADPATH|" + pad + "|" + seq + "|" + total + "|" + PathStr(path, seq * PATH_CHUNK, PATH_CHUNK));
}
IMyShipConnector ConnectedTeachConn()
{
    foreach (var c in teachConns) if (c.Status == MyShipConnectorStatus.Connected) return c;
    return null;
}
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
    ini.Set("sf", "remoteName", remoteName);
    ini.Set("sf", "teachSeg", teachSeg);
    ini.Set("sf", "teachTurn", teachTurn);
}
void SaveGrantMode()
{
    var ini = new MyIni();
    ini.TryParse(Me.CustomData);
    ini.Set("sf", "grant", manual ? "manual" : "auto");
    Me.CustomData = ini.ToString();
}
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
    haveZone = false;
    Vector3D zc, zf, zu, ze;
    if (TryVec(ini.Get("zone", "center").ToString(), out zc)
      & TryVec(ini.Get("zone", "fwd").ToString(), out zf)
      & TryVec(ini.Get("zone", "up").ToString(), out zu)
      & TryVec(ini.Get("zone", "ext").ToString(), out ze))
    {
        zoneCenter = zc; zoneFwd = zf; zoneUp = zu; zoneExt = ze; haveZone = true;
    }
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
int ParseInt(string s, int def) { int r; return int.TryParse(s, out r) ? r : def; }
long ParseLong(string s, long def) { long r; return long.TryParse(s, out r) ? r : def; }
double ParseDouble(string s, double def) { double r; return double.TryParse(s, out r) ? r : def; }
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
