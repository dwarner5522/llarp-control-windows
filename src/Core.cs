// LLARP Control for Windows: everything that isn't UI.
// C# 5 / .NET Framework 4.x so it builds with the csc.exe that ships with Windows.
//
// llarpd has no Windows port (llarp 0.10.0 dropped win32), so it runs in a dedicated WSL 2
// distro called "LLARP", driven by the same llarpctl/llarp_core.py as on Linux. This side:
//   - sets up that distro (WSL, Debian image, llarpd build) and installs itself
//   - "host" mode: keeps llarpd running (a hidden wsl.exe child) and connects Windows to it:
//     client -> route the tunnel range into WSL + NRPT rule so .loki/.snode resolve via llarpd
//     router -> relay UDP from the Windows port into WSL (WSL 2 doesn't forward inbound UDP)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LlarpControl
{
    static class Paths
    {
        public const string AppName = "LLARP Control";
        public static readonly string AppDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LLARP-Control");
        public static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LLARP Control");
        public static readonly string InstalledExe = Path.Combine(InstallDir, "LLARP-Control.exe");
        public static readonly string DistroDir = Path.Combine(AppDir, "wsl");
        public static readonly string PayloadDir = Path.Combine(AppDir, "payload");
        public static readonly string HostState = Path.Combine(AppDir, "host.json");
        public static readonly string HostError = Path.Combine(AppDir, "host-error.txt");
        public static readonly string SetupLog = Path.Combine(AppDir, "setup.log");

        public static string WindowsLog
        {
            get { return @"\\wsl$\" + Wsl.Distro + @"\root\.config\llarp-gui\llarpd.log"; }
        }

        public static string Exe
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        public static string InstalledExeOrSelf()
        {
            return File.Exists(InstalledExe) ? InstalledExe : Exe;
        }

        public static bool RunningInstalled()
        {
            return string.Equals(Path.GetFullPath(Exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ JSON helpers

    static class J
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = 64 << 20 };

        public static Dictionary<string, object> Parse(string text)
        {
            try { return Ser.Deserialize<Dictionary<string, object>>(text); }
            catch (Exception) { return null; }
        }

        public static string Write(object value) { return Ser.Serialize(value); }

        static object Get(IDictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            return v is bool && (bool)v;
        }

        public static double? Num(IDictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null || v is bool || v is string) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> Obj(IDictionary<string, object> d, string key)
        {
            return Get(d, key) as Dictionary<string, object>;
        }

        public static List<object> Arr(IDictionary<string, object> d, string key)
        {
            var e = Get(d, key) as IEnumerable;
            if (e == null || e is string) return new List<object>();
            return e.Cast<object>().ToList();
        }
    }

    // ------------------------------------------------------------------ processes

    class ProcResult
    {
        public int Code;
        public string Out = "";
        public string Err = "";

        public string Text
        {
            get { return (Out.Trim() + "\n" + Err.Trim()).Trim(); }
        }

        public bool Ok { get { return Code == 0; } }
    }

    static class Proc
    {
        static readonly Regex Ansi = new Regex(@"\x1b\[[0-9;?]*[A-Za-z]");

        public static string Clean(string s)
        {
            if (s == null) return "";
            // wsl.exe's own messages may be UTF-16; read as UTF-8 that leaves NULs between letters
            return Ansi.Replace(s.Replace("\0", ""), "").Replace("\r\n", "\n").Replace("\r", "\n");
        }

        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"', '\n' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        public static string Join(IEnumerable<string> args)
        {
            return string.Join(" ", args.Select(Quote));
        }

        public static ProcessStartInfo Info(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.EnvironmentVariables["WSL_UTF8"] = "1";
            return psi;
        }

        public static ProcResult Run(string exe, string args, int timeoutMs = 120000)
        {
            var r = new ProcResult();
            try
            {
                using (var p = Process.Start(Info(exe, args)))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    var e = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        r.Code = -1;
                        r.Err = "timed out: " + Path.GetFileName(exe) + " " + args;
                        return r;
                    }
                    p.WaitForExit();
                    r.Code = p.ExitCode;
                    r.Out = Clean(o.Result);
                    r.Err = Clean(e.Result);
                }
            }
            catch (Exception ex)
            {
                r.Code = -1;
                r.Err = ex.Message;
            }
            return r;
        }

        /// Run and pass every output line to `line` as it arrives. Returns the exit code.
        public static int Stream(string exe, string args, Action<string> line, CancellationToken cancel = default(CancellationToken))
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = Info(exe, args);
                    DataReceivedEventHandler h = (s, e) => { if (e.Data != null) line(Clean(e.Data).TrimEnd('\n')); };
                    p.OutputDataReceived += h;
                    p.ErrorDataReceived += h;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    while (!p.WaitForExit(500))
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            try { p.Kill(); } catch (Exception) { }
                            line("cancelled");
                            return -1;
                        }
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                line("error: " + ex.Message);
                return -1;
            }
        }

        public static string System32(string name)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            // a 32-bit process sees SysWOW64 as System32; wsl.exe only exists in the real one
            foreach (var dir in new[] { Path.Combine(win, "Sysnative"), Environment.SystemDirectory })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return name;
        }

        public static ProcResult PowerShell(string script)
        {
            return Run(System32(@"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(script), 60000);
        }
    }

    // ------------------------------------------------------------------ WSL

    static class Wsl
    {
        public const string Distro = "LLARP";
        public const string Llarpctl = "/usr/local/bin/llarpctl";

        public static string Exe { get { return Proc.System32("wsl.exe"); } }

        /// WSL is installed and usable (wsl --status succeeds; the inbox stub fails).
        public static bool Installed()
        {
            if (!File.Exists(Exe)) return false;
            return Proc.Run(Exe, "--status", 30000).Ok;
        }

        public static bool DistroExists()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            {
                if (k == null) return false;
                foreach (var name in k.GetSubKeyNames())
                {
                    using (var s = k.OpenSubKey(name))
                    {
                        if (s != null && string.Equals(s.GetValue("DistributionName") as string, Distro,
                                StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            return false;
        }

        public static string ExecArgs(params string[] argv)
        {
            return "-d " + Distro + " -u root --exec " + Proc.Join(argv);
        }

        public static ProcResult Exec(int timeoutMs, params string[] argv)
        {
            return Proc.Run(Exe, ExecArgs(argv), timeoutMs);
        }

        public static ProcResult Ctl(params string[] args)
        {
            var argv = new List<string> { Llarpctl };
            argv.AddRange(args);
            return Exec(120000, argv.ToArray());
        }

        public static Dictionary<string, object> Dump()
        {
            var r = Ctl("dump");
            return r.Ok ? J.Parse(r.Out) : null;
        }

        public static string ToLinuxPath(string p)
        {
            p = Path.GetFullPath(p);
            if (p.Length >= 2 && p[1] == ':')
                return "/mnt/" + char.ToLowerInvariant(p[0]) + p.Substring(2).Replace('\\', '/');
            return p.Replace('\\', '/');
        }

        public static bool IsArm64()
        {
            foreach (var v in new[] { "PROCESSOR_ARCHITEW6432", "PROCESSOR_ARCHITECTURE" })
            {
                if (string.Equals(Environment.GetEnvironmentVariable(v), "ARM64", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// WSLg (Linux GUI apps) starts a Remote Desktop client (msrdc) with every WSL start. The
    /// LLARP distro has no GUI apps, and that client fails with "Remote Desktop was unable to run
    /// ActiveX controls" when WSL is started by an elevated program like this one. Turn it off,
    /// unless the user has chosen a guiApplications setting themselves.
    static class WslConfig
    {
        internal static string PathOverride = null; // tests only

        static string FilePath
        {
            get
            {
                return PathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
            }
        }

        /// Returns true if .wslconfig was changed (the change applies after WSL restarts).
        public static bool DisableGui()
        {
            string path = FilePath;
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            if (Regex.IsMatch(text, @"^\s*guiApplications\s*=", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                return false; // the user decided
            const string comment = "# added by LLARP Control: WSLg's Remote Desktop client isn't needed by the LLARP distro\r\n";
            const string setting = "guiApplications=false\r\n";
            var section = Regex.Match(text, @"^\s*\[wsl2\]\s*$\r?\n?", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (section.Success)
                text = text.Insert(section.Index + section.Length, comment + setting);
            else
                text = text + (text.Length > 0 && !text.EndsWith("\n") ? "\r\n" : "") + comment + "[wsl2]\r\n" + setting;
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return true;
        }

        /// Restart WSL so the setting applies, but only if nothing but an idle LLARP distro would
        /// be affected. Returns true if it restarted.
        public static bool RestartIfIdle()
        {
            if (Host.IsRunning()) return false;
            var running = Proc.Run(Wsl.Exe, "--list --running --quiet", 30000);
            // fails (with a localized message) when nothing is running
            var names = !running.Ok ? new List<string>()
                : running.Out.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (names.Any(n => !string.Equals(n, Wsl.Distro, StringComparison.OrdinalIgnoreCase))) return false;
            if (names.Count > 0)
            {
                var st = Wsl.Ctl("status", "--json");
                var d = st.Ok ? J.Parse(st.Out) : null;
                if (d == null || J.Bool(d, "running")) return false;
            }
            return Proc.Run(Wsl.Exe, "--shutdown", 60000).Ok;
        }
    }

    // ------------------------------------------------------------------ files carried in the exe

    static class Payload
    {
        static readonly string[] Names = { "llarp_core.py", "llarpctl", "wsl-setup.sh", "opennet.signed", "LLARP_COMMIT" };

        static byte[] Read(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload/" + name))
            {
                if (s == null) throw new InvalidOperationException("this build is missing " + name + "; rebuild it with build.ps1");
                var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                if (name.EndsWith(".signed")) return bytes;
                return bytes.Where(b => b != (byte)'\r').ToArray(); // shell/python files must be LF
            }
        }

        public static string Extract()
        {
            Directory.CreateDirectory(Paths.PayloadDir);
            foreach (var n in Names) File.WriteAllBytes(Path.Combine(Paths.PayloadDir, n), Read(n));
            return Paths.PayloadDir;
        }

        public static string Text(string name)
        {
            return Encoding.UTF8.GetString(Read(name));
        }

        public static string Hash()
        {
            using (var sha = SHA256.Create())
            {
                var all = Names.SelectMany(n => Read(n)).ToArray();
                return BitConverter.ToString(sha.ComputeHash(all)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            }
        }
    }

    // ------------------------------------------------------------------ Windows networking

    static class WinNet
    {
        public const string Tag = "LLARP Control";

        /// "10.67.0.1/16" -> ("10.67.0.0", "255.255.0.0")
        public static bool ParseRange(string range, out string network, out string mask)
        {
            network = mask = null;
            var parts = (range ?? "").Split('/');
            IPAddress ip;
            int bits;
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out ip) || !int.TryParse(parts[1], out bits)
                || bits < 1 || bits > 32 || ip.AddressFamily != AddressFamily.InterNetwork)
                return false;
            uint m = bits == 32 ? 0xffffffffu : ~(0xffffffffu >> bits);
            var b = ip.GetAddressBytes();
            uint a = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            a &= m;
            network = string.Format("{0}.{1}.{2}.{3}", a >> 24, (a >> 16) & 255, (a >> 8) & 255, a & 255);
            mask = string.Format("{0}.{1}.{2}.{3}", m >> 24, (m >> 16) & 255, (m >> 8) & 255, m & 255);
            return true;
        }

        public static ProcResult RouteAdd(string network, string mask, string gateway)
        {
            RouteDelete(network, mask);
            return Proc.Run(Proc.System32("route.exe"), "ADD " + network + " MASK " + mask + " " + gateway + " METRIC 3", 30000);
        }

        public static void RouteDelete(string network, string mask)
        {
            Proc.Run(Proc.System32("route.exe"), "DELETE " + network + " MASK " + mask, 30000);
        }

        public static ProcResult RouteAdd(string network, string mask, string gateway, int ifIndex)
        {
            if (ifIndex <= 0) return RouteAdd(network, mask, gateway);
            RouteDelete(network, mask);
            return Proc.Run(Proc.System32("route.exe"), "ADD " + network + " MASK " + mask
                + " " + gateway + " METRIC 1 IF " + ifIndex, 30000);
        }

        static readonly Regex GatewayLine = new Regex(@"^(\d+\.\d+\.\d+\.\d+)\s+(\d+)\s*$", RegexOptions.Multiline);

        /// The next hop and interface Windows uses for internet traffic right now (the home
        /// router, or another VPN such as a Tailscale exit node). Asked before our own routes go in.
        public static bool DefaultGateway(out string gateway, out int ifIndex)
        {
            gateway = null;
            ifIndex = 0;
            var r = Proc.PowerShell(
                "Find-NetRoute -RemoteIPAddress 9.9.9.9 | Where-Object { $_.NextHop } | " +
                "Select-Object -First 1 | ForEach-Object { \"$($_.NextHop) $($_.InterfaceIndex)\" }");
            var m = GatewayLine.Match(r.Out.Trim());
            if (!m.Success || m.Groups[1].Value == "0.0.0.0") return false;
            gateway = m.Groups[1].Value;
            ifIndex = int.Parse(m.Groups[2].Value);
            return true;
        }

        /// The two halves of the internet into WSL: more specific than any 0.0.0.0/0, so they win
        /// without touching it, and LAN/WSL/Tailscale routes (more specific still) stay direct.
        public static ProcResult VpnRoutesAdd(string wslIp)
        {
            var a = RouteAdd("0.0.0.0", "128.0.0.0", wslIp);
            if (!a.Ok) return a;
            var b = RouteAdd("128.0.0.0", "128.0.0.0", wslIp);
            if (!b.Ok) RouteDelete("0.0.0.0", "128.0.0.0");
            return b;
        }

        public static void VpnRoutesDelete()
        {
            RouteDelete("0.0.0.0", "128.0.0.0");
            RouteDelete("128.0.0.0", "128.0.0.0");
        }

        /// Send .loki and .snode lookups to llarpd's DNS (Name Resolution Policy Table).
        public static ProcResult NrptAdd(string server)
        {
            return Proc.PowerShell(
                "$ErrorActionPreference='Stop';" +
                "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + Tag + "' } | Remove-DnsClientNrptRule -Force;" +
                "Add-DnsClientNrptRule -Namespace '.loki','.snode' -NameServers '" + server + "' -Comment '" + Tag + "' | Out-Null;" +
                "Clear-DnsClientCache");
        }

        public static void NrptRemove()
        {
            Proc.PowerShell(
                "Get-DnsClientNrptRule | Where-Object { $_.Comment -eq '" + Tag + "' } | Remove-DnsClientNrptRule -Force;" +
                "Clear-DnsClientCache");
        }

        public static ProcResult FirewallAllowUdp(int port)
        {
            FirewallRemove();
            return Proc.Run(Proc.System32("netsh.exe"),
                "advfirewall firewall add rule name=\"" + Tag + " relay\" dir=in action=allow protocol=UDP localport=" + port, 30000);
        }

        public static void FirewallRemove()
        {
            Proc.Run(Proc.System32("netsh.exe"), "advfirewall firewall delete rule name=\"" + Tag + " relay\"", 30000);
        }
    }

    /// Relays UDP from a Windows port to llarpd in WSL. One socket per remote peer so llarpd
    /// can tell peers apart; replies go back out of the public port.
    sealed class UdpRelay : IDisposable
    {
        const int SIO_UDP_CONNRESET = -1744830452;
        static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);

        class Peer
        {
            public UdpClient Sock;
            public IPEndPoint Remote;
            public DateTime Last;
        }

        readonly UdpClient listener;
        readonly IPEndPoint target;
        readonly Dictionary<IPEndPoint, Peer> peers = new Dictionary<IPEndPoint, Peer>();
        readonly object gate = new object();
        readonly Timer reaper;
        volatile bool closed;

        public UdpRelay(int port, IPEndPoint target)
        {
            this.target = target;
            listener = new UdpClient(AddressFamily.InterNetwork);
            NoReset(listener);
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            reaper = new Timer(Reap, null, 30000, 30000);
            Listen();
        }

        public int PeerCount
        {
            get { lock (gate) return peers.Count; }
        }

        // without this an ICMP "port unreachable" makes the next receive throw (WSAECONNRESET)
        static void NoReset(UdpClient c)
        {
            try { c.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
            catch (Exception) { }
        }

        void Listen()
        {
            if (closed) return;
            try { listener.BeginReceive(OnListen, null); }
            catch (Exception) { }
        }

        void OnListen(IAsyncResult ar)
        {
            try
            {
                IPEndPoint from = null;
                byte[] data = null;
                try { data = listener.EndReceive(ar, ref from); }
                catch (SocketException) { }
                if (data != null && from != null)
                {
                    Peer p;
                    lock (gate)
                    {
                        if (!peers.TryGetValue(from, out p))
                        {
                            p = new Peer { Sock = new UdpClient(AddressFamily.InterNetwork), Remote = from };
                            NoReset(p.Sock);
                            p.Sock.Connect(target);
                            peers[from] = p;
                            PeerListen(p);
                        }
                        p.Last = DateTime.UtcNow;
                    }
                    p.Sock.Send(data, data.Length);
                }
            }
            catch (Exception) { }
            Listen();
        }

        void PeerListen(Peer p)
        {
            if (closed) return;
            try { p.Sock.BeginReceive(ar => OnPeer(p, ar), null); }
            catch (Exception) { }
        }

        void OnPeer(Peer p, IAsyncResult ar)
        {
            try
            {
                IPEndPoint src = null;
                var data = p.Sock.EndReceive(ar, ref src);
                p.Last = DateTime.UtcNow;
                listener.Send(data, data.Length, p.Remote);
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception) { }
            PeerListen(p);
        }

        void Reap(object _)
        {
            lock (gate)
            {
                var cutoff = DateTime.UtcNow - Idle;
                foreach (var kv in peers.Where(k => k.Value.Last < cutoff).ToList())
                {
                    kv.Value.Sock.Close();
                    peers.Remove(kv.Key);
                }
            }
        }

        public void Dispose()
        {
            closed = true;
            reaper.Dispose();
            listener.Close();
            lock (gate)
            {
                foreach (var p in peers.Values) p.Sock.Close();
                peers.Clear();
            }
        }
    }

    // ------------------------------------------------------------------ host mode

    /// `LLARP-Control.exe --host`: runs llarpd (llarpctl start --foreground in WSL) and connects
    /// Windows to it until it exits. Started hidden by the GUI, at sign-in, or by llarpctl.cmd.
    static class Host
    {
        const string MutexName = @"Local\LLARPControlHost";

        public static bool IsRunning()
        {
            Mutex m;
            if (Mutex.TryOpenExisting(MutexName, out m))
            {
                m.Dispose();
                return true;
            }
            return false;
        }

        public static Dictionary<string, object> ReadState()
        {
            try { return File.Exists(Paths.HostState) ? J.Parse(File.ReadAllText(Paths.HostState)) : null; }
            catch (Exception) { return null; }
        }

        static void WriteState(Dictionary<string, object> st)
        {
            try { File.WriteAllText(Paths.HostState, J.Write(st)); }
            catch (Exception) { }
        }

        /// Undo whatever a previous host run set up on the Windows side.
        static void Teardown(Dictionary<string, object> st)
        {
            if (st == null) return;
            if (J.Bool(st, "vpn_on")) WinNet.VpnRoutesDelete();
            foreach (var peer in J.Arr(st, "vpn_peers")) WinNet.RouteDelete(peer.ToString(), "255.255.255.255");
            string net = J.Str(st, "route_net"), mask = J.Str(st, "route_mask");
            if (net != null && mask != null) WinNet.RouteDelete(net, mask);
            if (J.Bool(st, "nrpt")) WinNet.NrptRemove();
            if (J.Bool(st, "firewall")) WinNet.FirewallRemove();
        }

        /// If a host run was killed (no clean exit), remove the routes it left behind. Without
        /// this a VPN route into WSL would leave Windows without internet.
        public static void CleanupStale()
        {
            if (IsRunning()) return;
            var st = ReadState();
            if (st == null) return;
            Teardown(st);
            try { File.Delete(Paths.HostState); } catch (Exception) { }
        }

        // ---- VPN: all of Windows' internet traffic through llarpd's exit

        class VpnState
        {
            public bool On;
            public string Gateway;
            public int IfIndex;
            public readonly HashSet<string> Peers = new HashSet<string>();
        }

        /// Follow llarpd's VPN state. Its relays get /32 routes via the real gateway (the same
        /// ones llarpd pokes inside WSL); once they're in, 0.0.0.0/1 + 128.0.0.0/1 go into WSL.
        static void VpnTick(Dictionary<string, object> st, VpnState vpn, string wslIp)
        {
            var r = Wsl.Ctl("winhost", "info");
            var info = r.Ok ? J.Parse(r.Out) : null;
            if (info == null) return; // try again next tick
            var v = J.Obj(info, "vpn");
            var peers = J.Arr(v, "peers").Select(x => x.ToString()).ToList();
            // switch Windows over only once the exit has proven it passes traffic, so a dead exit
            // doesn't cut the PC off; once on, stay on while llarpd keeps routing (VPN semantics)
            bool want = v != null && J.Bool(v, "routed") && peers.Count > 0
                        && (vpn.On || J.Str(v, "state") == "on");
            st["vpn_exit"] = J.Str(v, "exit");
            st["vpn_state"] = J.Str(v, "state");

            if (!want)
            {
                if (vpn.On || vpn.Peers.Count > 0) VpnOff(st, vpn);
                return;
            }
            if (vpn.Gateway == null)
            {
                string gw;
                int ifIndex;
                if (!WinNet.DefaultGateway(out gw, out ifIndex))
                {
                    st["vpn_error"] = "no default gateway found on Windows";
                    return;
                }
                vpn.Gateway = gw;
                vpn.IfIndex = ifIndex;
                st["vpn_gateway"] = gw;
            }
            foreach (var gone in vpn.Peers.Where(p => !peers.Contains(p)).ToList())
            {
                WinNet.RouteDelete(gone, "255.255.255.255");
                vpn.Peers.Remove(gone);
            }
            foreach (var peer in peers.Where(p => !vpn.Peers.Contains(p)))
            {
                if (WinNet.RouteAdd(peer, "255.255.255.255", vpn.Gateway, vpn.IfIndex).Ok) vpn.Peers.Add(peer);
            }
            st["vpn_peers"] = vpn.Peers.ToList();
            if (!vpn.On && vpn.Peers.Count > 0)
            {
                var res = WinNet.VpnRoutesAdd(wslIp);
                vpn.On = res.Ok;
                if (res.Ok) st.Remove("vpn_error");
                else st["vpn_error"] = "route: " + res.Text;
            }
            st["vpn_on"] = vpn.On;
        }

        static void VpnOff(Dictionary<string, object> st, VpnState vpn)
        {
            if (vpn.On) WinNet.VpnRoutesDelete(); // first, so traffic never loops
            foreach (var p in vpn.Peers) WinNet.RouteDelete(p, "255.255.255.255");
            vpn.On = false;
            vpn.Gateway = null;
            vpn.IfIndex = 0;
            vpn.Peers.Clear();
            st["vpn_on"] = false;
            st["vpn_peers"] = new List<string>();
            st.Remove("vpn_gateway");
        }

        static void Error(string text)
        {
            try { File.AppendAllText(Paths.HostError, text.TrimEnd() + "\n"); }
            catch (Exception) { }
        }

        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // another host is running, or still cleaning up after a stop (restart)
                    bool got;
                    try { got = mutex.WaitOne(20000); }
                    catch (AbandonedMutexException) { got = true; }
                    if (!got) return 0;
                }
                Directory.CreateDirectory(Paths.AppDir);
                try { File.Delete(Paths.HostError); } catch (Exception) { }
                Teardown(ReadState());
                try { File.Delete(Paths.HostState); } catch (Exception) { }

                if (!Wsl.DistroExists())
                {
                    Error("LLARP isn't set up yet: open LLARP Control and run Setup.");
                    return 1;
                }

                var st = new Dictionary<string, object>();
                st["pid"] = Process.GetCurrentProcess().Id;
                st["started"] = DateTime.UtcNow.ToString("o");
                UdpRelay relay = null;
                int code;
                try
                {
                    var psi = Proc.Info(Wsl.Exe, Wsl.ExecArgs(Wsl.Llarpctl, "start", "--foreground"));
                    using (var p = new Process())
                    {
                        p.StartInfo = psi;
                        p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error(Proc.Clean(e.Data)); };
                        p.OutputDataReceived += (s, e) => { };
                        p.Start();
                        p.BeginErrorReadLine();
                        p.BeginOutputReadLine();
                        bool connected = false;
                        var vpn = new VpnState();
                        while (!p.WaitForExit(connected ? 2000 : 1500))
                        {
                            if (connected)
                            {
                                if (relay != null) st["relay_peers"] = relay.PeerCount;
                                else
                                {
                                    try { VpnTick(st, vpn, J.Str(st, "wsl_ip")); }
                                    catch (Exception ex) { st["vpn_error"] = ex.Message; }
                                }
                                WriteState(st);
                                continue;
                            }
                            try
                            {
                                connected = Connect(st, ref relay);
                            }
                            catch (Exception ex)
                            {
                                st["error"] = ex.Message;
                            }
                            WriteState(st);
                        }
                        p.WaitForExit();
                        code = p.ExitCode;
                    }
                }
                finally
                {
                    if (relay != null) relay.Dispose();
                    Teardown(st);
                    Wsl.Ctl("winhost", "down");
                    try { File.Delete(Paths.HostState); } catch (Exception) { }
                    mutex.ReleaseMutex();
                }
                return code;
            }
        }

        /// One attempt at connecting Windows to the running llarpd. True when done.
        static bool Connect(Dictionary<string, object> st, ref UdpRelay relay)
        {
            var r = Wsl.Ctl("winhost", "up");
            var info = r.Ok ? J.Parse(r.Out) : null;
            if (info == null)
            {
                st["error"] = r.Text;
                return false;
            }
            if (!J.Bool(info, "running")) return false;
            string wslIp = J.Str(info, "wsl_ip");
            st["wsl_ip"] = wslIp;
            st.Remove("error");
            if (wslIp == null) return false;

            if (J.Bool(info, "router_mode"))
            {
                int port = (int)(J.Num(info, "public_port") ?? 1090);
                st["mode"] = "router";
                st["relay_port"] = port;
                if (relay == null)
                {
                    relay = new UdpRelay(port, new IPEndPoint(IPAddress.Parse(wslIp), port));
                    st["firewall"] = WinNet.FirewallAllowUdp(port).Ok;
                }
                st["relay_peers"] = relay.PeerCount;
                return true;
            }

            st["mode"] = "client";
            string range = J.Str(info, "range"), dns = J.Str(info, "dns");
            string net, mask;
            if (range == null || !WinNet.ParseRange(range, out net, out mask)) return false; // tunnel not up yet
            st["range"] = range;
            var res = WinNet.RouteAdd(net, mask, wslIp);
            if (!res.Ok)
            {
                st["error"] = "route: " + res.Text;
                return false;
            }
            st["route_net"] = net;
            st["route_mask"] = mask;
            if (dns != null)
            {
                res = WinNet.NrptAdd(dns);
                st["nrpt"] = res.Ok;
                st["dns"] = dns;
                if (!res.Ok) st["error"] = "DNS rule: " + res.Text;
            }
            return true;
        }

        /// Start host mode in a hidden, independent process (it outlives the GUI).
        public static void Launch()
        {
            try { File.Delete(Paths.HostError); } catch (Exception) { }
            var psi = new ProcessStartInfo(Paths.Exe, "--host");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process.Start(psi);
        }

        public static string LastError()
        {
            try { return File.Exists(Paths.HostError) ? File.ReadAllText(Paths.HostError).Trim() : null; }
            catch (Exception) { return null; }
        }
    }

    // ------------------------------------------------------------------ autostart (Task Scheduler)

    static class Autostart
    {
        const string Task = "LLARP Control";
        const string ResumeTask = "LLARP Control Setup";

        static string Schtasks { get { return Proc.System32("schtasks.exe"); } }

        public static bool Enabled()
        {
            return Proc.Run(Schtasks, "/Query /TN " + Proc.Quote(Task), 30000).Ok;
        }

        public static ProcResult Set(bool on)
        {
            if (!on) return Proc.Run(Schtasks, "/Delete /F /TN " + Proc.Quote(Task), 30000);
            return Proc.Run(Schtasks, "/Create /F /RL HIGHEST /SC ONLOGON /DELAY 0000:20 /TN " + Proc.Quote(Task)
                + " /TR " + Proc.Quote("\"" + Paths.InstalledExeOrSelf() + "\" --host"), 30000);
        }

        /// Run setup again once the user signs back in (after the WSL install reboot).
        public static ProcResult SetResume(bool on)
        {
            if (!on) return Proc.Run(Schtasks, "/Delete /F /TN " + Proc.Quote(ResumeTask), 30000);
            return Proc.Run(Schtasks, "/Create /F /RL HIGHEST /SC ONLOGON /TN " + Proc.Quote(ResumeTask)
                + " /TR " + Proc.Quote("\"" + Paths.InstalledExeOrSelf() + "\" --setup"), 30000);
        }
    }
}
