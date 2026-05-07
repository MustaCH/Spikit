using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using Spikit.Cli;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Orchestration;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;
using Spikit.ViewModels;
using Spikit.ViewModels.Onboarding;
using Spikit.Views;
using Spikit.Views.Diagnostics;
using Spikit.Views.Onboarding;

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

                    services.AddSingleton<IHotkeyService, HotkeyService>();
                    services.AddSingleton<IHotkeyConfigWriter, HotkeyConfigWriter>();
                    services.AddSingleton<IAudioCaptureService, AudioCaptureService>();

                    services.AddSingleton<ISettingsService, JsonSettingsService>();
                    services.AddSingleton<ISecretStore, DpapiSecretStore>();
                    services.AddSingleton<IProviderConfigWriter, ProviderConfigWriter>();

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
                            Language = fallback.Language,
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

                    services.AddSingleton<ITextInsertionService, ClipboardPasteService>();

                    // Tester reusable: onboarding (EP-3.3) y Settings → Provider (EP-4)
                    // comparten la misma lógica de "GET /models con Bearer key".
                    services.AddHttpClient<IProviderConnectionTester, HttpProviderConnectionTester>();

                    // FloatingResultViewModel es transient: una instancia nueva por window.
                    services.AddTransient<FloatingResultViewModel>();
                    services.AddSingleton<IFloatingResultPresenter, WpfFloatingResultPresenter>();
                    services.AddSingleton<DictationOrchestrator>();
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
