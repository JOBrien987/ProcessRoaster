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
    }

    public class MainForm : Form
    {
        // Threshold where we declare a process guilty of crimes against battery life.
        private const double CpuThresholdPercent = 20.0;  // If you hit this, you're on the watchlist.
        private const double DurationSeconds = 10.0;      // Stay hot this long → public shaming.
        private const int PollIntervalMs = 2000;          // Scan rate. Fast enough to catch offenders, slow enough to not torch CPU itself.

        private readonly int _coreCount = Environment.ProcessorCount;  // Because Windows will happily pretend 2 cores is enough.
        private readonly string _logPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "power_watch_log.csv");

        private readonly Dictionary<int, ProcState> _state = new();

        // Must be WinForms Timer because cross-thread UI updates are worse than Windows Update UX.
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly NotifyIcon _tray = new();
        private readonly DataGridView _grid = new();
        private readonly Label _lblSummary = new();
        private readonly Label _lblDetails = new();
        private readonly ContextMenuStrip _gridMenu = new();

        // System-wide counters — because Task Manager thinks "Moderate" is a useful metric.
        private readonly PerformanceCounter? _cpuTotal;
        private readonly PerformanceCounter? _diskTotal;
        private readonly List<PerformanceCounter> _gpuCounters = new();

        public MainForm()
        {
            // Big ugly UI container for the crime scene board.
            Text = "PowerWatch – CPU Crime Scene";
            Width = 900;    // Big enough to list the offenders.
            Height = 550;   // Give a little extra room for details bar.
            StartPosition = FormStartPosition.CenterScreen;

            // ================= Summary Bar (top) =================
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

            // ================= Process Table (middle) =================
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

            // We want right-click to actually select the row we clicked before opening menu.
            _grid.MouseDown += Grid_MouseDown;
            _grid.SelectionChanged += Grid_SelectionChanged;

            // ================= Details Bar (bottom) =================
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

            // ================= Tray Icon =================
            _tray.Icon = SystemIcons.Warning; // Yellow triangle, because Windows deserves judgement.
            _tray.Visible = true;
            _tray.Text = "PowerWatch – babysitting Windows so you don’t have to";

            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open dashboard", null, (_, _) => ShowFromTray());
            trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = trayMenu;
            _tray.DoubleClick += (_, _) => ShowFromTray();

            // ================= Grid Context Menu (right-click) =================
            var killItem = new ToolStripMenuItem("Kill this process (I know what I’m doing)");
            killItem.Click += KillSelectedProcess;
            _gridMenu.Items.Add(killItem);

            var infoItem = new ToolStripMenuItem("Show full process info");
            infoItem.Click += (_, _) => ShowSelectedProcessInfoDialog();
            _gridMenu.Items.Add(infoItem);

            _grid.ContextMenuStrip = _gridMenu;

            // ================= Monitoring Timer =================
            _timer.Interval = PollIntervalMs; // 2 seconds — short enough to catch trashware, long enough to not BE trashware.
            _timer.Tick += (_, _) => TickScan();
            _timer.Start();

            // ================= Log File =================
            if (!File.Exists(_logPath))
            {
                File.WriteAllText(_logPath, "Timestamp,PID,ProcessName,CPUPercent,WorkingSetMB\n");
            }

            // ================= CPU Counter =================
            try { _cpuTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuTotal.NextValue(); }
            catch { } // If this fails, blame Microsoft telemetry, not us.

            // ================= Disk Counter =================
            try { _diskTotal = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total"); _diskTotal.NextValue(); }
            catch { } // Windows might not expose it. Like half its features.

            // ================= GPU Counters =================
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    if (inst.Contains("engtype_3D")) // Ignore video decode, copy engines — this is about *real* power burn.
                    {
                        var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                        c.NextValue();
                        _gpuCounters.Add(c);
                    }
                }
            }
            catch
            {
                // No GPU counters → Windows pretending GPU doesn't matter. Classic.
            }

            FormClosing += (_, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide(); // Not closing — just shrinking into shame in the taskbar.
                }
            };
        }

        // When you click tray → window returns like a guilt-ridden alcoholic.
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

        // Make right-click actually select the row under the cursor.
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

        // When selection changes, update the bottom "what is this thing?" bar.
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

            int pid;
            try { pid = Convert.ToInt32(row.Cells["Pid"].Value); }
            catch
            {
                _lblDetails.Text = "Cannot parse PID for this row. That’s… impressive.";
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

            _lblDetails.Text = $"{info.Name} (PID {pid}) – {desc} | {company} | {path}";
        }

        // Right-click → Kill this process (with guard rails).
        private void KillSelectedProcess(object? sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;

            var row = _grid.SelectedRows[0];
            if (row.Cells["Pid"].Value == null) return;

            int pid;
            try { pid = Convert.ToInt32(row.Cells["Pid"].Value); }
            catch { return; }

            if (!_state.TryGetValue(pid, out var info)) return;

            string name = info.Name;
            string company = string.IsNullOrWhiteSpace(info.Company) ? "Unknown publisher" : info.Company;

            // Mild sanity checks so you don't casually nuke core OS stuff on autopilot.
            bool looksSystem = name.Equals("System", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
                               || company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

            var msg =
                $"You are about to kill {name} (PID {pid}).\n\n" +
                $"Publisher: {company}\n" +
                (looksSystem
                    ? "\nThis looks like a system or Microsoft process. Killing it can freeze or crash Windows.\n\nAre you absolutely sure?"
                    : "\nIf this is some vendor trash or bad updater, killing it is usually safe.\n\nContinue?");

            var confirm = MessageBox.Show(
                msg,
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

        // Pop a dialog with full info – for when you want to inspect before deciding.
        private void ShowSelectedProcessInfoDialog()
        {
            if (_grid.SelectedRows.Count == 0) return;

            var row = _grid.SelectedRows[0];
            if (row.Cells["Pid"].Value == null) return;

            int pid;
            try { pid = Convert.ToInt32(row.Cells["Pid"].Value); }
            catch { return; }

            if (!_state.TryGetValue(pid, out var info)) return;

            string desc = string.IsNullOrWhiteSpace(info.Description) ? "No description" : info.Description;
            string company = string.IsNullOrWhiteSpace(info.Company) ? "Unknown publisher" : info.Company;
            string path = string.IsNullOrWhiteSpace(info.Path) ? "Path unavailable (system/permission)" : info.Path;

            string text =
                $"Name: {info.Name}\n" +
                $"PID: {info.Pid}\n" +
                $"Description: {desc}\n" +
                $"Publisher: {company}\n" +
                $"Path: {path}\n" +
                $"\nLast CPU% seen: {info.LastCpuPercent:F1}%\n" +
                $"Working set: {(info.WorkingSetBytes / 1024.0 / 1024.0):F1} MB";

            MessageBox.Show(
                text,
                $"Process details – {info.Name}",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // ===================== Main Monitoring Loop =====================
        private void TickScan()
        {
            var now = DateTime.UtcNow;
            var seen = new HashSet<int>();

            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return; } // If Windows refuses to enumerate processes, we simply choose violence elsewhere.

            foreach (var p in processes)
            {
                try
                {
                    if (p.HasExited) continue;
                    int pid = p.Id;
                    seen.Add(pid);

                    // ========== Track existence & CPU baseline ===============
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
                        // First time we see it, try to pull path + description so we know what garbage this is.
                        TryPopulateProcessMetadata(p, info);
                        continue; // Next scan will calculate delta.
                    }

                    var cpuNow = p.TotalProcessorTime;
                    var cpuDelta = cpuNow - info.LastCpu;
                    var dt = now - info.LastCheck;

                    info.LastCpu = cpuNow;
                    info.LastCheck = now;
                    info.WorkingSetBytes = p.WorkingSet64;
                    info.Name = p.ProcessName;

                    // In case we didn't get metadata before, try again.
                    if (string.IsNullOrWhiteSpace(info.Path))
                    {
                        TryPopulateProcessMetadata(p, info);
                    }

                    double wallMs = dt.TotalMilliseconds;
                    if (wallMs <= 0) continue;

                    // CPU % — because Task Manager can't show it fast enough to matter.
                    info.LastCpuPercent =
                        (cpuDelta.TotalMilliseconds / wallMs) * 100.0 / _coreCount;

                    // Track repeat offenders (hey, vendor RGB daemons).
                    if (info.LastCpuPercent >= CpuThresholdPercent)
                        info.OveruseSeconds += dt.TotalSeconds;
                    else
                        info.OveruseSeconds = 0;

                    // Guilty → toast alert + log
                    if (info.OveruseSeconds >= DurationSeconds)
                    {
                        info.OveruseSeconds = 0;
                        double ramMb = info.WorkingSetBytes / (1024.0 * 1024.0);

                        File.AppendAllText(_logPath,
                            $"{DateTime.Now:O},{pid},{info.Name},{info.LastCpuPercent:F1},{ramMb:F1}\n");

                        ShowHogToast(info.Name, pid, info.LastCpuPercent, ramMb);
                    }
                }
                catch
                {
                    // If Windows fails, we just ignore like Microsoft did QA.
                }
            }

            // Clean out the ghosts of dead processes.
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
                    catch
                    {
                        // Some processes don’t ship version info because optimization is clearly optional.
                    }
                }
            }
            catch
            {
                // Access denied on system processes is normal. Windows loves secrets.
            }
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
                    Ram = (x.WorkingSetBytes / 1024.0 / 1024.0).ToString("F1")
                }).ToList();

            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (var item in list)
                _grid.Rows.Add(item.Name, item.Pid, item.Cpu, item.Ram);
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

            // If everything is high simultaneously, assume Windows Update is awake and plotting.
        }
    }
}
