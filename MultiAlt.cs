// Multi Alt by Triple DDD - run many Roblox accounts at once, watch them all live, click one to play it.
// Build (no SDK needed, uses the compiler built into Windows):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:logo.ico /resource:logo.png,logo.png
//     /r:System.Security.dll /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /out:MultiAlt.exe MultiAlt.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

public class Account
{
    public string Name { get; set; }        // nickname
    public string Cookie { get; set; }
    public long UserId { get; set; }
    public string Username { get; set; }
    public string DisplayName { get; set; }
    public string Password { get; set; }     // optional, only if the user saves it for "Copy combo"; encrypted with the rest
}

public class AppSettings
{
    public int RamCapGb { get; set; }       // 0 = no cap
    public int FpsCap { get; set; }         // 0 = Roblox default
    public bool LowPriorityBackground { get; set; }
    public int LaunchDelaySec { get; set; }
    public string PlaceId { get; set; }
    public bool Windowed { get; set; }
    public bool AntiAfkEnabled { get; set; }   // off by default (new name so older builds' "on" default is dropped)
    public AppSettings() { LowPriorityBackground = true; LaunchDelaySec = 5; PlaceId = ""; }
}

class Client { public int Pid; public IntPtr Hwnd; public long Ram; public Account Account; }

static class Store
{
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string Dir = Path.Combine(AppData, "MultiAlt");
    public static readonly string AvatarDir = Path.Combine(Dir, "avatars");
    static readonly string AccountsFile = Path.Combine(Dir, "accounts.dat");
    static readonly string OldAccountsFile = Path.Combine(AppData, "MultiRoblox", "accounts.dat"); // from the first version
    static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    // Cookies are encrypted with Windows DPAPI: only this Windows user on this PC can decrypt them.
    public static byte[] Encode(List<Account> accounts)
    {
        return ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(accounts)), null, DataProtectionScope.CurrentUser);
    }

    public static List<Account> Decode(byte[] blob)
    {
        var text = Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        if (text.StartsWith("[")) return Json.Deserialize<List<Account>>(text);
        return text.Split('\n').Where(l => l.Contains('\t')) // old "name<TAB>cookie" format
            .Select(l => { var p = l.Split(new[] { '\t' }, 2); return new Account { Name = p[0], Cookie = p[1] }; }).ToList();
    }

    public static List<Account> Load()
    {
        var f = File.Exists(AccountsFile) ? AccountsFile : File.Exists(OldAccountsFile) ? OldAccountsFile : null;
        return f == null ? new List<Account>() : Decode(File.ReadAllBytes(f));
    }

    public static void Save(List<Account> accounts) { WriteSafely(AccountsFile, Encode(accounts)); }

    public static AppSettings LoadSettings()
    {
        try { if (File.Exists(SettingsFile)) return Json.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings(); }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings s) { WriteSafely(SettingsFile, Encoding.UTF8.GetBytes(Json.Serialize(s))); }

    // Write to a temp file then swap, so a crash mid-save never wipes hundreds of saved accounts.
    static void WriteSafely(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }
}

static class RobloxApi
{
    public static string Http(string url, string cookie)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "MultiAlt/1.0";
        req.Timeout = 15000;
        if (cookie != null) req.Headers.Add("Cookie", ".ROBLOSECURITY=" + cookie);
        try
        {
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) return sr.ReadToEnd();
        }
        catch (WebException ex)
        {
            var r = ex.Response as HttpWebResponse;
            if (r != null && (int)r.StatusCode == 401) throw new Exception("login expired or invalid - log in again.");
            throw;
        }
    }

    public static Account GetUser(string cookie)
    {
        var d = Store.Json.Deserialize<Dictionary<string, object>>(Http("https://users.roblox.com/v1/users/authenticated", cookie));
        return new Account { UserId = Convert.ToInt64(d["id"]), Username = (string)d["name"], DisplayName = (string)d["displayName"] };
    }

    // Headshots in batches of 100 (the API max), saved to avatars\<userId>.png.
    public static void FetchAvatars(IEnumerable<long> ids)
    {
        Directory.CreateDirectory(Store.AvatarDir);
        foreach (var batch in ids.Select((id, i) => new { id, i }).GroupBy(x => x.i / 100, x => x.id))
        {
            var d = Store.Json.Deserialize<Dictionary<string, object>>(Http(
                "https://thumbnails.roblox.com/v1/users/avatar-headshot?size=150x150&format=Png&isCircular=false&userIds=" + string.Join(",", batch), null));
            foreach (var o in (System.Collections.IEnumerable)d["data"])
            {
                var item = (Dictionary<string, object>)o;
                var url = item["imageUrl"] as string;
                if (string.IsNullOrEmpty(url)) continue;
                using (var wc = new WebClient()) wc.DownloadFile(url, Path.Combine(Store.AvatarDir, Convert.ToInt64(item["targetId"]) + ".png"));
            }
        }
    }

    // Exchanges the account's cookie for a one-time launch ticket (same flow the website's Play button uses).
    public static string GetTicket(string cookie)
    {
        // Roblox now wants a JSON body carrying a client assertion (fetched first, like the website does).
        string body = "{}";
        try
        {
            var d = Store.Json.Deserialize<Dictionary<string, object>>(Http("https://auth.roblox.com/v1/client-assertion/", cookie));
            object a;
            if (d != null && d.TryGetValue("clientAssertion", out a) && a is string) body = Store.Json.Serialize(new Dictionary<string, object> { { "clientAssertion", a } });
        }
        catch { } // older flow still works without it
        var bytes = Encoding.UTF8.GetBytes(body);

        string csrf = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var req = (HttpWebRequest)WebRequest.Create("https://auth.roblox.com/v1/authentication-ticket");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Accept = "application/json";
            req.UserAgent = "MultiAlt/1.0";
            req.Referer = "https://www.roblox.com/";
            req.Headers.Add("Cookie", ".ROBLOSECURITY=" + cookie);
            if (csrf != "") req.Headers.Add("X-CSRF-TOKEN", csrf);
            try
            {
                req.ContentLength = bytes.Length;
                using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    var ticket = resp.Headers["rbx-authentication-ticket"];
                    if (string.IsNullOrEmpty(ticket)) throw new Exception("Roblox returned no launch ticket.");
                    return ticket;
                }
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null && (int)r.StatusCode == 403 && csrf == "" && r.Headers["x-csrf-token"] != null)
                {
                    csrf = r.Headers["x-csrf-token"]; // first call always 403s to hand out the CSRF token
                    continue;
                }
                if (r != null && (int)r.StatusCode == 401) throw new Exception("login expired - remove and re-add this account.");
                throw;
            }
        }
        throw new Exception("could not get a launch ticket.");
    }

    public static string BuildUri(string ticket, string placeId)
    {
        var rng = new Random();
        var tracker = rng.Next(100000, 999999).ToString() + rng.Next(100000, 999999);
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var uri = "roblox-player:1+gameinfo:" + ticket + "+launchtime:" + time + "+browsertrackerid:" + tracker
                + "+robloxLocale:en_us+gameLocale:en_us+channel:";
        if (string.IsNullOrWhiteSpace(placeId)) return uri + "+launchmode:app";
        var launcher = "https://www.roblox.com/Game/PlaceLauncher.ashx?request=RequestGame&browserTrackerId=" + tracker
                     + "&placeId=" + placeId.Trim() + "&isPlayTogetherGame=false";
        return uri + "+launchmode:play+placelauncherurl:" + Uri.EscapeDataString(launcher) + "+LaunchExp:InApp";
    }
}

