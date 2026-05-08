using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Onboarding;
using Spikit.Services.Settings;

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
    private bool _sendCrashReports;

    private readonly IOnboardingCompletionStore _completionStore;
    private readonly ISettingsService _settingsService;

    public OnboardingViewModel(
        ILogger<OnboardingViewModel> logger,
        ProviderStepViewModel provider,
        HotkeyStepViewModel hotkey,
        PruebaStepViewModel prueba,
        IOnboardingCompletionStore completionStore,
        ISettingsService settingsService)
    {
        _logger = logger;
        _completionStore = completionStore;
        _settingsService = settingsService;
        Provider = provider;
        Hotkey = hotkey;
        Prueba = prueba;

        // Hidratamos el flag desde settings: si el usuario reabre el onboarding tras
        // haber tocado el toggle en una sesión anterior (que cerró sin apretar Empezar),
        // el checkbox del step Completed refleja esa preferencia.
        _sendCrashReports = _settingsService.Load().Privacy.SendCrashReports;

        // Suscripciones a eventos de los step VMs: cuando cambia algo que afecta CanGoNext
        // recomputamos y notificamos al CommandManager para que el botón "Siguiente" se
        // enable/disable.
        Provider.ConnectionStateChanged += OnStepStateChanged;
        Hotkey.HotkeyStateChanged += OnStepStateChanged;
        Prueba.TextStateChanged += OnStepStateChanged;

        GoNextCommand = new RelayCommand(GoNext, () => CanGoNext);
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        SkipCommand = new RelayCommand(Skip, () => IsSkipVisible);
        FinishCommand = new RelayCommand(Finish);
    }

    // VMs por paso, expuestos como propiedades para que cada UserControl haga
    // DataContext="{Binding Provider}" / {Binding Hotkey} / {Binding Prueba} en OnboardingWindow.
    public ProviderStepViewModel Provider { get; }
    public HotkeyStepViewModel Hotkey { get; }
    public PruebaStepViewModel Prueba { get; }

    // Disparado la primera vez que el wizard transiciona al step Prueba. La OnboardingWindow
    // lo usa para levantar la pill flotante + Start del DictationOrchestrator antes de que
    // el usuario apriete el hotkey configurado en EP-3.6.
    public event EventHandler? PruebaStepEntered;

    private void OnStepStateChanged(object? sender, EventArgs e)
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

    // Toggle "Enviar crash reports anónimos" en el step Completed (EP-8.3).
    // Default OFF coherente con privacy-strict del producto (Q-3 cerrada en infra.md).
    // Persistencia inmediata vía ISettingsService — si el usuario lo activa y luego
    // cierra el wizard sin apretar Empezar, la elección se mantiene para el próximo
    // arranque. El bootstrap de Sentry en Program.cs lee este flag en el siguiente
    // launch, no en vivo (Sentry SDK es bootstrap-time only).
    public bool SendCrashReports
    {
        get => _sendCrashReports;
        set
        {
            if (!SetProperty(ref _sendCrashReports, value)) return;
            try
            {
                var settings = _settingsService.Load();
                settings.Privacy.SendCrashReports = value;
                _settingsService.Save(settings);
                _logger.LogInformation("Onboarding toggle Privacy.sendCrashReports → {Value}", value);
            }
            catch (Exception ex)
            {
                // Persistir falló (disco lleno, perms). El estado del VM ya cambió y
                // el toggle visual refleja la intención del usuario; el próximo cambio
                // intenta de nuevo. No mostramos error inline porque el toggle es opt-in
                // — fallar silencioso es mejor UX que un mensaje agresivo en el step
                // de cierre.
                _logger.LogWarning(ex, "No se pudo persistir Privacy.sendCrashReports desde el onboarding");
            }
        }
    }

    // Flags de visibilidad por paso (consumidos via BooleanToVisibilityConverter en XAML).
    public bool IsWelcomeStep => CurrentStep == OnboardingStep.Welcome;
    public bool IsProviderStep => CurrentStep == OnboardingStep.Provider;
    public bool IsHotkeyStep => CurrentStep == OnboardingStep.Hotkey;
    public bool IsPruebaStep => CurrentStep == OnboardingStep.Prueba;
    public bool IsCompletedStep => CurrentStep == OnboardingStep.Completed;

    // Stepper: visible solo en los 3 pasos numerados (no en Welcome ni en Completed).
    public bool IsStepperVisible => CurrentStep != OnboardingStep.Welcome
                                    && CurrentStep != OnboardingStep.Completed;

    // Footer del Window: oculto en el step Completed (su CTA "Empezar" vive dentro
    // del UserControl). En Welcome/Provider/Hotkey/Prueba sigue visible con sus botones.
    public bool IsFooterVisible => CurrentStep != OnboardingStep.Completed;

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

    // CanGoNext por paso.
    //   Provider: IsConnectionOk + no estamos en medio del Save (EP-3.3 + EP-3.4).
    //   Hotkey:   hay combinación capturada + no estamos en medio del Save (EP-3.5 + EP-3.6).
    //             Warning sin modificadora NO bloquea — el ticket lo pide explícito.
    //   Prueba:   la TextBox tiene texto (EP-3.7). Saltar siempre disponible aparte (US-1.3).
    public bool CanGoNext => CurrentStep switch
    {
        OnboardingStep.Provider => Provider.IsConnectionOk && !Provider.IsSaving,
        OnboardingStep.Hotkey => Hotkey.HasHotkey && !Hotkey.IsSaving,
        OnboardingStep.Prueba => Prueba.HasText,
        _ => true,
    };

    public bool CanGoBack => IsBackVisible;

    public ICommand GoNextCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand SkipCommand { get; }

    // CTA del step Completed ("Empezar a usar Spikit"). El click cierra la window y
    // dispara la transición inline a MainApp en App.xaml.cs.
    public ICommand FinishCommand { get; }

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

        // Mismo patrón para el paso Hotkey (EP-3.6): persistir + reconfigurar runtime
        // antes de avanzar. CB-7 (combinación tomada) se muestra inline y no avanza.
        if (CurrentStep == OnboardingStep.Hotkey)
        {
            _ = AdvanceFromHotkeyAsync();
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

    private async Task AdvanceFromHotkeyAsync()
    {
        if (CurrentStep != OnboardingStep.Hotkey) return;

        OnPropertyChanged(nameof(CanGoNext));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        var ok = await Hotkey.SaveAsync().ConfigureAwait(true);

        OnPropertyChanged(nameof(CanGoNext));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();

        if (!ok)
        {
            _logger.LogInformation("Avance bloqueado en step Hotkey: SaveAsync devolvió false");
            return;
        }

        CurrentStep = OnboardingStep.Prueba;
        _logger.LogDebug("Onboarding → {Step}", CurrentStep);

        // EP-3.7: notificar a la window para que active la pill + DictationOrchestrator
        // ahora que la hotkey ya está registrada en el HotkeyService (EP-3.6 lo hizo
        // adentro del SaveAsync de arriba).
        PruebaStepEntered?.Invoke(this, EventArgs.Empty);
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
        // EP-3.8: persistir el flag ANTES de transicionar al step Completed. Si la
        // persistencia falla, logueamos pero seguimos al step Completed — el usuario
        // terminó su intención y bloquearlo acá sería peor UX. Próximo arranque va a
        // re-abrir el onboarding y listo (estado idempotente, los steps anteriores ya
        // persistieron lo suyo).
        try
        {
            _completionStore.MarkCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo persistir el flag onboardingCompleted (siguiendo igual)");
        }

        // IsCompleted=true ANTES de cambiar el step para que el OnClosing de la window
        // no muestre el confirm de "cerrar sin terminar" si el usuario X-cierra desde
        // el step Completed.
        IsCompleted = true;
        _logger.LogInformation("Onboarding completado (skipped={Skipped})", skipped);

        // Transición al step de cierre con animación. El OnboardingCompleted event
        // se dispara recién cuando el usuario apriete "Empezar" (FinishCommand).
        CurrentStep = OnboardingStep.Completed;
    }

    private void Finish()
    {
        // El usuario apretó "Empezar a usar Spikit" en el step Completed.
        // App.xaml.cs ya está suscripto al evento → cierra la window + transiciona a MainApp.
        _logger.LogDebug("Finish del onboarding: disparando OnboardingCompleted");
        OnboardingCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsWelcomeStep));
        OnPropertyChanged(nameof(IsProviderStep));
        OnPropertyChanged(nameof(IsHotkeyStep));
        OnPropertyChanged(nameof(IsPruebaStep));
        OnPropertyChanged(nameof(IsCompletedStep));
        OnPropertyChanged(nameof(IsStepperVisible));
        OnPropertyChanged(nameof(IsFooterVisible));
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
