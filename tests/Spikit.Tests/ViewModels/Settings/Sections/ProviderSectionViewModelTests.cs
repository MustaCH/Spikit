using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class ProviderSectionViewModelTests
{
    private const string ExistingKey = "sk-existing-key-1234567890123";
    private const string NewKey = "sk-new-replacement-key-1234567890";

    private static (ProviderSectionViewModel vm, FakeSettingsService settings, FakeSecretStore secrets) MakeVm(
        AppSettings? existingSettings = null,
        string? existingDpapiKey = ExistingKey,
        IProviderConnectionTester? tester = null,
        IProviderConfigWriter? writer = null)
    {
        var settings = new FakeSettingsService { Saved = existingSettings ?? DefaultPersistedSettings() };
        var secrets = new FakeSecretStore();
        if (!string.IsNullOrEmpty(existingDpapiKey))
        {
            secrets.Write(ProviderConfigWriter.ApiKeySecretName, existingDpapiKey!);
        }

        var vm = new ProviderSectionViewModel(
            NullLogger<ProviderSectionViewModel>.Instance,
            tester ?? new FakeTester(),
            writer ?? new FakeConfigWriter(),
            settings,
            secrets);

        return (vm, settings, secrets);
    }

    private static AppSettings DefaultPersistedSettings() => new()
    {
        Provider = new ProviderSettings
        {
            PresetId = "openai",
            BaseUrl = "https://api.openai.com/v1",
            Model = "whisper-1",
        },
    };

    // ===== Bootstrap / precarga =====

    [Fact]
    public void Bootstrap_loads_settings_into_form()
    {
        var (vm, _, _) = MakeVm();

        Assert.Equal(ProviderPreset.OpenAI, vm.SelectedPreset);
        Assert.Equal("https://api.openai.com/v1", vm.BaseUrl);
        Assert.Equal("whisper-1", vm.Model);
        // ApiKey property arranca vacío — la key real vive cacheada en el VM, no expuesta al form.
        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public void Bootstrap_with_existing_dpapi_key_starts_in_masked_state()
    {
        var (vm, _, _) = MakeVm();

        Assert.True(vm.HasExistingKey);
        Assert.False(vm.IsReplacingKey);
        Assert.True(vm.IsKeyMaskedPlaceholderVisible);
        Assert.True(vm.IsReplaceButtonVisible);
        Assert.False(vm.IsKeyEditable);
    }

    [Fact]
    public void Bootstrap_without_dpapi_key_starts_in_editable_state()
    {
        var (vm, _, _) = MakeVm(existingDpapiKey: null);

        Assert.False(vm.HasExistingKey);
        Assert.True(vm.IsReplacingKey);
        Assert.True(vm.IsKeyEditable);
        Assert.False(vm.IsKeyMaskedPlaceholderVisible);
    }

    [Fact]
    public void Bootstrap_loads_groq_preset_with_correct_models()
    {
        var settings = new AppSettings
        {
            Provider = new ProviderSettings
            {
                PresetId = "groq",
                BaseUrl = "https://api.groq.com/openai/v1",
                Model = "whisper-large-v3",
            },
        };
        var (vm, _, _) = MakeVm(existingSettings: settings);

        Assert.Equal(ProviderPreset.Groq, vm.SelectedPreset);
        Assert.Contains("whisper-large-v3", vm.AvailableModels);
        Assert.False(vm.IsCustomPreset);
    }

    // ===== Badge =====

    [Fact]
    public void Badge_reflects_persisted_preset_and_model()
    {
        var (vm, _, _) = MakeVm();

        Assert.Equal("OpenAI · whisper-1", vm.BadgeText);
    }

    [Fact]
    public void Badge_does_not_change_when_form_fields_are_edited_without_saving()
    {
        var (vm, _, _) = MakeVm();

        // Cambia el preset sin guardar — el badge debe seguir mostrando lo persistido.
        vm.SelectedPreset = ProviderPreset.Groq;

        Assert.Equal("OpenAI · whisper-1", vm.BadgeText);
    }

    // ===== Replace-key UX =====

    [Fact]
    public void ReplaceKey_command_enters_editable_mode_and_clears_form_apikey()
    {
        var (vm, _, _) = MakeVm();
        vm.ApiKey = "stale-from-some-flow";

        vm.ReplaceKeyCommand.Execute(null);

        Assert.True(vm.IsReplacingKey);
        Assert.True(vm.IsKeyEditable);
        Assert.False(vm.IsKeyMaskedPlaceholderVisible);
        Assert.Equal(string.Empty, vm.ApiKey);
    }

    [Fact]
    public async Task TestConnection_uses_existing_key_when_not_replacing()
    {
        var tester = new FakeTester();
        var (vm, _, _) = MakeVm(tester: tester);

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();

        Assert.Equal(ExistingKey, tester.LastApiKey);
    }

    [Fact]
    public async Task TestConnection_uses_new_key_when_replacing()
    {
        var tester = new FakeTester();
        var (vm, _, _) = MakeVm(tester: tester);

        vm.ReplaceKeyCommand.Execute(null);
        vm.ApiKey = NewKey;

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();

        Assert.Equal(NewKey, tester.LastApiKey);
    }

    // ===== HasPendingChanges (gate del SaveCommand) =====

    [Fact]
    public void HasPendingChanges_false_on_fresh_load()
    {
        var (vm, _, _) = MakeVm();

        Assert.False(vm.HasPendingChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void HasPendingChanges_true_when_preset_changes()
    {
        var (vm, _, _) = MakeVm();

        vm.SelectedPreset = ProviderPreset.Groq;

        Assert.True(vm.HasPendingChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void HasPendingChanges_true_when_replacing_with_typed_key()
    {
        var (vm, _, _) = MakeVm();

        vm.ReplaceKeyCommand.Execute(null);
        // Sin tipear todavía → no hay cambio aplicable.
        Assert.False(vm.HasPendingChanges);

        vm.ApiKey = NewKey;
        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public async Task HasPendingChanges_resets_to_false_after_successful_save()
    {
        var writer = new FakeConfigWriter();
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.SelectedPreset = ProviderPreset.Groq;
        Assert.True(vm.HasPendingChanges);

        vm.SaveCommand.Execute(null);
        await writer.WaitForCallAsync();

        Assert.False(vm.HasPendingChanges);
    }

    // ===== Save flow =====

    [Fact]
    public async Task SaveCommand_persists_existing_key_when_not_replacing()
    {
        var writer = new FakeConfigWriter();
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.SaveCommand.Execute(null);
        await writer.WaitForCallAsync();

        Assert.Equal(1, writer.CallCount);
        Assert.NotNull(writer.LastConfig);
        Assert.Equal(ExistingKey, writer.LastConfig!.ApiKey);
        Assert.Equal("openai", writer.LastConfig.PresetId);
    }

    [Fact]
    public async Task SaveCommand_persists_new_key_after_replace()
    {
        var writer = new FakeConfigWriter();
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.ReplaceKeyCommand.Execute(null);
        vm.ApiKey = NewKey;
        vm.SaveCommand.Execute(null);
        await writer.WaitForCallAsync();

        Assert.Equal(NewKey, writer.LastConfig!.ApiKey);
    }

    [Fact]
    public async Task SaveCommand_does_not_persist_when_test_fails()
    {
        var writer = new FakeConfigWriter();
        var tester = new FakeTester { Result = ProviderConnectionResult.Failed("Key inválida.") };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.SaveCommand.Execute(null);
        await tester.WaitForCallAsync();
        // Damos un tiempo prudente para que el SaveCommand se complete (test → no save).
        await Task.Delay(50);

        Assert.Equal(0, writer.CallCount);
        Assert.Equal(ProviderConnectionStatus.Error, vm.ConnectionStatus);
        Assert.Equal("Key inválida.", vm.ConnectionMessage);
    }

    [Fact]
    public async Task SaveSuccess_returns_to_masked_state_with_new_key_as_existing()
    {
        var writer = new FakeConfigWriter();
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.ReplaceKeyCommand.Execute(null);
        vm.ApiKey = NewKey;
        vm.SaveCommand.Execute(null);
        await writer.WaitForCallAsync();

        // Tras el save, salimos de "replacing", form ApiKey vuelve vacío, y la "nueva existente"
        // pasa a ser la efectiva. El próximo test usará NewKey sin necesidad de re-replace.
        Assert.False(vm.IsReplacingKey);
        Assert.True(vm.HasExistingKey);
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.True(vm.IsKeyMaskedPlaceholderVisible);
    }

    [Fact]
    public async Task SaveCommand_propagates_writer_failure_inline()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new ProviderConfigSaveException("DPAPI rechazó la escritura."),
        };
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var (vm, _, _) = MakeVm(tester: tester, writer: writer);

        vm.SaveCommand.Execute(null);
        await writer.WaitForCallAsync();

        Assert.True(vm.HasSaveError);
        Assert.Equal("DPAPI rechazó la escritura.", vm.SaveError);
    }

    // ===== Fakes =====

    private sealed class FakeTester : IProviderConnectionTester
    {
        private readonly TaskCompletionSource _calledTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderConnectionResult Result { get; set; } = ProviderConnectionResult.Ok();
        public string? LastBaseUrl { get; private set; }
        public string? LastApiKey { get; private set; }

        public Task<ProviderConnectionResult> TestAsync(string baseUrl, string apiKey, CancellationToken ct = default)
        {
            LastBaseUrl = baseUrl;
            LastApiKey = apiKey;
            _calledTcs.TrySetResult();
            return Task.FromResult(Result);
        }

        public Task WaitForCallAsync()
        {
            return Task.WhenAny(_calledTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeConfigWriter : IProviderConfigWriter
    {
        private readonly TaskCompletionSource _calledTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderConfig? LastConfig { get; private set; }
        public int CallCount { get; private set; }
        public ProviderConfigSaveException? ThrowOnSave { get; set; }

        public Task SaveAsync(ProviderConfig config, CancellationToken ct = default)
        {
            CallCount++;
            LastConfig = config;
            _calledTcs.TrySetResult();
            if (ThrowOnSave is not null) throw ThrowOnSave;
            return Task.CompletedTask;
        }

        public Task WaitForCallAsync() =>
            Task.WhenAny(_calledTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Saved { get; set; }

        public event EventHandler? SettingsChanged;

        public AppSettings Load() => Saved ?? new AppSettings();

        public void Save(AppSettings settings)
        {
            Saved = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();

        public string? Read(string key) => _store.TryGetValue(key, out var value) ? value : null;
        public void Write(string key, string value) => _store[key] = value;
        public void Delete(string key) => _store.Remove(key);
    }
}
