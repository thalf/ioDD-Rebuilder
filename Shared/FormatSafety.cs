using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ioDDRebuilder;

// Shared by both entry points. Unknown/ambiguous storage topology is rejected.
internal static class FormatSafety
{
    internal sealed record Target(string DiskId, string VolumeId, long Size);
    internal static string Letter(string root)
    {
        if (root.Length != 3 || root[1] != ':' || root[2] != '\\' ||
            char.ToUpperInvariant(root[0]) is < 'A' or > 'Z')
            throw new InvalidOperationException("Select a local drive root (for example F:\\).");
        return char.ToUpperInvariant(root[0]).ToString();
    }

    internal static void CheckSource(string source, string destination)
    {
        Letter(destination);
        if (Path.GetPathRoot(AppContext.BaseDirectory)!.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the application from a different drive.");
        var full = Path.GetFullPath(source);
        var root = Path.GetPathRoot(full)!;
        Letter(root); // UNC paths are deliberately unsupported.
        if (root.Equals(destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and destination must be on different disks.");
        for (var dir = new DirectoryInfo(full); dir != null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Source paths cannot contain links or mounted folders.");
    }

    internal static FileInfo[] Scan(string source)
    {
        var files = new List<FileInfo>();
        void Walk(DirectoryInfo dir)
        {
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Source contains a link or mounted folder: " + item.FullName);
                if (item is DirectoryInfo child) Walk(child);
                else
                {
                    using var stream = File.Open(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    files.Add(new FileInfo(item.FullName));
                }
            }
        }
        Walk(new DirectoryInfo(source));
        return files.ToArray();
    }

    internal static void CheckFiles(FileInfo[] files, long capacity, string fs)
    {
        if (fs is not ("exFAT" or "NTFS" or "FAT32"))
            throw new InvalidOperationException("Unsupported filesystem.");
        if (files.Length == 0) throw new InvalidOperationException("No files to copy.");
        if (fs == "FAT32" && (capacity > 32L * 1024 * 1024 * 1024 ||
            files.Any(f => f.Length > uint.MaxValue)))
            throw new InvalidOperationException("FAT32 requires a volume at most 32 GiB and files smaller than 4 GiB.");
        if (files.Sum(f => f.Length) > capacity - Math.Max(64L * 1024 * 1024, capacity / 100))
            throw new InvalidOperationException("Source is too large; leave space for filesystem overhead.");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    internal static string GuardScript(string destination, string source)
    {
        var dl = Letter(destination);
        var sl = Letter(Path.GetPathRoot(Path.GetFullPath(source))!);
        return $$"""
$ErrorActionPreference = 'Stop'
$p = @(Get-Partition -DriveLetter '{{dl}}' -ErrorAction Stop)
$s = @(Get-Partition -DriveLetter '{{sl}}' -ErrorAction Stop)
if ($p.Count -ne 1 -or $s.Count -ne 1) { throw 'Ambiguous partition mapping' }
$d = Get-Disk -Number $p[0].DiskNumber -ErrorAction Stop
$v = Get-Volume -DriveLetter '{{dl}}' -ErrorAction Stop
$system = Get-Partition -DriveLetter $env:SystemDrive.Substring(0,1) -ErrorAction Stop
if ($d.IsBoot -or $d.IsSystem -or $d.IsOffline -or $d.IsReadOnly -or
    $d.Number -eq $system.DiskNumber -or $d.Number -eq $s[0].DiskNumber -or
    $d.BusType -ne 'USB' -or $v.DriveType -notin @('Fixed','Removable') -or
    [string]::IsNullOrWhiteSpace($d.UniqueId) -or [string]::IsNullOrWhiteSpace($v.UniqueId)) {
    throw 'Destination must be a writable USB disk separate from Windows and source'
}
""";
    }

    internal static Target Inspect(string destination, string source)
    {
        var json = Run(GuardScript(destination, source) + "\n" + """
[pscustomobject]@{ DiskId = [string]$d.UniqueId; VolumeId = [string]$v.UniqueId; Size = [long]$v.Size } | ConvertTo-Json -Compress
""", false);
        return JsonSerializer.Deserialize<Target>(json.Trim()) ??
            throw new InvalidOperationException("Cannot identify destination.");
    }

    internal static string BuildFormatScript(string destination, string source, Target expected, string fs)
    {
        if (fs is not ("exFAT" or "NTFS" or "FAT32")) throw new InvalidOperationException("Unsupported filesystem.");
        return GuardScript(destination, source) + "\n" + $$"""
if ($d.UniqueId -ne {{Quote(expected.DiskId)}} -or $v.UniqueId -ne {{Quote(expected.VolumeId)}} -or $v.Size -ne {{expected.Size}}) {
    throw 'Destination changed since confirmation'
}
$v | Format-Volume -FileSystem {{fs}} -NewFileSystemLabel IODD -Confirm:$false -Force -ErrorAction Stop | Out-Null
$after = Get-Volume -DriveLetter '{{Letter(destination)}}' -ErrorAction Stop
if ($after.FileSystem -ne '{{fs}}' -or $after.FileSystemLabel -ne 'IODD') { throw 'Format verification failed' }
""";
    }

    internal static void Format(string destination, string source, Target expected, string fs) =>
        Run(BuildFormatScript(destination, source, expected, fs), true);

    private static string Run(string script, bool elevated)
    {
        // EncodedCommand avoids command-line quoting and writable elevated script files.
        var wrapped = "try {\n" + script + "\nexit 0\n} catch { Write-Error $_; exit 1 }";
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped)),
            UseShellExecute = elevated,
            Verb = elevated ? "runas" : "",
            RedirectStandardOutput = !elevated,
            RedirectStandardError = !elevated,
            CreateNoWindow = !elevated
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start storage validation.");
        Task<string>? output = elevated ? null : process.StandardOutput.ReadToEndAsync();
        Task<string>? error = elevated ? null : process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Storage validation or formatting failed. " + error?.GetAwaiter().GetResult());
        return output?.GetAwaiter().GetResult() ?? "";
    }
}


