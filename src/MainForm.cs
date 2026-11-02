// The control panel: same layout as the GTK app (power/address/exit up top; Bootstraps, Log,
// Settings tabs) plus a Setup tab. Everything goes through llarpctl inside the LLARP distro.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LlarpControl
{
    class MainForm : Form
    {
        const int InitialLogBytes = 256 * 1024;
        const int MaxLogChars = 600000;
        static readonly string[] LogLevels = { "trace", "debug", "info", "warn", "error" };

        // header
        readonly Label status = Theme.Label("", Theme.Dim, Theme.Status);
        readonly ToggleSwitch power = new ToggleSwitch();
        readonly Button restartBtn = Theme.Button("Restart");
        readonly TextBox address = new TextBox();
        readonly Button copyBtn = Theme.Button("Copy");
        readonly ToggleSwitch exitSwitch = new ToggleSwitch();
        readonly ComboBox vpnCombo = new ComboBox();
        readonly Button vpnAuth = Theme.Button("Auth code…");
        readonly Button vpnForget = Theme.Button("Forget");
        readonly ToggleSwitch vpnSwitch = new ToggleSwitch();
        readonly Label vpnStatus = Theme.Wrap("", Theme.Dim);
        string shownExits;
        readonly Label windowsInfo = Theme.Wrap("", Theme.Dim);
        readonly BorderPanel banner = new BorderPanel();
        readonly Label bannerText = Theme.Wrap("");
        readonly BorderPanel exitPanel = new BorderPanel();
        readonly Label exitInfo = Theme.Wrap("");
        readonly Label fwdStatus = Theme.Wrap("", Theme.Dim);
        readonly Button fwdEnable = Theme.Button("Enable forwarding");
        readonly Button fwdRemove = Theme.Button("Remove NAT");
        readonly ToolTip tips = new ToolTip();

        // pages
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(12) };
        readonly List<TabButton> tabs = new List<TabButton>();
        readonly List<Control> pages = new List<Control>();
        const int SetupPage = 0, BootstrapPage = 1, LogPage = 2, SettingsPage = 3;

        // setup page
        readonly Label wslState = Theme.Label("…");
        readonly Label distroState = Theme.Label("…");
        readonly Label llarpdState = Theme.Label("…");
        readonly Button setupBtn = Theme.Button("Set up everything", true);
        readonly Button rebuildBtn = Theme.Button("Rebuild llarpd");
        readonly Button cancelBtn = Theme.Button("Cancel");
        readonly Button shellBtn = Theme.Button("Open a Linux shell");
        readonly Button uninstallBtn = Theme.Button("Uninstall…");
        readonly TextBox setupOut = Theme.Output();

        // bootstraps page
        readonly ListView bsList = new ListView();
        readonly Button bsBundled = Theme.Button("Add bundled…");

        // log page
        readonly TextBox logView = Theme.Output();
        readonly CheckBox follow = Theme.Check("Follow");
        readonly StringBuilder heldLog = new StringBuilder();

        // settings page
        readonly ComboBox level = new ComboBox();
        readonly CheckBox router = Theme.Check("Router mode (snode): relay traffic for the network instead of being a client");
        readonly TextBox publicIp = Theme.Entry();
        readonly NumericUpDown publicPort = new NumericUpDown();
        readonly CheckBox persist = Theme.Check("Keep the same .loki address across restarts");
        readonly CheckBox autostart = Theme.Check("Start LLARP when I sign in to Windows");
        readonly Label paths = Theme.Wrap("", Theme.Dim);

        // state
        Dictionary<string, object> dump;
        Dictionary<string, object> shownSettings = new Dictionary<string, object>();
        string shownBootstraps;
        bool distroExists, ready, polling, syncing, logBusy, setupRunning;
        bool wasRunning, userStop, restartPending, firstDisplay = true;
        DateTime? startRequested;
        long logPos = -1;
        CancellationTokenSource setupCancel;
        readonly bool runSetupOnLoad;

        public MainForm(bool runSetup)
        {
            runSetupOnLoad = runSetup;
            Text = Paths.AppName;
            Icon = Theme.AppIcon();
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 680);
            MinimumSize = new Size(700, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Padding = new Padding(14, 10, 14, 14);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.AddRow(root, BuildHeader());
            Theme.AddRow(root, BuildAddressRow());
            Theme.AddRow(root, BuildVpnRow());
            Theme.AddRow(root, vpnStatus);
            Theme.AddRow(root, windowsInfo);
            Theme.AddRow(root, BuildBanner());
            Theme.AddRow(root, BuildExitPanel());
            Theme.AddRow(root, BuildTabs());
            Theme.AddRow(root, content, true);
            Controls.Add(root);

            AddPage("Setup", BuildSetupPage());
            AddPage("Bootstraps", BuildBootstrapPage());
            AddPage("Log", BuildLogPage());
            AddPage("Settings", BuildSettingsPage());
            Theme.Apply(this);
            ShowPage(BootstrapPage);

            var poll = new System.Windows.Forms.Timer { Interval = 2000 };
            poll.Tick += (s, e) => Poll();
            poll.Start();
            var logTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            logTimer.Tick += (s, e) => TailLog();
            logTimer.Start();
            Shown += (s, e) => OnFirstShown();
            FormClosing += OnClosing;
        }

        // ================================================================ layout

        Control BuildHeader()
        {
            var t = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(new LogoBox(), 0, 0);
            var brand = Theme.Label("LLARP", Theme.Lime, Theme.Brand);
            brand.Margin = new Padding(0, 0, 18, 0);
            t.Controls.Add(brand, 1, 0);
            t.Controls.Add(status, 2, 0);
            power.Toggled += (s, e) => { if (power.Checked) StopDaemon(); else StartDaemon(); };
            tips.SetToolTip(power, "Turn llarpd on / off");
            t.Controls.Add(power, 3, 0);
            restartBtn.Anchor = AnchorStyles.Right;
            restartBtn.Click += (s, e) => Restart();
            tips.SetToolTip(restartBtn, "Restart llarpd to apply bootstrap/setting changes");
            t.Controls.Add(restartBtn, 4, 0);
            return t;
        }

        Control BuildAddressRow()
        {
            var t = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 2) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(Theme.Label("Address:"), 0, 0);
            address.ReadOnly = true;
            address.BorderStyle = BorderStyle.None;
            address.BackColor = Theme.Bg;
            address.ForeColor = Theme.Text;
            address.Font = Theme.Mono;
            address.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            t.Controls.Add(address, 1, 0);
            copyBtn.Anchor = AnchorStyles.Left;
            copyBtn.Click += (s, e) => { if (!string.IsNullOrEmpty(AddressValue())) Clipboard.SetText(AddressValue()); };
            t.Controls.Add(copyBtn, 2, 0);
            var exitLabel = Theme.Label("Exit node");
            exitLabel.Margin = new Padding(16, 3, 0, 3);
            t.Controls.Add(exitLabel, 3, 0);
            exitSwitch.Toggled += (s, e) => SetExitNode(!exitSwitch.Checked);
            tips.SetToolTip(exitSwitch, "Let other LLARP users route their internet traffic through this PC");
            t.Controls.Add(exitSwitch, 4, 0);
            return t;
        }

        Control BuildVpnRow()
        {
            var t = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(Theme.Label("VPN via exit:"), 0, 0);
            vpnCombo.DropDownStyle = ComboBoxStyle.DropDown;
            vpnCombo.FlatStyle = FlatStyle.Flat;
            vpnCombo.BackColor = Theme.Well;
            vpnCombo.ForeColor = Theme.Text;
            vpnCombo.Font = Theme.Mono;
            vpnCombo.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            vpnCombo.Margin = new Padding(0, 3, 6, 3);
            vpnCombo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && !vpnSwitch.Checked) { e.SuppressKeyPress = true; ToggleVpn(true); } };
            tips.SetToolTip(vpnCombo, "An exit node's address, e.g. abc…xyz.loki or name.loki");
            t.Controls.Add(vpnCombo, 1, 0);
            vpnAuth.Click += (s, e) => EditExitAuth();
            tips.SetToolTip(vpnAuth, "Auth code for a private exit");
            t.Controls.Add(vpnAuth, 2, 0);
            vpnForget.Click += (s, e) => ForgetExit();
            tips.SetToolTip(vpnForget, "Remove this exit from the saved list");
            t.Controls.Add(vpnForget, 3, 0);
            vpnSwitch.Toggled += (s, e) => ToggleVpn(!vpnSwitch.Checked);
            tips.SetToolTip(vpnSwitch, "Send all of Windows' internet traffic through this exit node");
            t.Controls.Add(vpnSwitch, 4, 0);
            return t;
        }

        Control BuildBanner()
        {
            banner.BackColor = Theme.Banner;
            banner.BorderColor = Theme.Green;
            banner.AutoSize = true;
            banner.Dock = DockStyle.Fill;
            banner.Margin = new Padding(0, 6, 0, 6);
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Dock = DockStyle.Fill, BackColor = Theme.Banner };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bannerText.BackColor = Theme.Banner;
            t.Controls.Add(bannerText, 0, 0);
            var open = Theme.Button("Open Setup", true);
            open.Anchor = AnchorStyles.Right;
            open.Click += (s, e) => ShowPage(SetupPage);
            t.Controls.Add(open, 1, 0);
            banner.Controls.Add(t);
            banner.Visible = false;
            return banner;
        }

        Control BuildExitPanel()
        {
            exitPanel.BackColor = Color.FromArgb(0x18, 0x26, 0x1b);
            exitPanel.BorderColor = Color.FromArgb(0x4e, 0x7f, 0x37);
            exitPanel.AutoSize = true;
            exitPanel.Dock = DockStyle.Fill;
            exitPanel.Margin = new Padding(0, 6, 0, 6);
            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, BackColor = exitPanel.BackColor };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            exitInfo.Font = Theme.Mono;
            t.Controls.Add(exitInfo, 0, 0);
            t.SetColumnSpan(exitInfo, 3);
            t.Controls.Add(fwdStatus, 0, 1);
            fwdEnable.Click += (s, e) => Ctl(true, "fwd", "enable");
            tips.SetToolTip(fwdEnable, "Turn on IP forwarding and NAT the tunnel range out to the internet (inside WSL)");
            t.Controls.Add(fwdEnable, 1, 1);
            fwdRemove.Click += (s, e) => Ctl(true, "fwd", "disable");
            tips.SetToolTip(fwdRemove, "Delete the NAT/forwarding rules LLARP Control added");
            t.Controls.Add(fwdRemove, 2, 1);
            exitPanel.Controls.Add(t);
            exitPanel.Visible = false;
            return exitPanel;
        }

        Control BuildTabs()
        {
            var flow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0), WrapContents = false };
            flow.Name = "tabs";
            return flow;
        }

        void AddPage(string title, Control page)
        {
            var tab = new TabButton(title);
            int index = tabs.Count;
            tab.Click += (s, e) => ShowPage(index);
            tabs.Add(tab);
            Controls.Find("tabs", true)[0].Controls.Add(tab);
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            pages.Add(page);
            content.Controls.Add(page);
        }

