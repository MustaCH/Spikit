using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Provider;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Tests.ViewModels.Onboarding;

public class ProviderStepViewModelTests
{
    private const string ValidApiKey = "sk-test-key-1234567890123";

    private static ProviderStepViewModel MakeVm(IProviderConnectionTester? tester = null) =>
        new(NullLogger<ProviderStepViewModel>.Instance, tester ?? new FakeTester());

    // ===== Bootstrap =====

    [Fact]
    public void Bootstrap_uses_OpenAI_defaults()
    {
        var vm = MakeVm();

        Assert.Equal(ProviderPreset.OpenAI, vm.SelectedPreset);
        Assert.Equal("https://api.openai.com/v1", vm.BaseUrl);
        Assert.Equal("whisper-1", vm.Model);
        Assert.Equal(string.Empty, vm.ApiKey);
        Assert.Equal(ProviderConnectionStatus.Idle, vm.ConnectionStatus);
    }

    [Fact]
    public void Bootstrap_loads_OpenAI_available_models()
    {
        var vm = MakeVm();

        Assert.Contains("whisper-1", vm.AvailableModels);
        Assert.False(vm.IsCustomPreset);
    }

    // ===== AvailableModels + IsCustomPreset per preset =====

    [Fact]
    public void Switching_to_Groq_refreshes_available_models()
    {
        var vm = MakeVm();
        vm.SelectedPreset = ProviderPreset.Groq;

        Assert.Contains("whisper-large-v3", vm.AvailableModels);
        Assert.Contains("whisper-large-v3-turbo", vm.AvailableModels);
        Assert.DoesNotContain("whisper-1", vm.AvailableModels);
        Assert.False(vm.IsCustomPreset);
    }

    [Fact]
    public void Switching_to_Custom_clears_available_models_and_enables_free_text()
    {
        var vm = MakeVm();
        vm.SelectedPreset = ProviderPreset.Custom;

        Assert.Empty(vm.AvailableModels);
        Assert.True(vm.IsCustomPreset);
    }

    [Fact]
    public void Switching_back_from_Custom_repopulates_models()
    {
        var vm = MakeVm();
        vm.SelectedPreset = ProviderPreset.Custom;
        vm.SelectedPreset = ProviderPreset.OpenAI;

        Assert.Contains("whisper-1", vm.AvailableModels);
        Assert.False(vm.IsCustomPreset);
    }

    // ===== Validación sync de API key =====

    [Fact]
    public void HardError_is_empty_before_user_types()
    {
        var vm = MakeVm();
        Assert.Equal(string.Empty, vm.ApiKeyHardError);
        Assert.False(vm.HasApiKeyError);
    }

    [Fact]
    public void HardError_appears_after_user_clears_key()
    {
        var vm = MakeVm();
        vm.ApiKey = "sk-something";
        vm.ApiKey = string.Empty;
        Assert.Contains("vacía", vm.ApiKeyHardError);
        Assert.True(vm.HasApiKeyError);
    }

    [Fact]
    public void HardError_for_whitespace_inside()
    {
        var vm = MakeVm();
        vm.ApiKey = "sk-with space-inside";
        Assert.Contains("espacios", vm.ApiKeyHardError);
    }

    [Fact]
    public void HardError_for_too_short()
    {
        var vm = MakeVm();
        vm.ApiKey = "sk-short";
        Assert.Contains("al menos 20", vm.ApiKeyHardError);
    }

    [Fact]
    public void HardError_for_too_long()
    {
        var vm = MakeVm();
        vm.ApiKey = "sk-" + new string('a', 600);
        Assert.Contains("máximo 500", vm.ApiKeyHardError);
    }

    [Fact]
    public void Valid_key_with_sk_prefix_has_no_error_or_warning()
    {
        var vm = MakeVm();
        vm.ApiKey = ValidApiKey;
        Assert.Empty(vm.ApiKeyHardError);
        Assert.Empty(vm.ApiKeySoftWarning);
    }

    [Fact]
    public void Soft_warning_when_key_does_not_start_with_sk()
    {
        var vm = MakeVm();
        vm.ApiKey = "gsk_groq_style_key_1234567890";
        Assert.Empty(vm.ApiKeyHardError);
        Assert.Contains("sk-", vm.ApiKeySoftWarning);
        Assert.True(vm.HasApiKeyWarning);
    }

    [Fact]
    public void Soft_warning_is_suppressed_when_hard_error_exists()
    {
        var vm = MakeVm();
        vm.ApiKey = "abc"; // demasiado corta + no sk- — debería ganar el hard error
        Assert.NotEmpty(vm.ApiKeyHardError);
        Assert.Empty(vm.ApiKeySoftWarning);
    }

    // ===== CanTestConnection =====

