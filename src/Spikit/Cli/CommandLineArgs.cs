using Spikit.ViewModels.Onboarding;

namespace Spikit.Cli;

public sealed class CommandLineArgs
{
    private const string DiagnosticsPocFlag = "--diagnostics-poc";

    // Preview del shell de Onboarding (EP-3.1). Mientras el bootstrap gate no esté listo
    // (EP-3.8), este flag es la forma de abrir la window manualmente para validar.
    private const string OnboardingFlag = "--onboarding";

    // EP-11.5 dev — override del tier que el OnboardingViewModel usa para decidir la
    // variante del wizard. Útil para previewar las 3 variantes (byok/trial/pro) sin
    // tocar el tier real en Supabase. En producción nadie lo pasa y default es null
    // (el VM cae a IAuthService.CurrentEntitlement.Tier como siempre).
    //
    // Sintaxis: `--tier=trial` / `--tier=pro` / `--tier=byok` (case-insensitive).
    private const string TierPrefix = "--tier=";

    // EP-10.4: Windows invoca a la app con el URL completo del deep-link como argv[0]
    // cuando se dispara el protocol handler `spikit://`. Detectamos cualquier arg que
    // empiece con el scheme y lo capturamos para dispatch posterior (ver
    // ISpikitUriDispatcher). No validamos acá — el parser lo hace en el dispatcher.
    private const string SpikitUriPrefix = "spikit://";

    public bool DiagnosticsPoc { get; }

    // EP-11.7 — mutable porque tras un logout/login round-trip dentro de la misma sesión
    // del proceso, el flag de la CLI inicial queda "pegado". Sin un consume explícito un
    // user que abrió la app con `--onboarding`, completó el wizard, hizo logout y volvió
    // a loguearse re-entraría al wizard aunque ya esté marcado como completed. App
    // llama ConsumeOnboarding tras pasar por el flow una vez.
    public bool Onboarding { get; private set; }

    // null si --tier no se pasó. Si se pasó pero el valor no parseó a un OnboardingTierVariant
    // conocido, también queda null (mejor ignorar silenciosamente un typo que crashear).
    public OnboardingTierVariant? TierOverride { get; }

    // null si la app no fue lanzada vía protocol handler. Caller del dispatcher chequea
    // null antes de invocar.
    public string? SpikitUri { get; }

    public CommandLineArgs(string[] args)
    {
        DiagnosticsPoc = args.Any(arg =>
            string.Equals(arg, DiagnosticsPocFlag, StringComparison.OrdinalIgnoreCase));

        Onboarding = args.Any(arg =>
            string.Equals(arg, OnboardingFlag, StringComparison.OrdinalIgnoreCase));

        TierOverride = args
            .Select(ParseTierOverride)
            .FirstOrDefault(t => t is not null);

        SpikitUri = args.FirstOrDefault(arg =>
            arg is not null && arg.StartsWith(SpikitUriPrefix, StringComparison.OrdinalIgnoreCase));
    }

    // EP-11.7: tras consumir el flag Onboarding una vez (al pasar por ShowOnboardingWindow
    // en el bootstrap), nullearlo para que un logout/login posterior no re-entre al wizard
    // si el usuario ya completó el onboarding.
    public void ConsumeOnboarding()
    {
        Onboarding = false;
    }

    private static OnboardingTierVariant? ParseTierOverride(string? arg)
    {
        if (arg is null || !arg.StartsWith(TierPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = arg[TierPrefix.Length..];
        return value.ToLowerInvariant() switch
        {
            "byok" => OnboardingTierVariant.Byok,
            "trial" => OnboardingTierVariant.Trial,
            "pro" => OnboardingTierVariant.Pro,
            _ => null,
        };
    }
}
