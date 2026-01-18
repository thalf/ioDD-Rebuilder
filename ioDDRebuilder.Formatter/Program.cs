using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ioDDRebuilder.Formatter
{
    class Program
    {
        static int Main(string[] args)
        {
            try
            {
                // Get drive letter from args (e.g., "F" or "F:")
                string? driveLetter = args.FirstOrDefault();
                if (string.IsNullOrEmpty(driveLetter))
                {
                    Console.Error.WriteLine("Usage: ioDDRebuilder.Formatter.exe <drive_letter>");
                    Console.Error.WriteLine("Example: ioDDRebuilder.Formatter.exe F");
                    return 1;
                }

                // Clean and normalize drive letter
                driveLetter = driveLetter.Trim().TrimEnd(':').ToUpperInvariant();
                if (driveLetter.Length != 1 || !char.IsLetter(driveLetter[0]))
                {
                    Console.Error.WriteLine("Invalid drive letter. Must be A-Z.");
                    return 1;
                }

                string driveRoot = $"{driveLetter}:\\";

                // Verify drive exists
                if (!DriveInfo.GetDrives().Any(d => d.Name.StartsWith(driveLetter)))
                {
                    Console.Error.WriteLine($"Drive {driveLetter}: not found.");
                    return 1;
                }

                Console.WriteLine($"Formatting drive {driveLetter}: as exFAT with label IODD...");

                // Use PowerShell to format (requires admin)
                string powerShellCmd = $@"
$volume = Get-Volume -DriveLetter {driveLetter} -ErrorAction Stop
$volume | Format-Volume -FileSystem exFAT -NewFileSystemLabel IODD -Confirm:$false
Write-Host 'Format complete'
";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{powerShellCmd.Replace("\"", "\\\"")}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Console.Error.WriteLine("Failed to start PowerShell.");
                        return 1;
                    }

                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();

                    p.WaitForExit();

                    if (!string.IsNullOrEmpty(output))
                        Console.WriteLine(output);
                    if (!string.IsNullOrEmpty(error))
                        Console.Error.WriteLine(error);

                    if (p.ExitCode != 0)
                    {
                        Console.Error.WriteLine($"PowerShell exited with code {p.ExitCode}");
                        return p.ExitCode;
                    }
                }

                Console.WriteLine("Format successful.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Console.Error.WriteLine(ex.StackTrace);
                return 1;
            }
        }
    }
}
