// Installing LLARP Control itself, and setting up WSL + the LLARP distro + llarpd.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32;

namespace LlarpControl
{
    static class Installer
    {
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LLARPControl";

        public const string LlarpctlCmd =
            "@echo off\r\n" +
            "rem llarpctl for Windows: runs llarpctl inside the LLARP WSL distro (see llarpctl --help).\r\n" +
            "rem \"start\" and \"restart\" go through LLARP Control so Windows can reach .loki names.\r\n" +
            "setlocal\r\n" +
            "if /i \"%~1\"==\"start\" goto start\r\n" +
            "if /i \"%~1\"==\"restart\" (\r\n" +
            "  wsl.exe -d LLARP -u root --exec /usr/local/bin/llarpctl stop\r\n" +
            "  goto start\r\n" +
            ")\r\n" +
            "wsl.exe -d LLARP -u root --exec /usr/local/bin/llarpctl %*\r\n" +
            "exit /b %errorlevel%\r\n" +
            ":start\r\n" +
            "start \"\" \"%~dp0LLARP-Control.exe\" --host\r\n" +
            "echo llarpd is starting in the background. Check it with: llarpctl status\r\n";

        static string StartMenuLink
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Paths.AppName + ".lnk");
            }
        }

        static string DesktopLink
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Paths.AppName + ".lnk");
            }
        }

        public static bool IsInstalled()
        {
            return File.Exists(Paths.InstalledExe);
        }

        /// Copy this exe to the install dir and register it. Returns the installed exe path.
        public static string Install(bool desktopShortcut)
        {
            Directory.CreateDirectory(Paths.InstallDir);
            Directory.CreateDirectory(Paths.AppDir);
            if (!Paths.RunningInstalled())
            {
                if (File.Exists(Paths.InstalledExe))
                {
                    // the old version may be running (e.g. keeping llarpd up); a running exe can be renamed
                    string old = Paths.InstalledExe + ".old-" + DateTime.Now.Ticks;
                    File.Move(Paths.InstalledExe, old);
                }
                File.Copy(Paths.Exe, Paths.InstalledExe, true);
            }
            File.WriteAllText(Path.Combine(Paths.InstallDir, "llarpctl.cmd"), LlarpctlCmd);

            Shortcut(StartMenuLink, Paths.InstalledExe, "", "Control llarpd (LLARP) running in WSL");
            if (desktopShortcut) Shortcut(DesktopLink, Paths.InstalledExe, "", "Control llarpd (LLARP) running in WSL");
            AddToPath(Paths.InstallDir);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Paths.AppName);
                k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
                k.SetValue("Publisher", "llarp-control");
                k.SetValue("DisplayIcon", Paths.InstalledExe);
                k.SetValue("InstallLocation", Paths.InstallDir);
                k.SetValue("UninstallString", "\"" + Paths.InstalledExe + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            return Paths.InstalledExe;
        }

        public static void CleanupOldCopies()
        {
            try
            {
                foreach (var f in Directory.GetFiles(Paths.InstallDir, "LLARP-Control.exe.old-*"))
                {
                    try { File.Delete(f); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        /// Remove the app (and, if asked, the LLARP distro with llarpd and its keys).
        public static void Uninstall(bool removeDistro, Action<string> log)
        {
            if (Wsl.DistroExists())
            {
                log("Stopping llarpd");
                Wsl.Ctl("stop");
                for (int i = 0; i < 40 && Host.IsRunning(); i++) Thread.Sleep(500);
                if (removeDistro)
                {
                    log("Removing the LLARP WSL distro");
                    Proc.Run(Wsl.Exe, "--unregister " + Wsl.Distro, 120000);
                }
            }
            log("Removing scheduled tasks, shortcuts and settings");
            Autostart.Set(false);
            Autostart.SetResume(false);
            foreach (var l in new[] { StartMenuLink, DesktopLink })
            {
                try { File.Delete(l); } catch (Exception) { }
            }
            RemoveFromPath(Paths.InstallDir);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            if (removeDistro)
            {
                try { Directory.Delete(Paths.AppDir, true); } catch (Exception) { }
            }
            // this exe may be the installed one: delete the folder once we've exited
            var psi = new ProcessStartInfo(Proc.System32("cmd.exe"),
                "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            Process.Start(psi);
        }

        static void Shortcut(string link, string target, string args, string description)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object sc = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var t = sc.GetType();
                t.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target });
                t.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { args });
                t.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { description });
                t.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception) { }
        }

        static List<string> UserPath(RegistryKey env)
        {
            var value = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            return value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        static void SetUserPath(RegistryKey env, List<string> parts)
        {
            env.SetValue("Path", string.Join(";", parts), RegistryValueKind.ExpandString);
            BroadcastEnvironment();
        }

        static void AddToPath(string dir)
        {
            using (var env = Registry.CurrentUser.CreateSubKey("Environment"))
            {
                var parts = UserPath(env);
                if (parts.Any(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
                parts.Add(dir);
                SetUserPath(env, parts);
            }
        }

        static void RemoveFromPath(string dir)
        {
            using (var env = Registry.CurrentUser.CreateSubKey("Environment"))
            {
                var parts = UserPath(env);
                int n = parts.RemoveAll(p => string.Equals(p.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (n > 0) SetUserPath(env, parts);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

        static void BroadcastEnvironment()
        {
            UIntPtr result;
            SendMessageTimeout((IntPtr)0xffff, 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out result);
        }
    }

    enum SetupResult { Done, NeedsReboot, Failed }

    /// The whole setup, step by step: WSL, the LLARP distro, llarpd, llarpctl. Safe to re-run.
    class SetupRunner
    {
        const string DistroList = "https://raw.githubusercontent.com/microsoft/WSL/master/distributions/DistributionInfo.json";
        static string HashFile { get { return Path.Combine(Paths.AppDir, "payload.hash"); } }

        readonly Action<string> log;
        readonly CancellationToken cancel;
        bool installedWslNow;

        public SetupRunner(Action<string> log, CancellationToken cancel)
        {
            this.log = s =>
            {
                log(s);
                try { File.AppendAllText(Paths.SetupLog, s + "\n"); } catch (Exception) { }
            };
            this.cancel = cancel;
        }

        void Step(string s)
        {
            log("");
            log("==> " + s);
        }

        public SetupResult Run(bool forceBuild)
        {
            Directory.CreateDirectory(Paths.AppDir);
            try
            {
                var r = EnsureWsl();
                if (r != SetupResult.Done) return r;
                if (cancel.IsCancellationRequested) return SetupResult.Failed;
                if (!EnsureDistro())
                {
                    if (!installedWslNow) return SetupResult.Failed;
                    // WSL was only just installed: its virtual machine platform needs the restart first
                    log("This is usually fixed by the restart that finishes the WSL install.");
                    Autostart.SetResume(true);
                    return SetupResult.NeedsReboot;
                }
                if (cancel.IsCancellationRequested) return SetupResult.Failed;
                if (!RunScript(forceBuild ? "--force-build" : null)) return SetupResult.Failed;
                Autostart.SetResume(false);
                Step("Setup finished");
                return SetupResult.Done;
            }
            catch (Exception ex)
            {
                log("error: " + ex.Message);
                return SetupResult.Failed;
            }
        }

        SetupResult EnsureWsl()
        {
            Step("Checking WSL");
            if (Wsl.Installed())
            {
                log("WSL is installed.");
                Proc.Run(Wsl.Exe, "--set-default-version 2", 60000);
                return SetupResult.Done;
            }
            if (!File.Exists(Wsl.Exe))
            {
                // very old Windows 10 without wsl.exe --install: turn the features on directly
                Step("Turning on the Windows features WSL needs");
                string dism = Proc.System32("dism.exe");
                foreach (var feature in new[] { "Microsoft-Windows-Subsystem-Linux", "VirtualMachinePlatform" })
                    Proc.Stream(dism, "/online /enable-feature /featurename:" + feature + " /all /norestart", log, cancel);
                Autostart.SetResume(true);
                return SetupResult.NeedsReboot;
            }
            Step("Installing WSL (Windows Subsystem for Linux)");
            int code = Proc.Stream(Wsl.Exe, "--install --no-distribution", log, cancel);
            installedWslNow = true;
            if (code != 0) log("wsl --install exited with code " + code);
            if (Wsl.Installed())
            {
                Proc.Run(Wsl.Exe, "--set-default-version 2", 60000);
                return SetupResult.Done;
            }
            Autostart.SetResume(true);
            log("WSL needs a restart of Windows to finish installing.");
            return SetupResult.NeedsReboot;
        }

        bool EnsureDistro()
        {
            Step("Checking the LLARP WSL distro");
            if (Wsl.DistroExists())
            {
                log("The LLARP distro exists.");
                return true;
            }
            log("Looking up the official Debian image for WSL");
            var info = J.Parse(Download(DistroList));
            var modern = J.Obj(info, "ModernDistributions");
            var debian = modern == null ? null : J.Arr(modern, "Debian").FirstOrDefault() as Dictionary<string, object>;
            var image = J.Obj(debian, Wsl.IsArm64() ? "Arm64Url" : "Amd64Url");
            string url = J.Str(image, "Url"), sha = J.Str(image, "Sha256");
            if (url == null) throw new InvalidOperationException("couldn't find the Debian image in Microsoft's WSL distribution list");

            string file = Path.Combine(Paths.AppDir, "debian.wsl");
            log("Downloading " + url);
            DownloadFile(url, file);
            if (!string.IsNullOrEmpty(sha))
            {
                log("Checking the download");
                string got;
                using (var s = File.OpenRead(file))
                using (var h = SHA256.Create())
                    got = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
                if (!string.Equals(got, sha.Replace("0x", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("the Debian image's checksum doesn't match (expected " + sha + ", got " + got + ")");
            }

            Step("Creating the LLARP distro (" + Paths.DistroDir + ")");
            Directory.CreateDirectory(Paths.DistroDir);
            int code = Proc.Stream(Wsl.Exe, "--import " + Wsl.Distro + " " + Proc.Quote(Paths.DistroDir) + " "
                + Proc.Quote(file) + " --version 2", log, cancel);
            try { File.Delete(file); } catch (Exception) { }
            if (code != 0 || !Wsl.DistroExists())
            {
                log("error: wsl --import failed (code " + code + "). If it mentions virtualization, turn on "
                    + "virtualization (VT-x/AMD-V) in the BIOS and \"Virtual Machine Platform\" in Windows Features.");
                return false;
            }
            return true;
        }

        bool RunScript(string flag)
        {
            Step(flag == "--files-only" ? "Updating llarpctl in the LLARP distro" : "Setting up llarpd in the LLARP distro");
            string dir = Wsl.ToLinuxPath(Payload.Extract());
            var argv = new List<string> { "/bin/bash", dir + "/wsl-setup.sh", dir };
            if (flag != null) argv.Add(flag);
            int code = Proc.Stream(Wsl.Exe, Wsl.ExecArgs(argv.ToArray()), log, cancel);
            if (code != 0)
            {
                log("error: setup inside WSL failed (code " + code + "); see above.");
                return false;
            }
            File.WriteAllText(HashFile, Payload.Hash());
            return true;
        }

        /// After an update of this app: refresh llarpctl/llarp_core.py in the distro if they changed.
        public static bool FilesOutdated()
        {
            try { return !File.Exists(HashFile) || File.ReadAllText(HashFile).Trim() != Payload.Hash(); }
            catch (Exception) { return true; }
        }

        public bool UpdateFiles()
        {
            return RunScript("--files-only");
        }

        static string Download(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "llarp-control-windows";
                return wc.DownloadString(url);
            }
        }

        void DownloadFile(string url, string dest)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "llarp-control-windows";
            req.AllowAutoRedirect = true;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(dest))
            {
                long total = resp.ContentLength, done = 0;
                int lastPct = -10;
                var buf = new byte[1 << 16];
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException("download cancelled");
                    dst.Write(buf, 0, n);
                    done += n;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct >= lastPct + 10)
                        {
                            lastPct = pct - pct % 10;
                            log(string.Format("  {0}%  ({1:0.0} of {2:0.0} MB)", lastPct, done / 1048576.0, total / 1048576.0));
                        }
                    }
                }
            }
        }
    }
}
