using Spikit.Cli;

namespace Spikit.Tests.Cli;

// Tests puros del bootstrap gate. Cubren cada combinación de CLI flags + flag persistido
// onboardingCompleted. La integration "muestra OnboardingWindow" se cubre indirectamente
// vía el branch de StartupMode.Onboarding — instanciar la window real en xUnit requeriría
// un host WPF y no aporta a la confianza por encima de este test puro.
public class StartupRouterTests
{
    private static CommandLineArgs ParseArgs(params string[] args) => new(args);

    [Fact]
    public void DiagnosticsPoc_flag_wins_over_everything()
    {
        var cli = ParseArgs("--diagnostics-poc");

        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc, StartupRouter.Decide(cli, onboardingCompleted: true));
        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc, StartupRouter.Decide(cli, onboardingCompleted: false));
    }

    [Fact]
    public void Onboarding_flag_forces_onboarding_even_when_completed()
    {
        // Override manual para QA / re-test con flag ya en true.
        var cli = ParseArgs("--onboarding");

        Assert.Equal(StartupRouter.StartupMode.Onboarding, StartupRouter.Decide(cli, onboardingCompleted: true));
    }

    [Fact]
    public void DiagnosticsPoc_wins_over_Onboarding_flag()
    {
        var cli = ParseArgs("--diagnostics-poc", "--onboarding");

        Assert.Equal(StartupRouter.StartupMode.DiagnosticsPoc, StartupRouter.Decide(cli, onboardingCompleted: false));
    }

    [Fact]
    public void No_flags_and_not_completed_routes_to_Onboarding()
    {
        // Caso primer-arranque del usuario: no completó nunca el onboarding → wizard.
        var cli = ParseArgs();

        Assert.Equal(StartupRouter.StartupMode.Onboarding, StartupRouter.Decide(cli, onboardingCompleted: false));
    }

    [Fact]
    public void No_flags_and_completed_routes_to_MainApp()
    {
        // Caso happy path post-onboarding: pill + orchestrator + MainWindow directo.
        var cli = ParseArgs();

        Assert.Equal(StartupRouter.StartupMode.MainApp, StartupRouter.Decide(cli, onboardingCompleted: true));
    }

    [Fact]
    public void Decide_throws_on_null_cli()
    {
        Assert.Throws<ArgumentNullException>(() => StartupRouter.Decide(null!, onboardingCompleted: true));
    }
}
