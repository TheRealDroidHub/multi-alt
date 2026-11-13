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
    public bool Skip { get; set; }           // unticked: "Launch all" and Follow/Join leave it out
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
    public bool AutoRejoin { get; set; }       // off by default
    public bool MuteRoblox { get; set; }
    public bool CheckUpdates { get; set; }
    public bool KeepRobloxUpdated { get; set; }        // off by default
    public bool RemoveOldRobloxVersions { get; set; }  // off by default, needs KeepRobloxUpdated
    public string LastRobloxUpdateVersion { get; set; } // the Roblox version Multi Alt last ran the installer for (never twice)
    public bool CloseToTray { get; set; }
    public bool StartMenuShortcut { get; set; }        // off by default
    public bool CleanCrashHandlers { get; set; }       // off by default
    public string AcceptedTerms { get; set; }          // fingerprint of the Terms text the user pressed "I agree" on
    public AppSettings() { LowPriorityBackground = true; LaunchDelaySec = 5; PlaceId = ""; CheckUpdates = true; }
}

class Client { public int Pid; public IntPtr Hwnd; public long Ram; public double Cpu; public Account Account; }

static class Store
{
    static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static readonly string Dir = Path.Combine(AppData, "MultiAlt");
    public static readonly string AvatarDir = Path.Combine(Dir, "avatars");
    static readonly string AccountsFile = Path.Combine(Dir, "accounts.dat");
    static readonly string OldAccountsFile = Path.Combine(AppData, "MultiRoblox", "accounts.dat"); // from the first version
    static readonly string SettingsFile = Path.Combine(Dir, "settings.json");
    public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    // Which running client belongs to which account (pid, start time, user ID; no logins), so restarting
    // Multi Alt doesn't forget who's who. The start time stops a reused PID from matching a different program.
    public static readonly string RunningFile = Path.Combine(Dir, "running.txt");
    public static string RunningLine(int pid, long userId)
    {
        using (var p = Process.GetProcessById(pid)) return pid + "\t" + p.StartTime.ToFileTimeUtc() + "\t" + userId;
    }
    public static Dictionary<int, long> ParseRunning(IEnumerable<string> lines) // pid -> userId, only for processes still alive
    {
        var d = new Dictionary<int, long>();
        foreach (var l in lines)
        {
            var f = l.Split('\t');
            int pid; long start, uid;
            if (f.Length != 3 || !int.TryParse(f[0], out pid) || !long.TryParse(f[1], out start) || !long.TryParse(f[2], out uid)) continue;
            try { using (var p = Process.GetProcessById(pid)) if (p.StartTime.ToFileTimeUtc() == start) d[pid] = uid; } catch { } // gone
        }
        return d;
    }

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

    public static bool Frozen; // set once Burn starts: a save still queued can't write the burned data back

    // Write to a temp file then swap, so a crash mid-save never wipes hundreds of saved accounts.
    static void WriteSafely(string path, byte[] data)
    {
        if (Frozen) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    // Overwrites a file with random bytes before deleting it, so recovery tools can't bring it back.
    public static void Shred(string f)
    {
        try
        {
            var junk = new byte[new FileInfo(f).Length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(junk);
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Write, FileShare.None)) { fs.Write(junk, 0, junk.Length); fs.Flush(true); }
            File.Delete(f);
        }
        catch { }
    }

    // Deletes everything Multi Alt saved (shredding the logins first). Returns folders that couldn't be fully removed.
    public static List<string> Burn()
    {
        var left = new List<string>();
        foreach (var dir in new[] { Dir, Path.GetDirectoryName(OldAccountsFile) })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "accounts.dat*")) Shred(f);
            for (int i = 0; i < 10; i++)
            {
                try { Directory.Delete(dir, true); break; }
                catch { Thread.Sleep(300); } // a closing browser can hold its profile for a moment
            }
            if (Directory.Exists(dir)) left.Add(dir);
        }
        return left;
    }
}

static class RobloxApi
{
    public static string Http(string url, string cookie)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "MultiAlt/" + Program.Version;
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

    // Username -> user ID (0 if no such user). Public endpoint, no login sent.
    public static long UserIdFor(string username)
    {
        var body = Encoding.UTF8.GetBytes(Store.Json.Serialize(new Dictionary<string, object> { { "usernames", new[] { username } }, { "excludeBannedUsers", true } }));
        var req = (HttpWebRequest)WebRequest.Create("https://users.roblox.com/v1/usernames/users");
        req.Method = "POST";
        req.ContentType = "application/json";
        req.UserAgent = "MultiAlt/" + Program.Version;
        req.Timeout = 15000;
        using (var s = req.GetRequestStream()) s.Write(body, 0, body.Length);
        using (var resp = req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream()))
        {
            var d = Store.Json.Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
            foreach (var o in (System.Collections.IEnumerable)d["data"]) return Convert.ToInt64(((Dictionary<string, object>)o)["id"]);
        }
        return 0;
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
                Uri u;
                if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != "https" || !u.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)) continue; // Roblox's image CDN only
                using (var wc = new WebClient()) File.WriteAllBytes(Path.Combine(Store.AvatarDir, Convert.ToInt64(item["targetId"]) + ".png"), wc.DownloadData(url)); // a failed download keeps the old picture
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
            req.UserAgent = "MultiAlt/" + Program.Version;
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

    // query: "" = Roblox home, else a PlaceLauncher request from Program.ParseTarget or a follow ("request=RequestFollowUser&userId=...").
    public static string BuildUri(string ticket, string query)
    {
        var rng = new Random();
        var tracker = rng.Next(100000, 999999).ToString() + rng.Next(100000, 999999);
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var uri = "roblox-player:1+gameinfo:" + ticket + "+launchtime:" + time + "+browsertrackerid:" + tracker
                + "+robloxLocale:en_us+gameLocale:en_us+channel:";
        if (string.IsNullOrEmpty(query)) return uri + "+launchmode:app";
        var launcher = "https://www.roblox.com/Game/PlaceLauncher.ashx?" + query + "&browserTrackerId=" + tracker + "&isPlayTogetherGame=false";
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
        var browser = Program.StartNormal(new ProcessStartInfo(exe, "--user-data-dir=\"" + profile + "\" --remote-debugging-port=" + port
            + " --no-first-run --no-default-browser-check --new-window " + url));
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
        var profile = Path.Combine(Store.Dir, "browser", a.UserId > 0 ? a.UserId.ToString() : "name_" + Regex.Replace(a.Name ?? "", "[^A-Za-z0-9_]", "_")); // a nickname can't point outside
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
    static readonly object gate = new object(); // launches, the Performance page and a Roblox update can all apply it at once

    public static int Apply(int fps)
    {
        lock (gate)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(RobloxInstall.Root); } catch { return 0; } // Roblox isn't installed
            int n = 0;
            foreach (var dir in dirs)
            {
                try
                {
                    if (!File.Exists(Path.Combine(dir, "RobloxPlayerBeta.exe"))) continue;
                    var f = Path.Combine(dir, "ClientSettings", "ClientAppSettings.json");
                    var d = (File.Exists(f) ? Store.Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f)) : null) ?? new Dictionary<string, object>();
                    object cur;
                    bool has = d.TryGetValue("DFIntTaskSchedulerTargetFps", out cur);
                    if (fps > 0 ? !(has && Convert.ToString(cur) == fps.ToString()) : has) // only write when it changes
                    {
                        if (fps > 0) d["DFIntTaskSchedulerTargetFps"] = fps; else d.Remove("DFIntTaskSchedulerTargetFps");
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        File.WriteAllText(f, Store.Json.Serialize(d));
                    }
                    n++;
                }
                catch { } // a broken user-edited file is left alone
            }
            return n;
        }
    }
}

