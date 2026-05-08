using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Audio;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class AudioSectionViewModelTests
{
    private static (AudioSectionViewModel vm,
                    FakeSettingsService settings,
                    FakeEnumerator enumerator,
                    AudioRuntimeOptions audioRuntime,
                    WhisperApiOptions whisperRuntime) MakeVm(
        AppSettings? existingSettings = null,
        IReadOnlyList<AudioInputDevice>? availableDevices = null)
    {
        var settings = new FakeSettingsService { Saved = existingSettings ?? new AppSettings() };
        var enumerator = new FakeEnumerator
        {
            Devices = availableDevices ?? Array.Empty<AudioInputDevice>(),
        };
        var audioRuntime = new AudioRuntimeOptions { DeviceId = settings.Saved!.Audio.DeviceId };
        var whisperRuntime = new WhisperApiOptions
        {
            Language = settings.Saved.Transcription.ResolveWhisperLanguage(),
        };
        var vm = new AudioSectionViewModel(
            NullLogger<AudioSectionViewModel>.Instance,
            settings,
            enumerator,
            audioRuntime,
            whisperRuntime);
        return (vm, settings, enumerator, audioRuntime, whisperRuntime);
    }

    private static readonly AudioInputDevice MicHeadset = new("{0.0.1.00000000}.{abc}", "Auriculares Sony");
    private static readonly AudioInputDevice MicWebcam = new("{0.0.1.00000000}.{def}", "Webcam Logitech");

    // ===== Bootstrap =====

    [Fact]
    public void Bootstrap_lists_default_followed_by_real_devices()
    {
        var (vm, _, _, _, _) = MakeVm(availableDevices: new[] { MicHeadset, MicWebcam });

        Assert.Equal(3, vm.Devices.Count);
        Assert.Equal(AudioSectionViewModel.DefaultDevice, vm.Devices[0]);
        Assert.Equal(MicHeadset, vm.Devices[1]);
        Assert.Equal(MicWebcam, vm.Devices[2]);
    }

    [Fact]
    public void Bootstrap_selects_default_when_setting_is_empty()
    {
        var (vm, _, _, _, _) = MakeVm(availableDevices: new[] { MicHeadset });

        Assert.Equal(AudioSectionViewModel.DefaultDevice, vm.SelectedDevice);
    }

    [Fact]
    public void Bootstrap_selects_persisted_device_when_present()
    {
        var settings = new AppSettings { Audio = new AudioSettings { DeviceId = MicHeadset.Id } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings, availableDevices: new[] { MicHeadset, MicWebcam });

        Assert.Equal(MicHeadset, vm.SelectedDevice);
    }

    [Fact]
    public void Bootstrap_falls_back_to_default_when_persisted_device_disappeared()
    {
        // Hot-plug entre sesiones: el deviceId persistido ya no aparece en List(). El AC del
        // ticket dice que el toast del fallback en runtime se cubre en EP-5.3 — acá solo
        // garantizamos que la UI NO muestra una entry stale.
        var settings = new AppSettings { Audio = new AudioSettings { DeviceId = MicHeadset.Id } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings, availableDevices: new[] { MicWebcam });

        Assert.Equal(AudioSectionViewModel.DefaultDevice, vm.SelectedDevice);
    }

    [Fact]
    public void Bootstrap_does_not_persist_during_load()
    {
        // El _suppressEffects flag debe evitar que el setter de SelectedDevice/Language
        // dispare PersistDeviceId al construir el VM.
        var (_, settings, _, _, _) = MakeVm();

        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public void Bootstrap_loads_persisted_language()
    {
        var settings = new AppSettings { Transcription = new TranscriptionSettings { Language = "es" } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings);

        Assert.Equal(TranscriptionLanguageOption.Spanish, vm.Language);
        Assert.True(vm.IsLanguageSpanish);
    }

    [Fact]
    public void Bootstrap_unknown_language_defaults_to_auto()
    {
        // JSON corrupto o valor de versión futura → caer a Auto sin tirar.
        var settings = new AppSettings { Transcription = new TranscriptionSettings { Language = "klingon" } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings);

        Assert.Equal(TranscriptionLanguageOption.Auto, vm.Language);
    }

    // ===== Cambio de device =====

    [Fact]
    public void SelectedDevice_setter_persists_device_id()
    {
        var (vm, settings, _, _, _) = MakeVm(availableDevices: new[] { MicHeadset });

        vm.SelectedDevice = MicHeadset;

        Assert.Equal(MicHeadset.Id, settings.Saved!.Audio.DeviceId);
    }

    [Fact]
    public void SelectedDevice_setter_to_default_persists_empty_string()
    {
        var settings = new AppSettings { Audio = new AudioSettings { DeviceId = MicHeadset.Id } };
        var (vm, savedSettings, _, _, _) = MakeVm(existingSettings: settings, availableDevices: new[] { MicHeadset });

        vm.SelectedDevice = AudioSectionViewModel.DefaultDevice;

        Assert.Equal(string.Empty, savedSettings.Saved!.Audio.DeviceId);
    }

    // ===== Cambio de idioma =====

    [Fact]
    public void Language_setter_to_Spanish_persists_es()
    {
        var (vm, settings, _, _, _) = MakeVm();

        vm.Language = TranscriptionLanguageOption.Spanish;

        Assert.Equal("es", settings.Saved!.Transcription.Language);
    }

    [Fact]
    public void Language_setter_to_Auto_persists_auto()
    {
        var settings = new AppSettings { Transcription = new TranscriptionSettings { Language = "en" } };
        var (vm, savedSettings, _, _, _) = MakeVm(existingSettings: settings);

        vm.Language = TranscriptionLanguageOption.Auto;

        Assert.Equal("auto", savedSettings.Saved!.Transcription.Language);
    }

    [Fact]
    public void Setting_IsLanguageEnglish_true_changes_language()
    {
        var (vm, _, _, _, _) = MakeVm();

        vm.IsLanguageEnglish = true;

        Assert.Equal(TranscriptionLanguageOption.English, vm.Language);
        Assert.False(vm.IsLanguageAuto);
        Assert.False(vm.IsLanguageSpanish);
    }

    // ===== EP-4.10 — cableado runtime =====

    [Fact]
    public void Changing_device_mutates_audio_runtime_options()
    {
        var (vm, _, _, audioRuntime, _) = MakeVm(availableDevices: new[] { MicHeadset });

        vm.SelectedDevice = MicHeadset;

        Assert.Equal(MicHeadset.Id, audioRuntime.DeviceId);
    }

    [Fact]
    public void Changing_device_to_default_clears_audio_runtime_options()
    {
        var settings = new AppSettings { Audio = new AudioSettings { DeviceId = MicHeadset.Id } };
        var (vm, _, _, audioRuntime, _) = MakeVm(existingSettings: settings, availableDevices: new[] { MicHeadset });

        vm.SelectedDevice = AudioSectionViewModel.DefaultDevice;

        Assert.Equal(string.Empty, audioRuntime.DeviceId);
    }

    [Fact]
    public void Changing_language_to_spanish_mutates_whisper_runtime_options()
    {
        var (vm, _, _, _, whisperRuntime) = MakeVm();

        vm.Language = TranscriptionLanguageOption.Spanish;

        Assert.Equal("es", whisperRuntime.Language);
    }

    [Fact]
    public void Changing_language_to_auto_clears_whisper_runtime_language()
    {
        // "auto" se traduce a null en el WhisperApiOptions: el WhisperApiTranscriptionService
        // omite el parámetro language en la request cuando es null/vacío.
        var settings = new AppSettings { Transcription = new TranscriptionSettings { Language = "en" } };
        var (vm, _, _, _, whisperRuntime) = MakeVm(existingSettings: settings);

        vm.Language = TranscriptionLanguageOption.Auto;

        Assert.Null(whisperRuntime.Language);
    }

    // ===== ResolveWhisperLanguage (settings model) =====

    [Theory]
    [InlineData("auto", null)]
    [InlineData("es", "es")]
    [InlineData("en", "en")]
    [InlineData("ES", "es")] // case insensitive
    [InlineData("invalid", null)]
    [InlineData("", null)]
    public void TranscriptionSettings_ResolveWhisperLanguage(string raw, string? expected)
    {
        var settings = new TranscriptionSettings { Language = raw };

        Assert.Equal(expected, settings.ResolveWhisperLanguage());
    }

    // ===== Fakes =====

    private sealed class FakeEnumerator : IAudioDeviceEnumerator
    {
        public IReadOnlyList<AudioInputDevice> Devices { get; set; } = Array.Empty<AudioInputDevice>();

        public IReadOnlyList<AudioInputDevice> List() => Devices;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Saved { get; set; }
        public int SaveCount { get; private set; }

        public event EventHandler? SettingsChanged;

        public AppSettings Load() => Saved ?? new AppSettings();

        public void Save(AppSettings settings)
        {
            SaveCount++;
            Saved = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
