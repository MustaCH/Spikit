using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class PrivacySectionViewModelTests
{
    private static (PrivacySectionViewModel vm,
                    FakeSettingsService settings,
                    FakeSecretStore secrets,
                    WhisperApiKey runtimeKey,
                    FakeConfirmationDialog dialog) MakeVm(
        AppSettings? existingSettings = null,
        string existingApiKey = "sk-existing")
    {
        var settings = new FakeSettingsService { Saved = existingSettings ?? new AppSettings() };
        var secrets = new FakeSecretStore();
        if (!string.IsNullOrEmpty(existingApiKey))
        {
            secrets.Write(ProviderConfigWriter.ApiKeySecretName, existingApiKey);
        }
        var runtimeKey = new WhisperApiKey(existingApiKey);
        var dialog = new FakeConfirmationDialog();

        var vm = new PrivacySectionViewModel(
            NullLogger<PrivacySectionViewModel>.Instance,
            settings,
            secrets,
            runtimeKey,
            dialog);
        return (vm, settings, secrets, runtimeKey, dialog);
    }

    // ===== Bootstrap del toggle =====

    [Fact]
    public void Bootstrap_history_off_by_default()
    {
        var (vm, _, _, _, _) = MakeVm();

        Assert.False(vm.HistoryEnabled);
        Assert.True(vm.IsHistoryOff);
        Assert.False(vm.IsHistoryOn);
    }

    [Fact]
    public void Bootstrap_loads_persisted_history_enabled_true()
    {
        var settings = new AppSettings { Privacy = new PrivacySettings { HistoryEnabled = true } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings);

        Assert.True(vm.HistoryEnabled);
        Assert.True(vm.IsHistoryOn);
    }

    [Fact]
    public void Bootstrap_does_not_persist_during_load()
    {
        var (_, settings, _, _, _) = MakeVm();

        Assert.Equal(0, settings.SaveCount);
    }

    // ===== Cambio del toggle =====

    [Fact]
    public void Setting_IsHistoryOn_true_persists_true()
    {
        var (vm, settings, _, _, _) = MakeVm();

        vm.IsHistoryOn = true;

        Assert.True(settings.Saved!.Privacy.HistoryEnabled);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public void Setting_IsHistoryOff_true_when_already_on_persists_false()
    {
        var settings = new AppSettings { Privacy = new PrivacySettings { HistoryEnabled = true } };
        var (vm, savedSettings, _, _, _) = MakeVm(existingSettings: settings);

        vm.IsHistoryOff = true;

        Assert.False(savedSettings.Saved!.Privacy.HistoryEnabled);
    }

    [Fact]
    public void Setting_history_to_same_value_does_not_persist()
    {
        // Idempotencia: si el setter recibe el mismo valor que ya tiene, NO persiste de nuevo.
        // Importante porque WPF dispara setters dos veces con grupos de RadioButtons.
        var (vm, settings, _, _, _) = MakeVm();
        vm.IsHistoryOn = true;
        var saveCountAfterFirst = settings.SaveCount;

        vm.IsHistoryOn = true;

        Assert.Equal(saveCountAfterFirst, settings.SaveCount);
    }

    // ===== Crash reports (EP-8.3) =====

    [Fact]
    public void Bootstrap_send_crash_reports_off_by_default()
    {
        var (vm, _, _, _, _) = MakeVm();

        Assert.False(vm.SendCrashReports);
        Assert.True(vm.IsCrashReportsOff);
        Assert.False(vm.IsCrashReportsOn);
    }

    [Fact]
    public void Bootstrap_loads_persisted_send_crash_reports_true()
    {
        var settings = new AppSettings { Privacy = new PrivacySettings { SendCrashReports = true } };
        var (vm, _, _, _, _) = MakeVm(existingSettings: settings);

        Assert.True(vm.SendCrashReports);
        Assert.True(vm.IsCrashReportsOn);
    }

    [Fact]
    public void Setting_IsCrashReportsOn_true_persists_true()
    {
        var (vm, settings, _, _, _) = MakeVm();

        vm.IsCrashReportsOn = true;

        Assert.True(settings.Saved!.Privacy.SendCrashReports);
        Assert.Equal(1, settings.SaveCount);
    }

    [Fact]
    public void Setting_IsCrashReportsOff_true_when_already_on_persists_false()
    {
        var settings = new AppSettings { Privacy = new PrivacySettings { SendCrashReports = true } };
        var (vm, savedSettings, _, _, _) = MakeVm(existingSettings: settings);

        vm.IsCrashReportsOff = true;

        Assert.False(savedSettings.Saved!.Privacy.SendCrashReports);
    }

    [Fact]
    public void Setting_send_crash_reports_to_same_value_does_not_persist()
    {
        var (vm, settings, _, _, _) = MakeVm();
        vm.IsCrashReportsOn = true;
        var saveCountAfterFirst = settings.SaveCount;

        vm.IsCrashReportsOn = true;

        Assert.Equal(saveCountAfterFirst, settings.SaveCount);
    }

    [Fact]
    public void Toggling_history_does_not_disturb_crash_reports_persisted_value()
    {
        // Resguardo de aliasing: las dos persistencias usan settings.Privacy del mismo
        // AppSettings cargado, así que un toggle del historial no debería pisar el flag
        // de crash reports si el usuario lo había activado antes.
        var existing = new AppSettings { Privacy = new PrivacySettings { SendCrashReports = true } };
        var (vm, settings, _, _, _) = MakeVm(existingSettings: existing);

        vm.IsHistoryOn = true;

        Assert.True(settings.Saved!.Privacy.SendCrashReports);
        Assert.True(settings.Saved.Privacy.HistoryEnabled);
    }

    // ===== Borrado de API key =====

    [Fact]
    public void DeleteApiKey_when_user_cancels_does_not_touch_storage()
    {
        var (vm, _, secrets, runtimeKey, dialog) = MakeVm(existingApiKey: "sk-cancel");
        dialog.NextResult = false;

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.Equal("sk-cancel", secrets.Read(ProviderConfigWriter.ApiKeySecretName));
        Assert.Equal("sk-cancel", runtimeKey.Value);
        Assert.False(vm.HasDeleteFeedback);
    }

    [Fact]
    public void DeleteApiKey_when_user_confirms_deletes_secret()
    {
        var (vm, _, secrets, _, dialog) = MakeVm(existingApiKey: "sk-confirm");
        dialog.NextResult = true;

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.Null(secrets.Read(ProviderConfigWriter.ApiKeySecretName));
    }

    [Fact]
    public void DeleteApiKey_when_user_confirms_clears_runtime_key()
    {
        var (vm, _, _, runtimeKey, dialog) = MakeVm(existingApiKey: "sk-runtime");
        dialog.NextResult = true;

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.Equal(string.Empty, runtimeKey.Value);
        Assert.False(runtimeKey.IsConfigured);
    }

    [Fact]
    public void DeleteApiKey_success_sets_feedback_message_non_error()
    {
        var (vm, _, _, _, dialog) = MakeVm();
        dialog.NextResult = true;

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.True(vm.HasDeleteFeedback);
        Assert.False(vm.IsDeleteFeedbackError);
        Assert.NotNull(vm.DeleteFeedbackMessage);
    }

    [Fact]
    public void DeleteApiKey_dialog_request_marks_action_as_destructive()
    {
        var (vm, _, _, _, dialog) = MakeVm();

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.NotNull(dialog.LastRequest);
        Assert.True(dialog.LastRequest!.IsDestructive);
        Assert.Equal("Borrar", dialog.LastRequest.ConfirmLabel);
        Assert.Equal("Cancelar", dialog.LastRequest.CancelLabel);
    }

    [Fact]
    public void DeleteApiKey_when_secret_store_throws_shows_error_feedback()
    {
        var settings = new FakeSettingsService { Saved = new AppSettings() };
        var secrets = new ThrowingSecretStore();
        var runtimeKey = new WhisperApiKey("sk-throws");
        var dialog = new FakeConfirmationDialog { NextResult = true };

        var vm = new PrivacySectionViewModel(
            NullLogger<PrivacySectionViewModel>.Instance,
            settings,
            secrets,
            runtimeKey,
            dialog);

        vm.DeleteApiKeyCommand.Execute(null);

        Assert.True(vm.HasDeleteFeedback);
        Assert.True(vm.IsDeleteFeedbackError);
    }

    // ===== Fakes =====

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

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new();

        public string? Read(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public void Write(string key, string value) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public string? Read(string key) => null;
        public void Write(string key, string value) { }
        public void Delete(string key) => throw new IOException("disco lleno");
    }

    private sealed class FakeConfirmationDialog : IConfirmationDialogService
    {
        public bool NextResult { get; set; }
        public ConfirmationRequest? LastRequest { get; private set; }

        public bool Confirm(ConfirmationRequest request)
        {
            LastRequest = request;
            return NextResult;
        }
    }
}
