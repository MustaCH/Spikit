using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Orchestration;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration().CreateLogger();

        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((_, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<App>();
                services.AddSingleton<MainWindow>();

                services.AddSingleton<IHotkeyService, HotkeyService>();
                services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
                services.AddSingleton<ITranscriptionService, WhisperApiTranscriptionService>();
                services.AddSingleton<ITextInsertionService, ClipboardPasteService>();
                services.AddSingleton<ISettingsService, JsonSettingsService>();
                services.AddSingleton<ISecretStore, DpapiSecretStore>();
                services.AddSingleton<DictationOrchestrator>();
            })
            .Build();

        var app = host.Services.GetRequiredService<App>();
        return app.Run();
    }
}
