namespace Spikit.Cli;

public sealed class CommandLineArgs
{
    private const string DiagnosticsPocFlag = "--diagnostics-poc";

    public bool DiagnosticsPoc { get; }

    public CommandLineArgs(string[] args)
    {
        DiagnosticsPoc = args.Any(arg =>
            string.Equals(arg, DiagnosticsPocFlag, StringComparison.OrdinalIgnoreCase));
    }
}
