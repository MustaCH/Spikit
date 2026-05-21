using System.Net.Http;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Services.Auth;

namespace Spikit.ViewModels.Auth;

// ViewModel del LoginWindow (EP-11.3). Manualmente coordinado por App.xaml.cs vía
// tres entry points públicos:
//
//   - EnterIdle(variant)              — al mostrar el LoginWindow inicialmente
//   - HandleAuthPending(email)        — cuando llega `spikit://auth-pending?email=...`
//                                       (deep-link emitido por spikit.dev/auth post-send
//                                       del magic link, cierre Q-9 de ADR-0008)
//   - HandleAuthCallbackAsync(p, ct)  — cuando llega `spikit://auth-callback?...`
//                                       con tokens. Llama IAuthService internamente y
//                                       maneja errores con transición de estado UI.
//
// El cableado real de estos entry points desde el SpikitUriDispatcher + App.xaml.cs
// va en EP-11.4 (auth gate en startup). EP-11.3 sólo deja el VM listo para usar.
//
// Estados visuales en design-system.md §10.12. Diagrama de transiciones en LoginState.cs.
public sealed class LoginViewModel : ViewModelBase, IDisposable
{
    // Cooldown del botón "Reenviar email" (estado 0.2). Mismo valor que §10.11.B
    // (AccountWindow) — evita abuso de la API de Supabase.
    private const int ResendCooldownSeconds = 60;

    // Beat visual entre `ValidatingToken` y `Success` para honrar la spec §10.12 que
    // distingue 0.3 (validating) de 0.4 (loading_entitlement). El IAuthService.HandleAuthCallback
    // hace ambas operaciones internamente y sólo nos avisa al final con StateChanged, así
    // que mostramos `LoadingEntitlement` como bumper sintético — copy distinto, mismo
    // LogoWave shimmer — antes del microflash de éxito. Trade-off documentado en código.
    internal static readonly TimeSpan LoadingEntitlementBeat = TimeSpan.FromMilliseconds(150);

    // Microflash 0.5 antes del fade-out de la window (D-14 del flows.md).
    internal static readonly TimeSpan SuccessFlashDuration = TimeSpan.FromMilliseconds(200);

    // Después de 2s en ValidatingToken cambiamos el caption para no dejar al usuario
    // pensando que se colgó. Es UX puro — no afecta el flow.
    internal static readonly TimeSpan ValidatingCaptionSwitchAfter = TimeSpan.FromSeconds(2);

    private readonly IAuthService _auth;
    private readonly ILogger<LoginViewModel> _logger;
    private readonly TimeProvider _time;

    private LoginState _state = LoginState.Idle;
    private LoginIdleVariant _idleVariant = LoginIdleVariant.FirstLaunch;
    private string? _pendingEmail;
    private int _resendCooldownSec;
    private string _validatingCaption = DefaultValidatingCaption;
    private string? _errorReason;
    private int _consecutiveValidationErrors;

    // True mientras HandleAuthCallbackAsync está en vuelo. Sirve para que el handler
    // de StateChanged (que se dispara cuando IAuthService entra a LoggedIn) sepa que
    // el cierre lo va a manejar el propio HandleAuthCallbackAsync — sino dispararíamos
    // RequestClose dos veces.
    private bool _isHandlingCallback;

    private ITimer? _cooldownTimer;
    private ITimer? _watchdogTimer;       // 15 min en WaitingForMagicLink → vuelve a Idle
    private ITimer? _captionSwitchTimer;  // 2s en ValidatingToken → cambia el caption

    private const string DefaultValidatingCaption = "Esto suele tardar menos de un segundo.";
    private const string SlowValidatingCaption = "Esto está tardando un poco más de lo normal…";

    public LoginViewModel(
        IAuthService auth,
        ILogger<LoginViewModel> logger,
        TimeProvider? time = null)
    {
        _auth = auth;
        _logger = logger;
        _time = time ?? TimeProvider.System;

        StartLoginCommand = new AsyncRelayCommand(StartLoginAsync);
        ResendMagicLinkCommand = new AsyncRelayCommand(ResendMagicLinkAsync, () => !IsResendCoolingDown);
        StartOverCommand = new RelayCommand(StartOver);
        RetryNetworkCommand = new RelayCommand(RetryAfterNetworkError);

        _auth.StateChanged += OnAuthStateChanged;
        _auth.AuthPendingReceived += OnAuthPendingReceived;
    }

    // ===== State + variant =====