// "Login via Roblox": opens a throwaway Edge/Chrome profile on the Roblox login page and reads the login
// cookie through the browser's local debugging port once the user has signed in. The profile is deleted after.
static class BrowserLogin
{
    // Starts Chrome (or Edge) on its own profile with a local debugging port and returns the browser's control socket URL.
    static Process Start(string profile, string url, out string wsUrl)
    {
        var exe = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        }.FirstOrDefault(File.Exists);
        if (exe == null) throw new Exception("Google Chrome or Microsoft Edge is needed for this.");

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var browser = Process.Start(exe, "--user-data-dir=\"" + profile + "\" --remote-debugging-port=" + port
            + " --no-first-run --no-default-browser-check --new-window " + url);
        wsUrl = null;
        for (int i = 0; i < 60 && wsUrl == null; i++)
        {
            Thread.Sleep(500);
            try
            {
                var m = Regex.Match(RobloxApi.Http("http://127.0.0.1:" + port + "/json/version", null), "\"webSocketDebuggerUrl\"\\s*:\\s*\"([^\"]+)\"");
                if (m.Success) wsUrl = m.Groups[1].Value;
            }
            catch (WebException) { }
        }
        if (wsUrl == null) throw new Exception("the browser didn't start (is that account's browser window already open?).");
        return browser;
    }

    // Opens roblox.com logged in as this account, in a browser profile kept just for that account.
    public static void OpenAs(Account a)
    {
        var profile = Path.Combine(Store.Dir, "browser", a.UserId > 0 ? a.UserId.ToString() : a.Name);
        string wsUrl;
        Start(profile, "about:blank", out wsUrl);
        using (var ws = new ClientWebSocket())
        {
            ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait();
            var cookie = new Dictionary<string, object> { { "name", ".ROBLOSECURITY" }, { "value", a.Cookie }, { "domain", ".roblox.com" }, { "path", "/" }, { "secure", true }, { "httpOnly", true } };
            Send(ws, "{\"id\":1,\"method\":\"Storage.setCookies\",\"params\":" + Store.Json.Serialize(new Dictionary<string, object> { { "cookies", new[] { cookie } } }) + "}");
            Receive(ws, 1);
            Send(ws, "{\"id\":2,\"method\":\"Target.getTargets\"}");
            var blank = Regex.Match(Receive(ws, 2), "\"targetId\":\"(\\w+)\",\"type\":\"page\",\"title\":\"about:blank\"");
            Send(ws, "{\"id\":3,\"method\":\"Target.createTarget\",\"params\":{\"url\":\"https://www.roblox.com/home\"}}");
            Receive(ws, 3);
            if (blank.Success) { Send(ws, "{\"id\":4,\"method\":\"Target.closeTarget\",\"params\":{\"targetId\":\"" + blank.Groups[1].Value + "\"}}"); Receive(ws, 4); }
        }
    }

    public static string Run()
    {
        var profile = Path.Combine(Path.GetTempPath(), "MultiAltLogin_" + Guid.NewGuid().ToString("N"));
        string wsUrl;
        var browser = Start(profile, "https://www.roblox.com/login", out wsUrl);
        try
        {
            using (var ws = new ClientWebSocket())
            {
                ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None).Wait();
                var deadline = DateTime.Now.AddMinutes(10);
                for (int id = 1; DateTime.Now < deadline; id++)
                {
                    Send(ws, "{\"id\":" + id + ",\"method\":\"Storage.getCookies\"}");
                    var cookie = ExtractCookie(Receive(ws, id));
                    if (cookie != null)
                    {
                        try { Send(ws, "{\"id\":999999,\"method\":\"Browser.close\"}"); } catch { }
                        return cookie;
                    }
                    Thread.Sleep(1500);
                }
                throw new Exception("login timed out.");
            }
        }
        catch (AggregateException) { throw new Exception("the login window was closed."); }
        catch (WebSocketException) { throw new Exception("the login window was closed."); }
        finally
        {
            try { if (!browser.HasExited) browser.WaitForExit(5000); if (!browser.HasExited) browser.Kill(); } catch { }
            for (int i = 0; i < 10; i++) // browser helper processes hold the profile for a moment after exit
            {
                try { if (Directory.Exists(profile)) Directory.Delete(profile, true); break; }
                catch { Thread.Sleep(500); }
            }
        }
    }

    public static string ExtractCookie(string cdpJson)
    {
        var m = Regex.Match(cdpJson, "\"name\":\"\\.ROBLOSECURITY\",\"value\":\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    static void Send(ClientWebSocket ws, string msg)
    {
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
    }

    static string Receive(ClientWebSocket ws, int id)
    {
        var buf = new byte[65536];
        while (true)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                if (r.MessageType == WebSocketMessageType.Close) throw new Exception("the login window was closed.");
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);
            var s = Encoding.UTF8.GetString(ms.ToArray());
            if (s.StartsWith("{\"id\":" + id + ",") || s.StartsWith("{\"id\":" + id + "}")) return s;
        }
    }
}

// Roblox reads FFlags from ClientSettings\ClientAppSettings.json next to each installed client version.
static class FpsTweak
{
    public static int Apply(int fps)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
        if (!Directory.Exists(root)) return 0;
        int n = 0;
        foreach (var dir in Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe"))))
        {
            try
            {
                var f = Path.Combine(dir, "ClientSettings", "ClientAppSettings.json");
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                var d = (File.Exists(f) ? Store.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f)) : null) ?? new Dictionary<string, object>();
                if (fps > 0) d["DFIntTaskSchedulerTargetFps"] = fps; else d.Remove("DFIntTaskSchedulerTargetFps");
                File.WriteAllText(f, Store.Json.Serialize(d));
                n++;
            }
            catch { } // a broken user-edited file is left alone
        }
        return n;
    }
}

static class Grid
{
    // Picks the column count that makes the tiles biggest while all n fit in W x H (16:9 tiles + a label strip).
    public static List<Rectangle> Fit(int n, int W, int H, int gap, int label, int maxW)
    {
        var cells = new List<Rectangle>();
        if (n == 0) return cells;
        int bestCols = 1; double bestW = 0;
        for (int c = 1; c <= n; c++)
        {
            int rows = (n + c - 1) / c;
            double w = Math.Min((W - gap * (c + 1)) / (double)c, ((H - gap * (rows + 1)) / (double)rows - label) * 16 / 9.0);
            if (w > bestW) { bestW = w; bestCols = c; }
        }
        bestW = Math.Min(bestW, maxW); // a few clients shouldn't each fill the whole screen
        int tw = Math.Max(1, (int)bestW), th = (int)(tw * 9 / 16.0) + label;
        for (int i = 0; i < n; i++)
            cells.Add(new Rectangle(gap + (i % bestCols) * (tw + gap), gap + (i / bestCols) * (th + gap), tw, th));
        return cells;
    }
}

static class Tiler
{
    // Splits n windows round-robin across screens, then into a near-square grid on each screen.
    public static List<Rectangle> Tile(int n, Rectangle[] screens)
    {
        var result = new Rectangle[n];
        for (int s = 0; s < screens.Length; s++)
        {
            var idx = Enumerable.Range(0, n).Where(i => i % screens.Length == s).ToList();
            if (idx.Count == 0) continue;
            int cols = (int)Math.Ceiling(Math.Sqrt(idx.Count)), rows = (int)Math.Ceiling(idx.Count / (double)cols);
            var a = screens[s];
            for (int k = 0; k < idx.Count; k++)
                result[idx[k]] = new Rectangle(a.X + (k % cols) * a.Width / cols, a.Y + (k / cols) * a.Height / rows, a.Width / cols, a.Height / rows);
        }
        return result.ToList();
    }
}

static class Theme
{
    public static readonly Color Bg = Hex("#0d0f14"), Side = Hex("#12151c"), Card = Hex("#1a1e27"), Card2 = Hex("#262b38"),
        Fg = Hex("#e8ecf3"), Muted = Hex("#8b93a5"), Gold = Hex("#f6b73c"), Blue = Hex("#4cc2ff"), Red = Hex("#ff5c6c"), Green = Hex("#3ddc84");
    public static float S = 1f;
    public static int D(int px) { return (int)Math.Round(px * S); }
    static Color Hex(string h) { return ColorTranslator.FromHtml(h); }

    public static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRound(Graphics g, Color c, Rectangle r, int rad)
    {
        using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
    }

    public static Button Btn(string text, Color back, EventHandler click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = back, UseVisualStyleBackColor = false,
            ForeColor = back == Gold || back == Blue ? Bg : Fg, Font = new Font("Segoe UI Semibold", 10f), Cursor = Cursors.Hand,
            Padding = new Padding(D(10), D(3), D(10), D(3)), Margin = new Padding(0, 0, D(8), D(8)),
        };
        b.FlatAppearance.BorderSize = 0;
        if (click != null) b.Click += click;
        return b;
    }

    public static Label Lbl(string text, float size, Color color, bool bold = false)
    {
        return new Label { Text = text, AutoSize = true, ForeColor = color, BackColor = Color.Transparent, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size) };
    }
}

