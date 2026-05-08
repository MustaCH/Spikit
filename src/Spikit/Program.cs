using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Spikit.Cli;
using Spikit.Services.Audio;
using Spikit.Services.Autostart;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Onboarding;
using Spikit.Services.Orchestration;
using Spikit.Services.PillPosition;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Theme;
using Spikit.Services.Toast;
using Spikit.Services.Tray;
using Spikit.Services.Transcription;
using Spikit.ViewModels;
using Spikit.ViewModels.Onboarding;
using Spikit.ViewModels.Settings;
using Spikit.Views;
using Spikit.Views.Diagnostics;
using Spikit.Views.Onboarding;
using Spikit.Views.Settings;

namespace Spikit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ConfigureSerilog();

        try
        {
            var cliArgs = new CommandLineArgs(args);

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog()
                .ConfigureServices((ctx, services) =>
                {
                    services.AddSingleton(cliArgs);
                    services.AddSingleton<App>();
                    services.AddSingleton<MainWindow>();
                    services.AddSingleton<MainWindowViewModel>();
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
                    // PlanService V1 (EP-4.9): siempre BYOK hardcoded. Cuando exista backend Pro
                    // se reemplaza por una HttpPlanService sin tocar la VM.
                    services.AddSingleton<Spikit.Services.PlanInfo.IPlanService, Spikit.Services.PlanInfo.V1PlanService>();
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

                    services.AddHttpClient<ITranscriptionService, WhisperApiTranscriptionService>((sp, client) =>
                    {
                        var opts = sp.GetRequiredService<WhisperApiOptions>();
                        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
                    });

                    // EP-4.10: resolver del nombre del proceso target (HWND → "cursor.exe").
                    // Lo usa el orchestrator al persistir entries al historial.
                    services.AddSingleton<ITargetProcessResolver, Win32TargetProcessResolver>();

                    services.AddSingleton<ITextInsertionService, ClipboardPasteService>();

                    // Tester reusable: onboarding (EP-3.3) y Settings → Provider (EP-4)
                    // comparten la misma lógica de "GET /models con Bearer key".
                    services.AddHttpClient<IProviderConnectionTester, HttpProviderConnectionTester>();

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
