using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Auth;
using Spikit.Services.Onboarding;
using Spikit.Services.Settings;

namespace Spikit.ViewModels.Onboarding;

// Coordina el wizard de onboarding. Mantiene el paso activo y expone:
// - Flags de visibilidad por paso para que el ContentControl muestre el UserControl correcto.
// - Estado del stepper (●━━○━━○) por cada uno de los pasos numerados (2 o 3 según tier).
// - Visibilidad de los botones de footer (Atrás / Saltar / Siguiente / Empezar / Finalizar).
//
// EP-11.5 — bifurcación por tier (ADR-0008 / design-system §10.13):
//   - BYOK              → Welcome → Provider → Hotkey → Prueba → Completed (3 steps)
//   - Trial / Pro       → Welcome → Hotkey → Prueba → Completed             (2 steps, sin Provider)
//
// El tier se snapshot-ea al construir el VM desde IAuthService.CurrentEntitlement.
// Si tier es null al construir (entitlement no cargado por race con auth init), default a BYOK
// — incluye el Provider step que es la opción más conservadora (peor caso: un Trial/Pro
// pasaría por una pantalla de provider, pero el gate de auth de EP-11.4 garantiza que
// el entitlement esté cargado antes de mostrar este wizard en el 99% de los casos).
public sealed class OnboardingViewModel : ViewModelBase
{
    private readonly ILogger<OnboardingViewModel> _logger;
    private OnboardingStep _currentStep = OnboardingStep.Welcome;
    private bool _isCompleted;
    private bool _sendCrashReports;

    private readonly IOnboardingCompletionStore _completionStore;
    private readonly ISettingsService _settingsService;
    private readonly OnboardingTierVariant _tierVariant;

