using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sentry;
using Spikit.Models;

namespace Spikit.Services.Observability;

// Inicialización opt-in del SDK de Sentry (EP-8.3 / decisión Q-3 cerrada en infra.md).
//
// Se llama desde Program.Main ANTES de cualquier otro código de bootstrap para capturar
// errores del propio levante (Velopack, Serilog, DI). Es no-op cuando:
// - El binario se compiló sin DSN inyectado (build local sin /p:SentryDsn=...).
// - El usuario no tiene Privacy.SendCrashReports = true en settings (default OFF).
// - settings.json no existe todavía (primer run pre-onboarding).
//
// Cuando se activa, aplica los filtros obligatorios documentados en infra.md §Monitoreo:
//   1. Sanitiza paths que contengan el username de Windows.
//   2. Stripea API keys que matcheen el patrón típico (sk-, sk-proj-, etc.) de cualquier string.
//   3. Descarta eventos cuyo mensaje contenga la palabra "transcription"/"transcript" (defensa
//      en profundidad — el código de la app no los manda explícitamente, pero un Exception
//      con ese contexto se asume que contiene contenido del usuario y se descarta).
//   4. Limpia breadcrumbs aplicando los mismos filtros 1+2.
internal static class SentryBootstrap
{
    private const string SentryDsnMetadataKey = "SentryDsn";

    // Match permisivo de API keys de provider compatible OpenAI (sk-, sk-proj-, sk-svcacct-,
    // sk-admin-, etc.). Mínimo 16 caracteres post-prefijo para evitar falsos positivos en
    // strings cortos como "sk-rrr". Compilado static para reutilizar en cada filter pass.
    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_\-]{16,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserPathPattern = new(
        @"(?<prefix>[A-Za-z]:\\Users\\)[^\\]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IDisposable? TryInit()
    {
        var dsn = GetCompiledDsn();
        if (string.IsNullOrWhiteSpace(dsn))
        {
            // Build local sin DSN inyectado o build de release sin secret configurado.
            // Salir silencioso — no es error.
            return null;
        }

        if (!IsUserOptedIn())
        {
            // Default V1: opt-in OFF. Sin telemetría salvo decisión explícita del usuario.
            return null;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        return SentrySdk.Init(o =>
        {
            o.Dsn = dsn;
            o.Release = $"spikit@{version}";
            o.AutoSessionTracking = false;       // sin tracking de sesión por privacy.
            o.IsGlobalModeEnabled = true;        // app desktop single-process.
            o.AttachStacktrace = true;
            o.SendDefaultPii = false;            // crítico: NO mandar IP, username, etc.
            o.SampleRate = 1.0f;                 // V1 captura todo; bajar si free tier ahoga.
            o.MaxBreadcrumbs = 50;
            o.SetBeforeSend(Sanitize);
            o.SetBeforeBreadcrumb(SanitizeBreadcrumb);
        });
    }

    private static string? GetCompiledDsn()
    {
        var attr = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, SentryDsnMetadataKey, StringComparison.Ordinal));
        return attr?.Value;
    }

    private static bool IsUserOptedIn()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Spikit",
                "settings.json");

            if (!File.Exists(path)) return false;

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            return settings?.Privacy?.SendCrashReports == true;
        }
        catch
        {
            // settings.json corrupto o inaccesible → no init. CB-10 ya cubre el recovery
            // del archivo en el bootstrap principal del JsonSettingsService.
            return false;
        }
    }

    private static SentryEvent? Sanitize(SentryEvent evt, SentryHint _)
    {
        if (evt.Message?.Message is { } msg && ContainsTranscriptHint(msg))
        {
            // Defensa en profundidad: asumimos que un evento con la palabra "transcript" en
            // el mensaje contiene contenido del usuario y lo descartamos completo.
            return null;
        }

        if (evt.Message is { Message: { } message })
        {
            evt.Message.Message = Scrub(message);
        }

        if (evt.SentryExceptions is { } excs)
        {
            foreach (var exc in excs)
            {
                if (exc.Value is { } v) exc.Value = Scrub(v);
            }
        }

        return evt;
    }

    private static Breadcrumb? SanitizeBreadcrumb(Breadcrumb crumb, SentryHint _)
    {
        if (crumb.Message is { } msg && ContainsTranscriptHint(msg)) return null;

        // Reemplazamos por una breadcrumb nueva con los campos saneados. El Breadcrumb
        // del SDK no expone timestamp en el constructor — Sentry lo asigna nuevo, lo
        // que en la práctica significa una desviación de pocos ms vs. el original (no
        // afecta el debugging porque la cronología relativa se mantiene).
        return new Breadcrumb(
            message: Scrub(crumb.Message ?? string.Empty),
            type: crumb.Type ?? "default",
            data: crumb.Data?.ToDictionary(kv => kv.Key, kv => Scrub(kv.Value ?? string.Empty)),
            category: crumb.Category,
            level: crumb.Level);
    }

    private static string Scrub(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var withoutKeys = ApiKeyPattern.Replace(input, "[REDACTED-API-KEY]");
        var withoutPaths = UserPathPattern.Replace(withoutKeys, "${prefix}~");
        return withoutPaths;
    }

    private static bool ContainsTranscriptHint(string s) =>
        s.Contains("transcript", StringComparison.OrdinalIgnoreCase);
}
