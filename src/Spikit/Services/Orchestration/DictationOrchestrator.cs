using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.Services.Orchestration;

public class DictationOrchestrator
{
    public DictationOrchestrator(
        IHotkeyService hotkeyService,
        IAudioCaptureService audioCaptureService,
        ITranscriptionService transcriptionService,
        ITextInsertionService textInsertionService,
        ISettingsService settingsService,
        ISecretStore secretStore)
    {
        _ = hotkeyService;
        _ = audioCaptureService;
        _ = transcriptionService;
        _ = textInsertionService;
        _ = settingsService;
        _ = secretStore;
    }
}