#if PREVIEW
        public void PreviewPage(int index) { ShowPage(index); }
#endif

        void ShowPage(int index)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                tabs[i].Selected = i == index;
                pages[i].Visible = i == index;
            }
            if (index == SetupPage) RefreshSetupState();
        }

        Control BuildSetupPage()
        {
            var t = Theme.Table(1);
            t.Dock = DockStyle.Fill;
            t.AutoSize = false;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var row in new[] { Tuple.Create("WSL 2", wslState), Tuple.Create("LLARP distro", distroState), Tuple.Create("llarpd", llarpdState) })
            {
                grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var name = Theme.Label(row.Item1, Theme.Dim);
                name.Margin = new Padding(0, 3, 16, 3);
                grid.Controls.Add(name, 0, grid.RowCount - 1);
                grid.Controls.Add(row.Item2, 1, grid.RowCount - 1);
            }
            Theme.AddRow(t, grid);

            var buttons = Theme.Flow();
            setupBtn.Click += (s, e) => RunSetup(false);
            tips.SetToolTip(setupBtn, "Install WSL, create the LLARP distro, build llarpd and start it. Safe to run again: finished steps are skipped.");
            rebuildBtn.Click += (s, e) => RunSetup(true);
            tips.SetToolTip(rebuildBtn, "Build llarpd again from source (e.g. after a failed build)");
            cancelBtn.Click += (s, e) => { if (setupCancel != null) setupCancel.Cancel(); };
            cancelBtn.Enabled = false;
            shellBtn.Click += (s, e) => OpenShell();
            tips.SetToolTip(shellBtn, "A root terminal in the LLARP distro (llarpctl works there too)");
            uninstallBtn.Click += (s, e) => UninstallFromGui();
            foreach (var b in new[] { setupBtn, rebuildBtn, cancelBtn, shellBtn, uninstallBtn }) buttons.Controls.Add(b);
            Theme.AddRow(t, buttons);
            setupOut.Text = "Setup output appears here." + Environment.NewLine;
            Theme.AddRow(t, setupOut, true);
            return t;
        }

        Control BuildBootstrapPage()
        {
            var t = Theme.Table(1);
            t.Dock = DockStyle.Fill;
            t.AutoSize = false;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.AddRow(t, Theme.Wrap("Bootstrap files are signed router contacts (.signed) used to join the network. "
                + "Checked entries are passed to llarpd at start. With none checked, llarpd uses its config and built-in fallbacks."));
            var buttons = Theme.Flow();
            var addFile = Theme.Button("Add file…");
            addFile.Click += (s, e) => AddBootstrapFile();
            var addUrl = Theme.Button("Add from URL…");
            addUrl.Click += (s, e) => AddBootstrapUrl();
            bsBundled.Click += (s, e) => AddBootstrapBundled();
            var remove = Theme.Button("Remove");
            remove.Click += (s, e) => RemoveBootstrap();
            foreach (var b in new[] { addFile, addUrl, bsBundled, remove }) buttons.Controls.Add(b);
            Theme.AddRow(t, buttons);

            bsList.View = View.Details;
            bsList.CheckBoxes = true;
            bsList.FullRowSelect = true;
            bsList.MultiSelect = false;
            bsList.HideSelection = false;
            bsList.ShowItemToolTips = true;
            bsList.BackColor = Theme.Surface;
            bsList.ForeColor = Theme.Text;
            bsList.BorderStyle = BorderStyle.FixedSingle;
            bsList.Dock = DockStyle.Fill;
            bsList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            bsList.Columns.Add("Name", 200);
            bsList.Columns.Add("Routers", 500);
            bsList.OwnerDraw = true;
            bsList.DrawColumnHeader += (s, e) =>
            {
                using (var br = new SolidBrush(Theme.Raised)) e.Graphics.FillRectangle(br, e.Bounds);
                var r = e.Bounds;
                r.Inflate(-6, 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Bold, r, Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            };
            bsList.DrawItem += (s, e) => e.DrawDefault = true;
            bsList.DrawSubItem += (s, e) => e.DrawDefault = true;
            bsList.Resize += (s, e) => bsList.Columns[1].Width = -2; // fill the rest (no unpainted header strip)
            bsList.ItemChecked += OnBootstrapChecked;
            Theme.AddRow(t, bsList, true);
            return t;
        }

        Control BuildLogPage()
        {
            var t = Theme.Table(1);
            t.Dock = DockStyle.Fill;
            t.AutoSize = false;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.AddRow(t, logView, true);
            var row = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            follow.Checked = true;
            follow.CheckedChanged += (s, e) =>
            {
                if (follow.Checked && heldLog.Length > 0) { AppendLog(heldLog.ToString()); heldLog.Clear(); }
            };
            tips.SetToolTip(follow, "Keep showing new lines as they arrive (untick to read without the view moving)");
            row.Controls.Add(follow, 0, 0);
            var path = Theme.Label("/root/.config/llarp-gui/llarpd.log  (in the LLARP distro)", Theme.Dim);
            row.Controls.Add(path, 1, 0);
            var open = Theme.Button("Open in Notepad");
            open.Click += (s, e) => { try { Process.Start("notepad.exe", Proc.Quote(Paths.WindowsLog)); } catch (Exception ex) { Error(ex.Message); } };
            row.Controls.Add(open, 2, 0);
            var clear = Theme.Button("Clear view");
            clear.Click += (s, e) => logView.Clear();
            row.Controls.Add(clear, 3, 0);
            Theme.AddRow(t, row);
            return t;
        }

        Control BuildSettingsPage()
        {
            var t = new TableLayoutPanel { ColumnCount = 3, Dock = DockStyle.Fill, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Action<string, Control, Control> row = (label, main, extra) =>
            {
                t.RowCount++;
                t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                int r = t.RowCount - 1;
                if (label != null)
                {
                    var l = Theme.Label(label, Theme.Dim);
                    l.Anchor = AnchorStyles.Right;
                    l.Margin = new Padding(0, 6, 12, 6);
                    t.Controls.Add(l, 0, r);
                }
                t.Controls.Add(main, 1, r);
                if (extra != null) t.Controls.Add(extra, 2, r);
                else t.SetColumnSpan(main, 2);
            };

            level.DropDownStyle = ComboBoxStyle.DropDownList;
            level.FlatStyle = FlatStyle.Flat;
            level.BackColor = Theme.Well;
            level.ForeColor = Theme.Text;
            level.Items.AddRange(LogLevels);
            level.Width = 140;
            level.Anchor = AnchorStyles.Left;
            level.SelectedIndexChanged += (s, e) => { if (!syncing && level.SelectedItem != null) Ctl(false, "set", "log_level", (string)level.SelectedItem); };
            row("Log level", level, null);

            router.CheckedChanged += (s, e) => { if (!syncing) Ctl(false, "set", "router_mode", router.Checked ? "on" : "off"); };
            tips.SetToolTip(router, "Runs llarpd --router. Needs a public IP: on a home network, forward the UDP port below on your router to this PC.");
            row(null, router, null);

            var pub = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            pub.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            pub.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pub.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            publicIp.Margin = new Padding(0, 4, 8, 4);
            publicIp.Leave += (s, e) => SavePublicIp();
            publicIp.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SavePublicIp(); e.SuppressKeyPress = true; } };
            tips.SetToolTip(publicIp, "Only for router mode behind a home router (NAT): your router's public IP. Leave empty otherwise.");
            pub.Controls.Add(publicIp, 0, 0);
            pub.Controls.Add(Theme.Label("UDP port"), 1, 0);
            publicPort.Minimum = 1;
            publicPort.Maximum = 65535;
            publicPort.Value = 1090;
            publicPort.Width = 80;
            publicPort.BackColor = Theme.Well;
            publicPort.ForeColor = Theme.Text;
            publicPort.BorderStyle = BorderStyle.FixedSingle;
            publicPort.Leave += (s, e) => SavePort();
            publicPort.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SavePort(); e.SuppressKeyPress = true; } };
            pub.Controls.Add(publicPort, 2, 0);
            var detect = Theme.Button("Detect");
            detect.Anchor = AnchorStyles.Left;
            detect.Click += (s, e) => Ctl(false, "set", "public_ip", "auto");
            tips.SetToolTip(detect, "Look up this network's public IP (asks api.ipify.org)");
            row("Public IP", pub, detect);
            row(null, Theme.Wrap("Router mode on Windows: forward the UDP port on your router to this PC. LLARP Control "
                + "relays it into WSL and opens it in Windows Firewall while llarpd runs.", Theme.Dim), null);

            persist.CheckedChanged += (s, e) => { if (!syncing) Ctl(false, "persist", persist.Checked ? "on" : "off"); };
            tips.SetToolTip(persist, "Stores a key file in llarpd's data dir. Required for an exit node.");
            row(null, persist, null);

            autostart.CheckedChanged += (s, e) =>
            {
                if (syncing) return;
                bool on = autostart.Checked;
                Task.Run(() => Autostart.Set(on)).ContinueWith(task =>
                {
                    if (!task.Result.Ok) Error("Couldn't change autostart: " + task.Result.Text);
                }, TaskScheduler.FromCurrentSynchronizationContext());
            };
            tips.SetToolTip(autostart, "A Task Scheduler task starts llarpd (and the Windows side) when you sign in");
            row(null, autostart, null);

            var gen = Theme.Button("Generate default config");
            gen.Anchor = AnchorStyles.Left;
            gen.Click += (s, e) => Ctl(true, "generate-config");
            tips.SetToolTip(gen, "Run llarpd --generate for the config path (won't overwrite an existing file)");
            row(null, gen, null);
            row("Inside WSL", paths, null);
            row(null, Theme.Wrap("Changes take effect the next time llarpd starts. Everything here can also be done from a "
                + "Command Prompt with llarpctl (for example: llarpctl status).", Theme.Dim), null);
            return t;
        }

        // ================================================================ polling & display

        void OnFirstShown()
        {
            // a host that was killed may have left routes behind (a VPN route would cut Windows off)
            Task.Run(() => Host.CleanupStale());
            autostart.Checked = false;
            Task.Run(() => Autostart.Enabled()).ContinueWith(task => { syncing = true; autostart.Checked = task.Result; syncing = false; },
                TaskScheduler.FromCurrentSynchronizationContext());
            if (runSetupOnLoad)
            {
                RunSetup(false);
                return;
            }
            Poll();
            // after an app update: refresh llarpctl/llarp_core.py in the distro if they changed
            Task.Run(() => Wsl.DistroExists() && SetupRunner.FilesOutdated()).ContinueWith(task =>
            {
                if (task.Result) UpdateDistroFiles();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        async void Poll()
        {
            if (polling || setupRunning) return;
            polling = true;
            try
            {
                bool exists = await Task.Run(() => Wsl.DistroExists());
                Dictionary<string, object> d = exists ? await Task.Run(() => Wsl.Dump()) : null;
                var host = Host.ReadState();
                bool hostRunning = Host.IsRunning();
                distroExists = exists;
                Display(d, host, hostRunning);
            }
            catch (Exception ex)
            {
                status.Text = "Error: " + ex.Message;
            }
            finally
            {
                polling = false;
            }
        }

        static string Uptime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return (t.Days > 0 ? t.Days + "d " : "") + string.Format("{0}:{1:00}:{2:00}", t.Hours, t.Minutes, t.Seconds);
        }

        void SetStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }

        void Display(Dictionary<string, object> d, Dictionary<string, object> host, bool hostRunning)
        {
            dump = d;
            ready = d != null;
            if (firstDisplay && !ready && !setupRunning) ShowPage(SetupPage); // nothing to control yet
            firstDisplay = false;
            banner.Visible = !ready && !setupRunning;
            if (!ready)
            {
                bannerText.Text = distroExists
                    ? "Setup isn't finished: llarpd isn't installed in the LLARP distro yet."
                    : "LLARP isn't set up on this PC yet. Setup installs WSL 2, a Debian distro and llarpd.";
                SetStatus(setupRunning ? "Setting up…" : "Not set up", setupRunning ? Theme.Busy : Theme.Dim);
                foreach (Control c in new Control[] { power, restartBtn, exitSwitch, copyBtn, vpnSwitch, vpnCombo, vpnAuth, vpnForget }) c.Enabled = false;
                address.Text = "";
                windowsInfo.Visible = false;
                vpnStatus.Visible = false;
                exitPanel.Visible = false;
                return;
            }
            power.Enabled = exitSwitch.Enabled = vpnSwitch.Enabled = vpnAuth.Enabled = vpnForget.Enabled = true;

            bool running = J.Bool(d, "running"), stopping = J.Bool(d, "stopping");
            bool exitNode = J.Bool(d, "exit_node");

            // llarpd stopped by itself (or never came up)
            if (startRequested != null && !running)
            {
                double waited = (DateTime.Now - startRequested.Value).TotalSeconds;
                if (waited > 4 && !hostRunning)
                {
                    startRequested = null;
                    string why = Host.LastError() ?? J.Str(d, "startup_problem") ?? J.Str(d, "exit_reason");
                    Error("llarpd couldn't start" + (why != null ? ":\n\n" + why : ". See the Log tab."));
                }
                else if (waited > 90) startRequested = null;
            }
            if (running) startRequested = null;
            if (wasRunning && !running && !userStop && !restartPending)
            {
                ShowPage(LogPage);
                string why = J.Str(d, "exit_reason");
                Error(why != null ? "llarpd couldn't start: " + why : "llarpd stopped unexpectedly. See the Log tab for details.");
            }
            if (!running && !restartPending) userStop = false;
            wasRunning = running;

            if (restartPending) SetStatus("Restarting…", Theme.Busy);
            else if (running && stopping) SetStatus("Stopping…", Theme.Busy);
            else if (running)
            {
                double started = J.Num(d, "started_at") ?? 0;
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                string text = "Running · pid " + J.Str(d, "pid") + " · up " + Uptime(now - started);
                if (J.Bool(d, "router_mode")) text += " · snode";
                if (exitNode) text += " · exit node";
                if (J.Bool(d, "changes_pending")) text += " · restart to apply changes";
                var health = J.Obj(d, "health");
                if (J.Bool(health, "stuck"))
                {
                    // the watchdog in llarpctl start --foreground restarts it within a minute
                    text += " · stuck: no paths for " + (int)((J.Num(health, "for") ?? 0) / 60) + " min, restarting";
                    SetStatus(text, Theme.Err);
                }
                else SetStatus(text, Theme.Bright);
            }
            else if (startRequested != null) SetStatus("Starting…", Theme.Busy);
            else SetStatus("Stopped", Theme.Dim);
            power.Checked = (running && !stopping) || startRequested != null || restartPending;
            restartBtn.Enabled = running && !stopping && !restartPending;

            string addr = J.Str(d, "address"), kind = J.Str(d, "address_kind") ?? "";
            address.Text = addr ?? kind;
            address.ForeColor = addr != null ? Theme.Text : Theme.Dim;
            copyBtn.Enabled = addr != null;
            tips.SetToolTip(address, addr == null ? null : kind == "permanent" ? "Permanent address (from key file)"
                : "Temporary address: it changes every time llarpd starts. Turn on “Keep the same address” in Settings.");

            ShowWindowsInfo(d, running, host, hostRunning);
            ShowVpn(d, running, host, hostRunning);
            exitSwitch.Checked = exitNode;
            ShowExitPanel(d, running, exitNode, addr);
            ShowSettings(d);
            ShowBootstraps(d);
        }

        string AddressValue()
        {
            return dump == null ? null : J.Str(dump, "address");
        }

        void ShowWindowsInfo(Dictionary<string, object> d, bool running, Dictionary<string, object> host, bool hostRunning)
        {
            windowsInfo.Visible = true;
            bool snode = J.Bool(d, "router_mode");
            if (!running)
            {
                windowsInfo.ForeColor = Theme.Dim;
                windowsInfo.Text = snode
                    ? "Windows: when running, UDP port " + J.Str(J.Obj(d, "settings"), "public_port") + " on this PC is relayed to llarpd in WSL."
                    : "Windows: when running, .loki and .snode addresses work in Windows apps (DNS and routes go through the LLARP distro).";
                return;
            }
            if (!hostRunning)
            {
                windowsInfo.ForeColor = Theme.Busy;
                windowsInfo.Text = "llarpd was started outside LLARP Control, so Windows isn't connected to it. Restart it from here.";
                return;
            }
            string error = J.Str(host, "error");
            if (J.Str(host, "mode") == "router")
            {
                windowsInfo.ForeColor = error == null ? Theme.Bright : Theme.Err;
                windowsInfo.Text = "Windows: UDP " + J.Str(host, "relay_port") + " relayed to WSL " + J.Str(host, "wsl_ip")
                    + " · " + (J.Str(host, "relay_peers") ?? "0") + " peer(s)" + (error != null ? " · " + error : "");
            }
            else if (J.Str(host, "route_net") != null)
            {
                bool dnsOk = J.Bool(host, "nrpt");
                windowsInfo.ForeColor = error == null ? Theme.Bright : Theme.Err;
                windowsInfo.Text = "Windows: " + J.Str(host, "range") + " routed into WSL"
                    + (dnsOk ? " · .loki/.snode names resolve through " + J.Str(host, "dns") : " · DNS not set up")
                    + (error != null ? " · " + error : "");
            }
            else
            {
                windowsInfo.ForeColor = error == null ? Theme.Busy : Theme.Err;
                windowsInfo.Text = error ?? "Windows: waiting for llarpd's tunnel…";
            }
        }

        void ShowVpn(Dictionary<string, object> d, bool running, Dictionary<string, object> host, bool hostRunning)
        {
            var exits = J.Arr(d, "exits");
            string current = J.Str(d, "use_exit");
            string sig = J.Write(exits);
            if (sig != shownExits)
            {
                shownExits = sig;
                string typed = vpnCombo.Text;
                vpnCombo.Items.Clear();
                foreach (var o in exits)
                {
                    var e = o as Dictionary<string, object>;
                    vpnCombo.Items.Add(J.Str(e, "address"));
                }
                vpnCombo.Text = current ?? typed;
            }
            else if (current != null && !vpnCombo.Focused && vpnCombo.Text != current)
            {
                vpnCombo.Text = current;
            }

            var v = J.Obj(d, "vpn");
            string state = J.Str(v, "state") ?? "off";
            bool on = state != "off";
            vpnSwitch.Checked = on;
            vpnCombo.Enabled = !on;
            vpnStatus.Visible = true;
            if (!on)
            {
                vpnStatus.ForeColor = Theme.Dim;
                vpnStatus.Text = J.Bool(d, "router_mode") || J.Bool(d, "exit_node")
                    ? "VPN: not available in router mode or while this PC is an exit node."
                    : "VPN off. Pick or type an exit's .loki address and turn the switch on to send all of Windows' traffic through it.";
                return;
            }
            Color color = state == "on" ? Theme.Bright : state == "trouble" ? Theme.Err : Theme.Busy;
            string text = (state == "on" ? "● Connected: " : state == "trouble" ? "● Problem: " : "● ") + J.Str(v, "detail");
            if (running && state != "pending")
            {
                string err = J.Str(host, "vpn_error");
                if (!hostRunning)
                    text += " · Windows isn't routed through it (llarpd was started outside LLARP Control)";
                else if (J.Bool(host, "vpn_on"))
                    text += " · all Windows traffic goes through it";
                else if (err != null)
                {
                    text += " · Windows: " + err;
                    color = Theme.Err;
                }
                else
                    text += " · Windows keeps its normal connection until traffic gets through the exit";
            }
            vpnStatus.ForeColor = color;
            vpnStatus.Text = text;
        }

        void ShowExitPanel(Dictionary<string, object> d, bool running, bool exitNode, string addr)
        {
            exitPanel.Visible = exitNode;
            if (!exitNode) return;
            if (addr != null && running)
                exitInfo.Text = "Other users can route through you by adding this to their config:\n[network]\nexit-node=" + addr;
            else if (addr != null)
                exitInfo.Text = "Exit address: " + addr + " (start llarpd to serve)";
            else
                exitInfo.Text = "Exit address is created when llarpd starts.";
            var nat = J.Obj(d, "nat");
            var target = J.Arr(nat, "target");
            string natText;
            if (target.Count < 2) natText = "unknown (" + J.Str(nat, "why") + ")";
            else if (J.Bool(nat, "applied")) natText = "set up for " + target[0] + " via " + target[1];
            else natText = "not set up for " + target[0] + " via " + target[1];
            fwdStatus.Text = "IP forwarding (in WSL): " + (J.Bool(d, "ip_forward") ? "on" : "off") + " · NAT: " + natText;
            fwdEnable.Enabled = fwdRemove.Enabled = target.Count >= 2;
        }

        bool Changed(string key, object value)
        {
            object old;
            if (shownSettings.TryGetValue(key, out old) && Equals(old, value)) return false;
            shownSettings[key] = value;
            return true;
        }

        void ShowSettings(Dictionary<string, object> d)
        {
            var s = J.Obj(d, "settings");
            syncing = true;
            try
            {
                string lv = J.Str(s, "log_level");
                if (Changed("log_level", lv) && lv != null) level.SelectedItem = lv;
                bool rm = J.Bool(s, "router_mode");
                if (Changed("router_mode", rm)) router.Checked = rm;
                string ip = J.Str(s, "public_ip") ?? "";
                if (Changed("public_ip", ip) && !publicIp.Focused) publicIp.Text = ip;
                int port = (int)(J.Num(s, "public_port") ?? 1090);
                if (Changed("public_port", port) && !publicPort.Focused) publicPort.Value = Math.Max(1, Math.Min(65535, port));
                bool p = J.Bool(d, "persist");
                if (Changed("persist", p)) persist.Checked = p;
                paths.Text = "llarpd: " + J.Str(s, "llarpd") + "    config: " + J.Str(s, "config") + "    data: " + J.Str(d, "data_dir");
            }
            finally
            {
                syncing = false;
            }
        }

        void ShowBootstraps(Dictionary<string, object> d)
        {
            var list = J.Arr(d, "bootstraps");
            string sig = J.Write(list);
            bsBundled.Enabled = J.Arr(d, "bundled").Count > 0;
            if (sig == shownBootstraps) return;
            shownBootstraps = sig;
            syncing = true;
            bsList.BeginUpdate();
            try
            {
                bsList.Items.Clear();
                foreach (var o in list)
                {
                    var b = o as Dictionary<string, object>;
                    var item = new ListViewItem(J.Str(b, "name"));
                    item.SubItems.Add(J.Str(b, "summary"));
                    item.Checked = J.Bool(b, "enabled");
                    item.ForeColor = J.Bool(b, "ok") ? Theme.Text : Theme.Err;
                    item.ToolTipText = string.Join("\n", J.Arr(b, "details").Select(x => x.ToString()));
                    bsList.Items.Add(item);
                }
            }
            finally
            {
                bsList.EndUpdate();
                syncing = false;
            }
        }

        // ================================================================ actions

        /// Run llarpctl in the background, show its error (or output), then refresh.
        async void Ctl(bool showOutput, params string[] args)
        {
            if (!ready) { Error("LLARP isn't set up yet: run Setup first."); return; }
            UseWaitCursor = true;
            var r = await Task.Run(() => Wsl.Ctl(args));
            UseWaitCursor = false;
            if (!r.Ok) Error(r.Text.Length > 0 ? r.Text.Replace("error: ", "") : "llarpctl " + string.Join(" ", args) + " failed");
            else if (showOutput && r.Text.Length > 0) Info(r.Text);
            if (args[0] == "set" || args[0] == "persist") shownSettings.Clear(); // show what was actually stored
            Poll();
        }

        void StartDaemon()
        {
            if (!ready) { ShowPage(SetupPage); return; }
            if (J.Bool(dump, "running")) return;
            userStop = false;
            startRequested = DateTime.Now;
            try { Host.Launch(); }
            catch (Exception ex) { startRequested = null; Error("Couldn't start: " + ex.Message); }
            SetStatus("Starting…", Theme.Busy);
            power.Checked = true;
        }

        async void StopDaemon()
        {
            if (startRequested != null && !J.Bool(dump, "running"))
            {
                startRequested = null; // not up yet: stop it once it is
            }
            userStop = true;
            SetStatus("Stopping…", Theme.Busy);
            power.Checked = false;
            var r = await Task.Run(() => Wsl.Ctl("stop"));
            if (!r.Ok) Error(r.Text);
            Poll();
        }

        async void Restart()
        {
            if (dump == null) return;
            if (!J.Bool(dump, "running")) { StartDaemon(); return; }
            restartPending = true;
            userStop = true;
            SetStatus("Restarting…", Theme.Busy);
            await Task.Run(() =>
            {
                Wsl.Ctl("stop");
                for (int i = 0; i < 40 && Host.IsRunning(); i++) Thread.Sleep(500); // Windows side cleans up
            });
            restartPending = false;
            wasRunning = false;
            StartDaemon();
        }

        static string CleanError(ProcResult r, string fallback)
        {
            return r.Text.Length > 0 ? r.Text.Replace("error: ", "") : fallback;
        }

        string TypedExit()
        {
            string addr = vpnCombo.Text.Trim();
            if (addr.Length == 0) Error("Type or pick an exit's address first (something.loki).");
            return addr.Length == 0 ? null : addr;
        }

        async void ToggleVpn(bool on)
        {
            if (!ready) return;
            string addr = null;
            if (on && (addr = TypedExit()) == null) return;
            UseWaitCursor = true;
            var r = await Task.Run(() => on ? Wsl.Ctl("vpn", addr) : Wsl.Ctl("vpn", "off"));
            UseWaitCursor = false;
            if (!r.Ok)
            {
                Error(CleanError(r, "Couldn't change the VPN."));
                return;
            }
            shownExits = null;
            vpnSwitch.Checked = on;
            if (J.Bool(dump, "running")) Restart(); // llarpd reads the exit at start
            else if (on) StartDaemon();
            else Poll();
        }

        async void EditExitAuth()
        {
            string addr = TypedExit();
            if (addr == null) return;
            string code = InputDialog.Ask(this, "Exit auth code",
                "Auth code for " + addr + "\nOnly needed for private exits. Leave empty for public ones.", "", true);
            if (code == null) return;
            var r = await Task.Run(() => Wsl.Ctl("vpn", "save", addr, "--auth", code));
            if (!r.Ok)
            {
                Error(CleanError(r, "Couldn't save the auth code."));
                return;
            }
            shownExits = null;
            string current = J.Str(dump, "use_exit");
            if (current != null && J.Bool(dump, "running") && r.Out.Contains(current)) Restart(); // apply the new code
            else Poll();
        }

        void ForgetExit()
        {
            string addr = TypedExit();
            if (addr == null) return;
            shownExits = null;
            vpnCombo.Text = "";
            Ctl(false, "vpn", "forget", addr);
        }

        async void SetExitNode(bool on)
        {
            if (!ready) return;
            var r = await Task.Run(() => Wsl.Ctl("set", "exit_node", on ? "on" : "off"));
            if (!r.Ok)
            {
                Error(r.Text.Replace("error: ", ""));
                return;
            }
            exitSwitch.Checked = on;
            if (J.Bool(dump, "running")) Restart();
            else Poll();
        }

        void SavePublicIp()
        {
            if (syncing) return;
            string ip = publicIp.Text.Trim();
            object shown;
            if (shownSettings.TryGetValue("public_ip", out shown) && Equals(shown, ip)) return;
            Ctl(false, "set", "public_ip", ip.Length == 0 ? "none" : ip);
        }

        void SavePort()
        {
            if (syncing) return;
            int port = (int)publicPort.Value;
            object shown;
            if (shownSettings.TryGetValue("public_port", out shown) && Equals(shown, port)) return;
            Ctl(false, "set", "public_port", port.ToString());
        }

        void OnBootstrapChecked(object sender, ItemCheckedEventArgs e)
        {
            if (syncing) return;
            shownBootstraps = null;
            Ctl(false, "bootstrap", e.Item.Checked ? "enable" : "disable", (e.Item.Index + 1).ToString());
        }

        void AddBootstrapFile()
        {
            using (var dlg = new OpenFileDialog { Title = "Add bootstrap file", Filter = "Signed RCs (*.signed)|*.signed|All files|*.*", Multiselect = true })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var args = new List<string> { "bootstrap", "add" };
                args.AddRange(dlg.FileNames); // llarpctl in WSL understands C:\ paths
                Ctl(true, args.ToArray());
            }
        }

        void AddBootstrapUrl()
        {
            string url = InputDialog.Ask(this, "Add bootstrap from URL", "URL of a .signed bootstrap file:", "https://");
            if (string.IsNullOrEmpty(url) || url == "https://") return;
            Ctl(true, "bootstrap", "add", url);
        }

        void AddBootstrapBundled()
        {
            var menu = new ContextMenuStrip { BackColor = Theme.Raised, ForeColor = Theme.Text, ShowImageMargin = false };
            foreach (var o in J.Arr(dump, "bundled"))
            {
                string name = o.ToString();
                menu.Items.Add(name, null, (s, e) => Ctl(true, "bootstrap", "add", Path.GetFileNameWithoutExtension(name)));
            }
            menu.Show(bsBundled, new Point(0, bsBundled.Height));
        }

        void RemoveBootstrap()
        {
            if (bsList.SelectedItems.Count == 0) { Error("Select a bootstrap in the list first."); return; }
            shownBootstraps = null;
            Ctl(false, "bootstrap", "remove", (bsList.SelectedItems[0].Index + 1).ToString());
        }

        // ================================================================ log

        void TailLog()
        {
            if (!ready || logBusy) return;
            logBusy = true;
            long pos = logPos;
            Task.Run(() =>
            {
                using (var fs = new FileStream(Paths.WindowsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long len = fs.Length;
                    bool reset = false;
                    if (pos < 0) pos = Math.Max(0, len - InitialLogBytes);
                    else if (len < pos) { pos = 0; reset = true; } // llarpd restarted: new log file
                    if (len == pos) return Tuple.Create(pos, "", reset);
                    fs.Seek(pos, SeekOrigin.Begin);
                    var buf = new byte[len - pos];
                    int n = 0, k;
                    while (n < buf.Length && (k = fs.Read(buf, n, buf.Length - n)) > 0) n += k;
                    int cut = Array.LastIndexOf(buf, (byte)'\n', n - 1) + 1;
                    return Tuple.Create(pos + cut, Encoding.UTF8.GetString(buf, 0, cut), reset);
                }
            }).ContinueWith(task =>
            {
                logBusy = false;
                if (task.IsFaulted) return; // distro not running yet, no log yet
                logPos = task.Result.Item1;
                if (task.Result.Item3) { logView.Clear(); heldLog.Clear(); }
                if (task.Result.Item2.Length > 0) AppendLog(task.Result.Item2.Replace("\n", Environment.NewLine));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void AppendLog(string text)
        {
            if (!follow.Checked)
            {
                heldLog.Append(text);
                return;
            }
            if (logView.TextLength + text.Length > MaxLogChars)
            {
                string all = logView.Text + text;
                logView.Text = all.Substring(all.Length - MaxLogChars / 2);
                logView.SelectionStart = logView.TextLength;
                logView.ScrollToCaret();
                return;
            }
            logView.AppendText(text);
        }

        // ================================================================ setup

        void SetupLine(string line)
        {
            if (IsDisposed) return;
            BeginInvoke(new Action(() =>
            {
                if (setupOut.TextLength > MaxLogChars) setupOut.Text = setupOut.Text.Substring(setupOut.TextLength - MaxLogChars / 2);
                setupOut.AppendText(line + Environment.NewLine);
            }));
        }

        async void RefreshSetupState()
        {
            var info = await Task.Run(() =>
            {
                bool wsl = Wsl.Installed();
                bool distro = Wsl.DistroExists();
                string version = null;
                if (distro)
                {
                    var r = Wsl.Exec(60000, "/usr/local/bin/llarpd", "--version");
                    if (r.Ok) version = r.Out.Trim();
                }
                return Tuple.Create(wsl, distro, version);
            });
            wslState.Text = info.Item1 ? "installed" : "not installed";
            wslState.ForeColor = info.Item1 ? Theme.Bright : Theme.Dim;
            distroState.Text = info.Item2 ? "created (" + Paths.DistroDir + ")" : "not created";
            distroState.ForeColor = info.Item2 ? Theme.Bright : Theme.Dim;
            llarpdState.Text = info.Item3 ?? "not built";
            llarpdState.ForeColor = info.Item3 != null ? Theme.Bright : Theme.Dim;
            setupBtn.Text = info.Item3 != null ? "Check / repair setup" : "Set up everything";
            rebuildBtn.Enabled = info.Item2 && !setupRunning;
            shellBtn.Enabled = info.Item2;
        }

        async void RunSetup(bool forceBuild)
        {
            if (setupRunning) return;
            setupRunning = true;
            ShowPage(SetupPage);
            banner.Visible = false;
            SetStatus("Setting up…", Theme.Busy);
            setupBtn.Enabled = rebuildBtn.Enabled = uninstallBtn.Enabled = false;
            cancelBtn.Enabled = true;
            setupCancel = new CancellationTokenSource();
            setupOut.Clear();
            var runner = new SetupRunner(SetupLine, setupCancel.Token);
            var result = await Task.Run(() => runner.Run(forceBuild));
            setupRunning = false;
            cancelBtn.Enabled = false;
            setupBtn.Enabled = uninstallBtn.Enabled = true;
            RefreshSetupState();
            if (result == SetupResult.NeedsReboot)
            {
                var answer = MessageBox.Show(this,
                    "Windows needs to restart to finish installing WSL.\n\nSetup carries on by itself after you sign in again. Restart now?",
                    Paths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(Proc.System32("shutdown.exe"), "/r /t 5 /c \"Finishing the WSL install for LLARP Control\"") { CreateNoWindow = true, UseShellExecute = false });
                return;
            }
            if (result == SetupResult.Failed)
            {
                Error("Setup didn't finish. The output on the Setup tab says why; fix that and press the setup button again. "
                    + "(Full log: " + Paths.SetupLog + ")");
                Poll();
                return;
            }
            // done: start the node
            SetupLine("");
            SetupLine("==> Starting llarpd");
            var d = await Task.Run(() => Wsl.Dump());
            dump = d;
            ready = d != null;
            if (ready && !J.Bool(d, "running")) StartDaemon();
            else if (ready) Restart(); // pick up a rebuilt llarpd
            Poll();
        }

        async void UpdateDistroFiles()
        {
            if (setupRunning) return;
            setupRunning = true;
            var runner = new SetupRunner(SetupLine, CancellationToken.None);
            bool ok = await Task.Run(() => runner.UpdateFiles());
            setupRunning = false;
            if (!ok) Error("Updating llarpctl in the LLARP distro failed; see the Setup tab.");
            Poll();
        }

        void OpenShell()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Wsl.Exe, "-d " + Wsl.Distro + " -u root --cd /root") { UseShellExecute = true });
            }
            catch (Exception ex) { Error(ex.Message); }
        }

        void UninstallFromGui()
        {
            var answer = MessageBox.Show(this,
                "Remove LLARP Control from this PC?\n\n" +
                "Yes: also delete the LLARP WSL distro (llarpd, its keys and your .loki address).\n" +
                "No: keep the LLARP distro, so reinstalling keeps your address.\n" +
                "Cancel: don't uninstall.",
                "Uninstall LLARP Control", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Cancel) return;
            UseWaitCursor = true;
            Installer.Uninstall(answer == DialogResult.Yes, SetupLine);
            MessageBox.Show(this, "LLARP Control was removed.", Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            FormClosing -= OnClosing;
            Close();
        }

        // ================================================================ closing & dialogs

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (setupRunning && e.CloseReason == CloseReason.UserClosing)
            {
                var a = MessageBox.Show(this, "Setup is still running. Stop it and close?", Paths.AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (a != DialogResult.Yes) { e.Cancel = true; return; }
                if (setupCancel != null) setupCancel.Cancel();
                return;
            }
            if (!ready || !J.Bool(dump, "running") || e.CloseReason != CloseReason.UserClosing) return;
            var answer = MessageBox.Show(this,
                "llarpd is still running.\n\n" +
                "Yes: leave it running in the background (reopen LLARP Control or use llarpctl to control it).\n" +
                "No: stop it and quit.\n" +
                "Cancel: go back.",
                Paths.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) e.Cancel = true;
            else if (answer == DialogResult.No)
            {
                UseWaitCursor = true;
                Wsl.Ctl("stop");
            }
        }

        void Error(string text)
        {
            MessageBox.Show(this, text, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        void Info(string text)
        {
            MessageBox.Show(this, text, Paths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
