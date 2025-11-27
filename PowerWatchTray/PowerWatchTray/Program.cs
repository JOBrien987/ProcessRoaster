

using PowerWatchTray;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PowerWatchTray
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            // Boot the app. Because apparently Windows can't be trusted unsupervised.
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    // Object to track misbehaving processes over time like a jaded probation officer.
    public class ProcState
    {
        public TimeSpan LastCpu;
        public DateTime LastCheck;
        public double OveruseSeconds;
        public double LastCpuPercent;
        public long WorkingSetBytes;
        public string Name = "";
        public int Pid;

        // Extra intel so we don't kill things blind.
        public string Path = "";
        public string Description = "";
        public string Company = "";
        public bool IsVendorTrash;
    }

    public class MainForm : Form
    {
        // ========== CONFIG ==========

        // Threshold where we declare a process guilty of crimes against battery life.
        private const double CpuThresholdPercent = 20.0;  // If you hit this, you're on the watchlist.
        private const double DurationSeconds = 10.0;      // Stay hot this long → public shaming.
        private const int PollIntervalMs = 2000;          // Scan rate: fast enough to catch offenders, slow enough not to be one.

        private readonly int _coreCount = Environment.ProcessorCount;  // Because Windows will happily pretend 2 cores is enough.
        private readonly string _logPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "power_watch_log.csv");

        private readonly Dictionary<int, ProcState> _state = new();

        // Tray + timers
        private readonly NotifyIcon _tray = new();
        private Icon? _iconAngel;
        private Icon? _iconDevil;
        private readonly System.Windows.Forms.Timer _alertIconTimer = new();
        private readonly System.Windows.Forms.Timer _timer = new();

        // UI bits
        private readonly DataGridView _grid = new();
        private readonly Label _lblSummary = new();
        private readonly Label _lblDetails = new();
        private readonly ContextMenuStrip _gridMenu = new();

        // System-wide counters — because Task Manager thinks "Moderate" is a useful metric.
        private readonly PerformanceCounter? _cpuTotal;
        private readonly PerformanceCounter? _diskTotal;
        private readonly List<PerformanceCounter> _gpuCounters = new();

        // Keywords that scream "vendor trash" – tune these to your personal hate list.
        private readonly List<string> _vendorTrashKeywords = new()
        {
            "razer",
            "rzr",
            "rgb",
            "synapse",
            "riot",
            "vanguard",
            "epicgames",
            "launcher",
            "updater",
            "adobe",
            "armoury",
            "i-cue",
            "icue",
            "aura",
            "mystic light",
            "galaxyclient"
        };

        // ========== CONSTRUCTOR ==========

        public MainForm()
        {
            // ==== Load tray icons ====
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                _iconAngel = new Icon(Path.Combine(baseDir, "angel.ico"));
                _iconDevil = new Icon(Path.Combine(baseDir, "devil.ico"));
            }
            catch
            {
                // If we somehow lose the .ico files, at least don't crash.
                _iconAngel = SystemIcons.Information;
                _iconDevil = SystemIcons.Error;
            }

            // Start as "angel" – assume innocence until proven guilty.
            _tray.Icon = _iconAngel ?? SystemIcons.Information;
            _tray.Visible = true;
            _tray.Text = "PowerWatch – babysitting Windows so you don’t have to";

            // Timer that snaps icon back to angel after a bit of chaos.
            _alertIconTimer.Interval = 8000;
            _alertIconTimer.Tick += (_, _) =>
            {
                _alertIconTimer.Stop();
                if (_iconAngel != null)
                    _tray.Icon = _iconAngel;
            };

            // ==== Window / layout ====
            Text = "PowerWatch – CPU Crime Scene";
            Width = 950;
            Height = 560;
            StartPosition = FormStartPosition.CenterScreen;

            // Top summary bar
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(8)
            };
            _lblSummary.Dock = DockStyle.Fill;
            _lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            _lblSummary.Text = "Gathering evidence… (Windows is slow. Shock.)";
            topPanel.Controls.Add(_lblSummary);

            // Process grid
            _grid.Dock = DockStyle.Fill;
            _grid.ReadOnly = true;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.RowHeadersVisible = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

            _grid.Columns.Add("Name", "Process");
            _grid.Columns.Add("Pid", "PID");
            _grid.Columns.Add("Cpu", "CPU %");
            _grid.Columns.Add("Ram", "RAM (MB)");
            _grid.Columns.Add("Trash", "Vendor Trash?");

            _grid.MouseDown += Grid_MouseDown;
            _grid.SelectionChanged += Grid_SelectionChanged;

            // Bottom details bar
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(8)
            };
            _lblDetails.Dock = DockStyle.Fill;
            _lblDetails.TextAlign = ContentAlignment.MiddleLeft;
            _lblDetails.Text = "Select a process to see what it actually is before you murder it.";
            bottomPanel.Controls.Add(_lblDetails);

            Controls.Add(_grid);
            Controls.Add(topPanel);
            Controls.Add(bottomPanel);

            // Tray context menu
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open dashboard", null, (_, _) => ShowFromTray());
            trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = trayMenu;
            _tray.DoubleClick += (_, _) => ShowFromTray();

            // Grid context menu
            var killItem = new ToolStripMenuItem("Kill this process (I know what I’m doing)");
            killItem.Click += KillSelectedProcess;
            _gridMenu.Items.Add(killItem);

            var infoItem = new ToolStripMenuItem("Show full process info");
            infoItem.Click += (_, _) => ShowSelectedProcessInfoDialog();
            _gridMenu.Items.Add(infoItem);

            _grid.ContextMenuStrip = _gridMenu;

            // Monitoring timer
            _timer.Interval = PollIntervalMs;
            _timer.Tick += (_, _) => TickScan();
            _timer.Start();

            // Log file
            if (!File.Exists(_logPath))
                File.WriteAllText(_logPath, "Timestamp,PID,ProcessName,CPUPercent,WorkingSetMB\n");

            // CPU counter
            try { _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuTotal.NextValue(); }
            catch { }

            // Disk counter
            try { _diskTotal = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total"); _diskTotal.NextValue(); }
            catch { }

            // GPU counters
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst.Contains("engtype_3D"))
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                        c.NextValue();
                        _gpuCounters.Add(c);
                    }
                }
            }
            catch { }

            FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide(); // minimize to tray instead of dying
                }
            };
        }

        // ========== UI EVENT HELPERS ==========

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void ExitApp()
        {
            _tray.Visible = false;
            Application.Exit();
        }

        private void Grid_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _grid.HitTest(e.X, e.Y);
            if (hit.RowIndex >= 0)
            {
                _grid.ClearSelection();
                _grid.Rows[hit.RowIndex].Selected = true;
            }
        }

        private void Grid_SelectionChanged(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0)
            {
                _lblDetails.Text = "Select a process to see what it actually is before you murder it.";
                return;
            }

            var row = _grid.SelectedRows[0];
            if (row.Cells["Pid"].Value == null)
            {
                _lblDetails.Text = "No details – row is empty. Windows moment.";
                return;
            }

            if (!int.TryParse(row.Cells["Pid"].Value.ToString(), out int pid))
            {
                _lblDetails.Text = "Cannot parse PID for this row. Impressive.";
                return;
            }

            if (!_state.TryGetValue(pid, out var info))
            {
                _lblDetails.Text = "Process details not available. It may have just died or Windows hid it.";
                return;
            }

            string desc = string.IsNullOrWhiteSpace(info.Description) ? "No description" : info.Description;
            string company = string.IsNullOrWhiteSpace(info.Company) ? "Unknown publisher" : info.Company;
            string path = string.IsNullOrWhiteSpace(info.Path) ? "Path unavailable (system/permission)" : info.Path;
            string trashTag = info.IsVendorTrash ? " [Vendor trash flagged – generally safe to bully]" : "";

            _lblDetails.Text = $"{info.Name} (PID {pid}) – {desc} | {company} | {path}{trashTag}";
        }

        private void KillSelectedProcess(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            if (row.Cells["Pid"].Value == null) return;
            if (!int.TryParse(row.Cells["Pid"].Value.ToString(), out int pid)) return;
            if (!_state.TryGetValue(pid, out var info)) return;

            string name = info.Name;
            string company = string.IsNullOrWhiteSpace(info.Company) ? "Unknown publisher" : info.Company;

            bool looksSystem = name.Equals("System", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                               || company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

            string baseMsg =
                $"You are about to kill {name} (PID {pid}).\n\n" +
                $"Publisher: {company}\n" +
                (info.IsVendorTrash
                    ? "This process matches your vendor trash blacklist (RGB junk, launchers, updaters, etc.).\n"
                    : "");

            string tail =
                looksSystem
                    ? "\nThis looks like a system or Microsoft process. Killing it can freeze or crash Windows.\n\nAre you absolutely sure?"
                    : "\nIf this is some vendor trash or bad updater, killing it is usually safe.\n\nContinue?";

            var confirm = MessageBox.Show(
                baseMsg + tail,
                "Confirm process termination",
                MessageBoxButtons.YesNo,
                looksSystem ? MessageBoxIcon.Warning : MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to kill {name} (PID {pid}): {ex.Message}",
                    "Kill failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void ShowSelectedProcessInfoDialog()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            if (row.Cells["Pid"].Value == null) return;
            if (!int.TryParse(row.Cells["Pid"].Value.ToString(), out int pid)) return;
            if (!_state.TryGetValue(pid, out var info)) return;

            string desc = string.IsNullOrWhiteSpace(info.Description) ? "No description" : info.Description;
            string company = string.IsNullOrWhiteSpace(info.Company) ? "Unknown publisher" : info.Company;
            string path = string.IsNullOrWhiteSpace(info.Path) ? "Path unavailable (system/permission)" : info.Path;
            string trash = info.IsVendorTrash ? "YES – matches vendor trash blacklist" : "No";

            string text =
                $"Name: {info.Name}\n" +
                $"PID: {info.Pid}\n" +
                $"Description: {desc}\n" +
                $"Publisher: {company}\n" +
                $"Path: {path}\n" +
                $"Vendor trash: {trash}\n" +
                $"\nLast CPU% seen: {info.LastCpuPercent:F1}%\n" +
                $"Working set: {(info.WorkingSetBytes / 1024.0 / 1024.0):F1} MB";

            MessageBox.Show(
                text,
                $"Process details – {info.Name}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // ========== MONITORING LOOP ==========

        private void TickScan()
        {
            var now = DateTime.UtcNow;
            var seen = new HashSet<int>();

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return; }

            foreach (var p in processes)
            {
                try
                {
                    if (p.HasExited) continue;
                    int pid = p.Id;
                    seen.Add(pid);

                    if (!_state.TryGetValue(pid, out var info))
                    {
                        _state[pid] = info = new ProcState
                        {
                            Pid = pid,
                            Name = p.ProcessName,
                            LastCpu = p.TotalProcessorTime,
                            LastCheck = now,
                            OveruseSeconds = 0
                        };
                        TryPopulateProcessMetadata(p, info);
                        MarkVendorTrash(info);
                        continue;
                    }

                    var cpuNow = p.TotalProcessorTime;
                    var cpuDelta = cpuNow - info.LastCpu;
                    var dt = now - info.LastCheck;

                    info.LastCpu = cpuNow;
                    info.LastCheck = now;
                    info.WorkingSetBytes = p.WorkingSet64;
                    info.Name = p.ProcessName;

                    if (string.IsNullOrWhiteSpace(info.Path))
                    {
                        TryPopulateProcessMetadata(p, info);
                        MarkVendorTrash(info);
                    }

                    double wallMs = dt.TotalMilliseconds;
                    if (wallMs <= 0) continue;

                    info.LastCpuPercent =
                        (cpuDelta.TotalMilliseconds / wallMs) * 100.0 / _coreCount;

                    if (info.LastCpuPercent >= CpuThresholdPercent)
                        info.OveruseSeconds += dt.TotalSeconds;
                    else
                        info.OveruseSeconds = 0;

                    if (info.OveruseSeconds >= DurationSeconds)
                    {
                        info.OveruseSeconds = 0;
                        double ramMb = info.WorkingSetBytes / (1024.0 * 1024.0);

                        File.AppendAllText(_logPath,
                            $"{DateTime.Now:O},{pid},{info.Name},{info.LastCpuPercent:F1},{ramMb:F1}\n");

                        ShowHogToast(info.Name, pid, info.LastCpuPercent, ramMb);

                        if (_iconDevil != null)
                        {
                            _tray.Icon = _iconDevil;
                            _alertIconTimer.Stop();
                            _alertIconTimer.Start();
                        }
                    }
                }
                catch
                {
                    // System weirdness? Ignore and move on. Windows does.
                }
            }

            foreach (var pid in _state.Keys.Where(k => !seen.Contains(k)).ToList())
                _state.Remove(pid);

            UpdateGrid();
            UpdateSummary();
        }

        private void TryPopulateProcessMetadata(Process p, ProcState info)
        {
            try
            {
                var module = p.MainModule;
                if (module != null)
                {
                    info.Path = module.FileName;
                    try
                    {
                        var ver = FileVersionInfo.GetVersionInfo(info.Path);
                        info.Description = ver.FileDescription ?? "";
                        info.Company = ver.CompanyName ?? "";
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void MarkVendorTrash(ProcState info)
        {
            string blob = (info.Name + " " + info.Description + " " + info.Company + " " + info.Path)
                .ToLowerInvariant();
            info.IsVendorTrash = _vendorTrashKeywords.Any(k => blob.Contains(k));
        }

        private void ShowHogToast(string name, int pid, double cpu, double ram)
        {
            _tray.BalloonTipTitle = "Power Hog Detected";
            _tray.BalloonTipText =
                $"{name} (PID {pid}) is chewing {cpu:F1}% CPU + {ram:F1}MB like optimization is optional.";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.ShowBalloonTip(5000);
        }

        private void UpdateGrid()
        {
            var list = _state.Values
                .Where(x => x.LastCpuPercent > 0)
                .OrderByDescending(x => x.LastCpuPercent)
                .Take(30)
                .Select(x => new
                {
                    x.Name,
                    x.Pid,
                    Cpu = x.LastCpuPercent.ToString("F1"),
                    Ram = (x.WorkingSetBytes / 1024.0 / 1024.0).ToString("F1"),
                    x.IsVendorTrash
                }).ToList();

            _grid.SuspendLayout();
            _grid.Rows.Clear();

            foreach (var item in list)
            {
                int rowIndex = _grid.Rows.Add(
                    item.Name,
                    item.Pid,
                    item.Cpu,
                    item.Ram,
                    item.IsVendorTrash ? "Yes" : "");

                if (item.IsVendorTrash)
                {
                    var row = _grid.Rows[rowIndex];
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 40, 40, 40);
                    row.DefaultCellStyle.ForeColor = Color.FromArgb(255, 255, 200, 200);
                }
            }

            _grid.ResumeLayout();
        }

        private void UpdateSummary()
        {
            float cpu = -1, disk = -1, gpu = -1;

            try { cpu = _cpuTotal?.NextValue() ?? -1; } catch { }
            try { disk = _diskTotal?.NextValue() ?? -1; } catch { }

            if (_gpuCounters.Any())
                try { gpu = _gpuCounters.Sum(c => c.NextValue()); } catch { }

            _lblSummary.Text =
                $"CPU: {(cpu >= 0 ? $"{cpu:F1}%" : "N/A")}   |   Disk: {(disk >= 0 ? $"{disk:F1}%" : "N/A")}   |   GPU3D: {(gpu >= 0 ? $"{gpu:F1}%" : "N/A")}";
        }
    }
}
