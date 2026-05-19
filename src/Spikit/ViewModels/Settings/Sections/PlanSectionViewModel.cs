using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Auth;
using Spikit.Services.Billing;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Plan / Account en Settings (EP-10.12). Combina el estado de auth
// (logged out vs in + email) con el entitlement actual (Trial / Pro / BYOK / Expired)
// y expone CTAs hacia los flows externos: Login (browser), Logout, Upgrade (Stripe
// Checkout → browser), Manage subscription (Stripe Portal → browser).
//
// Reactivo: se suscribe a IAuthService.StateChanged y refresca todas las props
// derivadas cuando el state cambia (login/logout, refresh entitlement post-Stripe).
public sealed class PlanSectionViewModel : ViewModelBase, IDisposable
{
    public const string MonthlyLookupKey = "pro_monthly";
    public const string YearlyLookupKey = "pro_yearly";

    private readonly IAuthService _auth;
    private readonly IStripeBillingClient _billing;
    private readonly IBrowserLauncher _browser;
    private readonly ILogger<PlanSectionViewModel> _logger;
    private readonly TimeProvider _time;

    private bool _isBusy;
    private string? _busyMessage;
    private string? _errorMessage;
    private string _billingInterval = "monthly";

    public PlanSectionViewModel(
        IAuthService auth,
        IStripeBillingClient billing,
        IBrowserLauncher browser,
        ILogger<PlanSectionViewModel> logger)
        : this(auth, billing, browser, TimeProvider.System, logger)
    {
    }

    // Constructor extendido para tests. TimeProvider afecta el cálculo de "días restantes".
    public PlanSectionViewModel(
        IAuthService auth,
        IStripeBillingClient billing,
        IBrowserLauncher browser,
        TimeProvider time,
        ILogger<PlanSectionViewModel> logger)
    {
        _auth = auth;
        _billing = billing;
        _browser = browser;
        _time = time;
        _logger = logger;

        LoginCommand = new RelayCommand(OnLogin, () => CanLogin && !IsBusy);
        LogoutCommand = new RelayCommand(OnLogout, () => CanLogout && !IsBusy);
        UpgradeCommand = new RelayCommand(OnUpgrade, () => CanUpgrade && !IsBusy);
        ManageSubscriptionCommand = new RelayCommand(OnManageSubscription, () => CanManageSubscription && !IsBusy);
        SelectIntervalCommand = new RelayCommand<string>(SetBillingInterval);

        _auth.StateChanged += OnAuthStateChanged;
    }

    // ====== Auth state ======

    public bool IsLoggedIn => _auth.State == AuthSessionState.LoggedIn;
    public bool IsLoggedOut => !IsLoggedIn;
    public string? UserEmail => _auth.CurrentProfile?.Email;

    // ====== Tier / entitlement ======

    public Tier? CurrentTier => _auth.CurrentEntitlement?.Tier;

    // True si estamos logueados pero el cache de entitlement todavía no se pobló
    // (ej. arranque sin red, o refresh todavía en curso).
    public bool ShowLoadingDetails => IsLoggedIn && _auth.CurrentEntitlement is null;

    public bool ShowTrialDetails => CurrentTier == Tier.Trial;
    public bool ShowProDetails => CurrentTier == Tier.Pro;
    public bool ShowByokDetails => CurrentTier == Tier.Byok;
    public bool ShowExpiredDetails => CurrentTier == Tier.Expired;

    public string TierLabel => CurrentTier switch
    {
        Tier.Trial => "Trial",
        Tier.Pro => "Pro",
        Tier.Byok => "Creator program",
        Tier.Expired => "Expired",
        _ => string.Empty,
    };

    public string TierDescription => CurrentTier switch
    {
        Tier.Trial => "Estás probando Spikit. Tenés acceso completo durante 14 días.",
        Tier.Pro => "Tenés acceso completo a Spikit. Gracias por bancar el producto.",
        Tier.Byok => "Tenés acceso de por vida usando tu propia API key.",
        Tier.Expired => "Tu acceso terminó. Suscribite a Pro para seguir usando Spikit.",
        _ => string.Empty,
    };