class DBPanel : Panel { public DBPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

class NavItem : Control
{
    public bool Selected;
    public string Count = "";
    public Color? TextColor;
    public NavItem(string text) { Text = text; Cursor = Cursors.Hand; DoubleBuffered = true; Font = new Font("Segoe UI Semibold", 11f); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Side);
        var r = new Rectangle(Theme.D(10), Theme.D(3), Width - Theme.D(20), Height - Theme.D(6));
        if (Selected)
        {
            Theme.FillRound(g, Theme.Card, r, Theme.D(8));
            Theme.FillRound(g, Theme.Gold, new Rectangle(r.X, r.Y + Theme.D(10), Theme.D(4), r.Height - Theme.D(20)), Theme.D(2));
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        TextRenderer.DrawText(g, Text, Font, new Rectangle(r.X + Theme.D(18), r.Y, r.Width, r.Height), Selected ? Theme.Gold : TextColor ?? Theme.Fg, flags);
        TextRenderer.DrawText(g, Count, Font, new Rectangle(r.X, r.Y, r.Width - Theme.D(14), r.Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
    }
}

class MainForm : Form
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct PSIZE { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct ThumbProps { public int Flags; public RECT Dest; public RECT Src; public byte Opacity; public bool Visible; public bool ClientOnly; }
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out PSIZE size);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new IntPtr(1);
    readonly HashSet<IntPtr> tamed = new HashSet<IntPtr>(); // Roblox windows already hidden from the taskbar
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    // Every visible, unowned top-level window of these processes: those are the ones that get taskbar buttons.
    // One EnumWindows pass for all clients; Process.MainWindowHandle would do a full pass per process (slow with hundreds).
    static List<IntPtr> TopWindows(HashSet<int> pids, Dictionary<int, IntPtr> mainByPid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, l) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pids.Contains((int)pid) && IsWindowVisible(h) && GetWindow(h, 4 /*GW_OWNER*/) == IntPtr.Zero)
            {
                found.Add(h);
                if (!mainByPid.ContainsKey((int)pid)) mainByPid[(int)pid] = h; // topmost in z-order, same pick as MainWindowHandle
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Tool-window style keeps a window off the taskbar and Alt-Tab; it only takes effect across a hide/show.
    static void SetTaskbarHidden(IntPtr h, bool hidden)
    {
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        int want = hidden ? (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW : (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
        if (want == ex) return;
        ShowWindow(h, 0);            // SW_HIDE
        SetWindowLong(h, GWL_EXSTYLE, want);
        ShowWindow(h, 4);            // SW_SHOWNOACTIVATE
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    IntPtr playing; // the client currently shown over Home, if any (UI thread only)

    // Off every monitor but not minimized, so DWM keeps drawing its Home preview; small, so the GPU renders fewer pixels.
    static void Park(IntPtr h)
    {
        if (IsIconic(h) || IsZoomed(h)) ShowWindow(h, 4); // SW_SHOWNOACTIVATE restores it
        var vs = SystemInformation.VirtualScreen;
        SetWindowPos(h, HWND_BOTTOM, vs.Right + 200, vs.Top, 800, 450, 0x0010 /*NOACTIVATE*/);
    }

    // Win10+ "Efficiency mode" (EcoQoS): background clients run on low-power CPU scheduling.
    [StructLayout(LayoutKind.Sequential)] struct PowerThrottle { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PowerThrottle info, int size);
    static void SetEco(IntPtr h, bool on)
    {
        var s = new PowerThrottle { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
        SetProcessInformation(h, 4 /*ProcessPowerThrottling*/, ref s, 12);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (playing == IntPtr.Zero) return;
        Park(playing);
        playing = IntPtr.Zero;
        status.Text = "Back on Home.";
        UpdateThumbs();
    }

    const long GB = 1L << 30;
    static int D(int px) { return Theme.D(px); }

    List<Account> accounts;                     // only touched on the UI thread
    readonly AppSettings settings = Store.LoadSettings();
    readonly Dictionary<int, Account> pidAccount = new Dictionary<int, Account>();
    volatile List<Client> clients = new List<Client>();
    long robloxRam;
    readonly long installedRam = (long)new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
    readonly BlockingCollection<Account> launchQueue = new BlockingCollection<Account>();
    readonly Dictionary<IntPtr, IntPtr> thumbs = new Dictionary<IntPtr, IntPtr>();
    List<Rectangle> homeCells = new List<Rectangle>();
    List<Client> homeClients = new List<Client>();
    readonly Dictionary<long, Image> avatars = new Dictionary<long, Image>();
    System.Threading.Timer poller;
    int polling;

    NavItem[] nav;
    Panel[] pages;
    int page;
    Label status, pageTitle, homeInfo, memText;
    DBPanel grid, memBar;
    ListBox list;
    readonly Font fName = new Font("Segoe UI Semibold", 12f), fSmall = new Font("Segoe UI", 9.5f), fBtn = new Font("Segoe UI Semibold", 9.5f);

    public MainForm(List<Account> loaded)
    {
        accounts = loaded;
        Text = "Multi Alt";
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = new Font("Segoe UI", 10f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var side = new Panel { Dock = DockStyle.Left, Width = D(240), BackColor = Theme.Side };
        side.Controls.Add(new PictureBox { Image = Program.Logo(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(D(14), D(16), D(60), D(60)) });
        var title = Theme.Lbl("Multi Alt", 16, Theme.Fg, true); title.Location = new Point(D(80), D(18));
        var by = Theme.Lbl("by Triple DDD", 9, Theme.Gold); by.Location = new Point(D(83), D(52));
        side.Controls.Add(title);
        side.Controls.Add(by);
        var names = new[] { "Home", "Accounts", "Performance", "Settings" };
        nav = new NavItem[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int k = i;
            nav[i] = new NavItem(names[i]) { Bounds = new Rectangle(0, D(100) + i * D(50), D(240), D(48)) };
            nav[i].Click += (s, e) => ShowPage(k);
            side.Controls.Add(nav[i]);
        }
        var kill = new NavItem("Kill all Roblox") { Bounds = new Rectangle(0, D(100) + names.Length * D(50), D(240), D(48)), TextColor = Color.FromArgb(200, 55, 65) };
        kill.Click += (s, e) => KillAll();
        side.Controls.Add(kill);
        var tos = new Label { Text = "Terms of Service", Dock = DockStyle.Bottom, Height = D(40), TextAlign = ContentAlignment.MiddleCenter,
                              ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9.5f, FontStyle.Underline), Cursor = Cursors.Hand };
        tos.MouseEnter += (s, e) => tos.ForeColor = Theme.Fg;
        tos.MouseLeave += (s, e) => tos.ForeColor = Theme.Muted;
        tos.Click += (s, e) => ShowTerms();
        side.Controls.Add(tos);

        var header = new Panel { Dock = DockStyle.Top, Height = D(58), BackColor = Theme.Bg };
        pageTitle = Theme.Lbl("", 18, Theme.Fg, true);
        pageTitle.Location = new Point(D(24), D(10));
        var winBtns = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Width = D(180), Padding = new Padding(0, D(10), D(6), 0) };
        winBtns.Controls.Add(Theme.Btn("✕", Theme.Card, (s, e) => Close()));
        sizeBtn = Theme.Btn("❐", Theme.Card, (s, e) => SetWindowed(!settings.Windowed));
        winBtns.Controls.Add(sizeBtn);
        winBtns.Controls.Add(Theme.Btn("—", Theme.Card, (s, e) => WindowState = FormWindowState.Minimized));
        MinimumSize = new Size(D(960), D(620));
        header.Controls.Add(pageTitle);
        header.Controls.Add(winBtns);

        status = new Label { Dock = DockStyle.Bottom, Height = D(28), ForeColor = Theme.Muted, BackColor = Theme.Side, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(D(12), 0, 0, 0), Text = "Ready." };

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        content.Controls.Add(header);
        pages = new Panel[] { BuildHome(), BuildAccounts(), BuildPerformance(), BuildSettings() };
        foreach (var p in pages) { p.Dock = DockStyle.Fill; content.Controls.Add(p); p.BringToFront(); } // Fill docks last when in front

        Controls.Add(content);
        Controls.Add(side);
        Controls.Add(status);
        content.BringToFront();
        ShowPage(0);
        SetWindowed(settings.Windowed);
    }

    Button sizeBtn;

    const string Terms = @"MULTI ALT - TERMS OF SERVICE AND DISCLAIMER
Developer: Triple DDD

By downloading, installing or using Multi Alt (""the Software"") you agree to these terms. If you do not agree, do not use the Software.

1. WHAT MULTI ALT IS
Multi Alt is a free, standalone account manager and multi-instance launcher. It lets you sign in to your own Roblox accounts, keep them in one place, and run more than one official Roblox client at the same time on your own computer. That is all it does.

2. WHAT MULTI ALT DOES NOT DO
Multi Alt does not inject code into, modify, read the memory of, or tamper with the Roblox client or any game. It contains no exploits, cheats, scripts, bots, macros, malware, spyware or tracking of any kind. It does not automate gameplay (see section 3 for the optional Anti-AFK setting). It starts the official Roblox client the same way the Roblox website does, and shows previews of those windows using standard Windows features. The optional FPS cap edits Roblox's own client settings file on your computer, which you can turn off at any time.

3. ANTI-AFK (OFF BY DEFAULT)
Anti-AFK is turned OFF by default. You choose whether to use it. When you turn it on, every 15 minutes Multi Alt briefly switches to each idle Roblox window and presses the Space key once, the same as you tapping Space on your own keyboard. This keeps Roblox from disconnecting you for being idle. It is simple keyboard input emulation only: it does not inject scripts or code into Roblox or any game, does not read or change game memory, and does not play the game for you. It waits until you are not using your mouse or keyboard, skips the window you are playing, and never presses a key unless a Roblox window is in focus.
How to turn it on or off: open the Performance tab in the sidebar and tick or untick ""Anti-AFK"". You can also press the ""Anti-AFK setting"" button at the bottom of this window to go straight there. Some games or servers may have their own rules about staying idle; using Anti-AFK is at your own discretion and risk (see sections 5 to 7).

4. YOUR ACCOUNT DATA
Your account login cookies are stored only on your own computer, encrypted with Windows' built-in data protection for your Windows user. They are sent only to official Roblox servers (roblox.com) to sign you in and launch games. Triple DDD never receives, collects, sells or shares your account data. Never share your accounts.dat file or your login cookies with anyone.

5. USE AT YOUR OWN RISK
The Software is provided ""AS IS"", without warranty of any kind, express or implied, including but not limited to warranties of merchantability, fitness for a particular purpose and non-infringement. You use it entirely at your own risk and discretion.

6. NO RESPONSIBILITY FOR ACCOUNT ACTIONS
Triple DDD is not responsible or liable for any action taken against your accounts, devices or network, including but not limited to bans, suspensions, terminations, warnings, account flags, moderation actions, bot or alt detections, IP bans, rate limits, loss of items, currency, progress or data, or any other decision made by Roblox Corporation or any game developer. We do not cover, insure, guarantee, restore or compensate any account for any reason.

7. YOUR DUE DILIGENCE
It is your responsibility to follow the Roblox Terms of Use, Community Standards and the rules of every game you play. It is at your own discretion how many accounts you run at once. Running a large number of accounts may lead to rate limiting, bot or alt detection, IP restrictions or other actions by Roblox. Use a reasonable number of accounts and use the Software responsibly.

8. NO AFFILIATION
Multi Alt and Triple DDD are not affiliated with, endorsed by, sponsored by or connected to Roblox Corporation. ""Roblox"" and related names and logos are trademarks of Roblox Corporation. We are also not affiliated with GitHub, Microsoft, Google, Valve/Steam or any other gaming or technology company. Multi Alt is made by an independent, standalone developer.

9. OPEN SOURCE
Multi Alt is open source. Anyone may read the full source code to confirm exactly what it does. Only download Multi Alt from the official source; copies from anywhere else may have been modified and are not supported.

10. LIMITATION OF LIABILITY
To the maximum extent permitted by law, in no event shall Triple DDD be liable for any direct, indirect, incidental, special, consequential or exemplary damages, or any loss of accounts, data, profits or goodwill, arising from or related to your use of or inability to use the Software, even if advised of the possibility of such damages.

11. CHANGES
These terms may be updated with new versions of the Software. Continuing to use the Software after an update means you accept the updated terms.";

    void ShowTerms()
    {
        using (var f = new Form { Text = "Multi Alt - Terms of Service", BackColor = Theme.Bg, Icon = Icon, StartPosition = FormStartPosition.CenterParent,
                                  Size = new Size(D(720), D(640)), MinimizeBox = false, ShowInTaskbar = false })
        {
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                    BackColor = Theme.Card, ForeColor = Theme.Fg, Font = new Font("Segoe UI", 10.5f), Text = Terms.Replace("\r\n", "\n").Replace("\n", "\r\n") };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = D(52), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(D(10)), BackColor = Theme.Bg };
            bottom.Controls.Add(Theme.Btn("I understand", Theme.Gold, (s, e) => f.Close()));
            bottom.Controls.Add(Theme.Btn("Anti-AFK setting", Theme.Card2, (s, e) => { f.Close(); ShowPage(2); afkBox.Focus(); }));
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(D(16)), BackColor = Theme.Card };
            pad.Controls.Add(box);
            f.Controls.Add(pad);
            f.Controls.Add(bottom);
            f.Shown += (s, e) => { box.SelectionLength = 0; box.SelectionStart = 0; };
            f.ShowDialog(this);
        }
    }

    // Fullscreen = borderless and maximised; windowed = a normal resizable window at 3/4 of the screen.
    void SetWindowed(bool windowed)
    {
        if (settings.Windowed != windowed) { settings.Windowed = windowed; Store.SaveSettings(settings); }
        WindowState = FormWindowState.Normal;
        FormBorderStyle = windowed ? FormBorderStyle.Sizable : FormBorderStyle.None;
        if (windowed)
        {
            var wa = Screen.FromControl(this).WorkingArea;
            Bounds = new Rectangle(wa.X + wa.Width / 8, wa.Y + wa.Height / 8, wa.Width * 3 / 4, wa.Height * 3 / 4);
        }
        else WindowState = FormWindowState.Maximized;
        sizeBtn.Text = windowed ? "□" : "❐";
        UpdateThumbs();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        new Thread(LaunchWorker) { IsBackground = true }.Start();
        new Thread(RefreshAccountInfo) { IsBackground = true }.Start();
        new Thread(AntiAfkLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        if (settings.FpsCap > 0) new Thread(() => FpsTweak.Apply(settings.FpsCap)) { IsBackground = true }.Start(); // Roblox updates make new version folders
        poller = new System.Threading.Timer(_ => Poll(), null, 300, 2000);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (poller != null) poller.Dispose();
        // Give running clients their taskbar buttons back and bring them onto the main screen, cascaded.
        var wa = Screen.PrimaryScreen.WorkingArea;
        int n = 0;
        lock (tamed)
            foreach (var h in tamed)
            {
                SetTaskbarHidden(h, false);
                SetWindowPos(h, IntPtr.Zero, wa.X + 30 * n, wa.Y + 30 * n, Math.Min(1280, wa.Width), Math.Min(720, wa.Height), 0x0004 | 0x0010 /*NOZORDER|NOACTIVATE*/);
                n = (n + 1) % 10;
            }
        foreach (var t in thumbs.Values) DwmUnregisterThumbnail(t);
        thumbs.Clear();
        base.OnFormClosing(e);
    }

    void ShowPage(int i)
    {
        page = i;
        for (int j = 0; j < pages.Length; j++) { pages[j].Visible = j == i; nav[j].Selected = j == i; nav[j].Invalidate(); }
        pageTitle.Text = nav[i].Text;
        UpdateThumbs();
    }

    void SetStatus(string text)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => status.Text = text));
    }