    public LoginState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                NotifyDerivedStateChanged();
            }
        }
    }

    public LoginIdleVariant IdleVariant
    {
        get => _idleVariant;
        private set
        {
            if (SetProperty(ref _idleVariant, value))
            {
                OnPropertyChanged(nameof(IdleBodyText));
            }
        }
    }

    public string IdleBodyText => IdleVariant switch
    {
        LoginIdleVariant.SessionExpired =>
            "Tu sesión expiró. Volvé a iniciar sesión para seguir usando Spikit.",
        _ =>
            "Iniciá sesión con tu email para empezar a dictar. Sin contraseñas: te mandamos un magic link.",
    };

    // ===== Pending email (estado 0.2) =====

    public string? PendingEmail
    {
        get => _pendingEmail;
        private set => SetProperty(ref _pendingEmail, value);
    }

    // ===== Resend cooldown =====

    public int ResendCooldownSec
    {
        get => _resendCooldownSec;
        private set
        {
            if (SetProperty(ref _resendCooldownSec, value))
            {
                OnPropertyChanged(nameof(IsResendCoolingDown));
                OnPropertyChanged(nameof(ResendButtonLabel));
                ResendMagicLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsResendCoolingDown => ResendCooldownSec > 0;

    public string ResendButtonLabel => IsResendCoolingDown
        ? $"Reenviar en {ResendCooldownSec}s"
        : "Reenviar email";

    // ===== Validating caption =====

    public string ValidatingCaption
    {
        get => _validatingCaption;
        private set => SetProperty(ref _validatingCaption, value);
    }

    // ===== Error reason =====

    public string? ErrorReason
    {
        get => _errorReason;
        private set => SetProperty(ref _errorReason, value);
    }

    // Tras 3 errores consecutivos de validación mostramos "¿Problemas? hello@spikit.dev"
    // debajo del CTA del estado 0.6 (mismo patrón que §10.11.D AccountWindow).
    public bool ShowSupportHint => _consecutiveValidationErrors >= 3;

    // ===== Derived state booleans (binding-friendly) =====

    public bool IsIdle => State == LoginState.Idle;
    public bool IsWaitingForMagicLink => State == LoginState.WaitingForMagicLink;
    public bool IsValidatingToken => State == LoginState.ValidatingToken;
    public bool IsLoadingEntitlement => State == LoginState.LoadingEntitlement;
    public bool IsSuccess => State == LoginState.Success;
    public bool IsErrorValidating => State == LoginState.ErrorValidating;
    public bool IsErrorNetwork => State == LoginState.ErrorNetwork;

    // ===== Commands =====

    public AsyncRelayCommand StartLoginCommand { get; }
    public AsyncRelayCommand ResendMagicLinkCommand { get; }
    public ICommand StartOverCommand { get; }
    public ICommand RetryNetworkCommand { get; }

    // ===== Events =====

    // Disparado cuando el VM determina que la window debe cerrarse: tras el microflash
    // de éxito, o cuando StateChanged externo informa LoggedIn (caso edge: alguien más
    // resolvió la sesión — multi-instancia, test).
    public event EventHandler? RequestClose;

    // ===== Entry points públicos =====

    // Llamado por App.xaml.cs al mostrar el LoginWindow inicialmente.
    public void EnterIdle(LoginIdleVariant variant = LoginIdleVariant.FirstLaunch)
    {
        StopAllTimers();
        IdleVariant = variant;
        ErrorReason = null;
        _consecutiveValidationErrors = 0;
        OnPropertyChanged(nameof(ShowSupportHint));
        State = LoginState.Idle;
    }

    // Llamado por App.xaml.cs cuando el SpikitUriDispatcher recibe un
    // `spikit://auth-pending?email=...`. El email viene URL-decoded por el parser.
    public void HandleAuthPending(string? email)
    {
        if (!AuthEmail.IsValid(email))
        {
            _logger.LogWarning(
                "HandleAuthPending ignorado: email inválido o vacío ({Email})",
                email ?? "(null)");
            return;
        }

        StopWatchdog();
        PendingEmail = email;
        State = LoginState.WaitingForMagicLink;
        StartCooldown();
        StartWatchdog();
    }

    // Llamado por App.xaml.cs cuando el SpikitUriDispatcher recibe un
    // `spikit://auth-callback?access_token=...`. El método orquesta la transición
    // Validating → LoadingEntitlement → Success (o → ErrorValidating/ErrorNetwork)
    // y al terminar exitosamente dispara RequestClose.
    public async Task HandleAuthCallbackAsync(
        IReadOnlyDictionary<string, string> queryParams,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryParams);

        _isHandlingCallback = true;
        try
        {
            StopWatchdog();
            ValidatingCaption = DefaultValidatingCaption;
            State = LoginState.ValidatingToken;
            StartCaptionSwitchTimer();

            AuthCallbackResult result;
            try
            {
                result = await _auth.HandleAuthCallbackAsync(queryParams, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HandleAuthCallback: red caída");
                State = LoginState.ErrorNetwork;
                return;
            }
            catch (AuthException ex)
            {
                _logger.LogWarning(ex, "HandleAuthCallback: auth error");
                State = LoginState.ErrorNetwork;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HandleAuthCallback: error inesperado");
                _consecutiveValidationErrors++;
                ErrorReason = "Algo salió mal al validar tu sesión.";
                State = LoginState.ErrorValidating;
                OnPropertyChanged(nameof(ShowSupportHint));
                return;
            }
            finally
            {
                StopCaptionSwitchTimer();
            }

            if (!result.Success)
            {
                _consecutiveValidationErrors++;
                ErrorReason = result.ErrorReason;
                State = LoginState.ErrorValidating;
                OnPropertyChanged(nameof(ShowSupportHint));
                return;
            }

            _consecutiveValidationErrors = 0;
            OnPropertyChanged(nameof(ShowSupportHint));

            // Beat visual: §10.12 distingue 0.3 (validating) de 0.4 (loading_entitlement).
            // El AuthService hace ambas operaciones juntas — mostramos LoadingEntitlement
            // como bumper sintético para que la UI honre la spec sin extender el contrato.
            State = LoginState.LoadingEntitlement;
            await DelayAsync(LoadingEntitlementBeat, ct).ConfigureAwait(true);

            State = LoginState.Success;
            await DelayAsync(SuccessFlashDuration, ct).ConfigureAwait(true);

            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _isHandlingCallback = false;
        }
    }

    // ===== Internals reachable from tests =====

    // Decremento manual del cooldown. Producción lo dispara el ITimer cada 1s; tests
    // lo invocan directamente para ejercitar la transición sin esperar tiempo real.
    internal void TickCooldown()
    {
        if (ResendCooldownSec <= 0)
        {
            StopCooldown();
            return;
        }

        ResendCooldownSec = ResendCooldownSec - 1;
        if (ResendCooldownSec == 0)
        {
            StopCooldown();
        }
    }

    internal void SwitchToSlowValidatingCaption() =>
        ValidatingCaption = SlowValidatingCaption;

    internal void TriggerWatchdog()
    {
        if (State != LoginState.WaitingForMagicLink) return;
        _logger.LogInformation("LoginWindow watchdog: 15 min sin callback, volviendo a Idle");
        StopWatchdog();
        EnterIdle(LoginIdleVariant.FirstLaunch);
    }

    // ===== Commands impl =====

    private async Task StartLoginAsync(CancellationToken ct)
    {
        ErrorReason = null;

        try
        {
            await _auth.StartLoginAsync(ct).ConfigureAwait(true);

            // Si veníamos de Error*, transicionamos a Idle "limpio" — el user volvió
            // a apretar el CTA, mostrarle de nuevo el mismo error sería confuso.
            if (State is LoginState.ErrorValidating or LoginState.ErrorNetwork)
            {
                State = LoginState.Idle;
            }

            // De Idle a Idle no cambia nada visual — esperamos el deep-link auth-pending
            // para mutar al estado 0.2. Si el deep-link nunca llega (user cerró el browser)
            // el LoginWindow queda en Idle silencioso.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartLoginAsync falló al lanzar el browser");
            State = LoginState.ErrorNetwork;
        }
    }

    private async Task ResendMagicLinkAsync(CancellationToken ct)
    {
        try
        {
            await _auth.StartLoginAsync(ct).ConfigureAwait(true);
            StartCooldown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resend magic link falló al relanzar el browser");
            // No cambiamos de estado — el usuario sigue en 0.2 y puede reintentar.
        }
    }

    private void StartOver()
    {
        StopCooldown();
        StopWatchdog();
        PendingEmail = null;
        State = LoginState.Idle;
        IdleVariant = LoginIdleVariant.FirstLaunch;
    }

    private void RetryAfterNetworkError()
    {
        // Volvemos a Idle y dejamos que el usuario reinicie el flow. Reintentar el
        // último paso del HandleAuthCallback requeriría persistir los queryParams del
        // deep-link en el VM, lo que no aporta valor — el usuario va a reabrir el mail
        // y reclickear el link igual, ya que el link tiene TTL del lado de Supabase.
        State = LoginState.Idle;
        IdleVariant = LoginIdleVariant.FirstLaunch;
    }

    // ===== StateChanged handler =====

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        // Si el callback está en vuelo, HandleAuthCallbackAsync va a manejar el cierre.
        if (_isHandlingCallback) return;

        if (_auth.State == AuthSessionState.LoggedIn)
        {
            _logger.LogInformation("LoginVM: detectó LoggedIn externo, solicitando close");
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    // EP-11.4 — el SpikitUriDispatcher dispara este evento cuando llega un
    // `spikit://auth-pending?email=...`. Equivalente a invocar HandleAuthPending(email)
    // públicamente, pero por canal de evento para mantener al dispatcher desacoplado
    // de la capa Views/VMs.
    //
    // El handler se ejecuta en el thread del que dispara el dispatcher — típicamente
    // un threadpool del listener IPC. Marshallamos al UI thread (mismo patrón que
    // los callbacks de los timers).
    private void OnAuthPendingReceived(object? sender, string email)
    {
        DispatcherInvoke(() => HandleAuthPending(email));
    }

    // ===== Timers (System.Threading.Timer via TimeProvider) =====
    //
    // Usamos TimeProvider.CreateTimer en lugar de DispatcherTimer para que los tests
    // puedan inyectar un FakeTimeProvider sin necesidad de un Dispatcher activo. En
    // producción el callback puede llegar de un thread del threadpool — los .NET
    // Timer tickean ahí. Para tocar UI necesitamos marshalar; lo hacemos en el
    // callback con DispatcherInvoke (default a Application.Current.Dispatcher).

    private void StartCooldown()
    {
        StopCooldown();
        ResendCooldownSec = ResendCooldownSeconds;
        _cooldownTimer = _time.CreateTimer(
            _ => DispatcherInvoke(TickCooldown),
            state: null,
            dueTime: TimeSpan.FromSeconds(1),
            period: TimeSpan.FromSeconds(1));
    }

    private void StopCooldown()
    {
        _cooldownTimer?.Dispose();
        _cooldownTimer = null;
        if (ResendCooldownSec != 0)
        {
            ResendCooldownSec = 0;
        }
    }

    private void StartWatchdog()
    {
        StopWatchdog();
        _watchdogTimer = _time.CreateTimer(
            _ => DispatcherInvoke(TriggerWatchdog),
            state: null,
            dueTime: TimeSpan.FromMinutes(15),
            period: Timeout.InfiniteTimeSpan);
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private void StartCaptionSwitchTimer()
    {
        StopCaptionSwitchTimer();
        _captionSwitchTimer = _time.CreateTimer(
            _ => DispatcherInvoke(SwitchToSlowValidatingCaption),
            state: null,
            dueTime: ValidatingCaptionSwitchAfter,
            period: Timeout.InfiniteTimeSpan);
    }

    private void StopCaptionSwitchTimer()
    {
        _captionSwitchTimer?.Dispose();
        _captionSwitchTimer = null;
    }

    private void StopAllTimers()
    {
        StopCooldown();
        StopWatchdog();
        StopCaptionSwitchTimer();
    }

    // ===== Helpers =====

    // Marshallea al UI thread si hay Dispatcher activo. Tests inyectan VM sin
    // WPF activo → cae al else y corre sync, lo cual es OK porque los tests
    // ejercitan los callbacks invocando los internal methods directamente.
    private static void DispatcherInvoke(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(action);
            return;
        }
        action();
    }

    // Wrapper sobre Task.Delay que usa TimeProvider — tests con FakeTimeProvider
    // pueden avanzar el reloj sin esperar real time.
    private Task DelayAsync(TimeSpan duration, CancellationToken ct) =>
        Task.Delay(duration, _time, ct);

    private void NotifyDerivedStateChanged()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsWaitingForMagicLink));
        OnPropertyChanged(nameof(IsValidatingToken));
        OnPropertyChanged(nameof(IsLoadingEntitlement));
        OnPropertyChanged(nameof(IsSuccess));
        OnPropertyChanged(nameof(IsErrorValidating));
        OnPropertyChanged(nameof(IsErrorNetwork));
    }

    public void Dispose()
    {
        _auth.StateChanged -= OnAuthStateChanged;
        _auth.AuthPendingReceived -= OnAuthPendingReceived;
        StopAllTimers();
    }
}
