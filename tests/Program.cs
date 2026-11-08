using ioDDRebuilder;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;

internal static class Tests
{
    private static int passed;
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { passed++; return; }
        throw new Exception("Expected rejection");
    }
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        foreach (var bad in new[] { "", "C:", @"C:\folder", @"\\server\share", "é:\\", "F;Remove-Item" })
            Reject(() => FormatSafety.Letter(bad));
        if (FormatSafety.Letter(@"f:\") != "F") throw new Exception("Normalization");
        passed++;
        Reject(() => FormatSafety.CheckSource(@"F:\images", @"F:\"));
        Reject(() => FormatSafety.CheckSource(@"\\server\share", @"F:\"));
        Reject(() => FormatSafety.CheckFiles([], long.MaxValue, "exFAT"));
        Reject(() => FormatSafety.CheckFiles([], long.MaxValue, "injection"));
        var confirmed = new FormatSafety.Target("disk", "volume", 1024, "CURRENT USB",
            @"\\?\Volume{11111111-1111-1111-1111-111111111111}\");
        foreach (var badPath in new[] { "", @"F:\", @"\\server\share\", @"\\?\Volume{invalid}\",
            @"\\?\Volume{11111111-1111-1111-1111-111111111111}\subfolder" })
            Reject(() => FormatSafety.ValidateVolumePath(badPath));
        // A label change (including the format label IODD) must not change the pinned destination.
        if (FormatSafety.CopyRoot(confirmed, confirmed with { Label = "IODD" }) != confirmed.VolumePath)
            throw new Exception("Copy must use the confirmed GUID, never a drive letter.");
        passed++;
        Reject(() => FormatSafety.CopyRoot(confirmed, confirmed with { DiskId = "replacement" }));
        Reject(() => FormatSafety.CopyRoot(confirmed, confirmed with { VolumeId = "replacement" }));
        // Formatting may change the filesystem-reported usable size without changing the volume.
        if (FormatSafety.CopyRoot(confirmed, confirmed with { Size = 1000 }) != confirmed.VolumePath)
            throw new Exception("A changed filesystem size must not reject the same volume.");
        passed++;
        Reject(() => FormatSafety.CopyRoot(confirmed, confirmed with {
            VolumePath = @"\\?\Volume{22222222-2222-2222-2222-222222222222}\" }));
        Reject(() => FormatSafety.CopyRoot(confirmed, confirmed with { VolumePath = @"F:\" }));
        var currentMetadata = System.Text.Json.JsonSerializer.Deserialize<FormatSafety.Target>(
            """{"DiskId":"disk","VolumeId":"volume","Size":1024,"Label":"CURRENT USB","VolumePath":""}""")!;
        if (currentMetadata.Label != "CURRENT USB" || currentMetadata.Size != 1024)
            throw new Exception("Confirmation must receive freshly inspected metadata.");
        passed++;
        var dialogType = typeof(MainForm).GetNestedType("ConfirmFormatDialog", BindingFlags.NonPublic)!;
        using (var dialog = (Form)Activator.CreateInstance(dialogType, @"F:\", confirmed.Label, confirmed.Size)!)
        {
            IEnumerable<Control> Descendants(Control parent) =>
                parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
            var body = Descendants(dialog).Select(control => control.Text);
            if (!body.Any(text => text.Contains("Label: CURRENT USB") && text.Contains("Size:")))
                throw new Exception("Confirmation must show inspected label and capacity.");
            passed++;
        }
        using (var form = new MainForm())
        {
            var copy = typeof(MainForm).GetMethod("CopySequentialWithProgress", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                copy.Invoke(form, [@"D:\images", @"F:\", Array.Empty<FileInfo>(), 0L, CancellationToken.None]);
                throw new Exception("Copy worker accepted a mutable drive-letter destination.");
            }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException)
            {
                passed++;
            }
        }
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, "image.iso"), "test");
            var files = FormatSafety.Scan(temp);
            if (files.Length != 1) throw new Exception("Scan");
            FormatSafety.CheckFiles(files, 1024L * 1024 * 1024, "exFAT"); passed++;
            Reject(() => FormatSafety.CheckFiles(files, 1, "NTFS"));
            Reject(() => FormatSafety.CheckFiles(files, 33L * 1024 * 1024 * 1024, "FAT32"));
            using (var sparse = File.Create(Path.Combine(temp, "large.iso"))) sparse.SetLength(4294967296);
            Reject(() => FormatSafety.CheckFiles(FormatSafety.Scan(temp), 8L * 1024 * 1024 * 1024, "FAT32"));
            File.Delete(Path.Combine(temp, "large.iso"));
            Directory.CreateSymbolicLink(Path.Combine(temp, "link"), temp);
            Reject(() => FormatSafety.Scan(temp));
            Reject(() => FormatSafety.CheckSource(Path.Combine(temp, "link"), @"Z:\"));
            Directory.Delete(Path.Combine(temp, "link"));
            var script = FormatSafety.BuildFormatScript(@"F:\", @"D:\images",
                new FormatSafety.Target("disk'id", "volume'id", 1024, "CURRENT USB", @"\\?\Volume{11111111-1111-1111-1111-111111111111}\"), "exFAT");
            if (!script.Contains("'disk''id'") || !script.Contains("$d.IsBoot") ||
                !script.Contains("$d.IsSystem") || !script.Contains("$system.DiskNumber") ||
                !script.Contains("$s[0].DiskNumber") || !script.Contains("$d.BusType") ||
                script.IndexOf("Destination changed") > script.IndexOf("| Format-Volume") ||
                script.Contains("Clear-Disk") || script.Contains("diskpart"))
                throw new Exception("Elevated guard regression");
            File.WriteAllText(Path.Combine(args[0], "format-script-for-parser.ps1"), script); passed++;
        }
        finally { Directory.Delete(temp, true); }
        if (args.Contains("--screenshots"))
        {

            using var main = new MainForm();
            Capture(main, Path.Combine(args[0], "main.png"));

            using var dialog = (Form)Activator.CreateInstance(dialogType, @"F:\", "EXAMPLE USB DRIVE", 128L * 1024 * 1024 * 1024)!;
            Capture(dialog, Path.Combine(args[0], "confirmation.png"));
        }
        Console.WriteLine($"PASS: {passed} non-destructive safety checks. No formatting executed.");
    }
    private static void Capture(Form form, string path)
    {
        form.Show();
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path);
        form.Hide();
    }
}
