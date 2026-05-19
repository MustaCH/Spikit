namespace Spikit.Cli;

public sealed class CommandLineArgs
{
    private const string DiagnosticsPocFlag = "--diagnostics-poc";

    // Preview del shell de Onboarding (EP-3.1). Mientras el bootstrap gate no esté listo
    // (EP-3.8), este flag es la forma de abrir la window manualmente para validar.
    private const string OnboardingFlag = "--onboarding";

    // EP-10.4: Windows invoca a la app con el URL completo del deep-link como argv[0]
    // cuando se dispara el protocol handler `spikit://`. Detectamos cualquier arg que
    // empiece con el scheme y lo capturamos para dispatch posterior (ver
    // ISpikitUriDispatcher). No validamos acá — el parser lo hace en el dispatcher.
    private const string SpikitUriPrefix = "spikit://";

    public bool DiagnosticsPoc { get; }
    public bool Onboarding { get; }

    // null si la app no fue lanzada vía protocol handler. Caller del dispatcher chequea
    // null antes de invocar.
    public string? SpikitUri { get; }

    public CommandLineArgs(string[] args)
    {
        DiagnosticsPoc = args.Any(arg =>
            string.Equals(arg, DiagnosticsPocFlag, StringComparison.OrdinalIgnoreCase));

        Onboarding = args.Any(arg =>
            string.Equals(arg, OnboardingFlag, StringComparison.OrdinalIgnoreCase));

        SpikitUri = args.FirstOrDefault(arg =>
            arg is not null && arg.StartsWith(SpikitUriPrefix, StringComparison.OrdinalIgnoreCase));
    }
}
