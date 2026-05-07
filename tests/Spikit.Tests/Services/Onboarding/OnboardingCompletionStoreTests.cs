using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Onboarding;
using Spikit.Services.Settings;

namespace Spikit.Tests.Services.Onboarding;

public class OnboardingCompletionStoreTests
{
    [Fact]
    public void IsCompleted_false_by_default()
    {
        var settings = new FakeSettingsService();
        var store = new OnboardingCompletionStore(settings, NullLogger<OnboardingCompletionStore>.Instance);

        Assert.False(store.IsCompleted());
    }

    [Fact]
    public void MarkCompleted_persists_flag()
    {
        var settings = new FakeSettingsService();
        var store = new OnboardingCompletionStore(settings, NullLogger<OnboardingCompletionStore>.Instance);

        store.MarkCompleted();

        Assert.True(settings.LastSaved!.OnboardingCompleted);
        Assert.True(store.IsCompleted());
    }

    [Fact]
    public void MarkCompleted_is_no_op_when_already_completed()
    {
        var settings = new FakeSettingsService { Existing = new AppSettings { OnboardingCompleted = true } };
        var store = new OnboardingCompletionStore(settings, NullLogger<OnboardingCompletionStore>.Instance);

        store.MarkCompleted();

        Assert.Null(settings.LastSaved); // no Save adicional disparado
    }

    [Fact]
    public void MarkCompleted_preserves_other_sections()
    {
        var settings = new FakeSettingsService
        {
            Existing = new AppSettings
            {
                Provider = new ProviderSettings { PresetId = "groq", BaseUrl = "https://x", Model = "y" },
                Hotkey = new HotkeySettings { Modifiers = "Win", VirtualKey = 0x20, Mode = "Toggle" },
            },
        };
        var store = new OnboardingCompletionStore(settings, NullLogger<OnboardingCompletionStore>.Instance);

        store.MarkCompleted();

        Assert.Equal("groq", settings.LastSaved!.Provider.PresetId);
        Assert.Equal("Win", settings.LastSaved.Hotkey.Modifiers);
        Assert.True(settings.LastSaved.OnboardingCompleted);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Existing { get; set; }
        public AppSettings? LastSaved { get; private set; }

        public AppSettings Load() => LastSaved ?? Existing ?? new AppSettings();

        public void Save(AppSettings settings) => LastSaved = settings;
    }
}
