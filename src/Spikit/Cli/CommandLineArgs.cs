namespace Spikit.Cli;

public sealed class CommandLineArgs
{
    private const string DiagnosticsPocFlag = "--diagnostics-poc";

    // Preview del shell de Onboarding (EP-3.1). Mientras el bootstrap gate no esté listo
    // (EP-3.8), este flag es la forma de abrir la window manualmente para validar.
    private const string OnboardingFlag = "--onboarding";

    public bool DiagnosticsPoc { get; }
    public bool Onboarding { get; }

    public CommandLineArgs(string[] args)
    {
        DiagnosticsPoc = args.Any(arg =>
            string.Equals(arg, DiagnosticsPocFlag, StringComparison.OrdinalIgnoreCase));

        Onboarding = args.Any(arg =>
            string.Equals(arg, OnboardingFlag, StringComparison.OrdinalIgnoreCase));
    }
}