// The normal Roblox player install in %LOCALAPPDATA%\Roblox\Versions: used to start the live client directly, and by the
// opt-in "Keep Roblox updated" and "Remove old Roblox versions". Reads the registry but never writes it, and never touches
// Program Files, Studio, the Microsoft Store version or other launchers (Bloxstrap, Fishstrap...).
static class RobloxInstall
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
    static readonly object gate = new object();
    static string live;
    static DateTime liveAt = DateTime.MinValue;

    public static bool IsVersionName(string s) { return s != null && Regex.IsMatch(s, @"^version-[0-9a-f]{16}\z"); }

    public static string ParseLive(string json)
    {
        try
        {
            var d = Store.Json.Deserialize<Dictionary<string, object>>(json);
            object v;
            if (d != null && d.TryGetValue("clientVersionUpload", out v) && IsVersionName(v as string)) return (string)v;
        }
        catch { }
        return null;
    }

    // The Roblox version being served now (a client that differs updates itself first). Public endpoint, no login sent.
    // fresh: ask now, null if Roblox can't be reached. Otherwise asks at most every 5 minutes and keeps the last answer.
    public static string LiveVersion(bool fresh)
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            if (fresh || Program.Due(liveAt, now, 300))
            {
                liveAt = now;
                string v = null;
                try { v = ParseLive(RobloxApi.Http("https://clientsettingscdn.roblox.com/v2/client-version/WindowsPlayer", null)); } catch { }
                if (v != null) live = v;
                else if (fresh) return null;
            }
            return live;
        }
    }

    // The program in a registry "open" command: "C:\path\app.exe" %1 -> C:\path\app.exe
    public static string CommandExe(string command)
    {
        var m = Regex.Match(command ?? "", "^\\s*(?:\"([^\"]+)\"|(\\S+))");
        return !m.Success ? null : m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    // What this Windows user's roblox-player: (or roblox:) links run - what the website's Play button starts.
    public static string HandlerExe(string protocol) { return RegExe(@"Software\Classes\" + protocol + @"\shell\open\command", null); }

    // What Windows' "uninstall Roblox" entry runs (its folder is kept, so uninstalling Roblox keeps working).
    public static string UninstallerExe() { return RegExe(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\roblox-player", "UninstallString"); }

    static string RegExe(string key, string value)
    {
        try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)) return k == null ? null : CommandExe(k.GetValue(value) as string); }
        catch { return null; }
    }

    // "version-..." when exe sits directly in a version folder under root, else null (another launcher, Program Files...).
    public static string VersionOf(string exe) { return VersionOf(exe, Root); }
    public static string VersionOf(string exe, string root)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe));
            var name = Path.GetFileName(dir);
            if (IsVersionName(name) && string.Equals(Path.GetDirectoryName(dir), Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return name;
        }
        catch { } // no path
        return null;
    }

    public static string ClientExe(string version)
    {
        if (!IsVersionName(version)) return null;
        var exe = Path.Combine(Root, version, "RobloxPlayerBeta.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static bool IsOpen(string name)
    {
        var ps = Process.GetProcessesByName(name);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    // The first Roblox player program that's open (client, launcher, installer or crash handler), else null.
    public static string Running()
    {
        foreach (var n in new[] { "RobloxPlayerBeta", "RobloxPlayerLauncher", "RobloxPlayerInstaller", "RobloxCrashHandler" })
            if (IsOpen(n)) return n;
        return null;
    }

    // A launch link goes to the client as one command-line argument: refuse anything that could split or escape it.
    public static bool SafeArg(string s) { return !string.IsNullOrEmpty(s) && s.All(c => c > ' ' && c < 127 && c != '"'); }

    // Waits until no Roblox installer runs, two checks in a row (it restarts itself from a temp folder). Adds the
    // installers it sees to seen. False if one still runs after max.
    public static bool WaitForInstaller(TimeSpan max, ICollection<int> seen)
    {
        var end = DateTime.UtcNow + max;
        for (int clear = 0; ; Thread.Sleep(1000))
        {
            var ps = Process.GetProcessesByName("RobloxPlayerInstaller");
            foreach (var p in ps) { if (seen != null) seen.Add(p.Id); p.Dispose(); }
            clear = ps.Length == 0 ? clear + 1 : 0;
            if (clear >= 2) return true;
            if (DateTime.UtcNow >= end) return false;
        }
    }

    // Roblox's own installer (it ships in every version folder): the one next to the client roblox-player: links use, else the newest.
    public static string Installer()
    {
        try
        {
            var v = VersionOf(HandlerExe("roblox-player"));
            var f = v == null ? null : Path.Combine(Root, v, "RobloxPlayerInstaller.exe");
            if (f != null && File.Exists(f)) return f;
            return Directory.GetDirectories(Root).Where(d => IsVersionName(Path.GetFileName(d)))
                .Select(d => Path.Combine(d, "RobloxPlayerInstaller.exe")).Where(x => File.Exists(x))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(x)).FirstOrDefault();
        }
        catch { return null; }
    }

    public class VersionDir { public string Name; public bool Player, Studio, Link; }

    public static List<VersionDir> Scan()
    {
        var list = new List<VersionDir>();
        foreach (var d in Directory.GetDirectories(Root))
        {
            var v = new VersionDir { Name = Path.GetFileName(d) };
            try
            {
                v.Link = (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0;
                if (!v.Link)
                {
                    v.Player = File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe"));
                    v.Studio = Directory.GetFiles(d, "RobloxStudio*").Length > 0;
                }
            }
            catch { v.Link = true; } // can't read it: never touch it
            list.Add(v);
        }
        return list;
    }

    // Old Roblox player folders that are safe to delete: a real "version-" folder holding the player and no Studio, not a
    // link, and not one of keep (the live version and whatever Roblox links point at).
    public static List<string> PickOld(IEnumerable<VersionDir> dirs, IEnumerable<string> keep)
    {
        var k = new HashSet<string>(keep.Where(x => x != null), StringComparer.OrdinalIgnoreCase);
        return dirs.Where(d => IsVersionName(d.Name) && d.Player && !d.Studio && !d.Link && !k.Contains(d.Name)).Select(d => d.Name).ToList();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcEntry // PROCESSENTRY32W
    {
        public uint Size, Usage, Pid;
        public IntPtr Heap;
        public uint Module, Threads, Parent;
        public int Base;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32FirstW(IntPtr snap, ref ProcEntry e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] static extern bool Process32NextW(IntPtr snap, ref ProcEntry e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    // pid -> (parent pid, exe name) for every process, from the Windows process snapshot.
    public static Dictionary<int, Tuple<int, string>> Processes()
    {
        var d = new Dictionary<int, Tuple<int, string>>();
        var h = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return d;
        try
        {
            var e = new ProcEntry { Size = (uint)Marshal.SizeOf(typeof(ProcEntry)) };
            for (bool ok = Process32FirstW(h, ref e); ok; ok = Process32NextW(h, ref e))
                d[(int)e.Pid] = Tuple.Create((int)e.Parent, e.Exe);
        }
        finally { CloseHandle(h); }
        return d;
    }

    // Closes Roblox clients one of these installers opened on its own after updating, if they started after since.
    public static void CloseOpenedBy(ICollection<int> installers, DateTime since)
    {
        foreach (var kv in Processes())
        {
            if (!installers.Contains(kv.Value.Item1) || !string.Equals(kv.Value.Item2, "RobloxPlayerBeta.exe", StringComparison.OrdinalIgnoreCase)) continue;
            try { using (var p = Process.GetProcessById(kv.Key)) if (p.ProcessName == "RobloxPlayerBeta" && p.StartTime >= since) p.Kill(); }
            catch { } // gone already
        }
    }

    // "Clean up leftover crash handlers": kills RobloxCrashHandler processes whose client is gone (or whose parent PID now
    // belongs to a newer program). Anything it can't check is left running. Returns how many it closed.
    public static int CleanCrashHandlers()
    {
        int n = 0;
        var all = Processes();
        foreach (var kv in all)
        {
            if (kv.Value.Item1 == 0 || !string.Equals(kv.Value.Item2, "RobloxCrashHandler.exe", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using (var child = Process.GetProcessById(kv.Key))
                {
                    if (child.ProcessName != "RobloxCrashHandler") continue;
                    var childStart = child.StartTime;
                    DateTime? parentStart = null;
                    if (all.ContainsKey(kv.Value.Item1))
                        try { using (var parent = Process.GetProcessById(kv.Value.Item1)) parentStart = parent.StartTime; }
                        catch (ArgumentException) { } // exited a moment ago
                    if (Program.IsOrphan(childStart, parentStart)) { child.Kill(); n++; }
                }
            }
            catch { } // couldn't check it: leave it running
        }
        return n;
    }
}

// "Mute Roblox": the same per-app mute as the Windows volume mixer (Core Audio). Nothing touches Roblox itself.
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int flow, int stateMask, out IMMDeviceCollection devices); }
[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint n); [PreserveSig] int Item(uint i, out IMMDevice device); }
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr g, int flags, out IntPtr c);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr g, int flags, out IntPtr v);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
}
[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator { [PreserveSig] int GetCount(out int n); [PreserveSig] int GetSession(int i, out IAudioSessionControl2 s); }
[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2
{
    void R1(); void R2(); void R3(); void R4(); void R5(); void R6(); void R7(); void R8(); void R9(); // IAudioSessionControl slots (unused)
    void R10(); void R11();                                                                        // session identifiers (unused)
    [PreserveSig] int GetProcessId(out uint pid);
}
[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float v, ref Guid ctx);
    [PreserveSig] int GetMasterVolume(out float v);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

static class RobloxAudio
{
    // Mutes or unmutes every audio session these processes own, on every active speaker/headset.
    public static void SetMute(HashSet<int> pids, bool mute)
    {
        if (pids.Count == 0) return;
        IMMDeviceCollection devs;
        if (((IMMDeviceEnumerator)new MMDeviceEnumeratorCom()).EnumAudioEndpoints(0 /*render*/, 1 /*active*/, out devs) != 0) return;
        uint n;
        devs.GetCount(out n);
        var iid = typeof(IAudioSessionManager2).GUID;
        for (uint i = 0; i < n; i++)
        {
            IMMDevice dev;
            object o;
            IAudioSessionEnumerator sessions;
            int count;
            if (devs.Item(i, out dev) != 0 || dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out o) != 0) continue;
            if (((IAudioSessionManager2)o).GetSessionEnumerator(out sessions) != 0 || sessions.GetCount(out count) != 0) continue;
            for (int k = 0; k < count; k++)
            {
                IAudioSessionControl2 s;
                uint pid;
                bool now;
                if (sessions.GetSession(k, out s) != 0 || s.GetProcessId(out pid) != 0 || !pids.Contains((int)pid)) continue;
                var vol = (ISimpleAudioVolume)s;
                var ctx = Guid.Empty;
                if (vol.GetMute(out now) == 0 && now != mute) vol.SetMute(mute, ref ctx);
            }
        }
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
    double robloxCpu;                                                 // written by Poll, read for display
    Dictionary<int, TimeSpan> cpuPrev = new Dictionary<int, TimeSpan>(); // Poll thread only
    DateTime cpuAt = DateTime.UtcNow;
    readonly Dictionary<Account, string> targetFor = new Dictionary<Account, string>(); // where a queued launch goes; null = the Game link box
    // Auto-rejoin state, UI thread only.
    readonly HashSet<Account> userClosed = new HashSet<Account>(), rejoinStopped = new HashSet<Account>();
    readonly Dictionary<Account, List<DateTime>> rejoins = new Dictionary<Account, List<DateTime>>();
    readonly long installedRam = (long)new Microsoft.VisualBasic.Devices.ComputerInfo().TotalPhysicalMemory;
    readonly BlockingCollection<Account> launchQueue = new BlockingCollection<Account>();
    readonly Dictionary<IntPtr, IntPtr> thumbs = new Dictionary<IntPtr, IntPtr>();
    List<Rectangle> homeCells = new List<Rectangle>();
    List<Client> homeClients = new List<Client>();
    readonly Dictionary<long, Image> avatars = new Dictionary<long, Image>();
    System.Threading.Timer poller;
    int polling;
    readonly object updateLock = new object(); // one Roblox update at a time; a launch never starts under one
    int userLaunch;                            // 1 = you pressed Play, Launch all or Join: check for a Roblox update first
    DateTime lastTrim, lastCrashClean;         // Poll thread only
    DateTime nextAvatars = DateTime.UtcNow.AddHours(8); // Poll thread only; startup already fetches them once
    NotifyIcon tray;
    bool exiting, trayTipShown;
    CheckBox muteCheck;
    ContextMenuStrip popup;                    // the last right-click menu; disposed when the next one opens
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);

    NavItem[] nav;
    Panel[] pages;
    int page;
    Label status, pageTitle, homeInfo, memText;
    DBPanel grid, memBar;
    ListBox list;
    readonly Font fName = new Font("Segoe UI Semibold", 12f), fSmall = new Font("Segoe UI", 9.5f), fBtn = new Font("Segoe UI Semibold", 9.5f), fEmpty = new Font("Segoe UI", 13f);

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
        // Docked to the bottom in this order: update button, then version, then Terms of Service underneath.
        updateBtn = new NavItem("Update available") { Dock = DockStyle.Bottom, Height = D(44), TextColor = Theme.Gold, Visible = false };
        updateBtn.Click += (s, e) => { if (updateBtn.Tag is string) using (Program.StartNormal(new ProcessStartInfo((string)updateBtn.Tag))) { } };
        side.Controls.Add(updateBtn);
        side.Controls.Add(new Label { Text = "v" + Program.Version, Dock = DockStyle.Bottom, Height = D(20), TextAlign = ContentAlignment.MiddleCenter,
                                      ForeColor = Theme.Muted, Font = new Font("Segoe UI", 8.5f) });
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

        // Tray icon (right-click for quick actions). With "Close to tray" on, ✕ hides Multi Alt here instead of closing it.
        tray = new NotifyIcon { Icon = Icon, Text = "Multi Alt", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        var tm = tray.ContextMenuStrip;
        tm.Items.Add("Open Multi Alt", null, (s, e) => ShowFromTray());
        tm.Items.Add("Launch all", null, (s, e) =>
        {
            if (!IsWindowEnabled(Handle)) { ShowFromTray(); return; } // a dialog is open (removing an account, uninstalling...): finish that first
            LaunchAll();
            if (!Visible && status.Text != "") tray.ShowBalloonTip(3000, "Multi Alt", status.Text, ToolTipIcon.Info);
        });
        var muteItem = tm.Items.Add("Mute Roblox", null, (s, e) => muteCheck.Checked = !muteCheck.Checked);
        tm.Items.Add("Kill all Roblox", null, (s, e) => { bool free = IsWindowEnabled(Handle); ShowFromTray(); if (free) KillAll(); }); // a dialog already open: just show it
        tm.Items.Add(new ToolStripSeparator());
        tm.Items.Add("Exit", null, (s, e) => { if (!IsWindowEnabled(Handle)) { ShowFromTray(); return; } exiting = true; Close(); });
        tm.Opening += (s, e) => muteItem.Text = muteCheck.Checked ? "Unmute Roblox" : "Mute Roblox";
        tray.DoubleClick += (s, e) => ShowFromTray();
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { if (tray != null) tray.Visible = false; } catch { } }; // a crash leaves no ghost icon
    }

    Button sizeBtn;
    NavItem updateBtn;

    // Asks GitHub for the newest release number only; nothing about you or your accounts is sent. Never downloads anything.
    void CheckForUpdate()
    {
        try
        {
            var d = Store.Json.Deserialize<Dictionary<string, object>>(RobloxApi.Http("https://api.github.com/repos/TheRealDroidHub/multi-alt/releases/latest", null));
            var tag = d["tag_name"] as string;
            var url = d["html_url"] as string;
            if (!Program.IsNewer(tag, Program.Version) || url == null || !url.StartsWith("https://github.com/TheRealDroidHub/multi-alt/", StringComparison.Ordinal)) return;
            BeginInvoke((Action)(() => { updateBtn.Text = "Update available (" + tag + ")"; updateBtn.Tag = url; updateBtn.Visible = true; }));
        }
        catch { } // offline or GitHub down: just no button
    }

    const string Terms = @"MULTI ALT - TERMS OF SERVICE AND DISCLAIMER
Developer: Triple DDD
Last updated: 13 November 2026

IMPORTANT - PLEASE READ. By downloading, installing, opening, running or using Multi Alt (""the Software"") in any way, you confirm that you have read and understood these terms and that you agree to all of them. Multi Alt asks you to press ""I agree"" before you first use it and again whenever these terms change, but using the Software in any way also means you accept them, even without pressing a button. If you do not agree, do not download, install or use the Software, and delete every copy you have.

""Triple DDD"", ""we"" and ""us"" mean the developer of the Software. ""You"" means the person using the Software. ""Including"" means ""including but not limited to"".

1. WHAT MULTI ALT IS
Multi Alt is a free, standalone account manager and multi-instance launcher. It lets you sign in to your own Roblox accounts, keep them in one place, and run more than one official Roblox client at the same time on your own computer. That is all it does.

2. WHAT MULTI ALT DOES NOT DO
Multi Alt does not inject code into the Roblox client or any game, does not patch or change Roblox's program code, and does not read or change Roblox's memory. It contains no exploits, cheats, scripts, bots, macros, malware, spyware or tracking of any kind. It does not play games for you (see section 4 for the optional Anti-AFK setting). It starts the official Roblox client installed on your computer with the same launch link the Roblox website uses, and shows previews of those windows using standard Windows features.

3. OPTIONAL FEATURES
You decide whether to use each of these, and you can turn any of them off at any time. Settings marked ""off by default"" stay off until you turn them on.
- FPS cap (off until you set one): writes a frame rate limit into Roblox's own client settings file (ClientAppSettings.json) for each Roblox version installed on your computer. Setting it back to off removes it.
- Lower priority for background clients (on by default): uses standard Windows features (lower process priority and Windows efficiency mode) for the Roblox clients you are not playing.
- RAM cap (off until you set one) and the Free memory now button: ask Windows to trim the memory Roblox clients are using. The RAM cap does this at most every 30 seconds and never to the client you are playing. This does not read or change what is in Roblox's memory.
- Auto-rejoin (off by default): relaunches the official Roblox client when one closes by itself, at most 2 times per account in 8 minutes.
- Mute Roblox: uses the standard Windows volume mixer.
- Keep Roblox updated (off by default): runs Roblox's own official installer when Roblox has a new version, only while no Roblox program is open, and never twice for the same version.
- Remove old Roblox versions (off by default): deletes only old Roblox player version folders, only while no Roblox program is open, and never the current version or Roblox Studio.
- Clean up leftover crash handlers (off by default): closes only Roblox crash handler programs whose Roblox client has already closed.
- Kill all Roblox and Close this client: close Roblox programs the same way ending them in Task Manager does. Anything not yet saved in a game may be lost.
- Close to tray and Show in Start Menu (off by default): keep Multi Alt running next to the clock, and add a Multi Alt shortcut to your Start Menu that is removed when you turn the setting off.

4. ANTI-AFK (OFF BY DEFAULT)
Anti-AFK is turned OFF by default. You choose whether to use it. When you turn it on, every 15 minutes Multi Alt briefly switches to each idle Roblox window and presses the Space key once, the same as you tapping Space on your own keyboard. This keeps Roblox from disconnecting you for being idle. It is simple keyboard input emulation only: it does not inject scripts or code into Roblox or any game, does not read or change game memory, and does not play the game for you. It waits until you are not using your mouse or keyboard, skips the window you are playing, and never presses a key unless a Roblox window is in focus.
How to turn it on or off: open the Performance tab in the sidebar and tick or untick ""Anti-AFK"". You can also press the ""Anti-AFK setting"" button at the bottom of this window to go straight there. Roblox and some games or servers may have their own rules about staying idle or about automated input; using Anti-AFK is at your own discretion and risk (see sections 7 to 9).

5. ROBLOX VERSIONS, UPDATES AND YOUR FILES
Roblox can change its client, installer, launch links, version system and rules at any time without notice, and Multi Alt may stop working, or work differently, because of such a change. Multi Alt tries to start the Roblox version that Roblox reports as current (or hands the launch to your other Roblox launcher if you use one). We do not promise that the Roblox version Multi Alt starts or installs is the newest, the best, the right one for your computer or for any game, or that it will work at all, and we do not promise that any feature will keep working with future versions of Roblox, Windows or Multi Alt.
Some features change files or programs on your computer (see section 3). You use them at your own risk. Triple DDD is not responsible for any problem with your files, folders, settings, Roblox installation or computer, including failed, partial or unwanted updates, deleted, changed or corrupted files, lost settings, lost unsaved game progress, crashes or freezes, or having to reinstall Roblox or Windows. Keep backups of anything important. If Roblox stops working, you can reinstall it from roblox.com at any time.
Removing an account, and Remove Multi Alt (especially with BURN), permanently delete data. We cannot recover deleted data.
New versions of Multi Alt may add, change or remove features, and each one comes with its own terms (see section 16).

6. YOUR ACCOUNT DATA AND PRIVACY
Your account logins (and any passwords you choose to save) are stored only on your own computer, in %APPDATA%\MultiAlt, encrypted with Windows' built-in data protection for your Windows user. They are sent only to official Roblox servers (roblox.com) to sign you in, show your account details and launch games. Multi Alt has no servers of its own, and Triple DDD never receives, collects, sells or shares your account data.
If the update check is on (Settings, on by default), Multi Alt asks api.github.com for the latest version number when it starts; no account data is sent and nothing is downloaded. To start the current Roblox version, Multi Alt asks Roblox (clientsettingscdn.roblox.com) for the current Roblox version number; no login or account data is sent. Profile pictures are downloaded from Roblox's image servers (rbxcdn.com) with no login attached. Login via Roblox and Open in browser use Chrome or Edge on your computer; Open in browser keeps that account's browser login in the Multi Alt folder until you remove the account. If Multi Alt crashes, it writes the error to crash.log in the same folder; it contains no logins and is never sent anywhere.
Anyone who can use your Windows account, and any malware on your computer, may be able to reach this data. Keeping your computer, your Windows account and your accounts safe is your responsibility. Never share your accounts.dat file, login cookies or passwords with anyone, including anyone who says they are from Multi Alt or Triple DDD.

7. USE AT YOUR OWN RISK - NO WARRANTY
The Software is provided ""AS IS"" and ""AS AVAILABLE"", without warranty of any kind, express or implied, including warranties of merchantability, fitness for a particular purpose, title and non-infringement. We do not promise that the Software is free of bugs, errors or security issues, that it will be compatible with your computer or with Roblox, or that it will keep working or keep being updated. You use it entirely at your own risk and discretion.

8. NO RESPONSIBILITY FOR BANS OR ANY ACCOUNT ACTION
Triple DDD is not responsible or liable for any action taken against your accounts, devices or network by Roblox Corporation, any game developer or anyone else, for any reason. This includes temporary or permanent bans, suspensions, terminations, warnings, account flags, moderation actions, bot or alt detection, IP or device bans, rate limits, verification checks, login problems, and loss of items, Robux, currency, progress, data or account value. It also includes any action taken because you used third-party software, ran several accounts or clients at once, used Anti-AFK or any other feature of Multi Alt, or broke a rule of Roblox or of a game, whether or not you knew about that rule. We cannot reverse or appeal these actions for you, and we do not cover, insure, guarantee, restore, refund or compensate any account for any reason.

9. YOUR RESPONSIBILITIES
You are responsible for following the Roblox Terms of Use, the Roblox Community Standards, the rules of every game you play and the laws that apply to you. Only add accounts that belong to you. It is your choice how many accounts you run at once; running many accounts may lead to rate limits, bot or alt detection, IP restrictions or other actions by Roblox. Use a reasonable number of accounts and use the Software responsibly. You are responsible for everything done with the Software on your computer and accounts.

10. THIRD-PARTY SOFTWARE AND SERVICES
Multi Alt works with software and services made by others, including Roblox (its client, installer and servers), Windows, Chrome, Edge, GitHub and any other Roblox launcher you use. We do not control them and are not responsible for how they work, what they do with your data, or any change, outage or problem they cause. Your use of them is covered by their own terms.

11. NO AFFILIATION
Multi Alt and Triple DDD are not affiliated with, endorsed by, sponsored by or connected to Roblox Corporation. ""Roblox"" and related names and logos are trademarks of Roblox Corporation. We are also not affiliated with GitHub, Microsoft, Google, Valve/Steam, Bloxstrap, Fishstrap or any other company or project. Multi Alt is made by an independent, standalone developer.

12. OPEN SOURCE AND OFFICIAL DOWNLOADS
Multi Alt is free and open source under the MIT License. Anyone may read the full source code to check exactly what it does. The only official download is the Releases page of the official Multi Alt GitHub repository (the page the Update available button opens). Copies from anywhere else, and versions changed or rebuilt by anyone else, may have been modified, may be unsafe and are not supported, and Triple DDD is not responsible for them. If anyone sells you Multi Alt, or asks for your login, password or cookie so you can use it, it is a scam.

13. LIMITATION OF LIABILITY
To the maximum extent permitted by law, in no event shall Triple DDD be liable for any direct, indirect, incidental, special, consequential, punitive or exemplary damages, or for any loss of accounts, items, Robux, data, files, profits or goodwill, or any damage to your computer, arising from or related to the Software or these terms, or your use of or inability to use the Software, under any legal theory (contract, negligence or otherwise), even if advised of the possibility of such damages. If any liability cannot be excluded, our total liability to you for all claims together is limited to the amount you paid for the Software, which is nothing, because it is free.

14. INDEMNITY
To the extent the law allows, you agree to defend, indemnify and hold harmless Triple DDD from any claims, damages, losses, costs and expenses (including reasonable legal fees) that arise from your use or misuse of the Software, your accounts, or your breaking of these terms, the Roblox Terms of Use or any law.

15. AGE
If you are under the age of legal adulthood where you live, you may only use the Software with the permission of a parent or legal guardian, who agrees to these terms for you and is responsible for your use of it. You must also meet Roblox's own age rules.

16. CHANGES TO THESE TERMS
We may change these terms in any new version of the Software. The terms shown in the version you are using are the terms that apply to it, and using a new version means you accept its terms. When the terms change, Multi Alt shows them again and does not open until you press ""I agree"". We may change, pause or stop working on the Software at any time without notice.

17. GENERAL
Some places do not allow certain warranties or liability to be excluded or limited. In those places, these terms exclude and limit them only as far as the law allows, and nothing in these terms takes away rights you have by law that cannot be waived. If any part of these terms is found invalid or unenforceable, that part is limited or removed only as much as needed and the rest stays in full effect. If we do not enforce any part of these terms, we have not given up the right to enforce it later. The MIT License (the LICENSE file in the official repository) governs your rights to copy, change and share the source code; if it and these terms conflict about those rights, the MIT License wins. These terms and the MIT License are the whole agreement between you and Triple DDD about the Software. Sections 5, 7, 8, 13, 14 and 17 keep applying after you stop using or delete the Software.";

    void ShowTerms() { TermsForm(this, false, () => { ShowPage(2); afkBox.Focus(); }); }

    // First run, and again whenever the Terms text changes: Multi Alt doesn't open until you press "I agree".
    public static readonly string TermsId = Program.TermsHash(Terms);
    public static bool AgreeToTerms(AppSettings s)
    {
        if (s.AcceptedTerms == TermsId) return true;
        if (!TermsForm(null, true, null)) return false;
        s.AcceptedTerms = TermsId;
        try { Store.SaveSettings(s); } catch { } // couldn't save: they're just asked again next time
        return true;
    }

    // mustAgree: "I agree" / "I don't agree" buttons, and closing the window counts as not agreeing. Returns true only on "I agree".
    static bool TermsForm(Form owner, bool mustAgree, Action toAfk)
    {
        bool agreed = false;
        var wa = (owner != null ? Screen.FromControl(owner) : Screen.FromPoint(MousePosition)).WorkingArea; // never bigger than the screen, or I agree could end up under the taskbar
        using (var f = new Form { Text = "Multi Alt - Terms of Service", BackColor = Theme.Bg, StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                                  Size = new Size(Math.Min(D(720), wa.Width), Math.Min(D(640), wa.Height)), MinimizeBox = false, ShowInTaskbar = owner == null })
        {
            try { f.Icon = owner != null ? owner.Icon : Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                    BackColor = Theme.Card, ForeColor = Theme.Fg, Font = new Font("Segoe UI", 10.5f), Text = Terms.Replace("\r\n", "\n").Replace("\n", "\r\n") };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = D(52), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(D(10)), BackColor = Theme.Bg };
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(D(16)), BackColor = Theme.Card };
            pad.Controls.Add(box);
            f.Controls.Add(pad);
            if (mustAgree)
            {
                bottom.Controls.Add(Theme.Btn("I agree", Theme.Gold, (s, e) => { agreed = true; f.Close(); }));
                bottom.Controls.Add(Theme.Btn("I don't agree", Theme.Card2, (s, e) => f.Close()));
                f.Controls.Add(new Label { Text = "Read the terms above. Pressing I agree means you have read, understood and agree to them.", Dock = DockStyle.Bottom,
                                           Height = D(30), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Theme.Bg, Font = new Font("Segoe UI", 9.5f) });
            }
            else
            {
                bottom.Controls.Add(Theme.Btn("I understand", Theme.Gold, (s, e) => f.Close()));
                bottom.Controls.Add(Theme.Btn("Anti-AFK setting", Theme.Card2, (s, e) => { f.Close(); toAfk(); }));
            }
            f.Controls.Add(bottom); // added last so it docks at the very bottom, under the note
            f.Shown += (s, e) => { box.SelectionLength = 0; box.SelectionStart = 0; f.Activate(); };
            f.ShowDialog(owner);
        }
        return agreed;
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
        LoadRunning();
        new Thread(LaunchWorker) { IsBackground = true }.Start();
        new Thread(RefreshAccountInfo) { IsBackground = true }.Start();
        new Thread(AntiAfkLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
        if (settings.CheckUpdates) new Thread(CheckForUpdate) { IsBackground = true }.Start();
        if (settings.FpsCap > 0) new Thread(() => FpsTweak.Apply(settings.FpsCap)) { IsBackground = true }.Start(); // Roblox updates make new version folders
        if (Program.Elevated()) status.Text = Program.AdminWarning;
        if (settings.KeepRobloxUpdated) new Thread(() => { try { EnsureRobloxUpdated(); } catch { } }) { IsBackground = true }.Start();
        if (Program.ShowSignal != null) new Thread(WaitForShowSignal) { IsBackground = true }.Start();
        if (settings.StartMenuShortcut) try { Program.SetStartMenu(true); } catch { } // keeps it pointing at this exe if you moved it
        poller = new System.Threading.Timer(_ => Poll(), null, 300, 2000);
    }

    void SaveRunning()
    {
        lock (pidAccount) // also keeps the launch and poll threads from writing the file at the same time
        {
            if (Store.Frozen) return;
            var lines = new List<string>();
            foreach (var kv in pidAccount) try { if (kv.Value.UserId > 0) lines.Add(Store.RunningLine(kv.Key, kv.Value.UserId)); } catch { }
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllLines(Store.RunningFile, lines); } catch { }
        }
    }

    void LoadRunning()
    {
        try
        {
            if (!File.Exists(Store.RunningFile)) return;
            foreach (var kv in Store.ParseRunning(File.ReadAllLines(Store.RunningFile)))
            {
                var a = accounts.FirstOrDefault(x => x.UserId == kv.Value);
                if (a != null) lock (pidAccount) pidAccount[kv.Key] = a;
            }
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && settings.CloseToTray && e.CloseReason == CloseReason.UserClosing && tray != null)
        {
            e.Cancel = true; // "Close to tray": keep running, the tray icon brings it back
            Hide();
            if (!trayTipShown) { trayTipShown = true; tray.ShowBalloonTip(4000, "Multi Alt is still running", "Double-click the tray icon to open it. Right-click it to exit.", ToolTipIcon.Info); }
            return;
        }
        if (poller != null) poller.Dispose();
        if (settings.MuteRoblox) try { RobloxAudio.SetMute(RobloxPids(), false); } catch { } // Windows can remember per-app mute; don't leave Roblox silent
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

    protected override void OnFormClosed(FormClosedEventArgs e) { DisposeTray(); base.OnFormClosed(e); }

    void DisposeTray()
    {
        if (tray == null) return;
        var m = tray.ContextMenuStrip; // NotifyIcon doesn't dispose its menu
        tray.Visible = false;
        tray.Dispose();
        if (m != null) m.Dispose();
        tray = null;
    }

    void ShowFromTray()
    {
        if (IsDisposed) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = settings.Windowed ? FormWindowState.Normal : FormWindowState.Maximized;
        Activate();
        RefreshUi();
    }

    // A second copy of Multi Alt sets this signal instead of running twice: show this one.
    void WaitForShowSignal() { try { while (Program.ShowSignal.WaitOne()) BeginInvoke((Action)ShowFromTray); } catch { } }

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
        homeCells = Grid.Fit(live.Count, grid.ClientSize.Width - D(16), grid.ClientSize.Height - D(8), D(10), D(Strip), D(560));
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
            var box = new Rectangle(cell.X + off.X + D(4), cell.Y + off.Y + D(4), cell.Width - D(8), cell.Height - D(Strip) - D(4));
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
        homeInfo.Text = live.Count + " running  •  Roblox using " + Gb(Interlocked.Read(ref robloxRam)) + " / " + Gb(installedRam) + " GB RAM  •  "
            + robloxCpu.ToString("0") + "% CPU  •  click a window to play it, right-click to close it  •  click the sidebar to come back here";
        grid.Invalidate();
    }

    const int Strip = 40; // label area under each Home preview: name + PID, and CPU over RAM on the right
    static string Gb(long bytes) { return (bytes / (double)GB).ToString("0.0"); }

    void PaintGrid(object s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (homeCells.Count == 0)
        {
            TextRenderer.DrawText(g, "No Roblox clients running.\nGo to Accounts and press Play or Launch all.", fEmpty, grid.ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        for (int i = 0; i < homeCells.Count && i < homeClients.Count; i++)
        {
            var c = homeCells[i];
            Theme.FillRound(g, Theme.Card, c, D(8));
            var cl = homeClients[i];
            var who = cl.Account;
            var name = (who == null ? "Roblox" : Title(who) + (string.IsNullOrEmpty(who.Username) ? "" : "  @" + who.Username)) + "  PID=" + cl.Pid;
            var strip = new Rectangle(c.X + D(8), c.Bottom - D(Strip), c.Width - D(16), D(Strip - 2));
            bool stats = c.Width > D(140);
            TextRenderer.DrawText(g, name, fSmall, new Rectangle(strip.X, strip.Y, strip.Width - (stats ? D(70) : 0), strip.Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!stats) continue;
            var half = strip.Height / 2;
            TextRenderer.DrawText(g, cl.Cpu.ToString("0") + "% CPU", fSmall, new Rectangle(strip.X, strip.Y, strip.Width, half), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Gb(cl.Ram) + " GB", fSmall, new Rectangle(strip.X, strip.Y + half, strip.Width, half), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }

    void GridClick(object s, MouseEventArgs e)
    {
        int i = homeCells.FindIndex(r => r.Contains(e.Location));
        if (i < 0 || i >= homeClients.Count) return;
        var c = homeClients[i];
        if (e.Button == MouseButtons.Right)
        {
            var menu = NewPopup();
            menu.Items.Add("Play this window", null, (a, b) => FocusClient(c.Hwnd));
            menu.Items.Add("Close this client", null, (a, b) =>
            {
                if (c.Account != null) userClosed.Add(c.Account); // you closed it, so auto-rejoin leaves it closed
                try { using (var p = Process.GetProcessById(c.Pid)) if (p.ProcessName == "RobloxPlayerBeta") p.Kill(); } catch { } // already closed: its PID may be another program now
            });
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
        Process[] procs = null;
        try
        {
            procs = Process.GetProcessesByName("RobloxPlayerBeta");
            uint fgPid;
            GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
            var pids = new HashSet<int>(procs.Select(p => p.Id));
            var mainByPid = new Dictionary<int, IntPtr>();
            var windows = TopWindows(pids, mainByPid);
            var list = new List<Client>();
            long total = 0;
            double cpuTotal = 0;
            var now = DateTime.UtcNow;
            double cpuSpan = (now - cpuAt).TotalSeconds * Environment.ProcessorCount; // 100% = every core busy
            var cpuNow = new Dictionary<int, TimeSpan>();
            foreach (var p in procs)
            {
                try
                {
                    IntPtr hwnd;
                    mainByPid.TryGetValue(p.Id, out hwnd);
                    var c = new Client { Pid = p.Id, Hwnd = hwnd, Ram = p.WorkingSet64 };
                    lock (pidAccount) pidAccount.TryGetValue(p.Id, out c.Account);
                    // CPU % = processor time used since the last poll; Windows already counts it, so this costs nothing.
                    TimeSpan used = p.TotalProcessorTime, prev;
                    cpuNow[p.Id] = used;
                    if (cpuSpan > 0 && cpuPrev.TryGetValue(p.Id, out prev)) c.Cpu = Math.Max(0, (used - prev).TotalSeconds / cpuSpan * 100);
                    cpuTotal += c.Cpu;
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
            if (cap > 0 && total > cap && Program.Due(lastTrim, now, 30)) // over the cap: push background clients' memory out to the page file, at most every 30 s
            {
                lastTrim = now;
                foreach (var p in procs) if (p.Id != fgPid) try { EmptyWorkingSet(p.Handle); } catch { } // never the one you're playing
            }
            if (settings.CleanCrashHandlers && Program.Due(lastCrashClean, now, 30)) { lastCrashClean = now; try { RobloxInstall.CleanCrashHandlers(); } catch { } }
            cpuPrev = cpuNow;
            cpuAt = now;
            robloxCpu = cpuTotal;
            if (settings.MuteRoblox) try { RobloxAudio.SetMute(pids, true); } catch { } // repeats: a client only gets its audio session once a game starts

            var died = new List<Account>();
            lock (pidAccount)
                foreach (var dead in pidAccount.Keys.Where(k => !pids.Contains(k)).ToList()) { died.Add(pidAccount[dead]); pidAccount.Remove(dead); }
            if (died.Count > 0) SaveRunning();
            if (died.Count > 0 && IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => ClientsDied(died)));

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
                BeginInvoke((Action)(() => { if (playing == IntPtr.Zero && Visible && WindowState != FormWindowState.Minimized) Activate(); })); // don't yank you out of a game
            clients = list;
            Interlocked.Exchange(ref robloxRam, total);
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)RefreshUi);
            if (now >= nextAvatars) { nextAvatars = now.AddHours(8); new Thread(RefreshAccountInfo) { IsBackground = true }.Start(); } // profile pictures change
        }
        catch { }
        finally
        {
            if (procs != null) foreach (var p in procs) p.Dispose();
            polling = 0;
        }
    }

    void RefreshUi()
    {
        if (tray != null) tray.Text = "Multi Alt · " + clients.Count + " running";
        if (!Visible) return; // in the tray: nothing to draw
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
        // AutoSize: grows to however many rows the controls wrap into, so a narrow window can't push Follow/Join under the list
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(D(18), D(6), 0, D(6)), BackColor = Theme.Bg };
        bar.Controls.Add(Theme.Btn("+  Add account", Theme.Gold, AddAccountDialog));
        bar.Controls.Add(Theme.Btn("Login via Roblox", Theme.Blue, (s, e) => LoginViaRoblox()));
        bar.Controls.Add(Theme.Btn("▶  Launch all", Theme.Card, (s, e) => LaunchAll()));
        var stop = Theme.Btn("■  Stop launching", Theme.Card, (s, e) => { StopLaunching(); status.Text = "Launch queue cleared."; });
        bar.Controls.Add(stop);
        bar.SetFlowBreak(stop, true); // rows: buttons / game link / follow-join, so a label never ends up on a different row than its box
        var lp = Theme.Lbl("Game link / Place ID", 10, Theme.Muted); lp.Margin = new Padding(0, D(8), D(6), 0);
        var placeBox = new TextBox { Width = D(320), Text = settings.PlaceId, BackColor = Theme.Card, ForeColor = Theme.Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, D(5), 0, 0) };
        placeBox.TextChanged += (s, e) => { settings.PlaceId = placeBox.Text.Trim(); Store.SaveSettings(settings); };
        bar.Controls.Add(lp);
        bar.Controls.Add(placeBox);
        var hint = Theme.Lbl("(game link, place ID or private server link; empty = opens Roblox home)", 9, Theme.Muted); hint.Margin = new Padding(D(6), D(9), 0, 0);
        bar.Controls.Add(hint);
        bar.SetFlowBreak(hint, true);
        var lf = Theme.Lbl("Follow/Join", 10, Theme.Muted); lf.Margin = new Padding(0, D(8), D(6), 0);
        var followBox = new TextBox { Width = D(240), BackColor = Theme.Card, ForeColor = Theme.Fg, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, D(5), 0, 0) };
        var join = Theme.Btn("Join", Theme.Blue, (s, e) => FollowJoin(followBox.Text));
        join.Margin = new Padding(D(8), D(1), 0, 0);
        followBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FollowJoin(followBox.Text); } };
        var fh = Theme.Lbl("(username or user ID: ticked accounts that aren't running join the game that player is in)", 9, Theme.Muted);
        fh.Margin = new Padding(D(6), D(9), 0, 0);
        bar.Controls.AddRange(new Control[] { lf, followBox, join, fh });

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
        return CheckRect(card).Contains(p) || CopyRect(card, accounts[i]).Contains(p) || BrowserRect(card).Contains(p) || PlayRect(card).Contains(p) || XRect(card).Contains(p);
    }
    CheckBox afkBox;
    Rectangle CardRect(Rectangle item) { return new Rectangle(item.X + D(18), item.Y + D(6), item.Width - D(36), item.Height - D(12)); }
    Rectangle CheckRect(Rectangle card) { return new Rectangle(card.X + D(14), card.Y + (card.Height - D(20)) / 2, D(20), D(20)); }
    int AvatarX(Rectangle card) { return card.X + D(46); }
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

        var av = new Rectangle(AvatarX(card), card.Y + (card.Height - D(52)) / 2, D(52), D(52));
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

        // Unticked = left out of Launch all: dim the card, then draw the tick box on top so it stays bright.
        if (a.Skip) using (var dim = new SolidBrush(Color.FromArgb(120, Theme.Bg))) using (var path = Theme.Round(card, D(10))) g.FillPath(dim, path);
        var chk = CheckRect(card);
        if (a.Skip) using (var pen = new Pen(Theme.Muted, D(2))) using (var path = Theme.Round(chk, D(4))) g.DrawPath(pen, path);
        else
        {
            Theme.FillRound(g, Theme.Gold, chk, D(4));
            TextRenderer.DrawText(g, "✓", fBtn, chk, Theme.Bg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

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

    // Drops a cached profile picture so the next paint reloads it; deleteFile also removes it from disk (account removed).
    void ForgetAvatar(long id, bool deleteFile)
    {
        Image img;
        if (avatars.TryGetValue(id, out img)) { avatars.Remove(id); if (img != null) img.Dispose(); }
        if (deleteFile && id > 0) try { File.Delete(Path.Combine(Store.AvatarDir, id + ".png")); } catch { }
    }

    void AccountClick(object s, MouseEventArgs e)
    {
        int i = list.IndexFromPoint(e.Location);
        if (justDragged || dropAt >= 0 || i < 0 || i >= accounts.Count) return;
        var card = CardRect(list.GetItemRectangle(i));
        var a = accounts[i];
        if (CheckRect(card).Contains(e.Location))
        {
            a.Skip = !a.Skip;
            Store.Save(accounts);
            list.Invalidate(list.GetItemRectangle(i));
            status.Text = Title(a) + (a.Skip ? " is left out of Launch all and Follow/Join. Its Play button still works." : " is included in Launch all and Follow/Join.");
        }
        else if (CopyRect(card, a).Contains(e.Location)) CopyMenu(a, new Point(CopyRect(card, a).X, CopyRect(card, a).Bottom));
        else if (XRect(card).Contains(e.Location))
        {
            if (MessageBox.Show(this, "Remove " + Title(a) + "?\nYou'll need to log in again to add it back. Its Open in browser window's login is deleted too.",
                                "Multi Alt", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            accounts.Remove(a);
            Store.Save(accounts);
            if (a.UserId > 0 && !accounts.Any(x => x.UserId == a.UserId)) // its picture and its browser profile (a login) go too
            {
                ForgetAvatar(a.UserId, true);
                var profile = Path.Combine(Store.Dir, "browser", a.UserId.ToString());
                ThreadPool.QueueUserWorkItem(_ => { try { if (Directory.Exists(profile)) Directory.Delete(profile, true); } catch { } }); // still open: retried at the next start
            }
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
                    ForgetAvatar(u.UserId, false); // just downloaded: show the new one
                    RefreshList();
                    status.Text = (existing != null ? "Updated " : "Added ") + u.DisplayName + " (@" + u.Username + ").";
                }));
            }
            catch (Exception ex) { SetStatus("Couldn't add account: " + ex.Message); }
        }) { IsBackground = true }.Start();
    }

    // At startup and every 8 hours: fills in username/ID for accounts added before that info existed, re-downloads profile
    // pictures older than 8 hours, and deletes the pictures and browser profiles of removed accounts.
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
            var now = DateTime.UtcNow;
            var ids = new HashSet<long>(snap.Where(x => x.UserId > 0).Select(x => x.UserId));
            // a removed account's leftovers, untouched for an hour (so one being added right now is kept)
            Func<string, DateTime, bool> leftover = (name, written) => { long id; return long.TryParse(name, out id) && !ids.Contains(id) && now - written > TimeSpan.FromHours(1); };
            if (Directory.Exists(Store.AvatarDir))
                foreach (var f in Directory.GetFiles(Store.AvatarDir, "*.png"))
                    if (leftover(Path.GetFileNameWithoutExtension(f), File.GetLastWriteTimeUtc(f))) try { File.Delete(f); } catch { }
            var browsers = Path.Combine(Store.Dir, "browser");
            if (Directory.Exists(browsers))
                foreach (var d in Directory.GetDirectories(browsers))
                    if (leftover(Path.GetFileName(d), Directory.GetLastWriteTimeUtc(d))) try { Directory.Delete(d, true); } catch { } // its window still open: next time
            // a missing picture reads as written in 1601, so it's fetched too
            var stale = ids.Where(id => now - File.GetLastWriteTimeUtc(Path.Combine(Store.AvatarDir, id + ".png")) >= TimeSpan.FromHours(8)).ToList();
            if (stale.Count > 0) try { RobloxApi.FetchAvatars(stale); } catch { }
            BeginInvoke((Action)(() => { foreach (var id in stale) ForgetAvatar(id, false); Store.Save(accounts); list.Invalidate(); }));
        }
        catch { }
    }

    void LaunchAll()
    {
        var ticked = accounts.Where(a => !a.Skip).ToList();
        if (ticked.Count == 0) status.Text = "No accounts are ticked. Tick the box on the left of a card to include it in Launch all.";
        else QueueLaunch(ticked);
    }

    // target: null = the Game link box, else a PlaceLauncher request (used by Follow/Join). Pressing Play resets auto-rejoin for that account.
    void QueueLaunch(List<Account> chosen, string target = null)
    {
        if (chosen.Any(a => !IsRunning(a))) Interlocked.Exchange(ref userLaunch, 1); // set before queueing, so the worker sees it
        int n = 0;
        foreach (var a in chosen)
        {
            rejoinStopped.Remove(a);
            rejoins.Remove(a);
            if (IsRunning(a)) continue;
            lock (targetFor) targetFor[a] = target;
            launchQueue.Add(a);
            n++;
        }
        status.Text = n == 0 ? "Already running." : "Queued " + n + " account(s) to launch.";
    }

    void FollowJoin(string who)
    {
        who = (who ?? "").Trim().TrimStart('@');
        var ticked = accounts.Where(a => !a.Skip).ToList();
        if (who == "") { status.Text = "Type the username or user ID of the player to join."; return; }
        if (ticked.Count == 0) { status.Text = "No accounts are ticked. Tick the box on the left of a card to include it."; return; }
        status.Text = "Looking up " + who + "...";
        new Thread(() =>
        {
            try
            {
                long id;
                if (!(who.All(char.IsDigit) && long.TryParse(who, out id))) id = RobloxApi.UserIdFor(who);
                if (id <= 0) { SetStatus("Couldn't find user " + who + "."); return; }
                BeginInvoke((Action)(() =>
                {
                    QueueLaunch(ticked, "request=RequestFollowUser&userId=" + id);
                    status.Text = "Joining " + who + ": " + status.Text + " If they aren't in a game or their settings don't allow joins, Roblox will say so.";
                }));
            }
            catch (Exception ex) { SetStatus("Follow/Join: " + ex.Message); }
        }) { IsBackground = true }.Start();
    }

    // Clients that went away. Auto-rejoin relaunches ones that closed by themselves, at most 2 times per account in 8 minutes.
    void ClientsDied(List<Account> died)
    {
        foreach (var a in died)
        {
            if (userClosed.Remove(a) || !settings.AutoRejoin || !accounts.Contains(a) || rejoinStopped.Contains(a)) continue;
            List<DateTime> h;
            if (!rejoins.TryGetValue(a, out h)) rejoins[a] = h = new List<DateTime>();
            if (Program.AllowRejoin(h, DateTime.UtcNow))
            {
                launchQueue.Add(a); // same place it went last time
                status.Text = "Auto-rejoin: " + Title(a) + " closed, relaunching (" + h.Count + " of 2 in 8 min).";
            }
            else
            {
                rejoinStopped.Add(a);
                status.Text = Title(a) + " closed 3 times in 8 min. Auto-rejoin stopped for it (press its Play button to reset).";
            }
        }
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
        lock (pidAccount) userClosed.UnionWith(pidAccount.Values); // you closed them, so auto-rejoin leaves them closed
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
        int tx = AvatarX(card) + D(52) + D(16);
        int w = TextRenderer.MeasureText(SubText(a), fSmall).Width;
        return new Rectangle(Math.Min(tx + w + D(6), BrowserRect(card).X - D(30)), card.Y + D(40), D(22), D(22));
    }

    string SubText(Account a)
    {
        int pid = PidOf(a);
        return (string.IsNullOrEmpty(a.Username) ? "loading info..." : "@" + a.Username) + (a.UserId > 0 ? "   •   ID " + a.UserId : "") + (pid > 0 ? "   •   PID=" + pid : "");
    }

    int PidOf(Account a)
    {
        lock (pidAccount) foreach (var kv in pidAccount) if (kv.Value == a) return kv.Key;
        return 0;
    }

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

    // Right-click menus: each new one frees the last, so opening hundreds doesn't pile up window handles.
    ContextMenuStrip NewPopup() { if (popup != null) popup.Dispose(); return popup = new ContextMenuStrip(); }

    void CopyMenu(Account a, Point at)
    {
        var menu = NewPopup();
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

    // Starts the live Roblox client straight from its folder, so an old client never runs Roblox's updater (the blue screen
    // that closes running alts). Uses the roblox-player: link instead when another launcher (Bloxstrap...) owns those links,
    // the live version isn't installed yet, or the start fails. False = used the link.
    static bool StartClient(string uri)
    {
        var handler = RobloxInstall.HandlerExe("roblox-player");
        if (handler == null || RobloxInstall.VersionOf(handler) != null)
        {
            var exe = RobloxInstall.ClientExe(RobloxInstall.LiveVersion(false));
            if (exe != null && RobloxInstall.SafeArg(uri))
                try { using (Program.StartNormal(new ProcessStartInfo(exe, uri) { WorkingDirectory = Path.GetDirectoryName(exe) })) { } return true; }
                catch { }
        }
        using (Program.StartNormal(new ProcessStartInfo(uri))) { }
        return false;
    }

    // "Keep Roblox updated" (off by default), on a background thread at startup and before Play, Launch all and Join.
    // When Roblox has a new version and no Roblox program is open (checked twice), it runs Roblox's own installer alone and
    // waits for it, so launches never hit the update screen that closes running alts. Never twice for the same version.
    void EnsureRobloxUpdated()
    {
        if (!settings.KeepRobloxUpdated) return;
        lock (updateLock) // launches wait while this runs
        {
            try
            {
                var live = RobloxInstall.LiveVersion(true);
                if (live == null) { SetStatus("Couldn't check for a Roblox update: Roblox didn't answer."); return; }
                if (RobloxInstall.VersionOf(RobloxInstall.HandlerExe("roblox-player")) == null)
                {
                    SetStatus("Roblox links on this PC don't open the normal Roblox install, so Multi Alt leaves Roblox updates alone.");
                    return;
                }
                if (RobloxInstall.ClientExe(live) != null) { var msg = RemoveOldVersions(live); if (msg != null) SetStatus(msg); return; } // up to date
                if (settings.LastRobloxUpdateVersion == live) return; // already ran the installer for this version: never twice
                if (Program.Elevated()) { SetStatus(Program.AdminWarning + " Roblox update skipped."); return; }
                if (settings.CleanCrashHandlers) try { if (RobloxInstall.CleanCrashHandlers() > 0) Thread.Sleep(1500); } catch { } // let them close
                var open = RobloxInstall.Running();
                if (open == null) { Thread.Sleep(1000); open = RobloxInstall.Running(); }
                if (open != null)
                {
                    SetStatus("Roblox has an update. Multi Alt installs it when no Roblox program is open (" + open + " is)."
                              + (open == "RobloxCrashHandler" && !settings.CleanCrashHandlers ? " Tip: Settings > Clean up leftover crash handlers." : ""));
                    return;
                }
                var installer = RobloxInstall.Installer();
                if (installer == null) { SetStatus("Roblox has an update, but Roblox's installer wasn't found."); return; }
                bool ok = false;
                for (int attempt = 1; attempt <= 2 && !ok; attempt++)
                {
                    if (attempt == 2 && RobloxInstall.Running() != null) break; // something Roblox opened meanwhile
                    SetStatus(attempt == 1 ? "Updating Roblox. Launches wait until it's done..." : "Roblox didn't finish updating. Trying once more...");
                    var started = DateTime.Now; // Process.StartTime is local time
                    var seen = new HashSet<int>();
                    using (var p = Program.StartNormal(new ProcessStartInfo(installer) { WorkingDirectory = Path.GetDirectoryName(installer) }))
                        if (p != null) seen.Add(p.Id);
                    RobloxInstall.WaitForInstaller(TimeSpan.FromMinutes(10), seen);
                    Thread.Sleep(2000);
                    RobloxInstall.CloseOpenedBy(seen, started); // it may open Roblox when it's done; that window isn't one of your accounts
                    ok = RobloxInstall.ClientExe(live) != null;
                }
                settings.LastRobloxUpdateVersion = live;
                if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => Store.SaveSettings(settings)));
                if (ok)
                {
                    if (settings.FpsCap > 0) FpsTweak.Apply(settings.FpsCap);
                    SetStatus("Roblox updated. " + (RemoveOldVersions(live) ?? ""));
                    return;
                }
                const string fail = "Roblox didn't finish updating, so launches use Roblox's own updater. To try again, untick and tick Keep Roblox updated.";
                SetStatus(fail);
                if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)(() => { if (tray != null) tray.ShowBalloonTip(5000, "Multi Alt", fail, ToolTipIcon.Warning); }));
            }
            catch (Exception ex) { SetStatus("Roblox update check: " + ex.Message); }
        }
    }

    // "Remove old Roblox versions" (off by default, needs Keep Roblox updated): deletes old player folders in Roblox\Versions once
    // the current version is installed and no Roblox program is open. Keeps the current version, the folders Roblox's links and
    // its uninstall entry use, Studio, links and anything it can't read. Returns what to show, or null when there's nothing to say.
    string RemoveOldVersions(string live)
    {
        if (!settings.KeepRobloxUpdated || !settings.RemoveOldRobloxVersions || RobloxInstall.ClientExe(live) == null) return null;
        var link = RobloxInstall.VersionOf(RobloxInstall.HandlerExe("roblox-player"));
        if (link == null) return "Old Roblox versions kept: Roblox links don't open the normal Roblox install.";
        var old = RobloxInstall.PickOld(RobloxInstall.Scan(), new[] { live, link, RobloxInstall.VersionOf(RobloxInstall.HandlerExe("roblox")),
                                                                      RobloxInstall.VersionOf(RobloxInstall.UninstallerExe()) });
        if (old.Count == 0) return null;
        if (RobloxInstall.Running() != null) return "Old Roblox versions will be removed when no Roblox program is open.";
        int n = 0;
        foreach (var name in old)
            try { Directory.Delete(Path.Combine(RobloxInstall.Root, name), true); n++; }
            catch { } // a file in use: tried again next time
        return "Removed " + n + " old Roblox version(s)." + (n < old.Count ? " " + (old.Count - n) + " in use, kept for next time." : "");
    }

    // One background worker launches the queue in order, so clicking Play on many accounts never floods Roblox.
    void LaunchWorker()
    {
        foreach (var a in launchQueue.GetConsumingEnumerable())
        {
            try
            {
                if (Interlocked.Exchange(ref userLaunch, 0) == 1) EnsureRobloxUpdated(); // you pressed Play, Launch all or Join
                if (IsRunning(a)) continue;
                long cap = settings.RamCapGb * GB;
                if (cap > 0 && Interlocked.Read(ref robloxRam) + GB > cap)
                {
                    StopLaunching();
                    SetStatus("RAM cap of " + settings.RamCapGb + " GB reached - stopped launching. Raise it in Performance.");
                    continue;
                }
                string target;
                lock (targetFor) targetFor.TryGetValue(a, out target);
                var query = target ?? Program.ParseTarget(settings.PlaceId);
                if (query == null)
                {
                    StopLaunching();
                    SetStatus(Regex.IsMatch(settings.PlaceId, "share\\?code=", RegexOptions.IgnoreCase)
                        ? "roblox.com/share links aren't supported: open it in a browser and paste the address it turns into (with privateServerLinkCode)."
                        : "Game link box: paste a game link, its place ID, or a private server link.");
                    continue;
                }

                HashSet<int> before;
                lock (updateLock) // waits out a Roblox update, and keeps one from starting under this launch
                {
                    if (RobloxInstall.IsOpen("RobloxPlayerInstaller")) { SetStatus("Waiting for Roblox to finish updating..."); RobloxInstall.WaitForInstaller(TimeSpan.FromMinutes(3), null); }
                    if (settings.FpsCap > 0) FpsTweak.Apply(settings.FpsCap); // a Roblox update makes a new folder without it
                    SetStatus("Launching " + Title(a) + "  (" + launchQueue.Count + " more queued)...");
                    var uri = RobloxApi.BuildUri(RobloxApi.GetTicket(a.Cookie), query); // ticket after the waits, so it's fresh
                    before = RobloxPids();
                    StartClient(uri);
                }
                // Roblox first runs a short-lived launcher process, so wait (up to 60s) for a new one that has a game window.
                for (int t = 0; t < 120; t++)
                {
                    Thread.Sleep(500);
                    HashSet<int> skip;
                    lock (pidAccount) skip = new HashSet<int>(before.Concat(pidAccount.Keys));
                    int fresh = NewWindowPid(skip);
                    if (fresh != 0) { lock (pidAccount) pidAccount[fresh] = a; SaveRunning(); break; }
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
        add(note("Multi Alt won't launch more clients once Roblox reaches the cap, and squeezes background clients' memory when they go over it "
               + "(at most every 30 s, never the one you're playing). Your PC has " + Gb(installedRam) + " GB, so bigger sizes are greyed out."), D(46));
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
        add(delay, D(44));
        Func<string, bool, Action<bool>, CheckBox> check = (text, on, changed) =>
        {
            // fixed size (not AutoSize) so long text wraps onto a second line instead of running off the page
            var c = new CheckBox { Text = text, Checked = on, ForeColor = Theme.Fg, AutoSize = false, Size = new Size(D(780), D(44)), Font = new Font("Segoe UI", 10f) };
            c.CheckedChanged += (s, e) => changed(c.Checked);
            return c;
        };
        add(check("Auto-rejoin (off by default): relaunch a client that closes by itself. Max 2 times per account in 8 minutes, then it stops for that account. Never relaunches clients you closed.",
                  settings.AutoRejoin, v => { settings.AutoRejoin = v; Store.SaveSettings(settings); }), D(56));

        add(Theme.Lbl("AUDIO", 9, Theme.Gold, true), D(28));
        add(muteCheck = check("Mute Roblox: hard-mute every Roblox client in the Windows volume mixer (unmutes when you untick this or close Multi Alt).",
                  settings.MuteRoblox, v =>
                  {
                      settings.MuteRoblox = v; Store.SaveSettings(settings);
                      ThreadPool.QueueUserWorkItem(_ => { try { RobloxAudio.SetMute(RobloxPids(), v); } catch { } });
                  }), D(56));

        add(Theme.Lbl("ROBLOX UPDATES", 9, Theme.Gold, true), D(28));
        CheckBox oldBox = null;
        add(check("Keep Roblox updated (off by default): when Roblox has an update and no Roblox program is open, run Roblox's own installer before launching, so the update screen can't close running alts.",
                  settings.KeepRobloxUpdated, v => { settings.KeepRobloxUpdated = v; settings.LastRobloxUpdateVersion = null; Store.SaveSettings(settings); oldBox.Enabled = v; }), D(56));
        add(oldBox = check("Remove old Roblox versions (off by default, needs the option above): delete old Roblox player folders when no Roblox program is open. Never touches Studio, the current version or other launchers.",
                  settings.RemoveOldRobloxVersions, v => { settings.RemoveOldRobloxVersions = v; Store.SaveSettings(settings); }), D(56));
        oldBox.Enabled = settings.KeepRobloxUpdated;

        add(Theme.Lbl("CLIENTS", 9, Theme.Gold, true), D(28));
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };
        row.Controls.Add(Theme.Btn("Tile Roblox windows across my monitors", Theme.Card, (s, e) => TileWindows()));
        row.Controls.Add(Theme.Btn("Open data folder", Theme.Card, (s, e) => { Directory.CreateDirectory(Store.Dir); using (Program.StartNormal(new ProcessStartInfo("explorer.exe", "\"" + Store.Dir + "\""))) { } }));
        add(row, D(60));
        add(check("Clean up leftover crash handlers (off by default): close RobloxCrashHandler programs left behind after their Roblox client closed. Never touches one whose client is still running.",
                  settings.CleanCrashHandlers, v => { settings.CleanCrashHandlers = v; Store.SaveSettings(settings); }), D(56));

        add(Theme.Lbl("WINDOWS", 9, Theme.Gold, true), D(28));
        add(check("Close to tray (off by default): ✕ hides Multi Alt next to the clock and keeps your clients running. Double-click the tray icon to open it, right-click it to exit.",
                  settings.CloseToTray, v => { settings.CloseToTray = v; Store.SaveSettings(settings); }), D(56));
        add(check("Show in Start Menu (off by default): add Multi Alt to the Start Menu so Windows search finds it. Unticking removes it.",
                  settings.StartMenuShortcut, v =>
                  {
                      settings.StartMenuShortcut = v; Store.SaveSettings(settings);
                      try { Program.SetStartMenu(v); status.Text = v ? "Added Multi Alt to the Start Menu." : "Removed Multi Alt from the Start Menu."; }
                      catch (Exception ex) { status.Text = "Start Menu shortcut: " + ex.Message; }
                  }), D(56));

        add(Theme.Lbl("ABOUT", 9, Theme.Gold, true), D(28));
        add(Theme.Lbl("Multi Alt v" + Program.Version + "  —  by Triple DDD", 12, Theme.Fg, true), D(32));
        add(note("Keep Multi Alt open while you play: it's what lets more than one Roblox run at once.\r\n"
               + "Accounts are encrypted with Windows (DPAPI) so only your Windows user on this PC can read them, and logins are only ever sent to roblox.com.\r\n"
               + "Data folder: " + Store.Dir), D(80));
        add(check("Check GitHub for updates on startup (only asks for the latest version number; never downloads anything)",
                  settings.CheckUpdates, v => { settings.CheckUpdates = v; Store.SaveSettings(settings); }), D(50));

        add(Theme.Lbl("REMOVE MULTI ALT", 9, Theme.Gold, true), D(28));
        add(note("Closes all Roblox clients, then deletes Multi Alt.exe. Burning also wipes every saved account, setting and log."), D(28));
        var del = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Bg };
        del.Controls.Add(Theme.Btn("Delete Multi Alt (keep my data)", Theme.Card, (s, e) => Uninstall(false)));
        del.Controls.Add(Theme.Btn("Delete Multi Alt + burn all data", Color.FromArgb(120, 24, 32), (s, e) => Uninstall(true)));
        add(del, D(70));
        return p;
    }

    void Uninstall(bool burn)
    {
        var exe = Application.ExecutablePath;
        if (!burn)
        {
            if (MessageBox.Show("Close all Roblox clients and delete Multi Alt?\n\nYour saved accounts stay in " + Store.Dir + " so a fresh download picks them up.",
                                "Delete Multi Alt", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        else if (!ConfirmBurn()) return;

        // stop everything that could relaunch Roblox or rewrite data while we tear down
        if (poller != null) poller.Dispose();
        StopLaunching();
        userClosed.UnionWith(accounts);
        if (settings.MuteRoblox) try { RobloxAudio.SetMute(RobloxPids(), false); } catch { }
        foreach (var name in new[] { "RobloxPlayerLauncher", "RobloxPlayerBeta" })
            foreach (var pr in Process.GetProcessesByName(name))
                try { pr.Kill(); pr.WaitForExit(3000); } catch { } finally { pr.Dispose(); }
        try { Program.SetStartMenu(false); } catch { }

        var left = new List<string>();
        if (burn)
        {
            Store.Frozen = true; // the goodbye box below still runs queued work: none of it may save
            left = Store.Burn();
            try { Clipboard.Clear(); } catch { }
        }
        else try { if (Directory.Exists(Store.AvatarDir)) Directory.Delete(Store.AvatarDir, true); } catch { } // only a cache of profile pictures
        bool selfDelete = exe.IndexOf('%') < 0; // cmd would read %...% in the path as a variable: then you delete it by hand
        MessageBox.Show((burn ? "All Multi Alt data was burned." : "Multi Alt will be deleted. Your data is kept in " + Store.Dir + ".")
                        + (left.Count > 0 ? "\n\nCouldn't fully remove (delete by hand):\n" + string.Join("\n", left) : "")
                        + (selfDelete ? "" : "\n\nDelete this file by hand once Multi Alt closes:\n" + exe)
                        + (burn ? "\n\nTip: on roblox.com go to Settings > Security > \"Log out of all other sessions\" on each account so old logins stop working everywhere." : "")
                        + "\n\nGoodbye!", "Multi Alt");
        DisposeTray(); // Environment.Exit skips FormClosed
        // the exe can't delete itself while running: a hidden cmd waits ~2s for us to exit, then deletes it.
        // Started after the message box so a slow click on OK can't make it run while the exe is still open.
        if (selfDelete)
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + exe + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        Environment.Exit(0);
    }

    bool ConfirmBurn()
    {
        using (var f = new Form { Text = "Burn all data", BackColor = Theme.Bg, ForeColor = Theme.Fg, Icon = Icon, StartPosition = FormStartPosition.CenterParent,
                                  FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(D(520), D(300)) })
        {
            var msg = new Label { Text = "This permanently deletes Multi Alt AND everything it saved:\r\n"
                                       + "  • every saved account login and password (accounts.dat is overwritten, then deleted)\r\n"
                                       + "  • settings, crash logs, and the old MultiRoblox folder\r\n"
                                       + "  • whatever is on your clipboard\r\n\r\n"
                                       + "It can't be undone. Type BURN to confirm.",
                                  Location = new Point(D(16), D(14)), Size = new Size(D(488), D(150)), Font = new Font("Segoe UI", 10f) };
            var box = new TextBox { Location = new Point(D(16), D(170)), Width = D(200), BackColor = Theme.Card, ForeColor = Theme.Fg, Font = new Font("Segoe UI", 12f) };
            var go = Theme.Btn("Burn it", Color.FromArgb(120, 24, 32), (s, e) => f.DialogResult = DialogResult.OK);
            go.Location = new Point(D(16), D(220)); go.Enabled = false;
            var cancel = Theme.Btn("Cancel", Theme.Card, (s, e) => f.DialogResult = DialogResult.Cancel);
            cancel.Location = new Point(D(200), D(220));
            box.TextChanged += (s, e) => go.Enabled = box.Text.Trim() == "BURN";
            f.Controls.AddRange(new Control[] { msg, box, go, cancel });
            return f.ShowDialog(this) == DialogResult.OK && box.Text.Trim() == "BURN";
        }
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

    public const string Version = "1.2.0";

    // Game link box -> PlaceLauncher query. "" -> "" (Roblox home), place ID or game link -> RequestGame,
    // link with privateServerLinkCode -> RequestPrivateGame, anything else -> null.
    public static string ParseTarget(string input)
    {
        var s = (input ?? "").Trim();
        if (s == "") return "";
        if (s.All(char.IsDigit)) return "request=RequestGame&placeId=" + s;
        var m = Regex.Match(s, @"(?:/games/|placeId=)(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var code = Regex.Match(s, @"privateServerLinkCode=([\w-]+)", RegexOptions.IgnoreCase);
        return code.Success ? "request=RequestPrivateGame&placeId=" + m.Groups[1].Value + "&linkCode=" + code.Groups[1].Value
                            : "request=RequestGame&placeId=" + m.Groups[1].Value;
    }

    // Auto-rejoin limit: at most 2 rejoins in any 8 minutes. h = this account's rejoin times.
    public static bool AllowRejoin(List<DateTime> h, DateTime now)
    {
        h.RemoveAll(t => now - t > TimeSpan.FromMinutes(8));
        if (h.Count >= 2) return false;
        h.Add(now);
        return true;
    }

    public static bool IsNewer(string tag, string current)
    {
        try { return new Version((tag ?? "").Trim().TrimStart('v', 'V')) > new Version(current); }
        catch { return false; }
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

    // Fingerprint of the Terms text. Line endings don't count, so a checkout with CRLF and one with LF give the same result.
    public static string TermsHash(string text)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")))).Replace("-", "");
    }

    public static bool RamAllowed(int gb, long installedBytes) { return gb == 0 || gb <= Math.Ceiling(installedBytes / (double)GB); }

    // True when at least `seconds` passed since last (or the clock went back).
    public static bool Due(DateTime last, DateTime now, int seconds) { return now < last || now - last >= TimeSpan.FromSeconds(seconds); }

    // A child is left behind when its parent is gone, or the parent's PID now belongs to a program that started after it.
    public static bool IsOrphan(DateTime childStart, DateTime? parentStart) { return parentStart == null || parentStart.Value > childStart; }

    public const string AdminWarning = "Run Multi Alt normally, not as admin, to avoid duplicate Roblox installs.";
    public static bool Elevated()
    {
        try
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // Multi Alt runs at below-normal priority and Windows hands that down to whatever it starts, so lift it for the moment
    // of the start: Roblox, the installer and browsers start at normal priority.
    static readonly object startGate = new object();
    public static Process StartNormal(ProcessStartInfo psi)
    {
        lock (startGate)
            using (var me = Process.GetCurrentProcess())
            {
                var old = ProcessPriorityClass.Normal;
                try { old = me.PriorityClass; if (old != ProcessPriorityClass.Normal) me.PriorityClass = ProcessPriorityClass.Normal; } catch { }
                try { return Process.Start(psi); }
                finally { try { if (old != ProcessPriorityClass.Normal) me.PriorityClass = old; } catch { } }
            }
    }

    // "Show in Start Menu": a normal per-user shortcut (so Windows search finds Multi Alt), made by the Windows shell object.
    public static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Multi Alt.lnk");
    public static void SetStartMenu(bool on)
    {
        if (!on) { if (File.Exists(StartMenuLink)) File.Delete(StartMenuLink); return; }
        object shell = null, link = null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell", true);
            shell = Activator.CreateInstance(t);
            link = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartMenuLink });
            var lt = link.GetType();
            var exe = Application.ExecutablePath;
            lt.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { exe });
            lt.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(exe) });
            lt.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { exe + ",0" });
            lt.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "Run many Roblox accounts at once" });
            lt.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    // Set by a second copy of Multi Alt to bring this one to the front instead of running twice.
    public static EventWaitHandle ShowSignal;
    static Mutex instance;
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest") return SelfTest();

        // One Multi Alt at a time (two would overwrite each other's saved accounts): a second copy shows the first one.
        bool first;
        try { instance = new Mutex(true, @"Local\MultiAlt_Instance", out first); }
        catch (UnauthorizedAccessException) { first = false; } // held by a copy running as admin
        catch { first = true; }
        if (!first)
        {
            try { AllowSetForegroundWindow(-1); using (var ev = EventWaitHandle.OpenExisting(@"Local\MultiAlt_Show")) ev.Set(); }
            catch { MessageBox.Show("Multi Alt is already running. Look for its icon next to the clock.", "Multi Alt"); }
            return 0;
        }
        try { ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MultiAlt_Show"); } catch { }
        try { using (var me = Process.GetCurrentProcess()) me.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // games come first

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

        if (!MainForm.AgreeToTerms(Store.LoadSettings())) return 0; // "I don't agree" or closed: nothing else runs

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
        GC.KeepAlive(instance);
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

        var uri = RobloxApi.BuildUri("TICKET", "request=RequestGame&placeId=1818");
        Check(uri.StartsWith("roblox-player:1+gameinfo:TICKET+") && uri.Contains("placeId%3D1818") && uri.Contains("launchmode:play"), "uri");
        Check(RobloxApi.BuildUri("TICKET", "").Contains("launchmode:app"), "uri home");

        Check(BrowserLogin.ExtractCookie("{\"id\":3,\"result\":{\"cookies\":[{\"name\":\"RBXSource\",\"value\":\"x\"},{\"name\":\".ROBLOSECURITY\",\"value\":\"_|WARNING:-DO-NOT-SHARE|_ABC\",\"domain\":\".roblox.com\"}]}}")
              == "_|WARNING:-DO-NOT-SHARE|_ABC" && BrowserLogin.ExtractCookie("{\"id\":1,\"result\":{\"cookies\":[]}}") == null, "cookie extract");

        const string G = "request=RequestGame&placeId=";
        Check(ParseTarget("") == "" && ParseTarget(" 920587237 ") == G + "920587237" && ParseTarget("https://www.roblox.com/games/920587237/Adopt-Me") == G + "920587237"
              && ParseTarget("roblox.com/games/start?placeId=55&launchData=x") == G + "55" && ParseTarget("adopt me") == null
              && ParseTarget("https://www.roblox.com/games/920587237/Adopt-Me?privateServerLinkCode=1234-abcd_56")
                 == "request=RequestPrivateGame&placeId=920587237&linkCode=1234-abcd_56"
              && ParseTarget("https://www.roblox.com/share?code=abc&type=Server") == null, "target parse");

        var h = new List<DateTime>(); var t0 = new DateTime(2026, 1, 1);
        Check(AllowRejoin(h, t0) && AllowRejoin(h, t0.AddMinutes(1)) && !AllowRejoin(h, t0.AddMinutes(2))
              && !AllowRejoin(h, t0.AddMinutes(7)) && AllowRejoin(h, t0.AddMinutes(8.5)), "rejoin limit");

        Check(IsNewer("v1.2.0", "1.1.0") && !IsNewer("v1.1.0", "1.1.0") && !IsNewer("1.0.0", "1.1.0") && !IsNewer("garbage", "1.1.0") && !IsNewer(null, "1.1.0"), "version compare");

        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "secret");
        Store.Shred(tmp);
        Check(!File.Exists(tmp), "shred");

        int me = Process.GetCurrentProcess().Id;
        var running = Store.ParseRunning(new[] { Store.RunningLine(me, 42), me + "\t123\t7", "junk", "999999999\t1\t5" });
        Check(running.Count == 1 && running[me] == 42, "running restore"); // right start time kept; wrong start time, junk and dead pid dropped

        var l1 = new List<char>("abcd"); var l2 = new List<char>("abcd"); var l3 = new List<char>("abcd");
        Check(MoveItem(l1, 0, 3) == 2 && new string(l1.ToArray()) == "bcad" && MoveItem(l2, 3, 0) == 0 && new string(l2.ToArray()) == "dabc"
              && MoveItem(l3, 1, 2) == 1 && new string(l3.ToArray()) == "abcd" && MoveItem(l3, 1, 4) == 3 && new string(l3.ToArray()) == "acdb", "reorder");

        long ram16 =16L * GB - 300L * 1024 * 1024; // what Windows reports for a "16 GB" PC
        Check(Program.RamAllowed(16, ram16) && !Program.RamAllowed(24, ram16) && Program.RamAllowed(0, ram16), "ram options");

        // v1.2: names from the network or the registry only become folder paths when they're exactly "version-" + 16 hex
        const string V = "version-0123456789abcdef";
        Check(RobloxInstall.IsVersionName(V) && !RobloxInstall.IsVersionName(V + "\n") && !RobloxInstall.IsVersionName("version-0123456789abcde")
              && !RobloxInstall.IsVersionName(V + "0") && !RobloxInstall.IsVersionName(@"..\" + V) && !RobloxInstall.IsVersionName(null)
              && !RobloxInstall.IsVersionName("version-0123456789ABCDEF"), "version name");
        Check(RobloxInstall.ParseLive("{\"version\":\"0.700.0.7000000\",\"clientVersionUpload\":\"" + V + "\"}") == V
              && RobloxInstall.ParseLive("{\"clientVersionUpload\":\"..\\\\..\\\\x\"}") == null && RobloxInstall.ParseLive("junk") == null
              && RobloxInstall.ParseLive("{}") == null && RobloxInstall.ParseLive(null) == null, "live version parse");
        Check(RobloxInstall.SafeArg(RobloxApi.BuildUri("abc-DEF_123", G + "1818")) && RobloxInstall.SafeArg(RobloxApi.BuildUri("abc", ""))
              && !RobloxInstall.SafeArg("roblox-player:1 --x") && !RobloxInstall.SafeArg("a\"b") && !RobloxInstall.SafeArg("") && !RobloxInstall.SafeArg(null), "launch arg");

        var d0 = new DateTime(2026, 1, 1, 12, 0, 0);
        Check(!Due(d0, d0.AddSeconds(29), 30) && Due(d0, d0.AddSeconds(30), 30) && Due(DateTime.MinValue, d0, 30) && Due(d0, d0.AddSeconds(-5), 30), "every 30 s");
        Check(IsOrphan(d0, null) && IsOrphan(d0, d0.AddSeconds(1)) && !IsOrphan(d0, d0.AddSeconds(-1)) && !IsOrphan(d0, d0), "crash handler orphan");

        Func<string, bool, bool, bool, RobloxInstall.VersionDir> vd = (n, pl, st, ln) => new RobloxInstall.VersionDir { Name = n, Player = pl, Studio = st, Link = ln };
        var old = RobloxInstall.PickOld(new[] {
            vd("version-0000000000000001", true, false, false),  // live: kept
            vd("version-0000000000000002", true, true, false),   // Studio: kept
            vd("version-0000000000000003", true, false, false),  // old player: removed
            vd("version-0000000000000004", true, false, true),   // a link: kept
            vd("version-0000000000000005", false, false, false), // no player: kept
            vd("Version-0000000000000006", true, false, false),  // not a Roblox folder name: kept
            vd("version-0000000000000007", true, false, false),  // what Roblox links open: kept
        }, new[] { "version-0000000000000001", null, "VERSION-0000000000000007" });
        Check(old.Count == 1 && old[0] == "version-0000000000000003", "old versions");

        Check(RobloxInstall.CommandExe("\"C:\\R\\" + V + "\\RobloxPlayerBeta.exe\" %1") == "C:\\R\\" + V + "\\RobloxPlayerBeta.exe"
              && RobloxInstall.CommandExe("C:\\B\\Bloxstrap.exe -player \"%1\"") == "C:\\B\\Bloxstrap.exe" && RobloxInstall.CommandExe(null) == null
              && RobloxInstall.CommandExe("") == null, "registry command");
        Check(RobloxInstall.VersionOf("C:\\R\\" + V + "\\RobloxPlayerBeta.exe", "C:\\R") == V && RobloxInstall.VersionOf("c:\\r\\" + V + "\\a.exe", "C:\\R\\") == V
              && RobloxInstall.VersionOf("C:\\X\\" + V + "\\RobloxPlayerBeta.exe", "C:\\R") == null && RobloxInstall.VersionOf("C:\\R\\" + V + "\\sub\\a.exe", "C:\\R") == null
              && RobloxInstall.VersionOf("C:\\R\\..\\X\\" + V + "\\a.exe", "C:\\R") == null && RobloxInstall.VersionOf(null, "C:\\R") == null
              && RobloxInstall.VersionOf(RobloxInstall.CommandExe("\"C:\\R\\" + V + "\\RobloxPlayerInstaller.exe\" -uninstall"), "C:\\R") == V, "version folder");
        Check(TermsHash("a\r\nb") == TermsHash("a\nb") && TermsHash("a\nb") != TermsHash("a\nc") && TermsHash("x").Length == 64, "terms hash");
        Check(new AppSettings().AcceptedTerms == null && MainForm.AgreeToTerms(new AppSettings { AcceptedTerms = MainForm.TermsId }), "terms agreed once, not asked again");

        Console.WriteLine("selftest ok");
        return 0;
    }

    static void Check(bool ok, string what) { if (!ok) { Console.WriteLine("FAIL: " + what); Environment.Exit(1); } }
}

