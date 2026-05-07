using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace Spikit.ViewModels.Onboarding;

// Coordina el wizard de onboarding. Mantiene el paso activo y expone:
// - Flags de visibilidad por paso para que el ContentControl muestre el UserControl correcto.
// - Estado del stepper (●━━○━━○) por cada uno de los 3 pasos numerados.
// - Visibilidad de los botones de footer (Atrás / Saltar / Siguiente / Empezar / Finalizar).
//
// Esta es la SHELL (sub-task EP-3.1). Las validaciones reales por paso (Provider conectado,
// Hotkey registrada, Prueba con texto) se cablean en EP-3.2..EP-3.7 vía CanGoNext = true.
//
// Cuando esos sub-tasks aterricen, deberían reemplazar `CanGoNext => true` por una lectura
// del estado de cada step VM.
public sealed class OnboardingViewModel : ViewModelBase
{
    private readonly ILogger<OnboardingViewModel> _logger;
    private OnboardingStep _currentStep = OnboardingStep.Welcome;
    private bool _isCompleted;

    public OnboardingViewModel(
        ILogger<OnboardingViewModel> logger,
        ProviderStepViewModel provider)
    {
        _logger = logger;
        Provider = provider;

        // Suscripción al evento del Provider VM: cuando cambia el estado de conexión
        // (Ok/Error/Testing/Idle), recomputamos CanGoNext y notificamos al CommandManager
        // para que el botón "Siguiente" se enable/disable.
        Provider.ConnectionStateChanged += OnProviderConnectionStateChanged;

        GoNextCommand = new RelayCommand(GoNext, () => CanGoNext);
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        SkipCommand = new RelayCommand(Skip, () => IsSkipVisible);
    }

    // VMs por paso, expuestos como propiedades para que cada UserControl haga
    // DataContext="{Binding Provider}" en el OnboardingWindow.
    public ProviderStepViewModel Provider { get; }

