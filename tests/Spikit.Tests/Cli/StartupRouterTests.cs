using Spikit.Cli;

namespace Spikit.Tests.Cli;

// Tests puros del bootstrap gate. Cubren cada combinación de CLI flags + flag persistido
// onboardingCompleted + estado de auth. La integration "muestra LoginWindow/Onboarding/etc"
// se cubre indirectamente vía el branch del StartupMode — instanciar las windows reales
// en xUnit requeriría un host WPF y no aporta a la confianza por encima de este test puro.
public class StartupRouterTests
{
    private static CommandLineArgs ParseArgs(params string[] args) => new(args);

    // ====== --diagnostics-poc bypassea TODO, incluido el gate de auth ======

    [Fact]
    public void DiagnosticsPoc_flag_wins_over_everything()
    {
        var cli = ParseArgs("--diagnostics-poc");

        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc,
            StartupRouter.Decide(cli, onboardingCompleted: true, isLoggedIn: true));
        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: true));
        // POC bypassea incluso el gate de auth — sirve también sin sesión.
        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: false));
    }

    [Fact]
    public void DiagnosticsPoc_wins_over_Onboarding_flag()
    {
        var cli = ParseArgs("--diagnostics-poc", "--onboarding");

        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: true));
    }

    // ====== Gate de auth (EP-11.4) — sin sesión → LoginRequired ======

    [Fact]
    public void Not_logged_in_routes_to_LoginRequired_regardless_of_onboarding_completed()
    {
        var cli = ParseArgs();

        Assert.Equal(StartupRouter.StartupMode.LoginRequired,
            StartupRouter.Decide(cli, onboardingCompleted: true, isLoggedIn: false));
        Assert.Equal(StartupRouter.StartupMode.LoginRequired,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: false));
    }

    [Fact]
    public void Onboarding_flag_does_not_bypass_login_gate()
    {
        // EP-11.4 — `--onboarding` requiere sesión activa porque el step Provider
        // del BYOK necesita el JWT para validar contra el backend.
        var cli = ParseArgs("--onboarding");

        Assert.Equal(StartupRouter.StartupMode.LoginRequired,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: false));
        Assert.Equal(StartupRouter.StartupMode.LoginRequired,
            StartupRouter.Decide(cli, onboardingCompleted: true, isLoggedIn: false));
    }

    // ====== Con sesión activa: routing por --onboarding + OnboardingCompleted ======

    [Fact]
    public void Logged_in_with_onboarding_flag_routes_to_Onboarding_even_when_completed()
    {
        // Override manual para QA / re-test con flag ya en true. Requiere sesión.
        var cli = ParseArgs("--onboarding");

        Assert.Equal(StartupRouter.StartupMode.Onboarding,
            StartupRouter.Decide(cli, onboardingCompleted: true, isLoggedIn: true));
        Assert.Equal(StartupRouter.StartupMode.Onboarding,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: true));
    }

    [Fact]
    public void Logged_in_no_flags_and_not_completed_routes_to_Onboarding()
    {
        // Primer arranque post-login del usuario: sesión válida pero nunca completó
        // onboarding → wizard (variante por tier la decide EP-11.5).
        var cli = ParseArgs();

        Assert.Equal(StartupRouter.StartupMode.Onboarding,
            StartupRouter.Decide(cli, onboardingCompleted: false, isLoggedIn: true));
    }

    [Fact]
    public void Logged_in_no_flags_and_completed_routes_to_MainApp()
    {
        // Happy path post-onboarding: tray icon + pill + orchestrator activos, sin
        // ventana persistente visible.
        var cli = ParseArgs();

        Assert.Equal(StartupRouter.StartupMode.MainApp,
            StartupRouter.Decide(cli, onboardingCompleted: true, isLoggedIn: true));
    }

    // ====== Defensivos ======

    [Fact]
    public void Decide_throws_on_null_cli()
    {
        Assert.Throws<ArgumentNullException>(() =>
            StartupRouter.Decide(null!, onboardingCompleted: true, isLoggedIn: true));
    }

}
