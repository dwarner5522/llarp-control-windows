// LLARP Control for Windows: entry point and the install/update screen.
//
//   LLARP-Control.exe              install (first run) or open the control panel
//   LLARP-Control.exe --setup      open the control panel and run the setup
//   LLARP-Control.exe --host       (hidden) run llarpd and connect Windows to it
//   LLARP-Control.exe --uninstall  remove LLARP Control (Apps & Features uses this)

using System;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("LLARP Control")]
[assembly: AssemblyProduct("LLARP Control")]
[assembly: AssemblyDescription("Control panel for llarpd (LLARP) on Windows, running in WSL 2")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LlarpControl
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (mode == "--host") return Host.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (mode == "--uninstall") return Uninstall();
#if PREVIEW
            // UI preview builds only (build.ps1 -Preview): show a window without installing
            if (mode == "--preview-welcome") { Application.Run(new WelcomeForm()); return 0; }
            if (mode == "--preview-main")
            {
                var f = new MainForm(false);
                if (args.Length > 1) f.Shown += (s, e) => f.PreviewPage(int.Parse(args[1]));
                Application.Run(f);
                return 0;
            }
#endif

            bool created;
            using (new Mutex(true, @"Local\LLARPControlGui", out created))
            {
                if (!created)
                {
                    foreach (var p in Process.GetProcessesByName("LLARP-Control"))
                    {
                        if (p.Id != Process.GetCurrentProcess().Id && p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                            SetForegroundWindow(p.MainWindowHandle);
                        }
                    }
                    return 0;
                }
                if (Installer.IsInstalled() && Paths.RunningInstalled())
                {
                    Installer.CleanupOldCopies();
                    Application.Run(new MainForm(mode == "--setup"));
                }
                else
                {
                    Application.Run(new WelcomeForm());
                }
            }
            // after the welcome screen installed us: open the installed copy and run setup
            // (updates too: setup skips what's done and refreshes llarpctl in the distro)
            if (WelcomeForm.LaunchAfter != null)
                Process.Start(new ProcessStartInfo(WelcomeForm.LaunchAfter, "--setup") { UseShellExecute = true });
            return 0;
        }

        static int Uninstall()
        {
            var answer = MessageBox.Show(
                "Remove LLARP Control from this PC?\n\n" +
                "Yes: also delete the LLARP WSL distro (llarpd, its keys and your .loki address).\n" +
                "No: keep the LLARP distro, so reinstalling keeps your address.\n" +
                "Cancel: don't uninstall.",
                "Uninstall LLARP Control", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return 1;
            Cursor.Current = Cursors.WaitCursor;
            Installer.Uninstall(answer == DialogResult.Yes, s => { });
            MessageBox.Show("LLARP Control was removed.", "LLARP Control", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }

    /// First run (or a newer exe): explains what will happen, installs, then hands over to setup.
    class WelcomeForm : Form
    {
        public static string LaunchAfter;
        readonly CheckBox desktop;
        readonly CheckBox autostart;

        public WelcomeForm()
        {
            bool update = Installer.IsInstalled();
            Text = update ? "Update LLARP Control" : "Install LLARP Control";
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20);

            var t = Theme.Table(2);
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var logo = new LogoBox { Size = new Size(56, 56), Margin = new Padding(0, 0, 14, 0) };
            t.RowCount = 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(logo, 0, 0);
            var heading = Theme.Table(1);
            Theme.AddRow(heading, Theme.Label("LLARP Control", Theme.Lime, Theme.Brand));
            Theme.AddRow(heading, Theme.Label(update ? "A new version is ready to install." : "Run an LLARP node on Windows.", Theme.Dim));
            heading.Anchor = AnchorStyles.Left;
            heading.Dock = DockStyle.None;
            t.Controls.Add(heading, 1, 0);

            string body = update
                ? "This replaces the installed LLARP Control with this version. Your settings, " +
                  "your .loki address and the LLARP distro are kept. If llarpd is running, it keeps running."
                : "llarpd has no Windows version, so LLARP Control runs it in WSL 2 (a small Linux " +
                  "system built into Windows) and connects it to Windows for you.\n\n" +
                  "Setup will:\n" +
                  "  •  install WSL 2 if needed (may need one restart; setup then continues by itself)\n" +
                  "  •  download Debian from Microsoft's WSL list into a separate \"LLARP\" distro\n" +
                  "  •  build llarpd from source inside it (about 10-30 minutes)\n" +
                  "  •  start your node: .loki and .snode addresses then work in Windows\n\n" +
                  "It needs about 3 GB of disk space and an internet connection.\n" +
                  "Installs to: " + Paths.InstallDir;
            var text = Theme.Label(body);
            text.MaximumSize = new Size(640, 0);
            text.Margin = new Padding(0, 16, 0, 10);
            Theme.AddRow(t, text, false, 2);

            desktop = Theme.Check("Put a shortcut on the desktop");
            desktop.Checked = true;
            autostart = Theme.Check("Start LLARP when I sign in to Windows");
            if (!update)
            {
                Theme.AddRow(t, desktop, false, 2);
                Theme.AddRow(t, autostart, false, 2);
            }

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
            var install = Theme.Button(update ? "Update" : "Install", true);
            install.Click += (s, e) => DoInstall(update);
            var cancel = Theme.Button("Cancel");
            cancel.Click += (s, e) => Close();
            buttons.Controls.Add(install);
            buttons.Controls.Add(cancel);
            Theme.AddRow(t, buttons, false, 2);
            Controls.Add(t);
            AcceptButton = install;
            CancelButton = cancel;
        }

        void DoInstall(bool update)
        {
            string exe;
            try
            {
                Cursor = Cursors.WaitCursor;
                exe = Installer.Install(!update && desktop.Checked);
                if (!update && autostart.Checked) Autostart.Set(true);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, "Installing failed: " + ex.Message, "LLARP Control", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            LaunchAfter = exe;
            Close();
        }
    }
}