    public string? TrialCountdown
    {
        get
        {
            if (CurrentTier != Tier.Trial) return null;
            var endsAt = _auth.CurrentEntitlement?.TrialEndsAt;
            if (endsAt is null) return null;
            var days = (int)Math.Max(0, Math.Ceiling((endsAt.Value - _time.GetUtcNow()).TotalDays));
            return days switch
            {
                0 => "Termina hoy",
                1 => "Queda 1 día",
                _ => $"Quedan {days} días",
            };
        }
    }

    public string? ProRenewsAt
    {
        get
        {
            if (CurrentTier != Tier.Pro) return null;
            var renewsAt = _auth.CurrentEntitlement?.ProRenewsAt;
            return renewsAt?.ToLocalTime().ToString("d 'de' MMMM 'de' yyyy", new CultureInfo("es-AR"));
        }
    }

    public string? ByokGraceCountdown
    {
        get
        {
            if (CurrentTier != Tier.Byok) return null;
            var endsAt = _auth.CurrentEntitlement?.ByokGraceEndsAt;
            if (endsAt is null) return null;
            var days = (int)Math.Max(0, Math.Ceiling((endsAt.Value - _time.GetUtcNow()).TotalDays));
            return days switch
            {
                0 => "Tu acceso BYOK termina hoy",
                1 => "Te queda 1 día de acceso BYOK",
                _ => $"Te quedan {days} días de acceso BYOK",
            };
        }
    }

    public bool ShowByokGrace => CurrentTier == Tier.Byok && _auth.CurrentEntitlement?.ByokGraceEndsAt is not null;

    // ====== Billing interval toggle ======

    public string BillingInterval
    {
        get => _billingInterval;
        set
        {
            if (SetProperty(ref _billingInterval, value))
            {
                OnPropertyChanged(nameof(IsMonthlySelected));
                OnPropertyChanged(nameof(IsYearlySelected));
            }
        }
    }

    public bool IsMonthlySelected => string.Equals(_billingInterval, "monthly", StringComparison.OrdinalIgnoreCase);
    public bool IsYearlySelected => string.Equals(_billingInterval, "yearly", StringComparison.OrdinalIgnoreCase);

    private void SetBillingInterval(string? interval)
    {
        if (string.IsNullOrEmpty(interval)) return;
        BillingInterval = interval;
    }

    // ====== Commands + can-execute flags ======

    public bool CanLogin => IsLoggedOut;
    public bool CanLogout => IsLoggedIn;
    public bool CanUpgrade => IsLoggedIn && CurrentTier is Tier.Trial or Tier.Expired or Tier.Byok;
    public bool CanManageSubscription => IsLoggedIn && CurrentTier == Tier.Pro;

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand UpgradeCommand { get; }
    public ICommand ManageSubscriptionCommand { get; }
    public ICommand SelectIntervalCommand { get; }

