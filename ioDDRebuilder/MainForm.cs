using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ioDDRebuilder;

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
    private readonly TextBox source = new() { Dock = DockStyle.Fill, ReadOnly = true, PlaceholderText = "Choose a local source folder" };
    private readonly ComboBox destination = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Text" };
    private readonly ComboBox filesystem = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox verify = new() { Text = "Verify copied files with SHA-256", AutoSize = true };
    private readonly Button browse = MakeButton("Browse…"), refresh = MakeButton("Refresh drives"), start = MakeButton("Review rebuild"), cancel = MakeButton("Cancel");
    private readonly Label phase = MakeLabel("Ready"), detail = MakeLabel("Select a source folder and USB destination."), summary = MakeLabel("No files scanned yet.");
    private readonly Label overall = MakeLabel("—"), current = MakeLabel("—"), speed = MakeLabel("—"), eta = MakeLabel("—");
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 24 };
    private readonly ProgressBar fileProgress = new() { Dock = DockStyle.Fill, Height = 20 };
    private readonly string logPath = Path.Combine(Path.GetTempPath(), $"iodd_rebuilder_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.log");
    private bool running;
    private CancellationTokenSource? cancellation;
    private readonly object logLock = new();
    private static readonly string[] ExcludeNames = ["Thumbs.db", "desktop.ini", "iodd_push.ps1", "iodd_push_gui.ps1", "iodd_push_gui.bat", "iodd_push_to_drive.bat", "iodd_push_log.txt"];

    public MainForm()
    {
        Text = "iODD Rebuilder • 1.1";
        Font = new Font("Segoe UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1040, 820);
        MinimumSize = new Size(740, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(246, 248, 251);
        using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ioDDRebuilder.App.ico")
            ?? throw new InvalidOperationException("Embedded application icon is missing."))
        using (var embedded = new Icon(stream)) Icon = (Icon)embedded.Clone();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, AutoScroll = true };
        Controls.Add(layout);
        var title = MakeLabel("Rebuild your iODD library");
        title.Font = new Font(Font.FontFamily, 22, FontStyle.Bold);
        Add(layout, title);
        Add(layout, MakeLabel("Validate → review → format → copy. All destination data will be erased."));
        var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 16, 0, 16) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        void Row(string text, Control value, Control? action = null)
        {
            int row = settings.RowCount++;
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settings.Controls.Add(MakeLabel(text), 0, row);
            settings.Controls.Add(value, 1, row);
            if (action != null) settings.Controls.Add(action, 2, row);
            else settings.SetColumnSpan(value, 2);
        }
        Row("Source", source, browse);
        Row("Destination", destination, refresh);
        filesystem.Items.AddRange(["exFAT", "NTFS", "FAT32"]);
        filesystem.SelectedIndex = 0;
        Row("Format as", filesystem);
        Row("Read-only check", verify);
        Add(layout, settings);
        Add(layout, MakeLabel("Only writable USB disks separate from Windows and the source can pass validation."));
        Add(layout, summary);
        phase.Font = new Font(Font.FontFamily, 14, FontStyle.Bold);
        phase.ForeColor = Color.FromArgb(24, 80, 130);
        Add(layout, phase);
        detail.AutoEllipsis = true;
        Add(layout, detail);
        Add(layout, progress);
        Add(layout, overall);
        Add(layout, fileProgress);
        Add(layout, current);
        Add(layout, speed);
        Add(layout, eta);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 12, 0, 0), WrapContents = true };
        var openLog = MakeButton("Open log");
        var export = MakeButton("Export log…");
        actions.Controls.AddRange([start, cancel, openLog, export]);
        Add(layout, actions);
        cancel.Enabled = false;
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select source library", ShowNewFolderButton = false }; if (dialog.ShowDialog(this) == DialogResult.OK) { source.Text = dialog.SelectedPath; summary.Text = "Click Review rebuild to scan files and check capacity."; } };
        refresh.Click += async (_, _) => await PopulateDrivesAsync();
        start.Click += async (_, _) => await StartCopyAsync();
        cancel.Click += (_, _) => RequestCancel();
        openLog.Click += (_, _) => LogAction(() => { SafeLog("Log opened"); Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { logPath }, UseShellExecute = true }); });
        export.Click += (_, _) => LogAction(() => { using var dialog = new SaveFileDialog { Filter = "Log files|*.log", FileName = Path.GetFileName(logPath) }; if (dialog.ShowDialog(this) == DialogResult.OK) { SafeLog("Log exported"); File.Copy(logPath, dialog.FileName, true); } });
        FormClosing += (_, e) => { if (running) { e.Cancel = true; if (cancellation != null) RequestCancel(); } };
        Shown += async (_, _) => { await PopulateDrivesAsync(); layout.ScrollControlIntoView(title); };
        SafeLog("Application started. No operation starts automatically.");
    }

    private static Button MakeButton(string text) => new() { Text = text, AutoSize = true, MinimumSize = new Size(120, 36), Margin = new Padding(4), UseVisualStyleBackColor = true };
    private static Label MakeLabel(string text) => new() { Text = text, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(4, 7, 4, 7) };
    private static void Add(TableLayoutPanel parent, Control control) { parent.RowStyles.Add(new RowStyle(SizeType.AutoSize)); parent.Controls.Add(control, 0, parent.RowCount++); }
    private void SetRunning(bool value)
    {
        running = value;
        foreach (var control in new Control[] { browse, refresh, start, source, destination, filesystem, verify }) control.Enabled = !value;
        cancel.Enabled = value && cancellation != null;
    }
    private void SetPhase(string text, bool indeterminate = false) { phase.Text = text; progress.Style = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous; SafeLog(text); }
    private void RequestCancel() { cancellation?.Cancel(); cancel.Enabled = false; phase.Text = "Stopping — waiting for the current I/O to return…"; }
    private void LogAction(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Log could not be opened or exported"); } }

    private sealed record DriveItem(string Text, string Root);
    private async Task PopulateDrivesAsync()
    {
        if (running) return;
        SetRunning(true);
        SetPhase("Finding drives…", true);
        try
        {
            var items = await Task.Run(() => DriveInfo.GetDrives().Select(d =>
            {
                try { return d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable
                    ? new DriveItem($"{d.Name}  {d.VolumeLabel}  •  {FormatBytes(d.TotalSize)}  •  {d.DriveFormat}", d.Name) : null; }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
            }).Where(d => d != null).ToArray());
            destination.Items.Clear();
            destination.Items.Add(new DriveItem("Select a drive — USB eligibility is checked before confirmation", ""));
            foreach (var item in items) destination.Items.Add(item!);
            destination.SelectedIndex = 0;
            SetPhase("Ready");
        }
        catch (Exception ex) { SetPhase("Drive discovery failed"); detail.Text = ex.Message; SafeLog(ex.ToString()); }
        finally { progress.Style = ProgressBarStyle.Continuous; SetRunning(false); }
    }

    private static FileInfo[] Scan(string path) => FormatSafety.Scan(path)
        .Where(f => !ExcludeNames.Contains(f.Name, StringComparer.OrdinalIgnoreCase))
        .OrderByDescending(f => f.Extension.ToLowerInvariant() is ".iso" or ".img" or ".vhd" or ".vhdx").ThenByDescending(f => f.Length).ThenBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).ToArray();

    private async Task StartCopyAsync()
    {
        if (running) return;
        if (!Directory.Exists(source.Text) || destination.SelectedItem is not DriveItem drive || drive.Root.Length == 0)
        { MessageBox.Show(this, "Choose an existing source folder and a USB destination.", "Selection required"); return; }
        string src = source.Text, fs = filesystem.Text;
        bool check = verify.Checked, formatAttempted = false;
        SetRunning(true);
        var elapsed = Stopwatch.StartNew();
        try
        {
            SetPhase("Scanning and validating…", true);
            var preview = await Task.Run(() =>
            {
                FormatSafety.CheckSource(src, drive.Root);
                var files = Scan(src);
                var target = FormatSafety.Inspect(drive.Root, src);
                FormatSafety.CheckFiles(files, target.Size, fs);
                return (Files: files, Target: target, Bytes: files.Sum(f => f.Length), Snapshot: files.Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc)).ToArray());
            });
            summary.Text = $"{preview.Files.Length:N0} files • {FormatBytes(preview.Bytes)} source • {FormatBytes(preview.Target.Size)} destination";
            SetPhase("Ready for review");
            using var dialog = new ConfirmFormatDialog(drive.Root, preview.Target.Label, preview.Target.Size);
            dialog.Icon = Icon;
            dialog.AddPreview($"Copy {preview.Files.Length:N0} files ({FormatBytes(preview.Bytes)}) from:\n{src}\nFormat as {fs}, label IODD. SHA-256 verification: {(check ? "enabled" : "disabled")}.");
            if (dialog.ShowDialog(this) != DialogResult.OK) { SetPhase("Cancelled — nothing erased"); return; }
            SetPhase("Revalidating source…", true);
            await Task.Run(() =>
            {
                FormatSafety.CheckSource(src, drive.Root);
                var currentFiles = Scan(src);
                if (!preview.Snapshot.SequenceEqual(currentFiles.Select(f => (f.FullName, f.Length, f.LastWriteTimeUtc))))
                    throw new InvalidOperationException("Source changed since review. Start again.");
                FormatSafety.CheckFiles(currentFiles, preview.Target.Size, fs);
            });
            SetPhase("Formatting — wait for completion; this phase cannot be cancelled", true);
            formatAttempted = true;
            var root = await Task.Run(() =>
            {
                FormatSafety.Format(drive.Root, src, preview.Target, fs);
                return FormatSafety.CopyRoot(preview.Target, FormatSafety.Inspect(drive.Root, src));
            });
            cancellation = new CancellationTokenSource();
            cancel.Enabled = true;
            progress.Value = 0;
            SetPhase("Copying");
            await Task.Run(() => CopySequentialWithProgress(src, root, preview.Files, preview.Bytes, cancellation.Token));
            if (check)
            {
                SetPhase("Verifying SHA-256 checksums…", true);
                await Task.Run(() => VerifyCopies(src, root, preview.Files, cancellation.Token));
            }
            cancellation.Token.ThrowIfCancellationRequested();
            progress.Value = fileProgress.Value = 100;
            SetPhase("Completed");
            detail.Text = $"Copied {preview.Files.Length:N0} files ({FormatBytes(preview.Bytes)}) in {elapsed.Elapsed:g}. " + (check ? "All checksums matched." : "Verification was not selected.");
            eta.Text = "Remaining: 00:00:00";
            SafeLog(detail.Text);
        }
        catch (OperationCanceledException) { SetPhase("Cancelled"); detail.Text = "Destination contains an incomplete library. Keep your source and rebuild again."; }
        catch (Exception ex)
        {
            SetPhase("Failed"); SafeLog(ex.ToString());
            detail.Text = formatAttempted ? "Destination may be erased or incomplete. Keep your source. See the log for details." : "Validation stopped the rebuild. Nothing was erased.";
            MessageBox.Show(this, detail.Text + "\n\n" + ex.Message + "\n\nLog: " + logPath, "Rebuild stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { cancellation?.Dispose(); cancellation = null; progress.Style = ProgressBarStyle.Continuous; SetRunning(false); }
    }

    private void CopySequentialWithProgress(string srcRoot, string destRoot, FileInfo[] files, long totalBytes, CancellationToken token)
    {
        FormatSafety.ValidateVolumePath(destRoot);
        // Re-scan rejects links before any directory is recreated. Never recursively follow new links.
        FormatSafety.Scan(srcRoot);
        foreach (var directory in Directory.EnumerateDirectories(srcRoot, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        { token.ThrowIfCancellationRequested(); Directory.CreateDirectory(Path.Combine(destRoot, Path.GetRelativePath(srcRoot, directory))); }
        long done = 0;
        var watch = Stopwatch.StartNew();
        var throttle = Stopwatch.StartNew();
        byte[] buffer = new byte[1024 * 1024];
        for (int i = 0; i < files.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var file = files[i];
            var relative = Path.GetRelativePath(srcRoot, file.FullName);
            var output = Path.Combine(destRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            long copied = 0;
            int number = i + 1;
            FormatSafety.CheckSourcePath(Path.GetDirectoryName(file.FullName)!);
            if ((File.GetAttributes(file.FullName) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source became a link: " + relative);
            using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
            if (input.Length != file.Length) throw new IOException("Source changed: " + relative);
            using var writer = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan);
            void Report()
            {
                long bytes = done + copied, fileBytes = copied;
                double bps = bytes / Math.Max(0.001, watch.Elapsed.TotalSeconds);
                var remaining = bps > 0 ? TimeSpan.FromSeconds(Math.Min(TimeSpan.MaxValue.TotalSeconds - 1, Math.Max(0, totalBytes - bytes) / bps)).ToString(@"dd\.hh\:mm\:ss") : "estimating…";
                BeginInvoke(new Action(() =>
                {
                    progress.Value = totalBytes > 0 ? Math.Clamp((int)(100.0 * bytes / totalBytes), 0, 100) : 100;
                    fileProgress.Value = file.Length > 0 ? Math.Clamp((int)(100.0 * fileBytes / file.Length), 0, 100) : 100;
                    detail.Text = relative;
                    overall.Text = $"Overall: {progress.Value}% • {FormatBytes(bytes)} / {FormatBytes(totalBytes)} • file {number:N0} / {files.Length:N0}";
                    current.Text = $"Current file: {fileProgress.Value}% • {FormatBytes(fileBytes)} / {FormatBytes(file.Length)}";
                    speed.Text = $"Average transfer speed: {FormatBytes((long)bps)}/s";
                    eta.Text = "Estimated remaining: " + remaining;
                }));
            }
            int read;
            while ((read = input.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(buffer, 0, read); copied += read;
                if (throttle.ElapsedMilliseconds >= 200) { Report(); throttle.Restart(); }
            }
            writer.Flush(true);
            if (copied != file.Length) throw new IOException("Source changed while copying: " + relative);
            Report(); done += copied;
            SafeLog($"Copied {relative}: {copied} bytes");
        }
    }

    private void VerifyCopies(string srcRoot, string destRoot, FileInfo[] files, CancellationToken token)
    {
        FormatSafety.ValidateVolumePath(destRoot);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(srcRoot, file.FullName);
            BeginInvoke(new Action(() => detail.Text = "Verifying: " + relative));
            FileVerification.Verify(file.FullName, Path.Combine(destRoot, relative), token);
            SafeLog("Verified " + relative);
        }
    }
    private void SafeLog(string message) { lock (logLock) { try { File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch (IOException) { Debug.WriteLine(message); } catch (UnauthorizedAccessException) { Debug.WriteLine(message); } } }
    private static string FormatBytes(long bytes)
    {
        double value = bytes; string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; int index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.0} {units[index]}";
    }

    private sealed class ConfirmFormatDialog : Form
    {
        private readonly Label preview = MakeLabel("");
        public void AddPreview(string text) => preview.Text = text;
        public ConfirmFormatDialog(string drive, string label, long size = 0)
        {
            Text = "Confirm destination erasure";
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(640, 480); MinimumSize = new Size(540, 460);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), AutoScroll = true, ColumnCount = 1 };
            Controls.Add(layout);
            var warning = MakeLabel("All destination data will be permanently erased"); warning.ForeColor = Color.Firebrick; warning.Font = new Font(Font, FontStyle.Bold);
            Add(layout, warning);
            Add(layout, MakeLabel($"Drive: {drive}\nLabel: {label}\nSize: {FormatBytes(size)}\n\nThis action cannot be undone. Check your backups before continuing."));
            Add(layout, preview);
            var acknowledge = new CheckBox { Text = "I checked this destination and backed up its contents.", AutoSize = true, Margin = new Padding(4, 16, 4, 16) };
            Add(layout, acknowledge);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            var back = MakeButton("Cancel"); back.DialogResult = DialogResult.Cancel;
            var erase = MakeButton("FORMAT DISK"); erase.Enabled = false; erase.ForeColor = Color.Firebrick;
            acknowledge.CheckedChanged += (_, _) => erase.Enabled = acknowledge.Checked;
            erase.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.AddRange([back, erase]); Add(layout, buttons);
            AcceptButton = back; CancelButton = back;
        }
    }
}
