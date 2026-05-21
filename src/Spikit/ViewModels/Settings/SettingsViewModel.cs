using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Auth;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.ViewModels.Settings;

// Coordina el SettingsWindow shell (EP-4.1). Mantiene la sección activa y expone:
// - Flags `IsXxxSection` para que el ContentControl muestre el UserControl correcto.
// - Flags `IsXxxSelected` para que cada item del sidebar se resalte como activo.
// - `NavigateToCommand` parametrizado para los clicks del sidebar (uno por sección).
//
// Section VMs inyectados: cada uno se expone como property pública para que el XAML
// haga DataContext="{Binding Xxx}" en cada UserControl (mismo patrón que OnboardingViewModel
// con Provider/Hotkey/Prueba). EP-4.3 cableó Provider; el resto entra cuando aterricen
// EP-4.4 a EP-4.9.
//
// Reactivo al tier (EP-11.6 / ADR-0008 sub-task #6): la sección Provider solo es válida
// para BYOK (incluyendo grace 30d). Para Trial/Pro/Expired/no-logueado el sidebar item
// y la sección se ocultan vía IsProviderVisible. Si el usuario está parado en Provider y
// el tier cambia a uno que ya no debe verlo (revoke BYOK + expire), redirigimos a General
// automáticamente. Patrón de suscripción a IAuthService.StateChanged copiado de
// PlanSectionViewModel (commit 38a6fff).
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IAuthService _auth;
    private SettingsSection _currentSection = SettingsSection.General;

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IAuthService auth,
        ProviderSectionViewModel provider,
        HotkeySectionViewModel hotkey,
        GeneralSectionViewModel general,
        AudioSectionViewModel audio,
        PrivacySectionViewModel privacy,
        HistorySectionViewModel history,
        PlanSectionViewModel plan,
        AboutSectionViewModel about)
    {
        _logger = logger;
        _auth = auth;
        Provider = provider;
        Hotkey = hotkey;
        General = general;
        Audio = audio;
        Privacy = privacy;
        History = history;
        Plan = plan;
        About = about;

        // History VM dispara NavigateRequested cuando el usuario aprieta "Ir a Privacidad"
        // desde el empty-state OFF. Lo escuchamos acá y delegamos al NavigateTo del shell.
        History.NavigateRequested += OnHistoryNavigateRequested;

        NavigateToCommand = new RelayCommand<SettingsSection>(NavigateTo);

        _auth.StateChanged += OnAuthStateChanged;
    }

    // Section VMs — expuestos como properties para DataContext-binding desde el XAML.
    public ProviderSectionViewModel Provider { get; }
    public HotkeySectionViewModel Hotkey { get; }
    public GeneralSectionViewModel General { get; }
    public AudioSectionViewModel Audio { get; }
    public PrivacySectionViewModel Privacy { get; }
    public HistorySectionViewModel History { get; }
    public PlanSectionViewModel Plan { get; }
    public AboutSectionViewModel About { get; }

    private void OnHistoryNavigateRequested(object? sender, SettingsSection target)
    {
        NavigateTo(target);
    }

    public SettingsSection CurrentSection
    {
        get => _currentSection;
        private set
        {
            if (SetProperty(ref _currentSection, value))
            {
                NotifyDerivedStateChanged();
            }
        }
    }

    // Flag por sección — consumido vía BooleanToVisibilityConverter en el ContentControl
    // del XAML para mostrar el UserControl correspondiente.
    public bool IsGeneralSection => CurrentSection == SettingsSection.General;
    public bool IsProviderSection => CurrentSection == SettingsSection.Provider;
    public bool IsHotkeySection => CurrentSection == SettingsSection.Hotkey;
    public bool IsAudioSection => CurrentSection == SettingsSection.Audio;
    public bool IsPrivacySection => CurrentSection == SettingsSection.Privacy;
    public bool IsHistorySection => CurrentSection == SettingsSection.History;
    public bool IsPlanSection => CurrentSection == SettingsSection.Plan;
    public bool IsAboutSection => CurrentSection == SettingsSection.About;

    // EP-11.6: visibilidad del item Provider en el sidebar + la sección entera. Solo BYOK
    // (incluyendo grace 30d, donde el Tier sigue siendo Byok hasta el expire). Para
    // Trial/Pro/Expired/no-logueado queda oculto. Confirmado con Nacho 2026-05-20.
    public bool IsProviderVisible =>
        _auth.State == AuthSessionState.LoggedIn
        && _auth.CurrentEntitlement?.Tier == Tier.Byok;

    // Defensa en profundidad: el UserControl del content panel solo se renderiza si la
    // sección es la activa Y el tier la permite. Aunque el sidebar item esté oculto, si
    // por algún motivo CurrentSection quedara en Provider con un tier no-BYOK (race entre
    // el StateChanged y el redirect), la sección no se dibuja.
    public bool IsProviderSectionRendered => IsProviderSection && IsProviderVisible;

    // Flags `IsXxxSelected` son alias semánticos de los `IsXxxSection` para usarlos en el
    // sidebar (item activo). Los duplico en lugar de reusar IsXxxSection porque un futuro
    // refactor podría querer separar "sección renderizada" de "sección seleccionada en el
    // sidebar" (ej. animación de transición). Por ahora apuntan a lo mismo.
    public bool IsGeneralSelected => IsGeneralSection;
    public bool IsProviderSelected => IsProviderSection;
    public bool IsHotkeySelected => IsHotkeySection;
    public bool IsAudioSelected => IsAudioSection;
    public bool IsPrivacySelected => IsPrivacySection;
    public bool IsHistorySelected => IsHistorySection;
    public bool IsPlanSelected => IsPlanSection;
    public bool IsAboutSelected => IsAboutSection;

    public ICommand NavigateToCommand { get; }

    public void NavigateTo(SettingsSection section)
    {
        if (CurrentSection == section) return;
        CurrentSection = section;
        _logger.LogDebug("Settings → {Section}", section);
    }

    private void NotifyDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsGeneralSection));
        OnPropertyChanged(nameof(IsProviderSection));
        OnPropertyChanged(nameof(IsHotkeySection));
        OnPropertyChanged(nameof(IsAudioSection));
        OnPropertyChanged(nameof(IsPrivacySection));
        OnPropertyChanged(nameof(IsHistorySection));
        OnPropertyChanged(nameof(IsPlanSection));
        OnPropertyChanged(nameof(IsAboutSection));

        OnPropertyChanged(nameof(IsGeneralSelected));
        OnPropertyChanged(nameof(IsProviderSelected));
        OnPropertyChanged(nameof(IsHotkeySelected));
        OnPropertyChanged(nameof(IsAudioSelected));
        OnPropertyChanged(nameof(IsPrivacySelected));
        OnPropertyChanged(nameof(IsHistorySelected));
        OnPropertyChanged(nameof(IsPlanSelected));
        OnPropertyChanged(nameof(IsAboutSelected));

        OnPropertyChanged(nameof(IsProviderSectionRendered));
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        // El tier puede haber cambiado a uno que ya no debe ver Provider (BYOK → Expired
        // post-grace, o logout). Si estamos parados ahí, redirigimos a General antes de
        // notificar para evitar ver Provider "vacío" un frame.
        if (CurrentSection == SettingsSection.Provider && !IsProviderVisible)
        {
            _logger.LogInformation(
                "Settings: tier ya no permite ver Provider, redirigiendo a General");
            CurrentSection = SettingsSection.General;
        }

        OnPropertyChanged(nameof(IsProviderVisible));
        OnPropertyChanged(nameof(IsProviderSectionRendered));
    }

    public void Dispose() => _auth.StateChanged -= OnAuthStateChanged;
}
