using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Autostart;
using Spikit.Services.Settings;
using Spikit.Services.Theme;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class GeneralSectionViewModelTests
{
    private static (GeneralSectionViewModel vm, FakeSettingsService settings, FakeAutostart autostart, FakeTheme theme) MakeVm(
        AppSettings? existingSettings = null,
        bool autostartInitiallyEnabled = false)
    {
        var settings = new FakeSettingsService { Saved = existingSettings ?? new AppSettings() };
        var autostart = new FakeAutostart { Enabled = autostartInitiallyEnabled };
        var theme = new FakeTheme();
        var vm = new GeneralSectionViewModel(
            NullLogger<GeneralSectionViewModel>.Instance,
            settings,
            autostart,
            theme);
        return (vm, settings, autostart, theme);
    }

    // ===== Bootstrap =====

    [Fact]
    public void Bootstrap_loads_autostart_state_from_registry_not_settings()
    {
        // El autostart real lo lee del registry (no del settings) — si el usuario borró
        // la entrada manualmente entre sesiones, la UI debe reflejar OFF aunque el JSON
        // diga true.
        var settings = new AppSettings { General = new GeneralSettings { AutoStart = true } };
        var (vm, _, _, _) = MakeVm(existingSettings: settings, autostartInitiallyEnabled: false);

        Assert.False(vm.IsAutoStart);
    }

    [Fact]
    public void Bootstrap_loads_theme_from_settings()
    {
        var settings = new AppSettings { General = new GeneralSettings { Theme = "light" } };
        var (vm, _, _, _) = MakeVm(existingSettings: settings);

        Assert.Equal(AppTheme.Light, vm.Theme);
        Assert.True(vm.IsThemeLight);
    }

    [Fact]
    public void Bootstrap_loads_pill_anchor_from_settings()
    {
        var settings = new AppSettings { General = new GeneralSettings { PillAnchor = "topright" } };
        var (vm, _, _, _) = MakeVm(existingSettings: settings);

        Assert.Equal(PillAnchor.TopRight, vm.PillAnchor);
        Assert.True(vm.IsAnchorTopRight);
    }

    [Fact]
    public void Bootstrap_does_not_apply_theme_during_load()
    {
        // El bootstrap del tema lo hace App.OnStartup; el VM solo refleja el setting.
        // Si el VM lo aplicara cada vez que se abre Settings, habría doble aplicación.
        var settings = new AppSettings { General = new GeneralSettings { Theme = "light" } };
        var (_, _, _, theme) = MakeVm(existingSettings: settings);

        Assert.Equal(0, theme.ApplyCalls);
    }

    [Fact]
    public void Bootstrap_does_not_toggle_autostart_during_load()
    {
        // Mismo patrón que tema: el setter del IsAutoStart se suprime durante LoadFromPersistence
        // para no escribir al registry al abrir Settings.
        var (_, _, autostart, _) = MakeVm(autostartInitiallyEnabled: true);

        Assert.Equal(0, autostart.EnableCalls);
        Assert.Equal(0, autostart.DisableCalls);
    }

    // ===== Toggle autostart =====

    [Fact]
    public void IsAutoStart_setter_calls_Enable_and_persists()
    {
        var (vm, settings, autostart, _) = MakeVm(autostartInitiallyEnabled: false);

        vm.IsAutoStart = true;

        Assert.Equal(1, autostart.EnableCalls);
        Assert.True(settings.Saved!.General.AutoStart);
    }

    [Fact]
    public void IsAutoStart_setter_to_false_calls_Disable()
    {
        var (vm, settings, autostart, _) = MakeVm(autostartInitiallyEnabled: true);

        vm.IsAutoStart = false;

        Assert.Equal(1, autostart.DisableCalls);
        Assert.False(settings.Saved!.General.AutoStart);
    }

    [Fact]
    public void IsAutoStart_setter_reverts_when_registry_throws()
    {
        var autostart = new FakeAutostart { ThrowOnEnable = new InvalidOperationException("registry locked") };
        var settings = new FakeSettingsService { Saved = new AppSettings() };
        var vm = new GeneralSectionViewModel(
            NullLogger<GeneralSectionViewModel>.Instance, settings, autostart, new FakeTheme());

        vm.IsAutoStart = true;

        // El toggle no quedó pegado en true: el setter capturó la excepción y revirtió.
        Assert.False(vm.IsAutoStart);
    }

    // ===== Cambio de tema =====

    [Fact]
    public void Theme_setter_calls_Apply_and_persists()
    {
        var (vm, settings, _, theme) = MakeVm();

        vm.Theme = AppTheme.Dark;

        Assert.Equal(1, theme.ApplyCalls);
        Assert.Equal(AppTheme.Dark, theme.LastAppliedTheme);
        Assert.Equal("dark", settings.Saved!.General.Theme);
    }

    [Fact]
    public void Setting_IsThemeLight_true_changes_theme_to_Light()
    {
        var (vm, _, _, theme) = MakeVm();

        vm.IsThemeLight = true;

        Assert.Equal(AppTheme.Light, vm.Theme);
        Assert.Equal(AppTheme.Light, theme.LastAppliedTheme);
    }

    // ===== Pill anchor =====

    [Fact]
    public void SetPillAnchorCommand_persists_new_anchor()
    {
        var (vm, settings, _, _) = MakeVm();

        vm.SetPillAnchorCommand.Execute(PillAnchor.TopLeft);

        Assert.Equal(PillAnchor.TopLeft, vm.PillAnchor);
        Assert.True(vm.IsAnchorTopLeft);
        Assert.Equal("topleft", settings.Saved!.General.PillAnchor);
    }

    [Fact]
    public void SetPillAnchorCommand_with_same_anchor_is_no_op()
    {
        var (vm, settings, _, _) = MakeVm();
        var initialSaved = settings.SaveCount;

        // Default es BottomCenter, asignar lo mismo no debería re-persistir.
        vm.SetPillAnchorCommand.Execute(PillAnchor.BottomCenter);

        Assert.Equal(initialSaved, settings.SaveCount);
    }

    // ===== Fakes =====

    private sealed class FakeAutostart : IAutostartService
    {
        public bool Enabled { get; set; }
        public int EnableCalls { get; private set; }
        public int DisableCalls { get; private set; }
        public Exception? ThrowOnEnable { get; set; }

        public bool IsEnabled() => Enabled;

        public void Enable()
        {
            EnableCalls++;
            if (ThrowOnEnable is not null) throw ThrowOnEnable;
            Enabled = true;
        }

        public void Disable()
        {
            DisableCalls++;
            Enabled = false;
        }
    }

    private sealed class FakeTheme : IThemeService
    {
        public AppTheme CurrentTheme { get; private set; } = AppTheme.System;
        public AppTheme EffectiveTheme { get; private set; } = AppTheme.Dark;
        public AppTheme? LastAppliedTheme { get; private set; }
        public int ApplyCalls { get; private set; }

        public event EventHandler<AppTheme>? EffectiveThemeChanged;

        public void Apply(AppTheme theme)
        {
            ApplyCalls++;
            LastAppliedTheme = theme;
            CurrentTheme = theme;
        }

        // Suprime warning de evento no usado.
        private void Unused() => EffectiveThemeChanged?.Invoke(this, AppTheme.Dark);
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