    private void OnProviderConnectionStateChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanGoNext));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    // Disparado cuando el usuario llega al final del wizard (Finalizar o Saltar en Prueba).
    // El consumer (Window) decide si cerrar la ventana, persistir el flag onboardingCompleted, etc.
    // El cableado del flag (EP-3.8) es independiente de este shell.
    public event EventHandler? OnboardingCompleted;

    public OnboardingStep CurrentStep
    {
        get => _currentStep;
        private set
        {
            if (SetProperty(ref _currentStep, value))
            {
                NotifyDerivedStateChanged();
            }
        }
    }

    public bool IsCompleted
    {
        get => _isCompleted;
        private set => SetProperty(ref _isCompleted, value);
    }

    // Flags de visibilidad por paso (consumidos via BooleanToVisibilityConverter en XAML).
    public bool IsWelcomeStep => CurrentStep == OnboardingStep.Welcome;
    public bool IsProviderStep => CurrentStep == OnboardingStep.Provider;
    public bool IsHotkeyStep => CurrentStep == OnboardingStep.Hotkey;
    public bool IsPruebaStep => CurrentStep == OnboardingStep.Prueba;

    // Stepper: visible solo en los 3 pasos numerados (no en Welcome).
    public bool IsStepperVisible => CurrentStep != OnboardingStep.Welcome;

    // Por step del stepper:
    //   Done = el step ya quedó atrás (círculo brand sólido + check).
    //   Current = es el step actual (círculo brand sólido sin check).
    //   Upcoming (else) = todavía no llegamos (círculo border default).
    public bool IsStep1Done => (int)CurrentStep > (int)OnboardingStep.Provider;
    public bool IsStep1Current => CurrentStep == OnboardingStep.Provider;

    public bool IsStep2Done => (int)CurrentStep > (int)OnboardingStep.Hotkey;
    public bool IsStep2Current => CurrentStep == OnboardingStep.Hotkey;

    public bool IsStep3Done => false; // Step 3 (Prueba) nunca está "done" mientras estás en el wizard.
    public bool IsStep3Current => CurrentStep == OnboardingStep.Prueba;

    // Línea entre stepper 1→2 verde si ya pasamos el paso 1 (estamos en 2 o más).
    public bool IsLine12Active => (int)CurrentStep >= (int)OnboardingStep.Hotkey;
    public bool IsLine23Active => (int)CurrentStep >= (int)OnboardingStep.Prueba;

    public string StepperLabel => CurrentStep switch
    {
        OnboardingStep.Provider => "Paso 1 de 3",
        OnboardingStep.Hotkey => "Paso 2 de 3",
        OnboardingStep.Prueba => "Paso 3 de 3",
        _ => string.Empty,
    };

    // Footer:
    //
    // Welcome:                                    [ Empezar → ]   (centrado, primario único)
    // Provider (paso 1):                                   [ Siguiente → ]
    // Hotkey (paso 2):    [ ← Atrás ]                      [ Siguiente → ]
    // Prueba (paso 3):    [ ← Atrás ]      [ Saltar ]      [ Finalizar  ]
    //
    // (Decisión: sin Atrás en 1.0 Welcome ni en 1.3 Prueba — `flows.md` D del onboarding.
    // El ticket EP-3.1 dice "retrocede salvo en paso 1" pero `flows.md` es source-of-truth UX:
    // desde Prueba ya configuraste todo, retroceder sería raro).
    public bool IsBackVisible => CurrentStep == OnboardingStep.Hotkey;
    public bool IsSkipVisible => CurrentStep == OnboardingStep.Prueba;
    public bool IsNextCenteredOnly => CurrentStep == OnboardingStep.Welcome;

    public string NextButtonLabel => CurrentStep switch
    {
        OnboardingStep.Welcome => "Empezar",
        OnboardingStep.Prueba => "Finalizar",
        _ => "Siguiente",
    };

    // CanGoNext por paso. Welcome y Hotkey/Prueba siguen siendo `true` hasta que sus
    // sub-tasks (EP-3.5/3.7) los cablean. Provider exige IsConnectionOk del paso 1.1
    // (EP-3.3 cierra ese requisito) y que no estemos en medio de un Save (EP-3.4).
    public bool CanGoNext => CurrentStep switch
    {
        OnboardingStep.Provider => Provider.IsConnectionOk && !Provider.IsSaving,
        _ => true,
    };

    public bool CanGoBack => IsBackVisible;

    public ICommand GoNextCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand SkipCommand { get; }

    private void GoNext()
    {
        if (CurrentStep == OnboardingStep.Prueba)
        {
            CompleteOnboarding(skipped: false);
            return;
        }

        // Salir del paso Provider exige persistir la config (EP-3.4). El RelayCommand es
        // sync, así que disparamos la corrutina y avanzamos solo cuando completa OK. Si
        // falla, el VM mostró el error inline vía Provider.SaveError y nos quedamos en
        // el mismo step.
        if (CurrentStep == OnboardingStep.Provider)
        {
            _ = AdvanceFromProviderAsync();
            return;
        }

        CurrentStep = CurrentStep + 1;
        _logger.LogDebug("Onboarding → {Step}", CurrentStep);
    }

    private async Task AdvanceFromProviderAsync()
    {
        // Re-checkear el guard porque GoNext es disparado por el CommandManager y entre el
        // CanGoNext check y el await podríamos haber cambiado de step.
        if (CurrentStep != OnboardingStep.Provider) return;

        OnPropertyChanged(nameof(CanGoNext));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        var ok = await Provider.SaveAsync().ConfigureAwait(true);

        OnPropertyChanged(nameof(CanGoNext));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        if (!ok)
        {
            _logger.LogInformation("Avance bloqueado en step Provider: SaveAsync devolvió false");
            return;
        }

        CurrentStep = OnboardingStep.Hotkey;
        _logger.LogDebug("Onboarding → {Step}", CurrentStep);
    }

    private void GoBack()
    {
        if (!CanGoBack) return;
        CurrentStep = CurrentStep - 1;
        _logger.LogDebug("Onboarding ← {Step}", CurrentStep);
    }

    private void Skip()
    {
        if (!IsSkipVisible) return;
        _logger.LogInformation("Onboarding skipped en paso {Step}", CurrentStep);
        CompleteOnboarding(skipped: true);
    }

    private void CompleteOnboarding(bool skipped)
    {
        IsCompleted = true;
        _logger.LogInformation("Onboarding completado (skipped={Skipped})", skipped);
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsProviderStep));
        OnPropertyChanged(nameof(IsHotkeyStep));
        OnPropertyChanged(nameof(IsPruebaStep));
        OnPropertyChanged(nameof(IsStepperVisible));
        OnPropertyChanged(nameof(IsStep1Done));
        OnPropertyChanged(nameof(IsStep1Current));
        OnPropertyChanged(nameof(IsStep2Done));
        OnPropertyChanged(nameof(IsStep2Current));
        OnPropertyChanged(nameof(IsStep3Done));
        OnPropertyChanged(nameof(IsStep3Current));
        OnPropertyChanged(nameof(IsLine12Active));
        OnPropertyChanged(nameof(IsLine23Active));
        OnPropertyChanged(nameof(StepperLabel));
        OnPropertyChanged(nameof(IsBackVisible));
        OnPropertyChanged(nameof(IsSkipVisible));
        OnPropertyChanged(nameof(IsNextCenteredOnly));
        OnPropertyChanged(nameof(NextButtonLabel));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
    }
}
