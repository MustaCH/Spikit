namespace Spikit.Cli;

// Decisión pura del modo de startup: dado el set de CLI flags + el flag persistido
// onboardingCompleted, qué ventana inicial corresponde. Vive en Cli/ (junto al
// CommandLineArgs) y NO toca DI ni WPF para que sea unit-testeable.
public static class StartupRouter
{
    public enum StartupMode
    {
        // Herramienta de diagnóstico EP-1 (`--diagnostics-poc`). Bypassea todo lo demás.
        DiagnosticsPoc,

        // OnboardingWindow modal. Se elige cuando el usuario nunca completó el onboarding,
        // o cuando se forzó manualmente con `--onboarding` (override de QA / dev).
        Onboarding,

        // Flujo principal: tray icon + pill flotante + DictationOrchestrator activo,
        // sin ventana persistente visible (Settings se abre on-demand desde el tray).
        MainApp,
    }

    public static StartupMode Decide(CommandLineArgs cli, bool onboardingCompleted)
    {
        ArgumentNullException.ThrowIfNull(cli);

        if (cli.DiagnosticsPoc) return StartupMode.DiagnosticsPoc;

        // `--onboarding` sigue funcionando como override manual (útil para QA / re-test
        // con flag ya en true). Si no está, decidimos por el flag persistido.
        if (cli.Onboarding) return StartupMode.Onboarding;

        return onboardingCompleted ? StartupMode.MainApp : StartupMode.Onboarding;
    }
}