    // ====== Busy / error state ======

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isBusy;
    public string? BusyMessage
    {
        get => _busyMessage;
        private set
        {
            if (SetProperty(ref _busyMessage, value)) OnPropertyChanged(nameof(HasBusyMessage));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasErrorMessage));
        }
    }

    public bool HasBusyMessage => !string.IsNullOrEmpty(_busyMessage);
    public bool HasErrorMessage => !string.IsNullOrEmpty(_errorMessage);

    // ====== Command handlers ======

    private async void OnLogin()
    {
        ErrorMessage = null;
        try
        {
            await _auth.StartLoginAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login fallo al abrir browser");
            ErrorMessage = "No pudimos abrir el browser para iniciar sesión.";
        }
    }

    private async void OnLogout()
    {
        ErrorMessage = null;
        IsBusy = true;
        BusyMessage = "Cerrando sesión…";
        try
        {
            await _auth.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Logout fallo");
            ErrorMessage = "No pudimos cerrar la sesión en el server. Probá de nuevo.";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    private async void OnUpgrade()
    {
        ErrorMessage = null;
        IsBusy = true;
        BusyMessage = "Generando link de pago…";
        try
        {
            var token = await _auth.GetCurrentAccessTokenAsync(CancellationToken.None).ConfigureAwait(true);
            if (token is null)
            {
                ErrorMessage = "Tu sesión venció. Iniciá sesión y probá de nuevo.";
                return;
            }

            var lookupKey = IsYearlySelected ? YearlyLookupKey : MonthlyLookupKey;
            var url = await _billing
                .CreateCheckoutSessionAsync(token, lookupKey, CancellationToken.None)
                .ConfigureAwait(true);
            _browser.Open(url);
        }
        catch (AuthTokenInvalidException)
        {
            ErrorMessage = "Tu sesión venció. Iniciá sesión y probá de nuevo.";
        }
        catch (BillingException ex)
        {
            _logger.LogError(ex, "Stripe Checkout falló");
            ErrorMessage = "No pudimos generar el link de pago. Probá de nuevo en un momento.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upgrade falló con excepción no controlada");
            ErrorMessage = "Ocurrió un error inesperado. Probá de nuevo.";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    private async void OnManageSubscription()
    {
        ErrorMessage = null;
        IsBusy = true;
        BusyMessage = "Abriendo el portal de Stripe…";
        try
        {
            var token = await _auth.GetCurrentAccessTokenAsync(CancellationToken.None).ConfigureAwait(true);
            if (token is null)
            {
                ErrorMessage = "Tu sesión venció. Iniciá sesión y probá de nuevo.";
                return;
            }

            var url = await _billing.CreatePortalSessionAsync(token, CancellationToken.None).ConfigureAwait(true);
            _browser.Open(url);
        }
        catch (AuthTokenInvalidException)
        {
            ErrorMessage = "Tu sesión venció. Iniciá sesión y probá de nuevo.";
        }
        catch (BillingException ex)
        {
            _logger.LogError(ex, "Portal Stripe falló");
            ErrorMessage = "No pudimos abrir el portal de suscripción. Probá de nuevo en un momento.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ManageSubscription falló con excepción no controlada");
            ErrorMessage = "Ocurrió un error inesperado. Probá de nuevo.";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    // ====== Reactividad sobre IAuthService ======

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        // Todas las props derivadas dependen de _auth.State y _auth.CurrentEntitlement —
        // notificamos a la UI para que rebinds en bloque. No hace falta granularidad
        // porque el panel se redibuja entero al cambiar el tier.
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(IsLoggedOut));
        OnPropertyChanged(nameof(UserEmail));
        OnPropertyChanged(nameof(CurrentTier));
        OnPropertyChanged(nameof(ShowLoadingDetails));
        OnPropertyChanged(nameof(ShowTrialDetails));
        OnPropertyChanged(nameof(ShowProDetails));
        OnPropertyChanged(nameof(ShowByokDetails));
        OnPropertyChanged(nameof(ShowByokGrace));
        OnPropertyChanged(nameof(ShowExpiredDetails));
        OnPropertyChanged(nameof(TierLabel));
        OnPropertyChanged(nameof(TierDescription));
        OnPropertyChanged(nameof(TrialCountdown));
        OnPropertyChanged(nameof(ProRenewsAt));
        OnPropertyChanged(nameof(ByokGraceCountdown));
        OnPropertyChanged(nameof(CanLogin));
        OnPropertyChanged(nameof(CanLogout));
        OnPropertyChanged(nameof(CanUpgrade));
        OnPropertyChanged(nameof(CanManageSubscription));
    }

    public void Dispose() => _auth.StateChanged -= OnAuthStateChanged;
}
