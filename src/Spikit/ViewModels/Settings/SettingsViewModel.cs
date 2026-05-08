using System.Windows.Input;
using Microsoft.Extensions.Logging;
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
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ILogger<SettingsViewModel> _logger;
    private SettingsSection _currentSection = SettingsSection.General;

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        ProviderSectionViewModel provider,
        HotkeySectionViewModel hotkey,
        GeneralSectionViewModel general)
    {
        _logger = logger;
        Provider = provider;
        Hotkey = hotkey;
        General = general;
        NavigateToCommand = new RelayCommand<SettingsSection>(NavigateTo);
    }

    // Section VMs — expuestos como properties para DataContext-binding desde el XAML.
    public ProviderSectionViewModel Provider { get; }
    public HotkeySectionViewModel Hotkey { get; }
    public GeneralSectionViewModel General { get; }

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
    }
}
