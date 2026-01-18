// iODD Rebuilder
// - ALWAYS rebuild: Confirm -> format destination -> full sequential copy (contiguous best effort)
// - No admin by default; UAC only for formatting PowerShell
// - Progress is REAL bytes copied (not file length reservation)
// - Shows BOTH: file-based % (nice â€œ2%â€) + byte-based % (real data)
// - Fixes label wrap (Destination) + removes "FORMAT +" text

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ioDDRebuilder
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    public sealed class MainForm : Form
    {
        // UI
        private ComboBox cmbSource = null!;
        private ComboBox cmbDest = null!;
        private ComboBox cmbFs = null!; // Filesystem selector
        private string selectedSourcePath = "";  // Store selected source path
        private Button btnRefresh = null!;
        private Button btnOpenLog = null!;
        private Button btnStart = null!;
        private Button btnCancel = null!;

        private ProgressBar bar = null!;
        private Label lblCurrentRel = null!;

        private Label lblOverallFiles = null!;
        private Label lblOverallBytes = null!;
        private Label lblFile = null!;
        private Label lblCurrentFile = null!;
        private Label lblSpeed = null!;
        private Label lblEta = null!;

        private StatusStrip status = null!;
        private ToolStripStatusLabel statusLeft = null!;
        private ToolStripStatusLabel statusRight = null!;

        // Worker
        private CancellationTokenSource? _cts;

        // Log
        private readonly string _logPath;

        // Speed smoothing
        private double _emaBps = 0;
        private long _lastOverallBytes = 0;
        private readonly Stopwatch _tick = new();

        private static readonly string[] ExcludeNames =
        {
            "Thumbs.db","desktop.ini",
            "iodd_push.ps1", "iodd_push_gui.ps1", "iodd_push_gui.bat", "iodd_push_to_drive.bat",
            "iodd_push_log.txt"
        };

        public MainForm()
        {
            _logPath = Path.Combine(Path.GetTempPath(), $"iodd_rebuilder_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            BuildUi();
            PopulateDrives();
            SetIdleUi();
            
            // Set placeholder colors when form loads
            Load += (_, __) =>
            {
                if (cmbSource.SelectedIndex == 0 && cmbSource.Items[0]?.ToString() == "Select source folder...")
                    cmbSource.ForeColor = Color.FromArgb(150, 150, 150);
                if (cmbDest.SelectedIndex == 0)
                    cmbDest.ForeColor = Color.FromArgb(150, 150, 150);
            };
        }

        private void BuildUi()
        {
            Text = "iODD Rebuilder";
            try
            {
                // Extract icon from embedded EXE icon (set in .csproj ApplicationIcon)
                var exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (exeIcon != null)
                    Icon = exeIcon;
            }
            catch (Exception ex) { SafeLog($"[UI] Icon loading failed: {ex.Message}"); /* Icon loading failed, continue without */ }
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(1000, 740);
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.FromArgb(250, 250, 250);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 1,
                RowCount = 5,
                AutoScroll = true
            };
            root.RowStyles.Clear();
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Settings
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Progress
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));      // Spacer
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Stats
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Buttons
            Controls.Add(root);

            // ===== SETTINGS =====
            var gbSettings = new GroupBox
            {
                Text = "Settings",
                Dock = DockStyle.Top,
                Padding = new Padding(16),
                Height = 180
            };

            var settings = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 3
            };

            // FIX: Column 0 wider so "Destination" doesn't wrap to "n" line
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135)); // was 110
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));

            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            Label MakeLeft(string t) => new Label
            {
                Text = t,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = false,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                ForeColor = Color.Black
            };

            // Source
            settings.Controls.Add(MakeLeft("Source"), 0, 0);
            cmbSource = new ComboBox 
            { 
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(150, 150, 150),
                TabIndex = 1,
                Font = new Font("Segoe UI", 10f)
            };
            cmbSource.Items.Add("Select source folder...");
            cmbSource.Items.Add("Browse...");
            cmbSource.SelectedIndex = 0;
            cmbSource.DropDown += (_, __) =>
            {
                // When dropdown opens, set to black so all items are readable
                cmbSource.ForeColor = Color.Black;
            };
            cmbSource.DropDownClosed += (_, __) =>
            {
                // After dropdown closes, set color based on selected item
                if (cmbSource.SelectedIndex == 0)
                {
                    var item = cmbSource.Items[0]?.ToString();
                    if (item == "Select source folder...")
                        cmbSource.ForeColor = Color.FromArgb(150, 150, 150);
                    else
                        cmbSource.ForeColor = Color.Black;
                }
                else
                {
                    cmbSource.ForeColor = Color.Black;
                }
            };
            cmbSource.SelectedIndexChanged += (_, __) =>
            {
                if (cmbSource.SelectedIndex == 1)  // "Browse..." clicked
                {
                    PickSource();
                    if (!string.IsNullOrEmpty(selectedSourcePath))
                    {
                        // Valid path selected
                        if (cmbSource.Items.Count == 2)  // Only placeholder and Browse
                            cmbSource.Items.Insert(0, selectedSourcePath);
                        else
                            cmbSource.Items[0] = selectedSourcePath;
                        cmbSource.SelectedIndex = 0;
                        cmbSource.ForeColor = Color.Black;
                    }
                    else
                    {
                        // Cancelled or no selection - go back to placeholder
                        cmbSource.SelectedIndex = 0;
                        cmbSource.ForeColor = Color.FromArgb(150, 150, 150);
                    }
                }
            };
            settings.Controls.Add(cmbSource, 1, 0);
            // Force gray color for placeholder after adding to form
            cmbSource.ForeColor = Color.FromArgb(150, 150, 150);

            btnOpenLog = new Button 
            { 
                Text = "Log", 
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(100, 100, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                TabIndex = 2
            };
            btnOpenLog.FlatAppearance.BorderSize = 0;
            btnOpenLog.Click += (_, __) => { SafeLog("[UI] Log button clicked"); OpenLog(); };
            settings.Controls.Add(btnOpenLog, 2, 0);

            btnRefresh = new Button 
            { 
                Text = "Refresh", 
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(70, 130, 180),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                TabIndex = 3
            };
            btnRefresh.FlatAppearance.BorderSize = 0;
            btnRefresh.Click += (_, __) => { SafeLog("[UI] Refresh button clicked"); PopulateDrives(); };
            settings.Controls.Add(btnRefresh, 3, 0);

            // Destination
            settings.Controls.Add(MakeLeft("Destination"), 0, 1);
            cmbDest = new ComboBox 
            { 
                Dock = DockStyle.Fill, 
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(150, 150, 150),
                TabIndex = 4,
                Font = new Font("Segoe UI", 10f)
            };
            cmbDest.Tag = "Select a destination drive...";
            cmbDest.DropDown += (_, __) =>
            {
                // When dropdown opens, set to black so all items are readable
                cmbDest.ForeColor = Color.Black;
            };
            cmbDest.DropDownClosed += (_, __) =>
            {
                // After dropdown closes, set color based on selected item
                if (cmbDest.SelectedIndex == 0)
                    cmbDest.ForeColor = Color.FromArgb(150, 150, 150);
                else
                    cmbDest.ForeColor = Color.Black;
            };
            settings.Controls.Add(cmbDest, 1, 1);
            // Force gray color for placeholder after adding to form
            cmbDest.ForeColor = Color.FromArgb(150, 150, 150);

            settings.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 2, 1);
            settings.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 3, 1);

            // Filesystem
            settings.Controls.Add(MakeLeft("Filesystem"), 0, 2);
            cmbFs = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.White,
                ForeColor = Color.Black,
                TabIndex = 5,
                Font = new Font("Segoe UI", 10f)
            };
            cmbFs.Items.AddRange(new object[] { "exFAT", "NTFS", "FAT32" });
            cmbFs.SelectedIndex = 0; // default exFAT
            settings.Controls.Add(cmbFs, 1, 2);
            settings.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 2, 2);
            settings.Controls.Add(new Label { Text = "", Dock = DockStyle.Fill }, 3, 2);

            // (fjernet mode-tekst)

            gbSettings.Controls.Add(settings);
            root.Controls.Add(gbSettings);

            // ===== PROGRESS =====
            var gbProgress = new GroupBox
            {
                Text = "Progress",
                Dock = DockStyle.Top,
                Padding = new Padding(12),
                Height = 120
            };

            var progress = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            progress.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            progress.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            lblCurrentRel = new Label { Text = "Ready.", Dock = DockStyle.Fill, AutoEllipsis = true };
            bar = new ProgressBar { Dock = DockStyle.Top, Height = 30, Minimum = 0, Maximum = 100 };

            progress.Controls.Add(lblCurrentRel, 0, 0);
            progress.Controls.Add(bar, 0, 1);
            gbProgress.Controls.Add(progress);
            root.Controls.Add(gbProgress);

            root.Controls.Add(new Panel { Height = 20, Dock = DockStyle.Top });

            // ===== STATS =====
            var gbStats = new GroupBox
            {
                Text = "Stats",
                Dock = DockStyle.Top,
                Padding = new Padding(16),
                Height = 235
            };

            var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int r = 0; r < 6; r++) stats.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            Label Key(string t) => new Label { Text = t, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(70, 70, 70) };
            Label Val() => new Label { Text = "-", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 10f, FontStyle.Bold) };

            stats.Controls.Add(Key("Files (%)"), 0, 0); lblOverallFiles = Val(); stats.Controls.Add(lblOverallFiles, 1, 0);
            stats.Controls.Add(Key("Data (bytes)"), 0, 1); lblOverallBytes = Val(); stats.Controls.Add(lblOverallBytes, 1, 1);
            stats.Controls.Add(Key("File count"), 0, 2); lblFile = Val(); stats.Controls.Add(lblFile, 1, 2);
            stats.Controls.Add(Key("This file"), 0, 3); lblCurrentFile = Val(); stats.Controls.Add(lblCurrentFile, 1, 3);
            stats.Controls.Add(Key("Speed"), 0, 4); lblSpeed = Val(); stats.Controls.Add(lblSpeed, 1, 4);
            stats.Controls.Add(Key("ETA"), 0, 5); lblEta = Val(); stats.Controls.Add(lblEta, 1, 5);

            gbStats.Controls.Add(stats);
            root.Controls.Add(gbStats);

            // ===== BUTTONS =====
            var bottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(0, 10, 0, 0)
            };

            btnStart = new Button 
            { 
                Text = "START", 
                Width = 160, 
                Height = 44,
                BackColor = Color.FromArgb(0, 150, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold)
            };
            btnStart.FlatAppearance.BorderSize = 0;
            
            btnCancel = new Button 
            { 
                Text = "Cancel", 
                Width = 160, 
                Height = 44, 
                Enabled = false,
                BackColor = Color.FromArgb(150, 0, 0),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 12f, FontStyle.Bold)
            };
            btnCancel.FlatAppearance.BorderSize = 0;

            btnStart.Click += async (_, __) => { SafeLog("[UI] START button clicked"); await StartCopyAsync(); };
            btnCancel.Click += (_, __) => { SafeLog("[UI] Cancel button clicked"); CancelCopy(); };
            FormClosing += (_, __) => CancelCopy();

            bottom.Controls.Add(btnStart);
            bottom.Controls.Add(btnCancel);
            root.Controls.Add(bottom);

            // Status strip
            status = new StatusStrip();
            statusLeft = new ToolStripStatusLabel("Ready");
            statusRight = new ToolStripStatusLabel(DateTime.Now.ToString("HH:mm:ss"));
            status.Items.Add(statusLeft);
            status.Items.Add(new ToolStripStatusLabel { Spring = true });
            status.Items.Add(statusRight);
            Controls.Add(status);

            var timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += (_, __) => statusRight.Text = DateTime.Now.ToString("HH:mm:ss");
            timer.Start();
        }

        private void SetIdleUi()
        {
            statusLeft.Text = "Ready";
            lblOverallFiles.Text = "-";
            lblOverallBytes.Text = "-";
            lblFile.Text = "-";
            lblCurrentFile.Text = "-";
            lblSpeed.Text = "-";
            lblEta.Text = "-";
            lblCurrentRel.Text = "Ready.";
            bar.Value = 0;
        }

        private void SetRunning(bool running)
        {
            btnStart.Enabled = !running;
            cmbSource.Enabled = !running;
            btnRefresh.Enabled = !running;
            cmbDest.Enabled = !running;
            cmbFs.Enabled = !running;

            btnCancel.Enabled = running;
            statusLeft.Text = running ? "Working..." : "Ready";
        }

        private void PickSource()
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = @"Select source folder",
                    SelectedPath = !string.IsNullOrEmpty(selectedSourcePath) ? selectedSourcePath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ShowNewFolderButton = false
                };

                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                {
                    selectedSourcePath = dlg.SelectedPath;  // Store selected path
                    SafeLog($"[SOURCE] User selected: {selectedSourcePath}");
                }
                else
                {
                    SafeLog("[SOURCE] User cancelled source picker");
                }
            }
            catch (Exception ex)
            {
                SafeLog($"[SOURCE] PickSource FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void PopulateDrives()
        {
            try
            {
                cmbDest.Items.Clear();
                cmbDest.Items.Add(new DriveItem((string?)cmbDest.Tag ?? "Select a destination drive...", "", ""));
                SafeLog("[DRIVES] Cleared destination combo, added placeholder");

                var drives = DriveInfo.GetDrives().Where(x => x.IsReady).ToList();
                SafeLog($"[DRIVES] Found {drives.Count} ready drive(s)");

                foreach (var d in drives)
                {
                    try
                    {
                        var root = d.RootDirectory.FullName; // "F:\"
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "(no label)" : d.VolumeLabel;
                        var freeGB = Math.Round(d.AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
                        var sizeGB = Math.Round(d.TotalSize / 1024d / 1024d / 1024d, 1);
                        cmbDest.Items.Add(new DriveItem($"{root}  {label}  ({freeGB}/{sizeGB} GB free)", root, label));
                        SafeLog($"[DRIVES] Added: {root} Label={label} {freeGB}/{sizeGB}GB");
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"[DRIVES] Failed to add drive: {ex.Message}");
                    }
                }

                cmbDest.DisplayMember = nameof(DriveItem.Text);
                cmbDest.SelectedIndex = 0;
                cmbDest.ForeColor = Color.FromArgb(150, 150, 150);
                SafeLog("[DRIVES] PopulateDrives completed");
            }
            catch (Exception ex)
            {
                SafeLog($"[DRIVES] PopulateDrives FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OpenLog()
        {
            try
            {
                SafeLog("[UI] Opening log file");
                if (File.Exists(_logPath))
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_logPath}\"") { UseShellExecute = true });
                    SafeLog("[UI] Log opened in notepad");
                }
                else
                {
                    SafeLog($"[UI] Log file not found: {_logPath}");
                    MessageBox.Show("Log findes ikke endnu:\n" + _logPath);
                }
            }
            catch (Exception ex)
            {
                SafeLog($"[UI] OpenLog FAILED: {ex.GetType().Name}: {ex.Message}");
                MessageBox.Show(ex.Message);
            }
        }

        private void CancelCopy()
        {
            try { _cts?.Cancel(); } catch { }
            SetRunning(false);
            SafeLog("[UI] Copy operation cancelled by user");
            statusLeft.Text = "Cancelled";
        }

        private async Task StartCopyAsync()
        {
            // Get source from selectedSourcePath
            string src = selectedSourcePath?.Trim() ?? "";
            
            SafeLog($"[START] Copy initiated - Source: {src}");

            if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
            {
                MessageBox.Show(@"Source folder not found.

Please select a valid source folder by clicking on the dropdown.");
                SafeLog("[ERROR] Source folder not found or invalid");
                return;
            }

            if (cmbDest.SelectedItem is not DriveItem di || string.IsNullOrWhiteSpace(di.Root) || !Directory.Exists(di.Root))
            {
                MessageBox.Show("Vælg et gyldigt destination-drev.");
                SafeLog("[ERROR] Destination drive not selected or invalid");
                return;
            }
            
            SafeLog($"[START] Destination selected: {di.Root} ({di.Label})");

            // Confirm + format
            var destLetter = di.Root.Trim().TrimEnd('\\').TrimEnd(':'); // "F"
            using (var dlg = new ConfirmFormatDialog(di.Root, di.Label))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
            }

            try
            {
                SetRunning(true);
                statusLeft.Text = "Formatting drive...";
                lblCurrentRel.Text = $"Formatting {di.Root} ...";
                bar.Value = 0;
                SafeLog($"FORMAT START: {di.Root} Label={di.Label}");
                SafeLog($"DEBUG: destLetter={destLetter}, calling FormatDriveLetter...");

                // UAC only here
                var fsSel = (cmbFs?.SelectedItem?.ToString() ?? "exFAT").Trim();
                // Normalize casing for different tools
                string fileSystem = fsSel.Equals("exfat", StringComparison.OrdinalIgnoreCase) ? "exFAT" :
                                    fsSel.Equals("fat32", StringComparison.OrdinalIgnoreCase) ? "FAT32" :
                                    "NTFS".Equals(fsSel, StringComparison.OrdinalIgnoreCase) ? "NTFS" : "exFAT";

                // Optional advisory: FAT32 has 32GB format limits on Windows
                try
                {
                    var diDest = new DriveInfo(di.Root);
                    if (fileSystem == "FAT32" && diDest.TotalSize > (long)32 * 1024 * 1024 * 1024)
                        SafeLog("[WARN] FAT32 chosen on volume >32GB; Windows tools may limit formatting. Using best-effort.");
                }
                catch { }

                FormatDriveLetter(destLetter, fileSystem: fileSystem, newLabel: "IODD");

                SafeLog("FORMAT DONE");
                PopulateDrives();
                ReselectDrive(destLetter);

                // Ensure destination is empty after format (belt-and-braces cleanup)
                CleanDestination(di.Root);
            }
            catch (Exception ex)
            {
                SetRunning(false);
                SafeLog($"FORMAT FAILED: {ex.GetType().Name}: {ex.Message}");
                SafeLog($"STACK TRACE: {ex.StackTrace}");
                MessageBox.Show("Format fejl:\n" + ex.Message + "\n\nSe log: " + _logPath);
                statusLeft.Text = "Format failed";
                return;
            }

            // Scan
            FileInfo[] files;
            long totalBytes;

            try
            {
                SafeLog($"[SCAN] Starting file enumeration from {src}");
                var allFiles = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToList();
                SafeLog($"[SCAN] Found {allFiles.Count} total files before filtering");

                files = allFiles
                    .Select(p => new FileInfo(p))
                    .Where(f => !ExcludeNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
                    // Big first (best for contiguous)
                    .OrderByDescending(f => IsBig(f.Extension))
                    .ThenByDescending(f => f.Length)
                    .ToArray();

                totalBytes = files.Sum(f => f.Length);

                SafeLog($"[SCAN] After filtering: {files.Length} files, {totalBytes} bytes");
                SafeLog($"===== START {DateTime.Now:dd/MM/yyyy HH.mm.ss} =====");
                SafeLog($"Source: {src}");
                SafeLog($"Dest:   {di.Root}");
                SafeLog($"Files:  {files.Length}");
                SafeLog($"Bytes:  {totalBytes}");
                SafeLog("");
            }
            catch (Exception ex)
            {
                SetRunning(false);
                SafeLog($"[SCAN] FAILED: {ex.GetType().Name}: {ex.Message}");
                SafeLog($"[SCAN] STACK: {ex.StackTrace}");
                MessageBox.Show("Fejl under scanning:\n" + ex.Message);
                return;
            }

            if (files.Length == 0)
            {
                SetRunning(false);
                MessageBox.Show("Ingen filer at kopiere.");
                return;
            }

            // Reset progress
            _cts = new CancellationTokenSource();
            _emaBps = 0;
            _lastOverallBytes = 0;
            _tick.Restart();

            bar.Value = 0;
            lblCurrentRel.Text = "Starting...";
            lblOverallFiles.Text = $"0% (0 / {files.Length})";
            lblOverallBytes.Text = $"0% (0 / {FormatBytes(totalBytes)})";
            lblFile.Text = $"0 / {files.Length}";
            lblCurrentFile.Text = "-";
            lblSpeed.Text = "-";
            lblEta.Text = "-";
            statusLeft.Text = "Working...";

            btnCancel.Enabled = true;

            try
            {
                await Task.Run(() => CopySequentialWithProgress(src, di.Root, files, totalBytes, _cts.Token));

                BeginInvoke(new Action(() =>
                {
                    bar.Value = 100;
                    lblCurrentRel.Text = "Done âœ…";
                    lblOverallFiles.Text = $"100% ({files.Length} / {files.Length})";
                    lblOverallBytes.Text = $"100% ({FormatBytes(totalBytes)} / {FormatBytes(totalBytes)})";
                    lblFile.Text = $"{files.Length} / {files.Length}";
                    lblCurrentFile.Text = "100%";
                    lblEta.Text = "00:00:00";
                    statusLeft.Text = "Done âœ…";
                }));
            }
            catch (OperationCanceledException)
            {
                BeginInvoke(new Action(() =>
                {
                    statusLeft.Text = "Cancelled";
                    lblCurrentRel.Text = "Cancelled";
                }));
            }
            catch (Exception ex)
            {
                SafeLog($"[COPY] FAILED: {ex.GetType().Name}: {ex.Message}");
                SafeLog($"[COPY] STACK: {ex.StackTrace}");
                BeginInvoke(new Action(() =>
                {
                    statusLeft.Text = "Error";
                    MessageBox.Show("Fejl:\n" + ex.Message + "\n\nSe log: " + _logPath);
                }));
            }
            finally
            {
                SetRunning(false);
            }
        }

        private void ReselectDrive(string destLetter)
        {
            destLetter = destLetter.Trim().TrimEnd(':').ToUpperInvariant();
            for (int i = 0; i < cmbDest.Items.Count; i++)
            {
                if (cmbDest.Items[i] is DriveItem item)
                {
                    var letter = item.Root.Trim().TrimEnd('\\').TrimEnd(':').ToUpperInvariant();
                    if (letter == destLetter)
                    {
                        cmbDest.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void CopySequentialWithProgress(string srcRoot, string destRoot, FileInfo[] files, long totalBytes, CancellationToken token)
        {
            long doneBytes = 0;

            // Pre-create dirs
            try
            {
                var dirs = Directory.EnumerateDirectories(srcRoot, "*", SearchOption.AllDirectories).ToList();
                SafeLog($"[COPY] Pre-creating {dirs.Count} directories");
                foreach (var dir in dirs)
                {
                    token.ThrowIfCancellationRequested();
                    var relDir = Path.GetRelativePath(srcRoot, dir);
                    var destDir = Path.Combine(destRoot, relDir);
                    Directory.CreateDirectory(destDir);
                }
                SafeLog($"[COPY] Directories created successfully");
            }
            catch (Exception ex)
            {
                SafeLog($"[COPY] Pre-create directories FAILED: {ex.GetType().Name}: {ex.Message}");
                throw;
            }

            const int bufSize = 1024 * 1024; // 1MB
            var buffer = new byte[bufSize];

            for (int i = 0; i < files.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                var f = files[i];
                var rel = Path.GetRelativePath(srcRoot, f.FullName).Replace('/', '\\');
                var dst = Path.Combine(destRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                long curCopied = 0;
                long curTotal = f.Length;

                BeginInvoke(new Action(() =>
                {
                    lblCurrentRel.Text = rel;
                    lblFile.Text = $"{i + 1} / {files.Length}";
                    lblCurrentFile.Text = $"0% (0 / {FormatBytes(curTotal)})";
                }));

                using var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufSize, FileOptions.SequentialScan);

                using var dstFs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufSize, FileOptions.SequentialScan);

                int read;
                var lastUi = Stopwatch.StartNew();

                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();

                    dstFs.Write(buffer, 0, read);
                    curCopied += read;

                    // UI update ~4x/sek
                    if (lastUi.ElapsedMilliseconds >= 250)
                    {
                        lastUi.Restart();

                        long overallDone = doneBytes + curCopied;

                        // FILE-based percent (what you want as â€œ2%â€)
                        var filesPct = (int)Math.Round(((i + (curTotal > 0 ? (curCopied / (double)curTotal) : 0)) / files.Length) * 100.0);
                        filesPct = Math.Clamp(filesPct, 0, 100);

                        // BYTES-based percent (real data)
                        var bytesPct = totalBytes > 0 ? (int)Math.Round(overallDone * 100.0 / totalBytes) : 0;
                        bytesPct = Math.Clamp(bytesPct, 0, 100);

                        // speed/eta
                        var dt = Math.Max(0.001, _tick.Elapsed.TotalSeconds);
                        _tick.Restart();

                        var delta = Math.Max(0, overallDone - _lastOverallBytes);
                        _lastOverallBytes = overallDone;

                        var instBps = delta / dt;
                        const double alpha = 0.20;
                        _emaBps = _emaBps <= 0 ? instBps : (alpha * instBps) + ((1 - alpha) * _emaBps);

                        var remain = Math.Max(0, totalBytes - overallDone);
                        TimeSpan? eta = null;
                        if (_emaBps >= 1024)
                        {
                            var seconds = remain / _emaBps;
                            seconds = Math.Min(seconds, TimeSpan.MaxValue.TotalSeconds - 1);
                            eta = TimeSpan.FromSeconds(seconds);
                        }

                        var curPct = curTotal > 0 ? (int)Math.Round(curCopied * 100.0 / curTotal) : 0;
                        curPct = Math.Clamp(curPct, 0, 100);

                        BeginInvoke(new Action(() =>
                        {
                            // Progress bar shows FILE-based percent (so first file ~2-4% instead of 17%)
                            bar.Value = filesPct;

                            lblOverallFiles.Text = $"{filesPct}% ({i + 1} / {files.Length})";
                            lblOverallBytes.Text = $"{bytesPct}% ({FormatBytes(overallDone)} / {FormatBytes(totalBytes)})";

                            lblCurrentFile.Text = $"{curPct}% ({FormatBytes(curCopied)} / {FormatBytes(curTotal)})";
                            lblSpeed.Text = _emaBps > 0 ? $"{FormatBytes((long)_emaBps)}/s" : "-";
                            lblEta.Text = eta.HasValue ? eta.Value.ToString(@"hh\:mm\:ss") : "-";
                        }));
                    }
                }

                dstFs.Flush(true);
                doneBytes += curTotal;

                SafeLog($"[COPY] OK: {rel} ({curTotal} bytes)");
            }
        }

        private void SafeLog(string line)
        {
            try 
            { 
                File.AppendAllText(_logPath, line + Environment.NewLine); 
            } 
            catch (Exception ex) 
            { 
                // If log write fails, at least try to alert via Debug
                System.Diagnostics.Debug.WriteLine($"[LOG_FAIL] Could not write to log: {ex.Message}");
            }
        }

        private static bool IsBig(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext is ".iso" or ".img" or ".vhd" or ".vhdx";
        }

        private static string FormatBytes(long bytes)
        {
            double b = bytes;
            string[] suf = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < suf.Length - 1) { b /= 1024; i++; }
            return $"{b:0.0} {suf[i]}";
        }

        private void FormatDriveLetter(string driveLetter, string fileSystem, string newLabel)
        {
            driveLetter = driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
            string driveRoot = $"{driveLetter}:";

            SafeLog($"[FORMAT_DEBUG] Function called with drive={driveRoot}");

            try
            {
                // Run format with explicit path via PowerShell Start-Process -Verb RunAs, then verify label
                var formatPath = Path.Combine(Environment.SystemDirectory, "format.com");
                var formatArgs = $"{driveRoot} /FS:{fileSystem} /V:{newLabel} /Q /Y";

                // PowerShell command: Start-Process -FilePath 'format.com' -ArgumentList 'F: /FS:exFAT /V:IODD /Q /Y' -Verb RunAs -Wait
                string psCommand = $"Start-Process -FilePath '\"{formatPath}\"' -ArgumentList '\"{formatArgs}\"' -Verb RunAs -Wait";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psCommand}\"",
                    UseShellExecute = true,   // needed for Verb RunAs inside PS
                    Verb = "runas",
                    CreateNoWindow = false
                };

                SafeLog($"[FORMAT_DEBUG] FileName: {psi.FileName}");
                SafeLog($"[FORMAT_DEBUG] Arguments: {psi.Arguments}");

                SafeLog($"[FORMAT_DEBUG] Starting process...");
                var p = Process.Start(psi);
                SafeLog($"[FORMAT_DEBUG] Process.Start returned: {(p == null ? "NULL" : "OK (ID=" + p.Id + ")")}");

                if (p != null)
                {
                    SafeLog($"[FORMAT_DEBUG] Waiting for process to exit...");
                    p.WaitForExit();
                    SafeLog($"[FORMAT_DEBUG] Process exited.");
                    p.Dispose();
                }
                else
                {
                    SafeLog($"[FORMAT_DEBUG] WARNING: Process.Start returned null");
                }

                // Verify label to ensure format actually happened
                SafeLog($"[FORMAT_DEBUG] Verifying volume label...");
                bool ok = false;
                for (int i = 0; i < 8; i++)
                {
                    try
                    {
                        var di = new DriveInfo(driveRoot);
                        SafeLog($"[FORMAT_DEBUG] Verification attempt {i+1}: IsReady={di.IsReady}, Label='{di.VolumeLabel}'");
                        if (di.IsReady && string.Equals(di.VolumeLabel, newLabel, StringComparison.OrdinalIgnoreCase))
                        {
                            SafeLog($"[FORMAT_DEBUG] ✓ Label verified successfully!");
                            ok = true;
                            break;
                        }
                    }
                    catch (Exception ex) 
                    { 
                        SafeLog($"[FORMAT_DEBUG] Verification attempt {i+1} failed: {ex.Message}");
                    }
                    Thread.Sleep(1000);
                }

                if (!ok)
                {
                    SafeLog($"[FORMAT_DEBUG] Primary format verification failed; attempting DiskPart fallback...");
                    RunDiskPartFormat(driveLetter, fileSystem, newLabel);

                    // Re-verify after DiskPart
                    ok = false;
                    for (int i = 0; i < 10; i++)
                    {
                        try
                        {
                            var di2 = new DriveInfo(driveRoot);
                            if (di2.IsReady && string.Equals(di2.VolumeLabel, newLabel, StringComparison.OrdinalIgnoreCase))
                            {
                                ok = true;
                                break;
                            }
                        }
                        catch (Exception ex) 
                        { 
                            SafeLog($"[FORMAT_DEBUG] Label verification attempt {i+1} failed: {ex.Message}");
                        }
                        Thread.Sleep(1000);
                    }

                    if (!ok)
                        throw new Exception($"Format verification failed after DiskPart: volume label is not '{newLabel}'.");
                }

                SafeLog($"[FORMAT_DEBUG] Format verified: label={newLabel}");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                SafeLog($"[FORMAT_DEBUG] Win32Exception: {ex.Message} (Code: {ex.ErrorCode})");
                throw;
            }
            catch (Exception ex)
            {
                SafeLog($"[FORMAT_DEBUG] Exception: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        // DiskPart fallback: select volume by drive letter and format
        private void RunDiskPartFormat(string driveLetter, string fileSystem, string newLabel)
        {
            SafeLog($"[DISKPART] Starting fallback format for {driveLetter}:");
            string scriptPath = Path.Combine(Path.GetTempPath(), $"iodd_diskpart_{driveLetter}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            string diskpartScript = string.Join(Environment.NewLine, new[]
            {
                $"select volume {driveLetter}",
                $"format fs={fileSystem} label={newLabel} quick",
                $"assign letter={driveLetter}",
                "exit"
            });

            File.WriteAllText(scriptPath, diskpartScript);
            SafeLog($"[DISKPART] Script: {scriptPath}\n{diskpartScript}");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"Start-Process -FilePath 'diskpart.exe' -ArgumentList '/s \"{scriptPath}\"' -Verb RunAs -Wait\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false
            };

            var p = Process.Start(psi);
            if (p != null)
            {
                SafeLog($"[DISKPART] DiskPart process started (ID={p.Id})");
                p.WaitForExit();
                SafeLog($"[DISKPART] DiskPart process exited with code: {p.ExitCode}");
            }
            else
            {
                SafeLog($"[DISKPART] WARNING: Process.Start returned null");
            }

            try { File.Delete(scriptPath); } 
            catch (Exception ex) { SafeLog($"[DISKPART] Could not delete script {scriptPath}: {ex.Message}"); }
            SafeLog("[DISKPART] Fallback format completed.");
        }

        // Strong reset via PowerShell storage cmdlets (Clear-Disk, Initialize-Disk, New-Partition, Format-Volume)
        private void RunStrongResetPowerShell(string driveLetter, string fileSystem, string newLabel)
        {
            SafeLog($"[STRONG_RESET] Starting strong reset for {driveLetter}: FS={fileSystem}, Label={newLabel}");
            // PowerShell script: resolve disk from drive letter and recreate
            string ps = $@"
$dl = '{driveLetter}'
$part = Get-Partition -DriveLetter $dl -ErrorAction Stop
$disk = Get-Disk -Number $part.DiskNumber -ErrorAction Stop
Clear-Disk -Number $disk.Number -RemoveData -Confirm:$false
Initialize-Disk -Number $disk.Number -PartitionStyle GPT
$newPart = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter
Set-Partition -DiskNumber $disk.Number -PartitionNumber $newPart.PartitionNumber -NewDriveLetter $dl
Format-Volume -DriveLetter $dl -FileSystem {fileSystem} -NewFileSystemLabel {newLabel} -Confirm:$false
";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{ps.Replace("\"", "\\\"")}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = false
            };

            var p = Process.Start(psi);
            if (p == null)
            {
                SafeLog("[STRONG_RESET] FAILED: Process.Start returned null");
                throw new Exception("Failed to start PowerShell for strong reset");
            }
            SafeLog($"[STRONG_RESET] Process started (ID={p.Id}), waiting for exit...");
            p.WaitForExit();
            SafeLog($"[STRONG_RESET] Process exited with code: {p.ExitCode}");
            if (p.ExitCode != 0)
                throw new Exception($"Strong reset failed (exit code {p.ExitCode})");
        }

        // Remove any existing files/folders from destination root after format
        private void CleanDestination(string root)
        {
            SafeLog($"[CLEAN] Removing existing files in {root}");
            try
            {
                var di = new DirectoryInfo(root);
                if (!di.Exists)
                {
                    SafeLog($"[CLEAN] Path not found: {root}");
                    return;
                }

                // Delete files
                foreach (var file in di.GetFiles())
                {
                    try
                    {
                        file.Attributes = FileAttributes.Normal;
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"[CLEAN] File delete failed {file.FullName}: {ex.Message}");
                        throw;
                    }
                }

                // Delete directories
                foreach (var dir in di.GetDirectories())
                {
                    try
                    {
                        dir.Attributes = FileAttributes.Normal;
                        dir.Delete(true);
                    }
                    catch (Exception ex)
                    {
                        SafeLog($"[CLEAN] Dir delete failed {dir.FullName}: {ex.Message}");
                        throw;
                    }
                }

                SafeLog("[CLEAN] Destination is empty");
            }
            catch (Exception ex)
            {
                SafeLog($"[CLEAN] Exception: {ex.Message}");
                throw;
            }
        }

        private readonly record struct DriveItem(string Text, string Root, string Label);

        private sealed class ConfirmFormatDialog : Form
        {
            public ConfirmFormatDialog(string drive, string label)
            {
                Text = "Confirm FORMAT";
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                Width = 600;
                Height = 440;

                var wrap = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(18),
                    ColumnCount = 2,
                    RowCount = 2
                };
                wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
                wrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                wrap.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                wrap.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                Controls.Add(wrap);

                var icon = new Label
                {
                    Text = "!",
                    Dock = DockStyle.Fill,
                    Font = new Font("Segoe UI", 40f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(180, 20, 20),
                    TextAlign = ContentAlignment.TopCenter
                };

                var text = new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    TextAlign = ContentAlignment.TopLeft
                };
                text.Font = new Font("Segoe UI", 11f, FontStyle.Regular);
                text.Text =
                    "This will DELETE ALL data on the destination drive.\n" +
                    "This action CANNOT be undone.\n\n" +
                    $"Drive: {drive}\n" +
                    $"Label: {label}";

                // Make "ADVARSEL" stand out by coloring the first line via simple trick:
                // (we keep it simple: bold + red in a separate label)
                var danger = new Label
                {
                    Text = "WARNING",
                    Dock = DockStyle.Top,
                    ForeColor = Color.FromArgb(180, 20, 20),
                    Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                    Height = 44,
                    TextAlign = ContentAlignment.MiddleLeft
                };

                var textPanel = new Panel { Dock = DockStyle.Fill };
                textPanel.Controls.Add(text);
                textPanel.Controls.Add(danger);

                // push body text down a bit
                text.Padding = new Padding(0, 8, 0, 0);

                wrap.Controls.Add(icon, 0, 0);
                wrap.Controls.Add(textPanel, 1, 0);

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(0),
                    WrapContents = false
                };

                var btnFormat = new Button
                {
                    Text = "FORMAT DISK",
                    Width = 160,
                    Height = 38,
                    BackColor = Color.FromArgb(200, 40, 40),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnFormat.FlatAppearance.BorderSize = 0;
                btnFormat.Click += (_, __) => { DialogResult = DialogResult.OK; Close(); };

                var btnCancel = new Button
                {
                    Text = "Cancel",
                    Width = 120,
                    Height = 36,
                    DialogResult = DialogResult.Cancel
                };

                buttons.Controls.Add(btnFormat); // farlig til hÃ¸jre
                buttons.Controls.Add(btnCancel); // sikker til venstre

                wrap.Controls.Add(buttons, 0, 1);
                wrap.SetColumnSpan(buttons, 2);

                // Safety: Enter/Esc = Cancel (sÃ¥ man ikke kan "next next")
                AcceptButton = btnCancel;
                CancelButton = btnCancel;
            }
        }
    }
}

