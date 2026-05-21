namespace Spikit.Cli;

// Decisión pura del modo de startup: dado el set de CLI flags + el flag persistido
// onboardingCompleted + estado de auth, qué ventana inicial corresponde. Vive en Cli/
// (junto al CommandLineArgs) y NO toca DI ni WPF para que sea unit-testeable.
public static class StartupRouter
{
    public enum StartupMode
    {
        // Herramienta de diagnóstico EP-1 (`--diagnostics-poc`). Bypassea todo lo demás
        // — incluido el gate de auth. Tiene sentido porque la POC no requiere sesión.
        DiagnosticsPoc,

        // Gate de auth (EP-11.4 / ADR-0008): no hay sesión activa al startup. Mostramos
        // LoginWindow como única UI visible y no inicializamos tray/hotkey/main shell.
        // El re-routing post-login lo decide App.xaml.cs al cerrar la LoginWindow.
        LoginRequired,

        // OnboardingWindow modal. Se elige cuando el usuario nunca completó el onboarding,
        // o cuando se forzó manualmente con `--onboarding` (override de QA / dev). Desde
        // EP-11.4 requiere sesión activa — sin ella, el step Provider del BYOK no puede
        // testear la key contra el backend y el onboarding Trial/Pro no sabe qué tier
        // mostrar.
        Onboarding,

        // Flujo principal: tray icon + pill flotante + DictationOrchestrator activo,
        // sin ventana persistente visible (Settings se abre on-demand desde el tray).
        MainApp,
    }

    // Orden de evaluación (importante — el comentario es load-bearing):
    //   1. --diagnostics-poc gana sobre todo (bypassea auth, es herramienta de debug).
    //   2. Sin sesión activa → LoginRequired (ignora --onboarding también; sin sesión
    //      no se puede testear el provider step ni decidir variante por tier).
    //   3. --onboarding override fuerza el wizard incluso con OnboardingCompleted=true
    //      (útil para QA / re-test). Requiere sesión activa (filtrado por el paso 2).
    //   4. Sin override → si nunca completó onboarding, mostrar wizard; sino, main shell.
    public static StartupMode Decide(CommandLineArgs cli, bool onboardingCompleted, bool isLoggedIn)
    {
        ArgumentNullException.ThrowIfNull(cli);

        if (cli.DiagnosticsPoc) return StartupMode.DiagnosticsPoc;

        // Gate de auth pre-EP-11.4. Si no hay sesión, lo único visible es LoginWindow —
        // ni siquiera el override de --onboarding lo saltea (la app necesita un JWT
        // válido para que el provider step funcione contra byok_whitelist).
        if (!isLoggedIn) return StartupMode.LoginRequired;

        // `--onboarding` sigue funcionando como override manual (útil para QA / re-test
        // con flag ya en true).
        if (cli.Onboarding) return StartupMode.Onboarding;

        return onboardingCompleted ? StartupMode.MainApp : StartupMode.Onboarding;
    }
}