    static string Title(Account a) { return string.IsNullOrEmpty(a.DisplayName) ? a.Name : a.DisplayName; }

    bool IsRunning(Account a) { lock (pidAccount) return pidAccount.ContainsValue(a); }

    // ---------------- Home: live previews of every Roblox window ----------------

    Panel BuildHome()
    {
        var p = new Panel { BackColor = Theme.Bg };
        homeInfo = new Label { Dock = DockStyle.Top, Height = D(30), ForeColor = Theme.Muted, Padding = new Padding(D(24), 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        grid = new DBPanel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Cursor = Cursors.Hand };
        grid.Paint += PaintGrid;
        grid.MouseClick += GridClick;
        grid.Resize += (s, e) => UpdateThumbs();
        p.Controls.Add(homeInfo);
        p.Controls.Add(grid);
        grid.BringToFront();
        return p;
    }

    // Previews are DWM thumbnails: Windows composites the real client windows on the GPU, so they cost almost nothing.
    void UpdateThumbs()
    {
        if (grid == null || !IsHandleCreated) return;
        // Home follows the Accounts tab order (drag cards there to rearrange); clients not launched by Multi Alt go last.
        var order = new Dictionary<Account, int>();
        for (int i = 0; i < accounts.Count; i++) order[accounts[i]] = i;
        int pos;
        var live = clients.Where(c => c.Hwnd != IntPtr.Zero)
                          .OrderBy(c => c.Account != null && order.TryGetValue(c.Account, out pos) ? pos : int.MaxValue).ThenBy(c => c.Pid).ToList();
        var liveSet = new HashSet<IntPtr>(live.Select(c => c.Hwnd));
        foreach (var h in thumbs.Keys.Where(h => !liveSet.Contains(h)).ToList()) { DwmUnregisterThumbnail(thumbs[h]); thumbs.Remove(h); }

        homeClients = live;
        homeCells = Grid.Fit(live.Count, grid.ClientSize.Width - D(16), grid.ClientSize.Height - D(8), D(10), D(24), D(560));
        for (int i = 0; i < homeCells.Count; i++) homeCells[i] = new Rectangle(homeCells[i].X + D(16), homeCells[i].Y, homeCells[i].Width, homeCells[i].Height);
        bool show = page == 0 && WindowState != FormWindowState.Minimized;
        var off = PointToClient(grid.PointToScreen(Point.Empty));
        for (int i = 0; i < live.Count; i++)
        {
            IntPtr t;
            if (!thumbs.TryGetValue(live[i].Hwnd, out t))
            {
                if (DwmRegisterThumbnail(Handle, live[i].Hwnd, out t) != 0) continue;
                thumbs[live[i].Hwnd] = t;
            }
            var cell = homeCells[i];
            var box = new Rectangle(cell.X + off.X + D(4), cell.Y + off.Y + D(4), cell.Width - D(8), cell.Height - D(24) - D(4));
            PSIZE src;
            if (DwmQueryThumbnailSourceSize(t, out src) == 0 && src.X > 0 && src.Y > 0) // letterbox, keep aspect
            {
                double k = Math.Min(box.Width / (double)src.X, box.Height / (double)src.Y);
                int w = (int)(src.X * k), h = (int)(src.Y * k);
                box = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            }
            var props = new ThumbProps { Flags = 0x1 | 0x4 | 0x8 | 0x10, Dest = new RECT { L = box.Left, T = box.Top, R = box.Right, B = box.Bottom }, Opacity = 255, Visible = show, ClientOnly = true };
            DwmUpdateThumbnailProperties(t, ref props);
        }
        nav[0].Count = live.Count > 0 ? live.Count.ToString() : "";
        nav[0].Invalidate();
        homeInfo.Text = live.Count + " running  •  Roblox using " + Gb(Interlocked.Read(ref robloxRam)) + " GB of " + Gb(installedRam)
            + " GB  •  click a window to play it, right-click to close it  •  click the sidebar to come back here";
        grid.Invalidate();
    }

    static string Gb(long bytes) { return (bytes / (double)GB).ToString("0.0"); }

    void PaintGrid(object s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (homeCells.Count == 0)
        {
            TextRenderer.DrawText(g, "No Roblox clients running.\nGo to Accounts and press Play or Launch all.", new Font("Segoe UI", 13f), grid.ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        for (int i = 0; i < homeCells.Count && i < homeClients.Count; i++)
        {
            var c = homeCells[i];
            Theme.FillRound(g, Theme.Card, c, D(8));
            var who = homeClients[i].Account;
            var name = who == null ? "Roblox · " + homeClients[i].Pid : Title(who) + (string.IsNullOrEmpty(who.Username) ? "" : "  @" + who.Username);
            var label = new Rectangle(c.X + D(8), c.Bottom - D(24), c.Width - D(16), D(22));
            TextRenderer.DrawText(g, name, fSmall, label, Theme.Fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (c.Width > D(140))
                TextRenderer.DrawText(g, Gb(homeClients[i].Ram) + " GB", fSmall, label, Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    void GridClick(object s, MouseEventArgs e)
    {
        int i = homeCells.FindIndex(r => r.Contains(e.Location));
        if (i < 0 || i >= homeClients.Count) return;
        var c = homeClients[i];
        if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Play this window", null, (a, b) => FocusClient(c.Hwnd));
            menu.Items.Add("Close this client", null, (a, b) => { try { Process.GetProcessById(c.Pid).Kill(); } catch { } });
            menu.Show(grid, e.Location);
        }
        else FocusClient(c.Hwnd);
    }

    // Opens the client over Multi Alt's main area so it feels built in; clicking Multi Alt again puts it back behind.
    void FocusClient(IntPtr h)
    {
        if (playing != IntPtr.Zero && playing != h) Park(playing);
        playing = h;
        if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
        var r = grid.RectangleToScreen(grid.ClientRectangle);
        SetWindowPos(h, HWND_TOP, r.X, r.Y, r.Width, r.Height, 0x0040 /*SHOWWINDOW*/);
        SetForegroundWindow(h);
        status.Text = "Playing in that window. Click anywhere on Multi Alt (like the sidebar) to come back to Home.";
    }

    // Runs every 2s off the UI thread: reads the clients, applies priority and the RAM cap.
    void Poll()
    {
        if (Interlocked.Exchange(ref polling, 1) == 1) return;
        try
        {
            var procs = Process.GetProcessesByName("RobloxPlayerBeta");
            uint fgPid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            var pids = new HashSet<int>(procs.Select(p => p.Id));
            var mainByPid = new Dictionary<int, IntPtr>();
            var windows = TopWindows(pids, mainByPid);
            var list = new List<Client>();
            long total = 0;
            foreach (var p in procs)
            {
                try
                {
                    IntPtr hwnd;
                    mainByPid.TryGetValue(p.Id, out hwnd);
                    var c = new Client { Pid = p.Id, Hwnd = hwnd, Ram = p.WorkingSet64 };
                    lock (pidAccount) pidAccount.TryGetValue(p.Id, out c.Account);
                    list.Add(c);
                    total += c.Ram;
                    bool background = settings.LowPriorityBackground && p.Id != fgPid;
                    var want = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                    if (p.PriorityClass != want) p.PriorityClass = want;
                    try { SetEco(p.Handle, background); } catch (EntryPointNotFoundException) { } // pre-Win10
                }
                catch { } // process exited mid-read
            }
            long cap = settings.RamCapGb * GB;
            if (cap > 0 && total > cap) // over the cap: push background clients' memory out to the page file
                foreach (var p in procs) if (p.Id != fgPid) try { EmptyWorkingSet(p.Handle); } catch { }
            foreach (var p in procs) p.Dispose();

            lock (pidAccount)
                foreach (var dead in pidAccount.Keys.Where(k => !pids.Contains(k)).ToList()) pidAccount.Remove(dead);

            // New Roblox windows: take them off the taskbar and park them off-screen; they live in Home.
            bool fresh = false;
            lock (tamed)
            {
                tamed.IntersectWith(windows);
                foreach (var h in windows.Where(h => !tamed.Contains(h)))
                {
                    SetTaskbarHidden(h, true);
                    Park(h);
                    tamed.Add(h);
                    fresh = true;
                }
            }
            if (fresh && IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => { if (playing == IntPtr.Zero && WindowState != FormWindowState.Minimized) Activate(); })); // don't yank you out of a game
            clients = list;
            Interlocked.Exchange(ref robloxRam, total);
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)RefreshUi);
        }
        catch { }
        finally { polling = 0; }
    }

    void RefreshUi()
    {
        UpdateThumbs();
        long limit = settings.RamCapGb > 0 ? settings.RamCapGb * GB : installedRam;
        memText.Text = "Roblox is using " + Gb(Interlocked.Read(ref robloxRam)) + " GB across " + clients.Count + " clients  •  limit "
            + Gb(limit) + " GB" + (settings.RamCapGb > 0 ? " (your cap)" : " (all installed RAM)");
        memBar.Invalidate();
        if (page == 1) list.Invalidate();
    }

    // ---------------- Accounts ----------------

    Panel BuildAccounts()
    {
        var p = new Panel { BackColor = Theme.Bg };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = D(54), Padding = new Padding(D(18), D(6), 0, 0), BackColor = Theme.Bg };
        bar.Controls.Add(Theme.Btn("+  Add account", Theme.Gold, AddAccountDialog));
        bar.Controls.Add(Theme.Btn("Login via Roblox", Theme.Blue, (s, e) => LoginViaRoblox()));
        bar.Controls.Add(Theme.Btn("▶  Launch all", Theme.Card, (s, e) => QueueLaunch(accounts.ToList())));
        bar.Controls.Add(Theme.Btn("■  Stop launching", Theme.Card, (s, e) => { StopLaunching(); status.Text = "Launch queue cleared."; }));
        var lp = Theme.Lbl("Game link / Place ID", 10, Theme.Muted); lp.Margin = new Padding(D(12), D(8), D(6), 0);
        var placeBox = new TextBox { Width = D(320), Text = settings.PlaceId, BackColor = Theme.Card, ForeColor = Theme.Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, D(5), 0, 0) };
        placeBox.TextChanged += (s, e) => { settings.PlaceId = placeBox.Text.Trim(); Store.SaveSettings(settings); };
        bar.Controls.Add(lp);
        bar.Controls.Add(placeBox);
        var hint = Theme.Lbl("(empty = opens Roblox home)", 9, Theme.Muted); hint.Margin = new Padding(D(6), D(9), 0, 0);
        bar.Controls.Add(hint);

        list = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Math.Min(255, D(84)), BackColor = Theme.Bg, BorderStyle = BorderStyle.None, IntegralHeight = false };
        list.DrawItem += DrawAccount;
        list.MouseClick += AccountClick;
        list.MouseDown += (s, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            if (e.Button != MouseButtons.Left || i < 0 || i >= accounts.Count || OnCardButton(i, e.Location)) return;
            dragFrom = i;
            dragStart = e.Location;
        };
        list.MouseMove += (s, e) =>
        {
            if (dragFrom < 0 || e.Button != MouseButtons.Left) return;
            if (dropAt < 0 && Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height * 2) return; // not a drag yet, just a click
            int i = list.IndexFromPoint(e.Location), at;
            if (e.Y < 0) at = list.TopIndex;
            else if (i < 0) at = accounts.Count;
            else { var r = list.GetItemRectangle(i); at = e.Y < r.Top + r.Height / 2 ? i : i + 1; }
            if (e.Y < D(24) && list.TopIndex > 0) list.TopIndex--;                       // drag to the edge to scroll
            else if (e.Y > list.ClientSize.Height - D(24)) list.TopIndex++;
            list.Cursor = Cursors.SizeNS;
            if (at != dropAt) { dropAt = at; list.Invalidate(); }
        };
        list.MouseUp += (s, e) =>
        {
            int from = dragFrom, to = dropAt;
            dragFrom = dropAt = -1;
            list.Cursor = Cursors.Default;
            if (from < 0 || to < 0) return;
            justDragged = true;
            BeginInvoke((Action)(() => justDragged = false)); // swallow the click that ends the drag, whichever order WinForms sends it
            var a = accounts[from];
            to = Program.MoveItem(accounts, from, to);
            if (to != from)
            {
                Store.Save(accounts);
                status.Text = "Moved " + Title(a) + " to #" + (to + 1) + ". Home shows clients in this order.";
                UpdateThumbs();
            }
            list.Invalidate();
        };
        var tip = new ToolTip();
        list.MouseMove += (s, e) =>
        {
            int i = list.IndexFromPoint(e.Location);
            int over = i >= 0 && i < accounts.Count && CopyRect(CardRect(list.GetItemRectangle(i)), accounts[i]).Contains(e.Location) ? i : -1;
            if (over == hoverCopy) return;
            if (hoverCopy >= 0 && hoverCopy < accounts.Count) list.Invalidate(list.GetItemRectangle(hoverCopy));
            hoverCopy = over;
            if (over >= 0) { list.Invalidate(list.GetItemRectangle(over)); tip.Show("Copy", list, e.X + D(12), e.Y + D(16), 1500); }
            else tip.Hide(list);
        };
        p.Controls.Add(bar);
        p.Controls.Add(list);
        list.BringToFront();
        RefreshList();
        return p;
    }