    public OnboardingViewModel(
        ILogger<OnboardingViewModel> logger,
        ProviderStepViewModel provider,
        HotkeyStepViewModel hotkey,
        PruebaStepViewModel prueba,
        IOnboardingCompletionStore completionStore,
        ISettingsService settingsService,
        IAuthService authService)
    {
        _logger = logger;
        _completionStore = completionStore;
        _settingsService = settingsService;
        Provider = provider;
        Hotkey = hotkey;
        Prueba = prueba;

        // EP-11.5 — snapshot del tier al construir. La decisión queda fija para todo el
        // wizard; si el tier muta mid-flow (caso edge: webhook Stripe), el wizard sigue
        // con la variante original (decisión documentada en design-system §10.13).
        _tierVariant = ResolveTierVariant(authService);
        _logger.LogInformation(
            "Onboarding bifurcation: tier={Tier} → variant={Variant}",
            authService.CurrentEntitlement?.Tier, _tierVariant);

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

    private static OnboardingTierVariant ResolveTierVariant(IAuthService authService) =>
        authService.CurrentEntitlement?.Tier switch
        {
            Tier.Trial => OnboardingTierVariant.Trial,
            Tier.Pro => OnboardingTierVariant.Pro,
            Tier.Byok => OnboardingTierVariant.Byok,
            // Cualquier otro tier (Expired) o null → BYOK como default seguro. En el flow
            // real de EP-11.4, el LoginWindow garantiza que el entitlement esté cargado
            // antes de transicionar al wizard, así que este path solo se da en tests o
            // en el edge case "fetch entitlement falló post-success" (raro).
            _ => OnboardingTierVariant.Byok,
        };

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

    // ===== Tier variant (EP-11.5) =====

    public OnboardingTierVariant TierVariant => _tierVariant;
    public bool IsByokVariant => _tierVariant == OnboardingTierVariant.Byok;
    public bool IsTrialVariant => _tierVariant == OnboardingTierVariant.Trial;
    public bool IsProVariant => _tierVariant == OnboardingTierVariant.Pro;

    // ===== Welcome copy bifurcado por tier (consumido por bindings del WelcomeStepView) =====

    public string WelcomeH1 => _tierVariant switch
    {
        OnboardingTierVariant.Pro => "Gracias por pasarte a Pro 🚀",
        // Trial y Byok comparten el h1 genérico; lo que cambia es el sub-h tier-specific.
        _ => "Bienvenido a Spikit",
    };

    // Sub-h tier-specific (sobre la lista de pasos). Pro no usa sub-h — el h1 ya dice todo.
    public string WelcomeTierMessage => _tierVariant switch
    {
        OnboardingTierVariant.Byok => "Tu acceso BYOK es de por vida.",
        OnboardingTierVariant.Trial => "Tenés 14 días para probarlo todo. Sin tarjeta, sin trabas.",
        _ => string.Empty,
    };

    public bool WelcomeTierMessageVisible => !string.IsNullOrEmpty(WelcomeTierMessage);

    // Intro a la lista de pasos. N varía por tier.
    public string WelcomeIntro => IsByokVariant
        ? "Vamos a configurarlo en 3 pasos rápidos."
        : "Vamos a configurarlo en 2 pasos rápidos.";

    // Items de la lista numerada del Welcome. Si IsByokVariant=false, Step1Text es Hotkey
    // (no Provider) y Step3 no se muestra.
    public string WelcomeStep1Text => IsByokVariant
        ? "Conectá tu API key"
        : "Elegí tu hotkey";
    public string WelcomeStep2Text => IsByokVariant
        ? "Elegí tu hotkey"
        : "Probalo";
    public string WelcomeStep3Text => "Probalo"; // solo se muestra en BYOK
    public bool WelcomeStep3Visible => IsByokVariant;

    // Caption tiempo. BYOK pide ~2 min (Provider requiere ingresar key); Trial/Pro ~1 min.
    public string WelcomeTimeText => IsByokVariant
        ? "¿Ya configurás API keys? Esto te lleva ~2 min."
        : "Esto te lleva ~1 minuto.";

    // ===== Flags de visibilidad por step =====

    public bool IsWelcomeStep => CurrentStep == OnboardingStep.Welcome;
    public bool IsProviderStep => CurrentStep == OnboardingStep.Provider;
    public bool IsHotkeyStep => CurrentStep == OnboardingStep.Hotkey;
    public bool IsPruebaStep => CurrentStep == OnboardingStep.Prueba;
    public bool IsCompletedStep => CurrentStep == OnboardingStep.Completed;

    // Stepper: visible solo en los pasos numerados (no en Welcome ni en Completed).
    public bool IsStepperVisible => CurrentStep != OnboardingStep.Welcome
                                    && CurrentStep != OnboardingStep.Completed;

    // Footer del Window: oculto en el step Completed (su CTA "Empezar" vive dentro
    // del UserControl). En Welcome/Provider/Hotkey/Prueba sigue visible con sus botones.
    public bool IsFooterVisible => CurrentStep != OnboardingStep.Completed;

    // ===== Stepper visual =====
    //
    // Step1/Step2/Step3 representan POSICIONES del stepper visual, no OnboardingStep
    // específicos. El mapping cambia por variante:
    //
    //   BYOK:        Step1=Provider, Step2=Hotkey, Step3=Prueba  (3 círculos visibles)
    //   Trial/Pro:   Step1=Hotkey,   Step2=Prueba, Step3=oculto  (2 círculos visibles)
    //
    // Done = el step ya quedó atrás (círculo brand sólido + check).
    // Current = es el step actual (círculo brand sólido sin check).
    // Upcoming (else) = todavía no llegamos (círculo border default).

    public bool IsStep1Done => IsByokVariant
        ? (int)CurrentStep > (int)OnboardingStep.Provider
        : (int)CurrentStep > (int)OnboardingStep.Hotkey;
    public bool IsStep1Current => IsByokVariant
        ? CurrentStep == OnboardingStep.Provider
        : CurrentStep == OnboardingStep.Hotkey;

    public bool IsStep2Done => IsByokVariant
        ? (int)CurrentStep > (int)OnboardingStep.Hotkey
        : false; // En Trial/Pro Step2 es Prueba (último), nunca "done" durante el wizard.
    public bool IsStep2Current => IsByokVariant
        ? CurrentStep == OnboardingStep.Hotkey
        : CurrentStep == OnboardingStep.Prueba;

    // Step3 solo existe en BYOK (Prueba). En Trial/Pro se colapsa el círculo + la línea 2→3.
    public bool IsStep3Done => false; // Step3 (Prueba) nunca está "done" mientras estás en el wizard.
    public bool IsStep3Current => IsByokVariant && CurrentStep == OnboardingStep.Prueba;
    public bool IsStep3Visible => IsByokVariant;

    // Línea 1→2 activa si pasamos el primer step. Mapping por variante:
    //   BYOK: activa si CurrentStep >= Hotkey
    //   Trial/Pro: activa si CurrentStep >= Prueba
    public bool IsLine12Active => IsByokVariant
        ? (int)CurrentStep >= (int)OnboardingStep.Hotkey
        : (int)CurrentStep >= (int)OnboardingStep.Prueba;
    // Línea 2→3 solo aplica en BYOK.
    public bool IsLine23Active => IsByokVariant && (int)CurrentStep >= (int)OnboardingStep.Prueba;
    public bool IsLine23Visible => IsByokVariant;

    // Label "Paso N de M". Denominador 3 (BYOK) o 2 (Trial/Pro).
    public string StepperLabel => (CurrentStep, IsByokVariant) switch
    {
        (OnboardingStep.Provider, true) => "Paso 1 de 3",
        (OnboardingStep.Hotkey, true) => "Paso 2 de 3",
        (OnboardingStep.Prueba, true) => "Paso 3 de 3",
        (OnboardingStep.Hotkey, false) => "Paso 1 de 2",
        (OnboardingStep.Prueba, false) => "Paso 2 de 2",
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

        // EP-11.5 — desde Welcome el siguiente step depende del tier:
        //   BYOK → Provider (paso 1)
        //   Trial/Pro → Hotkey (paso 1, saltea Provider)
        if (CurrentStep == OnboardingStep.Welcome)
        {
            CurrentStep = IsByokVariant ? OnboardingStep.Provider : OnboardingStep.Hotkey;
            _logger.LogDebug("Onboarding → {Step} (variant={Variant})", CurrentStep, _tierVariant);
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

        // EP-11.5 — desde Hotkey, retroceder a Welcome en Trial/Pro (no hay Provider).
        // En BYOK retrocede a Provider (paso 1).
        if (CurrentStep == OnboardingStep.Hotkey && !IsByokVariant)
        {
            CurrentStep = OnboardingStep.Welcome;
            _logger.LogDebug("Onboarding ← {Step} (skip Provider en variant={Variant})",
                CurrentStep, _tierVariant);
            return;
        }

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
        // IsStep3Visible / IsLine23Visible no cambian con CurrentStep (dependen solo
        // del tier, snapshot al ctor), pero se notifican igual por si algún DataTrigger
        // del XAML las usa con StringFormat/IValueConverter que rebindee on-change.
    }
}