    [Fact]
    public void CanTestConnection_false_with_empty_apikey()
    {
        var vm = MakeVm();
        Assert.False(vm.CanTestConnection);
    }

    [Fact]
    public void CanTestConnection_false_when_apikey_has_hard_error()
    {
        var vm = MakeVm();
        vm.ApiKey = "short";
        Assert.False(vm.CanTestConnection);
    }

    [Fact]
    public void CanTestConnection_true_with_valid_inputs()
    {
        var vm = MakeVm();
        vm.ApiKey = ValidApiKey;
        Assert.True(vm.CanTestConnection);
    }

    [Fact]
    public void CanTestConnection_false_while_busy_testing()
    {
        var vm = MakeVm();
        vm.ApiKey = ValidApiKey;
        vm.IsBusyTesting = true;
        Assert.False(vm.CanTestConnection);
    }

    // ===== Test connection (async via command) =====

    [Fact]
    public async Task TestConnection_marks_status_Ok_when_tester_succeeds()
    {
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var vm = MakeVm(tester);
        vm.ApiKey = ValidApiKey;

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();

        Assert.Equal(ProviderConnectionStatus.Ok, vm.ConnectionStatus);
        Assert.True(vm.IsConnectionOk);
        Assert.Equal("Conexión OK", vm.ConnectionMessage);
        Assert.NotNull(vm.ConnectionTestedAt);
        Assert.False(vm.IsBusyTesting);
    }

    [Fact]
    public async Task TestConnection_marks_status_Error_with_message_when_tester_fails()
    {
        var tester = new FakeTester { Result = ProviderConnectionResult.Failed("API key inválida.") };
        var vm = MakeVm(tester);
        vm.ApiKey = ValidApiKey;

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();

        Assert.Equal(ProviderConnectionStatus.Error, vm.ConnectionStatus);
        Assert.False(vm.IsConnectionOk);
        Assert.Equal("API key inválida.", vm.ConnectionMessage);
        Assert.Null(vm.ConnectionTestedAt);
    }

    [Fact]
    public async Task TestConnection_passes_current_baseurl_and_apikey_to_tester()
    {
        var tester = new FakeTester();
        var vm = MakeVm(tester);
        vm.SelectedPreset = ProviderPreset.Groq;
        vm.ApiKey = "gsk_groq_test_key_long_enough";

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();

        Assert.Equal("https://api.groq.com/openai/v1", tester.LastBaseUrl);
        Assert.Equal("gsk_groq_test_key_long_enough", tester.LastApiKey);
    }

    // ===== Invalidación on edit =====

    [Fact]
    public async Task Editing_BaseUrl_invalidates_a_previous_Ok()
    {
        var vm = await MakeVmWithSuccessfulConnectionAsync();

        vm.BaseUrl = "https://example.com/v1";

        Assert.Equal(ProviderConnectionStatus.Idle, vm.ConnectionStatus);
        Assert.False(vm.IsConnectionOk);
    }

    [Fact]
    public async Task Editing_ApiKey_invalidates_a_previous_Ok()
    {
        var vm = await MakeVmWithSuccessfulConnectionAsync();

        vm.ApiKey = "sk-different-key-1234567890";

        Assert.Equal(ProviderConnectionStatus.Idle, vm.ConnectionStatus);
    }

    [Fact]
    public async Task Editing_Model_invalidates_a_previous_Ok()
    {
        var vm = await MakeVmWithSuccessfulConnectionAsync();

        vm.Model = "whisper-large-v3";

        Assert.Equal(ProviderConnectionStatus.Idle, vm.ConnectionStatus);
    }

    [Fact]
    public async Task ConnectionStateChanged_fires_on_Ok_and_invalidate()
    {
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var vm = MakeVm(tester);
        vm.ApiKey = ValidApiKey;

        var fired = 0;
        vm.ConnectionStateChanged += (_, _) => fired++;

        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();
        // 2 cambios: Idle → Testing, Testing → Ok
        Assert.True(fired >= 2);

        var beforeInvalidate = fired;
        vm.ApiKey = "sk-rotated-1234567890123";
        Assert.Equal(beforeInvalidate + 1, fired);
    }

    private static async Task<ProviderStepViewModel> MakeVmWithSuccessfulConnectionAsync()
    {
        var tester = new FakeTester { Result = ProviderConnectionResult.Ok() };
        var vm = MakeVm(tester);
        vm.ApiKey = ValidApiKey;
        vm.TestConnectionCommand.Execute(null);
        await tester.WaitForCallAsync();
        Assert.Equal(ProviderConnectionStatus.Ok, vm.ConnectionStatus);
        return vm;
    }

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
            // Esperamos al menos al primer await del VM. 1s es overkill pero da margen
            // para que el continuation post-await del TestConnectionAsync se ejecute.
            return Task.WhenAny(_calledTcs.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        }
    }
}
