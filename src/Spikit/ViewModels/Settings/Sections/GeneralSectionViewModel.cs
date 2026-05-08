using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Autostart;
using Spikit.Services.Settings;
using Spikit.Services.Theme;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección General de Settings (EP-4.5). Tres bloques con onChange (sin botón
// Guardar — ver flows.md FLOW 3 "los cambios se persisten en onChange salvo donde tienen
// test/registro asociado"):
//   1. Iniciar con Windows (US-5.1) — toggle OFF default, muta registry HKCU\…\Run.
//   2. Tema (US-5.2) — radio System/Dark/Light, aplica al swap de ResourceDictionaries.
//   3. Posición de la pill (D-1) — selector visual 3×2, se persiste y la pill lo lee al
//      próximo OnLoaded / SettingsChanged.
//
// La precarga toma:
//   - Theme/PillAnchor del settings.json.
//   - AutoStart del registry actual (no del settings, para reflejar el estado real). Si
//     el usuario borró la entrada manualmente entre sesiones, el toggle muestra OFF aunque
//     settings diga true. Después del primer toggle todo queda sincronizado.
public sealed class GeneralSectionViewModel : ViewModelBase
{
    private readonly ILogger<GeneralSectionViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IAutostartService _autostart;
    private readonly IThemeService _themeService;

    private bool _autoStart;
    private AppTheme _theme;
    private PillAnchor _pillAnchor;

    // Suprime el efecto de los setters durante la precarga inicial — sino el set de
    // _autoStart desde el constructor dispararía Enable/Disable del registry al arrancar.
    private bool _suppressEffects;

    public GeneralSectionViewModel(
        ILogger<GeneralSectionViewModel> logger,
        ISettingsService settingsService,
        IAutostartService autostart,
        IThemeService themeService)
    {
        _logger = logger;
        _settingsService = settingsService;
        _autostart = autostart;
        _themeService = themeService;

        SetPillAnchorCommand = new RelayCommand<PillAnchor>(SetPillAnchor);

        LoadFromPersistence();
    }

    // ============ Iniciar con Windows ============

    public bool IsAutoStart
    {
        get => _autoStart;
        set
        {
            if (!SetProperty(ref _autoStart, value)) return;
            if (_suppressEffects) return;

            try
            {
                if (value) _autostart.Enable();
                else _autostart.Disable();

                PersistAutoStart(value);
            }
            catch (Exception ex)
            {
                // Si el registry rechaza la escritura (raro: HKCU casi nunca falla por
                // permisos), revertimos el toggle sin avisar — el setter siguiente puede
                // probar de nuevo. Logueamos a error para que aparezca en logs si hay
                // problema persistente.
                _logger.LogError(ex, "AutoStart toggle falló — revirtiendo a {Previous}", !value);
                _suppressEffects = true;
                try { SetProperty(ref _autoStart, !value); }
                finally { _suppressEffects = false; }
                OnPropertyChanged(nameof(IsAutoStart));
            }
        }
    }

    // ============ Tema ============

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            OnPropertyChanged(nameof(IsThemeSystem));
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));

            if (_suppressEffects) return;

            _themeService.Apply(value);
            PersistTheme(value);
        }
    }

    public bool IsThemeSystem
    {
        get => _theme == AppTheme.System;
        set { if (value) Theme = AppTheme.System; }
    }

    public bool IsThemeDark
    {
        get => _theme == AppTheme.Dark;
        set { if (value) Theme = AppTheme.Dark; }
    }

    public bool IsThemeLight
    {
        get => _theme == AppTheme.Light;
        set { if (value) Theme = AppTheme.Light; }
    }

    // ============ Posición de la pill ============

    public PillAnchor PillAnchor
    {
        get => _pillAnchor;
        private set
        {
            if (!SetProperty(ref _pillAnchor, value)) return;
            // Las 6 properties IsAnchor* dependen del anchor actual.
            OnPropertyChanged(nameof(IsAnchorTopLeft));
            OnPropertyChanged(nameof(IsAnchorTopCenter));
            OnPropertyChanged(nameof(IsAnchorTopRight));
            OnPropertyChanged(nameof(IsAnchorBottomLeft));
            OnPropertyChanged(nameof(IsAnchorBottomCenter));
            OnPropertyChanged(nameof(IsAnchorBottomRight));
        }
    }

    public bool IsAnchorTopLeft => _pillAnchor == Models.PillAnchor.TopLeft;
    public bool IsAnchorTopCenter => _pillAnchor == Models.PillAnchor.TopCenter;
    public bool IsAnchorTopRight => _pillAnchor == Models.PillAnchor.TopRight;
    public bool IsAnchorBottomLeft => _pillAnchor == Models.PillAnchor.BottomLeft;
    public bool IsAnchorBottomCenter => _pillAnchor == Models.PillAnchor.BottomCenter;
    public bool IsAnchorBottomRight => _pillAnchor == Models.PillAnchor.BottomRight;

    public ICommand SetPillAnchorCommand { get; }

    private void SetPillAnchor(PillAnchor anchor)
    {
        if (_pillAnchor == anchor) return;
        PillAnchor = anchor;
        if (_suppressEffects) return;

        PersistPillAnchor(anchor);
        _logger.LogDebug("Pill anchor → {Anchor}", anchor);
    }

    // ============ Persistencia ============

    private void LoadFromPersistence()
    {
        _suppressEffects = true;
        try
        {
            var settings = _settingsService.Load();
            // AutoStart: estado real del registry (no del settings) — más confiable porque
            // el usuario podría haber borrado la entrada manualmente y el settings quedaría
            // stale. Persist on first toggle re-sincroniza.
            _autoStart = _autostart.IsEnabled();
            _theme = settings.General.TryToTheme();
            _pillAnchor = settings.General.TryToAnchor();

            OnPropertyChanged(nameof(IsAutoStart));
            OnPropertyChanged(nameof(Theme));
            OnPropertyChanged(nameof(IsThemeSystem));
            OnPropertyChanged(nameof(IsThemeDark));
            OnPropertyChanged(nameof(IsThemeLight));
            OnPropertyChanged(nameof(IsAnchorTopLeft));
            OnPropertyChanged(nameof(IsAnchorTopCenter));
            OnPropertyChanged(nameof(IsAnchorTopRight));
            OnPropertyChanged(nameof(IsAnchorBottomLeft));
            OnPropertyChanged(nameof(IsAnchorBottomCenter));
            OnPropertyChanged(nameof(IsAnchorBottomRight));
        }
        finally
        {
            _suppressEffects = false;
        }
    }

    private void PersistAutoStart(bool value)
    {
        var settings = _settingsService.Load();
        settings.General.AutoStart = value;
        _settingsService.Save(settings);
    }

    private void PersistTheme(AppTheme theme)
    {
        var settings = _settingsService.Load();
        settings.General.Theme = GeneralSettings.ToThemeId(theme);
        _settingsService.Save(settings);
    }

    private void PersistPillAnchor(PillAnchor anchor)
    {
        var settings = _settingsService.Load();
        settings.General.PillAnchor = GeneralSettings.ToAnchorId(anchor);
        _settingsService.Save(settings);
    }
}
