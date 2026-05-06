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
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;
using Spikit.ViewModels;
using Spikit.Views;
using Spikit.Views.Diagnostics;

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

                    // Herramienta de diagnóstico EP-1 accesible vía --diagnostics-poc. Ver ADR-0003.
                    services.AddSingleton<PocLatencyWindow>();

                    services.AddSingleton<IHotkeyService, HotkeyService>();
                    services.AddSingleton<IAudioCaptureService, AudioCaptureService>();

                    services.Configure<WhisperApiOptions>(ctx.Configuration.GetSection("Whisper"));
                    services.AddSingleton(_ => new WhisperApiKey(
                        // User scope (registry) primero — sobrevive a procesos parent con entorno
                        // heredado viejo. Process scope como fallback (CI / scripts que la pasan
                        // explícita). En EP-3 esto se reemplaza por DPAPI.
                        Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                        ?? string.Empty));
                    services.AddHttpClient<ITranscriptionService, WhisperApiTranscriptionService>((sp, client) =>
                    {
                        var opts = sp.GetRequiredService<IOptions<WhisperApiOptions>>().Value;
                        client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
                    });

                    services.AddSingleton<ITextInsertionService, ClipboardPasteService>();
                    services.AddSingleton<ISettingsService, JsonSettingsService>();
                    services.AddSingleton<ISecretStore, DpapiSecretStore>();

                    // Stub hasta sub-task #7 (FloatingResultWindow).
                    services.AddSingleton<IFloatingResultPresenter, LoggingFloatingResultPresenter>();
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