    void RefreshList()
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var a in accounts) list.Items.Add(a);
        list.EndUpdate();
        nav[1].Count = accounts.Count.ToString();
        nav[1].Invalidate();
    }

    int hoverCopy = -1, dragFrom = -1, dropAt = -1;
    Point dragStart;
    bool justDragged;

    bool OnCardButton(int i, Point p)
    {
        var card = CardRect(list.GetItemRectangle(i));
        return CopyRect(card, accounts[i]).Contains(p) || BrowserRect(card).Contains(p) || PlayRect(card).Contains(p) || XRect(card).Contains(p);
    }
    CheckBox afkBox;
    Rectangle CardRect(Rectangle item) { return new Rectangle(item.X + D(18), item.Y + D(6), item.Width - D(36), item.Height - D(12)); }
    Rectangle PlayRect(Rectangle card) { return new Rectangle(card.Right - D(160), card.Y + (card.Height - D(34)) / 2, D(96), D(34)); }
    Rectangle BrowserRect(Rectangle card) { return new Rectangle(card.Right - D(310), card.Y + (card.Height - D(34)) / 2, D(140), D(34)); }
    Rectangle XRect(Rectangle card) { return new Rectangle(card.Right - D(52), card.Y + (card.Height - D(34)) / 2, D(34), D(34)); }

    // Owner-drawn rows: one control for the whole list, so hundreds of accounts scroll smoothly.
    void DrawAccount(object s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= accounts.Count) return;
        var a = accounts[e.Index];
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, e.Bounds);
        var card = CardRect(e.Bounds);
        Theme.FillRound(g, Theme.Card, card, D(10));

        var av = new Rectangle(card.X + D(14), card.Y + (card.Height - D(52)) / 2, D(52), D(52));
        var img = Avatar(a.UserId);
        using (var circle = new GraphicsPath())
        {
            circle.AddEllipse(av);
            if (img != null) { g.SetClip(circle); g.DrawImage(img, av); g.ResetClip(); }
            else using (var b = new SolidBrush(Theme.Card2)) g.FillPath(b, circle);
        }
        bool running = IsRunning(a);
        if (running) using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, av.Right - D(14), av.Bottom - D(14), D(14), D(14));

        int tx = av.Right + D(16);
        var textW = Math.Max(10, BrowserRect(card).X - tx - D(10));
        TextRenderer.DrawText(g, Title(a), fName, new Rectangle(tx, card.Y + D(12), textW, D(26)), Theme.Fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, SubText(a), fSmall, new Rectangle(tx, card.Y + D(40), textW, D(22)), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        var cp = CopyRect(card, a);
        DrawCopyIcon(g, cp, e.Index == hoverCopy ? Theme.Fg : Theme.Muted);

        var web = BrowserRect(card);
        Theme.FillRound(g, Theme.Card2, web, D(8));
        TextRenderer.DrawText(g, "Open in browser", fBtn, web, Theme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var play = PlayRect(card);
        Theme.FillRound(g, running ? Theme.Card2 : Theme.Gold, play, D(8));
        TextRenderer.DrawText(g, running ? "Running" : "▶  Play", fBtn, play, running ? Theme.Green : Theme.Bg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var x = XRect(card);
        Theme.FillRound(g, Theme.Card2, x, D(8));
        TextRenderer.DrawText(g, "✕", fBtn, x, Theme.Red, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (dropAt < 0) return; // drag in progress: outline the card being moved and draw a gold line where it will land
        if (e.Index == dragFrom) using (var pen = new Pen(Theme.Gold, D(2))) using (var path = Theme.Round(card, D(10))) g.DrawPath(pen, path);
        int lineY = dropAt == e.Index ? e.Bounds.Top + D(2) : dropAt == e.Index + 1 && e.Index == accounts.Count - 1 ? e.Bounds.Bottom - D(4) : -1;
        if (lineY >= 0) Theme.FillRound(g, Theme.Gold, new Rectangle(card.X, lineY, card.Width, D(3)), D(1));
    }

    Image Avatar(long id)
    {
        Image img;
        if (avatars.TryGetValue(id, out img)) return img;
        var f = Path.Combine(Store.AvatarDir, id + ".png");
        if (id == 0 || !File.Exists(f)) return null;
        try { using (var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(f)))) img = new Bitmap(src, D(52), D(52)); }
        catch { return null; }
        avatars[id] = img;
        return img;
    }

    void AccountClick(object s, MouseEventArgs e)
    {
        int i = list.IndexFromPoint(e.Location);
        if (justDragged || dropAt >= 0 || i < 0 || i >= accounts.Count) return;
        var card = CardRect(list.GetItemRectangle(i));
        var a = accounts[i];
        if (CopyRect(card, a).Contains(e.Location)) CopyMenu(a, new Point(CopyRect(card, a).X, CopyRect(card, a).Bottom));
        else if (XRect(card).Contains(e.Location))
        {
            if (MessageBox.Show(this, "Remove " + Title(a) + "?\nYou'll need to log in again to add it back.", "Multi Alt", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            accounts.Remove(a);
            Store.Save(accounts);
            RefreshList();
        }
        else if (PlayRect(card).Contains(e.Location)) QueueLaunch(new List<Account> { a });
        else if (BrowserRect(card).Contains(e.Location))
        {
            status.Text = "Opening " + Title(a) + " in Chrome...";
            new Thread(() =>
            {
                try { BrowserLogin.OpenAs(a); SetStatus("Opened " + Title(a) + " in its own browser window."); }
                catch (Exception ex) { SetStatus(Title(a) + " browser: " + (ex is AggregateException ? ex.InnerException.Message : ex.Message)); }
            }) { IsBackground = true }.Start();
        }
    }

    void AddAccountDialog(object s, EventArgs e)
    {
        using (var dlg = new Form { Text = "Add account with cookie", ClientSize = new Size(D(480), D(190)), FormBorderStyle = FormBorderStyle.FixedDialog,
                                    StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Side, ForeColor = Theme.Fg, MaximizeBox = false, MinimizeBox = false, Font = Font })
        {
            var help = Theme.Lbl("Easiest is \"Login via Roblox\". To paste a cookie instead: log in on roblox.com,\r\npress F12 > Application > Cookies, and copy the .ROBLOSECURITY value.", 9, Theme.Muted);
            help.Location = new Point(D(12), D(10));
            var l1 = Theme.Lbl("Nickname", 10, Theme.Fg); l1.Location = new Point(D(12), D(66));
            var nick = new TextBox { Location = new Point(D(110), D(64)), Width = D(355) };
            var l2 = Theme.Lbl("Cookie", 10, Theme.Fg); l2.Location = new Point(D(12), D(102));
            var cookie = new TextBox { Location = new Point(D(110), D(100)), Width = D(355), UseSystemPasswordChar = true };
            var ok = Theme.Btn("Save", Theme.Gold, null);
            ok.Location = new Point(D(390), D(140));
            ok.DialogResult = DialogResult.OK;
            dlg.Controls.AddRange(new Control[] { help, l1, nick, l2, cookie, ok });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) != DialogResult.OK || cookie.Text.Trim() == "") return;
            AddCookie(cookie.Text.Trim(), nick.Text.Trim());
        }
    }

    void LoginViaRoblox()
    {
        status.Text = "A browser window opened - log in to Roblox there. It closes by itself once you're in.";
        new Thread(() =>
        {
            try { AddCookie(BrowserLogin.Run(), ""); }
            catch (Exception ex) { SetStatus("Login cancelled: " + ex.Message); }
        }) { IsBackground = true }.Start();
    }

    void AddCookie(string cookie, string nick)
    {
        new Thread(() =>
        {
            try
            {
                SetStatus("Checking account...");
                var u = RobloxApi.GetUser(cookie);
                try { RobloxApi.FetchAvatars(new[] { u.UserId }); } catch { } // no avatar is not fatal
                BeginInvoke((Action)(() =>
                {
                    var existing = accounts.FirstOrDefault(x => x.UserId == u.UserId);
                    if (existing != null) existing.Cookie = cookie; // re-login refreshes the cookie instead of duplicating
                    else { u.Name = nick == "" ? u.Username : nick; u.Cookie = cookie; accounts.Add(u); }
                    Store.Save(accounts);
                    avatars.Remove(u.UserId);
                    RefreshList();
                    status.Text = (existing != null ? "Updated " : "Added ") + u.DisplayName + " (@" + u.Username + ").";
                }));
            }
            catch (Exception ex) { SetStatus("Couldn't add account: " + ex.Message); }
        }) { IsBackground = true }.Start();
    }

    // Fills in username/avatar for accounts added before that info existed, and any missing avatars.
    void RefreshAccountInfo()
    {
        try
        {
            var snap = (List<Account>)Invoke(new Func<List<Account>>(() => accounts.ToList()));
            foreach (var a in snap.Where(x => x.UserId == 0))
            {
                try
                {
                    var u = RobloxApi.GetUser(a.Cookie);
                    var acc = a;
                    BeginInvoke((Action)(() => { acc.UserId = u.UserId; acc.Username = u.Username; acc.DisplayName = u.DisplayName; }));
                    a.UserId = u.UserId; // local copy for the avatar step below
                }
                catch { }
            }
            var missing = snap.Where(x => x.UserId > 0 && !File.Exists(Path.Combine(Store.AvatarDir, x.UserId + ".png"))).Select(x => x.UserId).ToList();
            if (missing.Count > 0) try { RobloxApi.FetchAvatars(missing); } catch { }
            BeginInvoke((Action)(() => { Store.Save(accounts); list.Invalidate(); }));
        }
        catch { }
    }

    void QueueLaunch(List<Account> chosen)
    {
        int n = 0;
        foreach (var a in chosen) if (!IsRunning(a)) { launchQueue.Add(a); n++; }
        status.Text = n == 0 ? "Already running." : "Queued " + n + " account(s) to launch.";
    }

    void StopLaunching() { Account x; while (launchQueue.TryTake(out x)) { } }

    // ---------------- Anti-AFK: a real Space key press, the same as you tapping it. No injection, no memory access. ----------------

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size, Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput li);

    static uint IdleMs()
    {
        var li = new LastInput { Size = 8 };
        return GetLastInputInfo(ref li) ? (uint)Environment.TickCount - li.Time : 0;
    }

    // Windows only lets the foreground app hand focus away; sharing its input queue for a moment makes the switch allowed.
    static bool Focus(IntPtr h)
    {
        uint pid, fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
        return GetForegroundWindow() == h;
    }

    void AntiAfkLoop()
    {
        var last = DateTime.UtcNow;
        while (true)
        {
            Thread.Sleep(30000);
            // Roblox kicks after 20 idle minutes; wait until you've stopped typing/clicking for 3s so we never interrupt you.
            if (!settings.AntiAfkEnabled || clients.Count == 0 || DateTime.UtcNow - last < TimeSpan.FromMinutes(15) || IdleMs() < 3000) continue;
            last = DateTime.UtcNow;
            var back = GetForegroundWindow();
            foreach (var c in clients)
            {
                if (c.Hwnd == IntPtr.Zero || c.Hwnd == back) continue; // the one you're playing is already active
                if (!Focus(c.Hwnd)) continue;                           // never press a key into the wrong window
                keybd_event(0x20, 0x39, 0, UIntPtr.Zero);               // Space down
                Thread.Sleep(40);
                keybd_event(0x20, 0x39, 2, UIntPtr.Zero);               // Space up
                Thread.Sleep(40);
            }
            if (back != IntPtr.Zero) Focus(back);
            SetStatus("Anti-AFK: kept " + clients.Count + " client(s) awake at " + DateTime.Now.ToShortTimeString() + ".");
        }
    }

    void KillAll()
    {
        int n = clients.Count;
        if (MessageBox.Show(this, "Force-close all " + n + " Roblox clients right now?\nAnything not saved in-game is lost.", "Kill all Roblox",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        StopLaunching();
        playing = IntPtr.Zero;
        status.Text = "Killing Roblox...";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            int killed = 0;
            foreach (var name in new[] { "RobloxPlayerLauncher", "RobloxPlayerBeta" })
                foreach (var p in Process.GetProcessesByName(name)) using (p) try { p.Kill(); killed++; } catch { }
            SetStatus("Killed " + killed + " Roblox process(es).");
        });
    }

    // ---------------- Copy combo / cookie (stays on this PC: kept out of clipboard history and cloud sync) ----------------

    Rectangle CopyRect(Rectangle card, Account a)
    {
        int tx = card.X + D(14) + D(52) + D(16);
        int w = TextRenderer.MeasureText(SubText(a), fSmall).Width;
        return new Rectangle(Math.Min(tx + w + D(6), BrowserRect(card).X - D(30)), card.Y + D(40), D(22), D(22));
    }

    static string SubText(Account a) { return (string.IsNullOrEmpty(a.Username) ? "loading info..." : "@" + a.Username) + (a.UserId > 0 ? "   •   ID " + a.UserId : ""); }

    static void DrawCopyIcon(Graphics g, Rectangle r, Color c)
    {
        int s = r.Width * 9 / 22, o = r.Width * 4 / 22;
        using (var pen = new Pen(c, Math.Max(1f, r.Width / 14f)))
        {
            g.DrawRectangle(pen, r.X + o * 2, r.Y + o, s, s);     // back sheet
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r.X + o, r.Y + o * 2, s, s);
            g.DrawRectangle(pen, r.X + o, r.Y + o * 2, s, s);     // front sheet
        }
    }

    // Windows skips clipboard history (Win+V) and cloud clipboard sync for data carrying these formats.
    static void CopyPrivate(string text)
    {
        var d = new DataObject();
        d.SetData(DataFormats.UnicodeText, text);
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
        d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        Clipboard.SetDataObject(d, true);
    }

    void CopyMenu(Account a, Point at)
    {
        var menu = new ContextMenuStrip();
        bool hasCombo = !string.IsNullOrEmpty(a.Password) && !string.IsNullOrEmpty(a.Username);
        var combo = menu.Items.Add(hasCombo ? "Copy combo (username:password)" : "Copy combo - no password saved", null, (s, e) =>
        {
            CopyPrivate(a.Username + ":" + a.Password);
            status.Text = "Copied " + Title(a) + "'s combo. Don't share it with anyone.";
        });
        combo.Enabled = hasCombo;
        menu.Items.Add("Copy cookie", null, (s, e) =>
        {
            CopyPrivate(a.Cookie);
            status.Text = "Copied " + Title(a) + "'s cookie. Anyone with it can get into the account - don't share it.";
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(string.IsNullOrEmpty(a.Password) ? "Save password for combo..." : "Change saved password...", null, (s, e) => AskPassword(a));
        menu.Show(list, at);
    }

    void AskPassword(Account a)
    {
        using (var dlg = new Form { Text = "Password for " + Title(a), ClientSize = new Size(D(420), D(130)), FormBorderStyle = FormBorderStyle.FixedDialog,
                                    StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Side, ForeColor = Theme.Fg, MaximizeBox = false, MinimizeBox = false, Font = Font })
        {
            var help = Theme.Lbl("Saved encrypted on this PC only, never uploaded. Leave empty to forget it.", 9, Theme.Muted);
            help.Location = new Point(D(12), D(12));
            var box = new TextBox { Location = new Point(D(12), D(42)), Width = D(396), UseSystemPasswordChar = true, Text = a.Password ?? "" };
            var ok = Theme.Btn("Save", Theme.Gold, null);
            ok.Location = new Point(D(330), D(80));
            ok.DialogResult = DialogResult.OK;
            dlg.Controls.AddRange(new Control[] { help, box, ok });
            dlg.AcceptButton = ok;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            a.Password = box.Text == "" ? null : box.Text;
            Store.Save(accounts);
            status.Text = a.Password == null ? "Password forgotten." : "Password saved for " + Title(a) + ".";
        }
    }

    static HashSet<int> RobloxPids()
    {
        var ps = Process.GetProcessesByName("RobloxPlayerBeta");
        var ids = new HashSet<int>(ps.Select(p => p.Id));
        foreach (var p in ps) p.Dispose();
        return ids;
    }

    // Only checks the brand-new processes for a window, so polling stays cheap with hundreds of clients running.
    static int NewWindowPid(HashSet<int> skip)
    {
        foreach (var id in RobloxPids().Where(i => !skip.Contains(i)))
            try { using (var p = Process.GetProcessById(id)) if (p.MainWindowHandle != IntPtr.Zero) return id; } catch { }
        return 0;
    }

    // One background worker launches the queue in order, so clicking Play on many accounts never floods Roblox.
    void LaunchWorker()
    {
        foreach (var a in launchQueue.GetConsumingEnumerable())
        {
            try
            {
                if (IsRunning(a)) continue;
                long cap = settings.RamCapGb * GB;
                if (cap > 0 && Interlocked.Read(ref robloxRam) + GB > cap)
                {
                    StopLaunching();
                    SetStatus("RAM cap of " + settings.RamCapGb + " GB reached - stopped launching. Raise it in Performance.");
                    continue;
                }
                var place = Program.ParsePlace(settings.PlaceId);
                if (place == null) { StopLaunching(); SetStatus("Place ID box: paste a game link or its number."); continue; }

                SetStatus("Launching " + Title(a) + "  (" + launchQueue.Count + " more queued)...");
                var before = RobloxPids();
                Process.Start(RobloxApi.BuildUri(RobloxApi.GetTicket(a.Cookie), place));
                // Roblox first runs a short-lived launcher process, so wait (up to 60s) for a new one that has a game window.
                for (int t = 0; t < 120; t++)
                {
                    Thread.Sleep(500);
                    HashSet<int> skip;
                    lock (pidAccount) skip = new HashSet<int>(before.Concat(pidAccount.Keys));
                    int fresh = NewWindowPid(skip);
                    if (fresh != 0) { lock (pidAccount) pidAccount[fresh] = a; break; }
                }
                Thread.Sleep(Math.Max(0, settings.LaunchDelaySec) * 1000);
                if (launchQueue.Count == 0) SetStatus("All launches done.");
            }
            catch (Exception ex) { SetStatus(Title(a) + ": " + ex.Message); }
        }
    }

    // ---------------- Performance ----------------

    Panel BuildPerformance()
    {
        var p = new Panel { BackColor = Theme.Bg, AutoScroll = true };
        int x = D(26), y = D(6);
        Action<Control, int> add = (c, h) => { c.Location = new Point(x, y); p.Controls.Add(c); y += h; };
        Func<string, Label> note = t => { var l = Theme.Lbl(t, 9.5f, Theme.Muted); l.MaximumSize = new Size(D(760), 0); return l; };

        add(Theme.Lbl("MEMORY", 9, Theme.Gold, true), D(26));
        memText = Theme.Lbl("", 11, Theme.Fg);
        add(memText, D(32));
        memBar = new DBPanel { Size = new Size(D(760), D(14)) };
        memBar.Paint += (s, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, memBar.Width - 1, memBar.Height - 1);
            Theme.FillRound(g, Theme.Card, r, r.Height / 2);
            long limit = settings.RamCapGb > 0 ? settings.RamCapGb * GB : installedRam;
            double f = Math.Min(1, Interlocked.Read(ref robloxRam) / (double)limit);
            if (f > 0) Theme.FillRound(g, f > 0.9 ? Theme.Red : Theme.Blue, new Rectangle(0, 0, Math.Max(r.Height, (int)(r.Width * f)), r.Height), r.Height / 2);
        };
        add(memBar, D(34));

        add(Theme.Lbl("RAM cap", 12, Theme.Fg, true), D(30));
        add(note("Multi Alt won't launch more clients once Roblox reaches the cap, and squeezes background clients' memory when they go over it. "
               + "Your PC has " + Gb(installedRam) + " GB, so bigger sizes are greyed out."), D(46));
        add(Choices(new[] { 0, 8, 16, 24, 32, 48, 64, 96 }, v => v == 0 ? "No cap" : v + " GB", v => Program.RamAllowed(v, installedRam),
                    () => settings.RamCapGb, v => { settings.RamCapGb = v; Store.SaveSettings(settings); RefreshUi(); }), D(56));
        add(Theme.Btn("Free memory now", Theme.Card, (s, e) => TrimAll()), D(56));

        add(Theme.Lbl("FPS cap", 12, Theme.Fg, true), D(30));
        add(note("Lower FPS = much less CPU and GPU per client, which is the biggest win for running lots of alts. Applies to clients launched after you change it."), D(46));
        add(Choices(new[] { 0, 5, 10, 15, 30, 60 }, v => v == 0 ? "Roblox default" : v + " FPS", v => true,
                    () => settings.FpsCap, v =>
                    {
                        settings.FpsCap = v;
                        Store.SaveSettings(settings);
                        int n = FpsTweak.Apply(v);
                        status.Text = n == 0 ? "Roblox install not found - FPS cap will apply once Roblox is installed." : "FPS cap set for " + n + " Roblox install(s).";
                    }), D(66));

        add(Theme.Lbl("CPU priority", 12, Theme.Fg, true), D(30));
        var pri = new CheckBox { Text = "Run background clients at lower priority, so the one you're playing stays smooth", Checked = settings.LowPriorityBackground,
                                 AutoSize = true, ForeColor = Theme.Fg, Font = new Font("Segoe UI", 10f) };
        pri.CheckedChanged += (s, e) => { settings.LowPriorityBackground = pri.Checked; Store.SaveSettings(settings); };
        add(pri, D(40));
        var afk = afkBox = new CheckBox { Text = "Anti-AFK (off by default): tap Space in idle clients every 15 min so Roblox doesn't kick them. Simple key press, nothing injected. See Terms of Service.", Checked = settings.AntiAfkEnabled,
                                 AutoSize = true, ForeColor = Theme.Fg, Font = new Font("Segoe UI", 10f) };
        afk.CheckedChanged += (s, e) => { settings.AntiAfkEnabled = afk.Checked; Store.SaveSettings(settings); };
        add(afk, D(40));
        return p;
    }

    // Row of toggle buttons where exactly one is lit.
    FlowLayoutPanel Choices(int[] values, Func<int, string> label, Func<int, bool> enabled, Func<int> get, Action<int> set)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg, Margin = Padding.Empty, MaximumSize = new Size(D(900), 0) };
        Action paint = () =>
        {
            foreach (Button b in f.Controls)
            {
                bool on = (int)b.Tag == get();
                b.BackColor = on ? Theme.Gold : Theme.Card;
                b.ForeColor = on ? Theme.Bg : b.Enabled ? Theme.Fg : Theme.Muted;
            }
        };
        foreach (var v in values)
        {
            int val = v;
            var b = Theme.Btn(label(v), Theme.Card, null);
            b.Tag = v;
            b.Enabled = enabled(v);
            b.Click += (s, e) => { set(val); paint(); };
            f.Controls.Add(b);
        }
        paint();
        return f;
    }

    void TrimAll()
    {
        int n = 0;
        foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta")) using (p) try { if (EmptyWorkingSet(p.Handle)) n++; } catch { }
        status.Text = "Freed memory from " + n + " client(s). It grows back as they need it.";
    }

    // ---------------- Settings ----------------

    Panel BuildSettings()
    {
        var p = new Panel { BackColor = Theme.Bg, AutoScroll = true };
        int x = D(26), y = D(6);
        Action<Control, int> add = (c, h) => { c.Location = new Point(x, y); p.Controls.Add(c); y += h; };
        Func<string, Label> note = t => { var l = Theme.Lbl(t, 9.5f, Theme.Muted); l.MaximumSize = new Size(D(760), 0); return l; };

        add(Theme.Lbl("LAUNCHING", 9, Theme.Gold, true), D(28));
        add(Theme.Lbl("Seconds between launches", 12, Theme.Fg, true), D(30));
        add(note("Roblox can fail or rate-limit if many clients start in the same second. 5 is safe; go higher if launches fail."), D(28));
        var delay = new NumericUpDown { Minimum = 0, Maximum = 120, Value = Math.Min(120, Math.Max(0, settings.LaunchDelaySec)), Width = D(90), BackColor = Theme.Card, ForeColor = Theme.Fg };
        delay.ValueChanged += (s, e) => { settings.LaunchDelaySec = (int)delay.Value; Store.SaveSettings(settings); };
        add(delay, D(50));

        add(Theme.Lbl("CLIENTS", 9, Theme.Gold, true), D(28));
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };
        row.Controls.Add(Theme.Btn("Tile Roblox windows across my monitors", Theme.Card, (s, e) => TileWindows()));
        row.Controls.Add(Theme.Btn("Open data folder", Theme.Card, (s, e) => { Directory.CreateDirectory(Store.Dir); Process.Start("explorer.exe", Store.Dir); }));
        add(row, D(60));

        add(Theme.Lbl("ABOUT", 9, Theme.Gold, true), D(28));
        add(Theme.Lbl("Multi Alt v1.0  —  by Triple DDD", 12, Theme.Fg, true), D(32));
        add(note("Keep Multi Alt open while you play: it's what lets more than one Roblox run at once.\r\n"
               + "Accounts are encrypted with Windows (DPAPI) so only your Windows user on this PC can read them, and logins are only ever sent to roblox.com.\r\n"
               + "Data folder: " + Store.Dir), D(80));
        return p;
    }

    void TileWindows()
    {
        var windows = clients.Select(c => c.Hwnd).Where(h => h != IntPtr.Zero).ToList();
        var rects = Tiler.Tile(windows.Count, Screen.AllScreens.Select(sc => sc.WorkingArea).ToArray());
        for (int i = 0; i < windows.Count; i++)
        {
            ShowWindow(windows[i], 9); // un-maximise first or the move is ignored
            SetWindowPos(windows[i], IntPtr.Zero, rects[i].X, rects[i].Y, rects[i].Width, rects[i].Height, 0x0004 /*NOZORDER*/);
        }
        status.Text = "Tiled " + windows.Count + " window(s) across " + Screen.AllScreens.Length + " screen(s).";
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    static Mutex singleton, singletonOld;
    const long GB = 1L << 30;

    public static Image Logo()
    {
        var s = typeof(Program).Assembly.GetManifestResourceStream("logo.png");
        return s == null ? null : Image.FromStream(s);
    }

    // "" -> "" (Roblox home), "123" -> "123", "https://www.roblox.com/games/123/Name" -> "123", anything else -> null.
    public static string ParsePlace(string input)
    {
        var s = (input ?? "").Trim();
        if (s.All(char.IsDigit)) return s;
        var m = Regex.Match(s, @"(?:/games/|placeId=)(\d+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    static readonly string CrashLog = Path.Combine(Store.Dir, "crash.log");
    static void LogCrash(Exception ex)
    {
        try { Directory.CreateDirectory(Store.Dir); File.AppendAllText(CrashLog, DateTime.Now + "\r\n" + ex + "\r\n\r\n"); } catch { }
    }

    // Moves item `from` to the gap before index `insertAt` (0..Count); returns where it ended up.
    public static int MoveItem<T>(List<T> l, int from, int insertAt)
    {
        int to = insertAt > from ? insertAt - 1 : insertAt;
        if (to == from) return from;
        var x = l[from];
        l.RemoveAt(from);
        l.Insert(to, x);
        return to;
    }

    public static bool RamAllowed(int gb, long installedBytes) { return gb == 0 || gb <= Math.Ceiling(installedBytes / (double)GB); }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();

        // Each Roblox client claims "ROBLOX_singletonEvent" (older builds: ...Mutex) and quits if another client has it.
        // Owning both names first as mutexes means no client can claim them, so all of them run.
        bool createdNew = false, createdOld;
        try { singleton = new Mutex(true, "ROBLOX_singletonEvent", out createdNew); } catch (WaitHandleCannotBeOpenedException) { }
        try { singletonOld = new Mutex(true, "ROBLOX_singletonMutex", out createdOld); } catch (WaitHandleCannotBeOpenedException) { }
        if (!createdNew)
            MessageBox.Show("Roblox (or another Multi Alt) is already open.\nClose all Roblox windows, then reopen Multi Alt, or only one Roblox can run.", "Multi Alt");

        // Write every crash to crash.log instead of vanishing silently.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => { LogCrash(e.Exception); MessageBox.Show("Multi Alt hit an error and kept running:\n" + e.Exception.Message + "\n\nDetails saved to " + CrashLog, "Multi Alt"); };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);

        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.DefaultConnectionLimit = 16;
        SetProcessDPIAware();
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.S = g.DpiX / 96f;
        Application.EnableVisualStyles();

        List<Account> accounts;
        try { accounts = Store.Load(); }
        catch (Exception ex)
        {
            // never start with an empty list over an unreadable file, or the next save would wipe it
            MessageBox.Show("Couldn't read saved accounts (" + ex.Message + ").\nNothing was changed. Data folder: " + Store.Dir, "Multi Alt");
            return 1;
        }
        Application.Run(new MainForm(accounts));
        GC.KeepAlive(singleton);
        GC.KeepAlive(singletonOld);
        return 0;
    }

    static int SelfTest()
    {
        var orig = new List<Account> { new Account { Name = "Main", Cookie = "_|WARNING:abc|_x", UserId = 42, Username = "u", DisplayName = "D", Password = "pw" } };
        var back = Store.Decode(Store.Encode(orig));
        Check(back.Count == 1 && back[0].Cookie == orig[0].Cookie && back[0].UserId == 42 && back[0].DisplayName == "D" && back[0].Password == "pw", "store roundtrip");
        var legacy = Store.Decode(ProtectedData.Protect(Encoding.UTF8.GetBytes("Main\tabc\nAlt\tdef"), null, DataProtectionScope.CurrentUser));
        Check(legacy.Count == 2 && legacy[1].Name == "Alt" && legacy[1].Cookie == "def", "legacy store");

        var cells = Grid.Fit(155, 1600, 900, 8, 22, 100000);
        Check(cells.Count == 155 && cells.All(c => c.Right <= 1600 && c.Bottom <= 900 && c.Width > 60), "grid fits 155");
        for (int i = 0; i < cells.Count; i++) for (int j = i + 1; j < cells.Count; j++) Check(!cells[i].IntersectsWith(cells[j]), "grid overlap");
        Check(Grid.Fit(0, 100, 100, 8, 22, 100000).Count == 0 && Grid.Fit(1, 1600, 900, 8, 22, 100000)[0].Width > 1400, "grid edge cases");

        Check(Grid.Fit(1, 1600, 900, 8, 22, 560)[0].Width == 560, "grid tile cap");

        var t = Tiler.Tile(5, new[] { new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1280, 1024) });
        Check(t[0] == new Rectangle(0, 0, 960, 540) && t[1] == new Rectangle(1920, 0, 640, 1024), "tile layout");

        var uri = RobloxApi.BuildUri("TICKET", "1818");
        Check(uri.StartsWith("roblox-player:1+gameinfo:TICKET+") && uri.Contains("placeId%3D1818") && uri.Contains("launchmode:play"), "uri");

        Check(BrowserLogin.ExtractCookie("{\"id\":3,\"result\":{\"cookies\":[{\"name\":\"RBXSource\",\"value\":\"x\"},{\"name\":\".ROBLOSECURITY\",\"value\":\"_|WARNING:-DO-NOT-SHARE|_ABC\",\"domain\":\".roblox.com\"}]}}")
              == "_|WARNING:-DO-NOT-SHARE|_ABC" && BrowserLogin.ExtractCookie("{\"id\":1,\"result\":{\"cookies\":[]}}") == null, "cookie extract");

        Check(ParsePlace("") == "" && ParsePlace(" 920587237 ") == "920587237" && ParsePlace("https://www.roblox.com/games/920587237/Adopt-Me") == "920587237"
              && ParsePlace("roblox.com/games/start?placeId=55&launchData=x") == "55" && ParsePlace("adopt me") == null, "place parse");

        var l1 = new List<char>("abcd"); var l2 = new List<char>("abcd"); var l3 = new List<char>("abcd");
        Check(MoveItem(l1, 0, 3) == 2 && new string(l1.ToArray()) == "bcad" && MoveItem(l2, 3, 0) == 0 && new string(l2.ToArray()) == "dabc"
              && MoveItem(l3, 1, 2) == 1 && new string(l3.ToArray()) == "abcd" && MoveItem(l3, 1, 4) == 3 && new string(l3.ToArray()) == "acdb", "reorder");

        long ram16 =16L * GB - 300L * 1024 * 1024; // what Windows reports for a "16 GB" PC
        Check(Program.RamAllowed(16, ram16) && !Program.RamAllowed(24, ram16) && Program.RamAllowed(0, ram16), "ram options");
        Console.WriteLine("selftest ok");
        return 0;
    }

    static void Check(bool ok, string what) { if (!ok) { Console.WriteLine("FAIL: " + what); Environment.Exit(1); } }
}

