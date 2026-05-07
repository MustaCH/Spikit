using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Settings;

namespace Spikit.Tests.Services.Settings;

// Tests del file-system real (testing-strategy.md §Integration: "JsonSettingsService con
// Path.GetTempPath()"). xUnit aísla cada test con un tmpdir único + IDisposable cleanup.
public class JsonSettingsServiceTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _filePath;

    public JsonSettingsServiceTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "spikit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
        _filePath = Path.Combine(_tmpDir, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
    }

    private JsonSettingsService MakeService() =>
        new(_filePath, NullLogger<JsonSettingsService>.Instance);

    [Fact]
    public void Load_returns_defaults_when_file_does_not_exist()
    {
        var svc = MakeService();

        var result = svc.Load();

        Assert.NotNull(result);
        Assert.NotNull(result.Provider);
        Assert.Equal("openai", result.Provider.PresetId);
        Assert.Equal("https://api.openai.com/v1", result.Provider.BaseUrl);
        Assert.Equal("whisper-1", result.Provider.Model);
        // Defaults V1 del bloque hotkey (EP-3.6).
        // Enum.ToString() para flags ordena por valor de bit ascendente — Alt (0x1) antes
        // que Control (0x2). El parser de TryToRuntime acepta cualquier orden.
        Assert.NotNull(result.Hotkey);
        Assert.Equal("Alt, Control", result.Hotkey.Modifiers);
        Assert.Equal((uint)0x4D, result.Hotkey.VirtualKey); // 'M'
        Assert.Equal("PushToTalk", result.Hotkey.Mode);
    }

    [Fact]
    public void Default_OnboardingCompleted_is_false()
    {
        var svc = MakeService();

        var result = svc.Load();

        Assert.False(result.OnboardingCompleted);
    }

    [Fact]
    public void Roundtrip_preserves_OnboardingCompleted_flag()
    {
        var svc = MakeService();
        svc.Save(new AppSettings { OnboardingCompleted = true });

        var loaded = svc.Load();

        Assert.True(loaded.OnboardingCompleted);
    }

    [Fact]
    public void Roundtrip_preserves_hotkey_section()
    {
        var svc = MakeService();
        var saved = new AppSettings
        {
            Hotkey = new HotkeySettings
            {
                Modifiers = "Control, Shift",
                VirtualKey = 0x20,
                Mode = "Toggle",
            },
        };

        svc.Save(saved);
        var loaded = svc.Load();

        Assert.Equal("Control, Shift", loaded.Hotkey.Modifiers);
        Assert.Equal((uint)0x20, loaded.Hotkey.VirtualKey);
        Assert.Equal("Toggle", loaded.Hotkey.Mode);
    }

    [Fact]
    public void Save_creates_file_and_directory_if_missing()
    {
        var nestedPath = Path.Combine(_tmpDir, "nested", "subdir", "settings.json");
        var svc = new JsonSettingsService(nestedPath, NullLogger<JsonSettingsService>.Instance);
        var settings = new AppSettings
        {
            Provider = new ProviderSettings { PresetId = "groq", BaseUrl = "https://api.groq.com/openai/v1", Model = "whisper-large-v3" },
        };

        svc.Save(settings);

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void Roundtrip_preserves_provider_section()
    {
        var svc = MakeService();
        var saved = new AppSettings
        {
            Provider = new ProviderSettings
            {
                PresetId = "groq",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "whisper-large-v3-turbo",
            },
        };

        svc.Save(saved);
        var loaded = svc.Load();

        Assert.Equal("groq", loaded.Provider.PresetId);
        Assert.Equal("https://api.groq.com/openai/v1", loaded.Provider.BaseUrl);
        Assert.Equal("whisper-large-v3-turbo", loaded.Provider.Model);
    }

    [Fact]
    public void Save_writes_camelCase_property_names()
    {
        var svc = MakeService();
        svc.Save(new AppSettings
        {
            Provider = new ProviderSettings { PresetId = "openai", BaseUrl = "https://x", Model = "y" },
        });

        var raw = File.ReadAllText(_filePath);

        Assert.Contains("\"provider\"", raw);
        Assert.Contains("\"presetId\"", raw);
        Assert.Contains("\"baseUrl\"", raw);
        Assert.DoesNotContain("\"PresetId\"", raw); // PascalCase explícito ausente
    }

    [Fact]
    public void Load_returns_defaults_when_file_is_corrupt_json()
    {
        File.WriteAllText(_filePath, "{ this is not valid json");
        var svc = MakeService();

        var result = svc.Load();

        Assert.Equal("openai", result.Provider.PresetId); // defaults
    }

    [Fact]
    public void Save_does_not_leak_temp_file()
    {
        var svc = MakeService();
        svc.Save(new AppSettings
        {
            Provider = new ProviderSettings { PresetId = "openai", BaseUrl = "https://x", Model = "y" },
        });

        var tempLeftover = Path.Combine(_tmpDir, "settings.json.tmp");
        Assert.False(File.Exists(tempLeftover));
    }
}
