using ioDDRebuilder;

try
{
    if (args.Length != 2)
        throw new InvalidOperationException("Usage: ioDDRebuilder.Formatter.exe F:\\ D:\\source");
    FormatSafety.CheckSource(args[1], args[0]);
    var files = FormatSafety.Scan(args[1]);
    var target = FormatSafety.Inspect(args[0], args[1]);
    FormatSafety.CheckFiles(files, target.Size, "exFAT");
    Console.WriteLine($"WARNING: ALL DATA on {args[0]} ({target.Size} bytes) will be erased. No copy is performed by this helper.");
    Console.Write($"Type ERASE {args[0]} to continue: ");
    if (Console.IsInputRedirected || Console.ReadLine() != $"ERASE {args[0]}")
        throw new InvalidOperationException("Formatting cancelled.");
    FormatSafety.Format(args[0], args[1], target, "exFAT");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
