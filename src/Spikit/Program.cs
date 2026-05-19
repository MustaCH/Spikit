using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Spikit.Cli;
using Spikit.Services.Audio;
using Spikit.Services.Auth;
using Spikit.Services.Autostart;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Observability;
using Spikit.Services.Onboarding;
using Spikit.Services.Orchestration;
using Spikit.Services.PillPosition;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.SingleInstance;
using Spikit.Services.Theme;
using Spikit.Services.Toast;
using Spikit.Services.Tray;
using Spikit.Services.Transcription;
using Spikit.Services.Velopack;
using Spikit.ViewModels;
using Spikit.ViewModels.Onboarding;
using Spikit.ViewModels.Settings;
using Spikit.Views;
using Spikit.Views.Diagnostics;
using Spikit.Views.Onboarding;
using Spikit.Views.Settings;
using Velopack;

namespace Spikit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Sentry primero (EP-8.3 / Q-3): se inicializa solo si hay DSN compilado y el
        // usuario tiene Privacy.SendCrashReports = true. Cuando es no-op (default V1),
        // el costo es leer settings.json una vez. Ver Services/Observability/SentryBootstrap.
        // Mantenido vivo durante toda la sesión vía using en Main → flush al exit.
        using var sentry = SentryBootstrap.TryInit();

        // Velopack hooks (EP-8.3 / Q-2): si el binario fue lanzado con args internos de
        // Velopack (--veloapp-install, --veloapp-uninstall, --veloapp-firstrun, etc.) los
        // procesa y termina el proceso ahí. En lanzamientos normales sigue al flow regular.
        // Debe llamarse antes de cualquier otra inicialización para que los hooks tengan
        // efecto antes de que la app intente abrir ventanas o tocar el filesystem.
        // Velopack inspecciona Environment.GetCommandLineArgs() internamente — no se le
        // pasan los `args` del Main como parámetro.
        //
        // EP-10.4 / 3.1 — registro del protocol handler `spikit://`:
        // - OnAfterInstallFastCallback: primera instalación → escribe HKCU\Software\Classes\spikit.
        // - OnAfterUpdateFastCallback: cada update → re-escribe porque el path del .exe
        //   cambia con la versión (Velopack instala en .../app-X.Y.Z/Spikit.exe).
        // - OnBeforeUninstallFastCallback: limpia la key antes de borrar archivos.
        // Los FastCallbacks tienen 15-30s budget y llaman Environment.Exit() después, así
        // que Serilog NO está disponible — el handler usa su propio logger a archivo.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => SpikitProtocolHandler.Register())
            .OnAfterUpdateFastCallback(_ => SpikitProtocolHandler.Register())
            .OnBeforeUninstallFastCallback(_ => SpikitProtocolHandler.Unregister())
            .Run();

        ConfigureSerilog();

        // CLI args parseados antes del single-instance gate: si la app fue lanzada con
        // un deep-link `spikit://...` (Windows abre la app via protocol handler) y ya
        // hay una instancia corriendo, la segunda forwardea el URI exacto a la primera
        // en lugar del default OPEN_SETTINGS. Ver SingleInstanceGuard.TryAcquire(uri).
        var cliArgs = new CommandLineArgs(args);

        // Single-instance gate (RN-9 / CB-11): se evalúa antes de levantar el host de DI
        // para evitar inicializar tray, hotkey, audio, etc. en una segunda instancia que
        // está condenada a salir. El guard pasa al DI como singleton sólo si somos
        // primary (o degradación de zombie); en modo SecondaryNotified retornamos antes.
        var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddSerilog(Log.Logger));
        var instanceGuard = new SingleInstanceGuard(
            SingleInstanceOptions.Default,
            bootstrapLoggerFactory.CreateLogger<SingleInstanceGuard>());
        var acquisition = instanceGuard.TryAcquire(forwardedUri: cliArgs.SpikitUri);
        if (acquisition == SingleInstanceAcquisition.SecondaryNotified)
        {
            instanceGuard.Dispose();
            Log.CloseAndFlush();
            return 0;
        }

        try
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddSingleton(cliArgs);
                    // El guard se mantiene vivo durante toda la sesión: el listener IPC
                    // sigue corriendo y App.xaml.cs se suscribe a OpenRequested. El DI
                    // dispara su Dispose en OnExit (ver App.OnExit).
                    services.AddSingleton<ISingleInstanceGuard>(instanceGuard);
                    services.AddSingleton<App>();
                    services.AddSingleton<DictationPillWindow>();
                    services.AddSingleton<DictationPillViewModel>();

                    // Herramienta de diagnóstico EP-1 accesible vía --diagnostics-poc. Ver ADR-0003.
                    services.AddSingleton<PocLatencyWindow>();

                    // Onboarding shell (EP-3.1) + step VMs.
                    // Transient: la window se crea/destruye por sesión de configuración inicial
                    // y los VMs tienen estado mutable (CurrentStep, campos del form) que no debe
                    // sobrevivir entre aperturas.
                    services.AddTransient<OnboardingWindow>();
                    services.AddTransient<OnboardingViewModel>();
                    services.AddTransient<ProviderStepViewModel>();
                    services.AddTransient<HotkeyStepViewModel>();
                    services.AddTransient<PruebaStepViewModel>();

                    // Settings shell (EP-4.1). Transient por la misma razón que el onboarding:
                    // cada apertura debe arrancar limpia (currentSection en General, sin estados
                    // colgados de aperturas anteriores). El presenter es singleton porque tiene
                    // que mantener referencia a la window viva para el bring-to-front.
                    services.AddTransient<SettingsWindow>();
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.ProviderSectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.HotkeySectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.GeneralSectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.AudioSectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.PrivacySectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.HistorySectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.PlanSectionViewModel>();
                    services.AddTransient<Spikit.ViewModels.Settings.Sections.AboutSectionViewModel>();
                    services.AddSingleton<ISettingsWindowPresenter, WpfSettingsWindowPresenter>();
                    // Modal de confirmación reusable (EP-4.7 — borrar API key; EP-4.8 — borrar
                    // historial). Singleton stateless: cada Confirm() instancia su propia ConfirmDialog.
                    services.AddSingleton<Spikit.ViewModels.Settings.Sections.IConfirmationDialogService,
                                          Spikit.Services.Dialogs.WpfConfirmationDialogService>();
                    // Historial local (EP-4.8). Store singleton porque mantiene cache en memoria;
                    // el orchestrator EP-4.10 va a llamar Append cuando privacy.historyEnabled = true.
                    services.AddSingleton<Spikit.Services.History.IHistoryStore, Spikit.Services.History.JsonHistoryStore>();
                    services.AddSingleton<Spikit.Services.Clip.IClipboardService, Spikit.Services.Clip.WpfClipboardService>();

                    // Servicios de la sección General (EP-4.5). Singletons porque mantienen
                    // estado runtime (theme effective, suscripción a SystemEvents, registry handle).
                    services.AddSingleton<IAutostartService, RegistryAutostartService>();
                    services.AddSingleton<IThemeService, WpfThemeService>();
                    services.AddSingleton<IPillPositionService, WorkAreaPillPositionService>();
                    // Sección Audio (EP-4.6). Singleton porque la enumeración no cambia en runtime
                    // a corto plazo y reusar la instancia evita crear MMDeviceEnumerator innecesarios.
                    services.AddSingleton<IAudioDeviceEnumerator, NAudioAudioDeviceEnumerator>();
                    // EP-4.10 — AudioRuntimeOptions singleton mutable. Bootstrapeado desde
                    // settings.Audio.DeviceId; AudioCaptureService lo lee fresh en cada StartAsync
                    // (hot-swap entre sesiones), AudioSectionVM lo muta on toggle change.
                    services.AddSingleton(sp =>
                    {
                        var settings = sp.GetRequiredService<ISettingsService>().Load();
                        return new AudioRuntimeOptions { DeviceId = settings.Audio.DeviceId };
                    });

                    // TrayIcon (EP-4.2) — singleton inicializado en App.EnterMainAppMode.
                    services.AddSingleton<ITrayIconService, WpfTrayIconService>();

                    services.AddSingleton<IHotkeyService, HotkeyService>();
                    services.AddSingleton<IHotkeyConfigWriter, HotkeyConfigWriter>();
                    services.AddSingleton<IAudioCaptureService, AudioCaptureService>();

                    services.AddSingleton<ISettingsService, JsonSettingsService>();
                    services.AddSingleton<ISecretStore, DpapiSecretStore>();
                    services.AddSingleton<IProviderConfigWriter, ProviderConfigWriter>();
                    services.AddSingleton<IOnboardingCompletionStore, OnboardingCompletionStore>();

                    // Bootstrap del WhisperApiKey desde DPAPI con fallback a env vars (compat
                    // con sesiones anteriores a EP-3.4 — EP-3.8 puede limpiar esto cuando
                    // exista el flag onboardingCompleted). Singleton mutable → lo refresca
                    // ProviderConfigWriter en runtime sin reiniciar la app.
                    services.AddSingleton(sp =>
                    {
                        var secrets = sp.GetRequiredService<ISecretStore>();
                        var fromDpapi = secrets.Read(ProviderConfigWriter.ApiKeySecretName);
                        if (!string.IsNullOrEmpty(fromDpapi))
                        {
                            return new WhisperApiKey(fromDpapi);
                        }

                        var fallback = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
                                       ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                                       ?? string.Empty;
                        return new WhisperApiKey(fallback);
                    });

                    // WhisperApiOptions hidratado desde settings.json (provider.baseUrl /
                    // provider.model) cuando ya hay onboarding completado, con appsettings.json
                    // como fallback para Language/TimeoutSeconds (no son user-config en V1).
                    // Singleton compartido + IOptions wrappeando la misma referencia → mutaciones
                    // del writer se reflejan en el transient WhisperApiTranscriptionService.
                    services.AddSingleton(sp =>
                    {
                        var settings = sp.GetRequiredService<ISettingsService>().Load();
                        var section = ctx.Configuration.GetSection("Whisper");
                        var fallback = new WhisperApiOptions();
                        section.Bind(fallback);

                        return new WhisperApiOptions
                        {
                            BaseUrl = !string.IsNullOrWhiteSpace(settings.Provider.BaseUrl)
                                ? settings.Provider.BaseUrl
                                : fallback.BaseUrl,
                            Model = !string.IsNullOrWhiteSpace(settings.Provider.Model)
                                ? settings.Provider.Model
                                : fallback.Model,
                            // EP-4.10: language preferido del usuario (Settings → Audio).
                            // ResolveWhisperLanguage devuelve null para "auto" → WhisperApi
                            // omite el parámetro language en la request (auto-detect del provider).
                            Language = settings.Transcription.ResolveWhisperLanguage() ?? fallback.Language,
                            TimeoutSeconds = fallback.TimeoutSeconds,
                        };
                    });
                    services.AddSingleton<IOptions<WhisperApiOptions>>(sp =>
                        Options.Create(sp.GetRequiredService<WhisperApiOptions>()));

                    // EP-10.11 — bifurcación BYOK / Trial+Pro de la transcripción.
                    // Los dos clientes concretos viven como typed HttpClients independientes;
                    // TieredTranscriptionService es la fachada pública vía ITranscriptionService
                    // y elige según el tier del user en runtime.
                    services.AddHttpClient<WhisperApiTranscriptionService>((sp, client) =>
                    {
                        var opts = sp.GetRequiredService<WhisperApiOptions>();
                        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
                    });
                    services.AddHttpClient<ProxyTranscriptionService>((sp, client) =>
                    {
                        // Mismo timeout que el direct path — la diferencia de latencia del hop
                        // adicional al Edge Function es ~50-150ms, despreciable vs el upload
                        // del WAV + el round-trip a OpenAI.
                        var opts = sp.GetRequiredService<WhisperApiOptions>();
                        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
                    });
                    services.AddSingleton<ITranscriptionService, TieredTranscriptionService>();

                    // EP-4.10: resolver del nombre del proceso target (HWND → "cursor.exe").
                    // Lo usa el orchestrator al persistir entries al historial.
                    services.AddSingleton<ITargetProcessResolver, Win32TargetProcessResolver>();

                    services.AddSingleton<ITextInsertionService, ClipboardPasteService>();

                    // Tester reusable: onboarding (EP-3.3) y Settings → Provider (EP-4)
                    // comparten la misma lógica de "GET /models con Bearer key".
                    services.AddHttpClient<IProviderConnectionTester, HttpProviderConnectionTester>();

                    // EP-10.4 — Auth deep-link plumbing (servicios + storage + clients).
                    // El argv handling + UI Account window se cablean en EP-10.4 (parte 3.3
                    // pendiente) y EP-10.12 respectivamente. Por ahora el AuthService queda
                    // disponible vía DI pero nadie lo invoca todavía — es bedrock para
                    // EP-10.11 y EP-10.12.
                    services.Configure<SupabaseOptions>(ctx.Configuration.GetSection("Supabase"));
                    services.AddSingleton<IAuthTokenStore, AuthTokenStore>();
                    services.AddSingleton<IEntitlementCache, EntitlementCache>();
                    services.AddSingleton<IBrowserLauncher, DefaultBrowserLauncher>();
                    // Los dos HTTP clients viven en HttpClientFactory: timeout uniforme,
                    // pooling de conexiones, mismo patrón que WhisperApiTranscriptionService.
                    services.AddHttpClient<ISupabaseAuthClient, SupabaseAuthClient>(c =>
                        c.Timeout = TimeSpan.FromSeconds(15));
                    services.AddHttpClient<ISupabaseEntitlementClient, SupabaseEntitlementClient>(c =>
                        c.Timeout = TimeSpan.FromSeconds(15));
                    services.AddSingleton<IAuthService, AuthService>();
                    // EP-10.4 — Dispatcher invocado en boot directo (argv `spikit://...`)
                    // y vía SingleInstance.UriForwardRequested (segunda instancia forwardea
                    // a la primaria). Parsea + rutea por SpikitUriKind.
                    services.AddSingleton<ISpikitUriDispatcher, SpikitUriDispatcher>();

                    // EP-10.12 — Stripe billing client. POST /create-checkout-session y
                    // POST /create-portal-session. Mismo timeout que los demás clients de
                    // Supabase (15s).
                    services.AddHttpClient<Spikit.Services.Billing.IStripeBillingClient,
                                           Spikit.Services.Billing.StripeBillingClient>(c =>
                        c.Timeout = TimeSpan.FromSeconds(15));

                    // FloatingResultViewModel es transient: una instancia nueva por window.
                    services.AddTransient<FloatingResultViewModel>();
                    services.AddSingleton<IFloatingResultPresenter, WpfFloatingResultPresenter>();

                    // Toast bottom-right (EP-5.3 / FLOW 5). Host singleton — mantiene la
                    // lista de windows visibles en memoria. Service singleton también porque
                    // tiene el state de la cola (max-3, dedupe).
                    services.AddSingleton<IToastHost, WpfToastHost>();
                    services.AddSingleton<IToastService, ToastService>();

                    services.AddSingleton<DictationOrchestrator>();
                    // Demo mode (EP-4.4) — la implementación es el mismo singleton del orchestrator;
                    // exponemos la interfaz por separado para que la sección Hotkey pueda
                    // depender solo del subset que usa (BeginDemoMode/EndDemoMode + evento).
                    services.AddSingleton<IDictationDemoMode>(sp =>
                        sp.GetRequiredService<DictationOrchestrator>());
                })
                .Build();

            var app = host.Services.GetRequiredService<App>();
            return app.Run();
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static void ConfigureSerilog()
    {
        var logsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Spikit",
            "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logsDir, "spikit-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
