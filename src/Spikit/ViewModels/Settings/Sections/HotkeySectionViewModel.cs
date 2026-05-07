using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Hotkey de Settings (EP-4.4). Acceptance criteria en ClickUp 86ahbnayy.
//
// Diferencias con HotkeyStepViewModel (onboarding EP-3.5):
//   1. Precarga: lee settings.json al construir y arranca con la combinación + modo
//      persistidos, no con defaults. Snapshot en _persistedHotkey/_persistedMode para
//      detectar HasPendingChanges.
//   2. Probar (modo demo): activa IDictationDemoMode → próximo press del hotkey global
//      cortocircuita en el orchestrator (no llama AudioCaptureService/Whisper) y dispara
//      el flash visual de la pill. La sección muestra un toast inline "Probá tu hotkey
//      ahora — Esc para cancelar" → "✓ Hotkey detectado" → auto-dismiss.
//   3. Guardar cambios: usa el mismo IHotkeyConfigWriter del onboarding (transacción
//      Unregister-prev → Register-nuevo → JsonSettings → SetMode orchestrator). Mismo
//      mensaje CB-7 inline si la combinación está tomada.
//
// El flag IsDemoActive es la fuente de verdad del toast. ToastMessage cambia de "Probá…"
// a "✓ Hotkey detectado" entre el BeginDemoMode y el auto-dismiss.
public sealed class HotkeySectionViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan DemoSuccessDismissDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DemoNoPressTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DemoWarningDismissDelay = TimeSpan.FromSeconds(4);

    private readonly ILogger<HotkeySectionViewModel> _logger;
    private readonly IHotkeyConfigWriter _configWriter;
    private readonly ISettingsService _settingsService;
    private readonly IDictationDemoMode _demoMode;
    private readonly IHotkeyService _hotkeyService;
    private readonly Dispatcher _dispatcher;

    private HotkeyDefinition? _hotkey;
    private HotkeyMode _mode;

    // Snapshot persistido — fuente de verdad para HasPendingChanges. Se refresca tras
    // un Save exitoso (mismo patrón que ProviderSectionViewModel). Se inicializa al default
    // para satisfacer el compilador (nullable analysis no ve que LoadFromPersistence siempre
    // los asigna); el ctor pisa estos valores apenas corre la precarga.
    private HotkeyDefinition _persistedHotkey = HotkeyDefinition.Default;
    private HotkeyMode _persistedMode = HotkeyMode.PushToTalk;

    private bool _isDemoActive;
    private string _toastMessage = string.Empty;
    private bool _toastIsSuccess;
    private bool _toastIsWarning;
    private CancellationTokenSource? _toastDismissCts;
    private CancellationTokenSource? _demoTimeoutCts;

    private bool _isSaving;
    private string _saveError = string.Empty;

    private bool _disposed;

    public HotkeySectionViewModel(
        ILogger<HotkeySectionViewModel> logger,
        IHotkeyConfigWriter configWriter,
        ISettingsService settingsService,
        IDictationDemoMode demoMode,
        IHotkeyService hotkeyService)
    {
        _logger = logger;
        _configWriter = configWriter;
        _settingsService = settingsService;
        _demoMode = demoMode;
        _hotkeyService = hotkeyService;
        _dispatcher = Dispatcher.CurrentDispatcher;

        TestCommand = new RelayCommand(ExecuteTest, () => !_isDemoActive && !_isSaving);
        CancelTestCommand = new RelayCommand(CancelTest, () => _isDemoActive);
        SaveCommand = new RelayCommand(
            execute: () => _ = SaveAsync(),
            canExecute: () => !_isSaving && !_isDemoActive && HasPendingChanges);

        LoadFromPersistence();

        _demoMode.DemoHotkeyDetected += OnDemoHotkeyDetected;
    }

    // ============ Capture lifecycle ============

    // Llamado por la View cuando el HotkeyCaptureField entra en modo Capturing. Suspende
    // el hotkey global activo (si lo hay) para que el press del usuario llegue al control
    // como KeyDown WPF en vez de ser interceptado por Win32. Sin esto, recapturar la
    // misma combinación que está activa empieza una grabación en lugar de capturar.
    public void BeginCapture()
    {
        _hotkeyService.SuspendForCapture();
        _logger.LogDebug("Hotkey section: capture iniciado, hotkey global suspendido");
    }

    // Llamado al cerrar el capture (combinación capturada o Esc). Re-registra la combinación
    // que estaba activa antes del SuspendForCapture. Si en el ínterin el usuario apretó
    // Guardar y la combinación nueva ya se persistió, ResumeFromCapture es no-op (el writer
    // ya hizo Unregister/Register transaccional).
    public void EndCapture()
    {
        _hotkeyService.ResumeFromCapture();
        _logger.LogDebug("Hotkey section: capture cerrado, hotkey global resumido");
    }

    // ============ Bindings de form ============

    public HotkeyDefinition? Hotkey
    {
        get => _hotkey;
        set
        {
            if (SetProperty(ref _hotkey, value))
            {
                if (!string.IsNullOrEmpty(_saveError)) SaveError = string.Empty;
                NotifyDerivedChanged();
                CommandManager.InvalidateRequerySuggested();
                _logger.LogDebug("Hotkey actualizada → {Hotkey}", value?.ToString() ?? "(null)");
            }
        }
    }

    public HotkeyMode Mode
    {
        get => _mode;
        set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsPushToTalk));
                OnPropertyChanged(nameof(IsToggle));
                OnPropertyChanged(nameof(HasPendingChanges));
                CommandManager.InvalidateRequerySuggested();
                _logger.LogDebug("Hotkey mode → {Mode}", value);
            }
        }
    }

    public bool IsPushToTalk
    {
        get => _mode == HotkeyMode.PushToTalk;
        set { if (value) Mode = HotkeyMode.PushToTalk; }
    }

    public bool IsToggle
    {
        get => _mode == HotkeyMode.Toggle;
        set { if (value) Mode = HotkeyMode.Toggle; }
    }

    public bool HasHotkey => _hotkey is not null;

    // Soft warning: combinación sin modificadora. No bloquea — mismo patrón que onboarding
    // (US-1.2 / EP-3.5).
    public bool HasWarning => _hotkey is not null && _hotkey.Modifiers == HotkeyModifiers.None;

    public string WarningMessage => HasWarning
        ? "Esta combinación puede entrar en conflicto con otras apps."
        : string.Empty;

    public bool HasPendingChanges
    {
        get
        {
            if (_hotkey is null) return false; // sin combo válida no tiene sentido habilitar Save
            if (!_hotkey.Equals(_persistedHotkey)) return true;
            if (_mode != _persistedMode) return true;
            return false;
        }
    }

    // ============ Probar (demo mode) ============

    public ICommand TestCommand { get; }
    public ICommand CancelTestCommand { get; }

    public bool IsDemoActive
    {
        get => _isDemoActive;
        private set
        {
            if (SetProperty(ref _isDemoActive, value))
            {
                OnPropertyChanged(nameof(IsEditable));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // Form deshabilitado mientras corre el demo (toast visible) o estamos guardando.
    // El VM lo expone para que la View pueda bindearlo a IsEnabled de los inputs.
    public bool IsEditable => !_isDemoActive && !_isSaving;

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastIsSuccess
    {
        get => _toastIsSuccess;
        private set => SetProperty(ref _toastIsSuccess, value);
    }

    // Warning surface el caso "registramos OK pero el OS no nos reporta el press en 5s".
    // Causa típica: otra app/driver tiene la misma combinación con prioridad. RegisterHotKey
    // no detecta esto (devuelve OK), solo se descubre con la POC del demo.
    public bool ToastIsWarning
    {
        get => _toastIsWarning;
        private set => SetProperty(ref _toastIsWarning, value);
    }

    private void ExecuteTest()
    {
        if (_isDemoActive) return;

        _toastDismissCts?.Cancel();
        _demoTimeoutCts?.Cancel();
        _demoMode.BeginDemoMode();
        ToastMessage = "Probá tu hotkey ahora — Esc para cancelar";
        ToastIsSuccess = false;
        ToastIsWarning = false;
        IsDemoActive = true;
        ScheduleDemoTimeout();
        _logger.LogDebug("Hotkey section: demo mode iniciado por usuario");
    }

    private void CancelTest()
    {
        if (!_isDemoActive) return;

        _toastDismissCts?.Cancel();
        _demoTimeoutCts?.Cancel();
        _demoMode.EndDemoMode();
        IsDemoActive = false;
        ToastMessage = string.Empty;
        ToastIsSuccess = false;
        ToastIsWarning = false;
        _logger.LogDebug("Hotkey section: demo mode cancelado por usuario (Esc)");
    }

    // Si pasan ~5s sin que el OS reporte el press, asumimos que la combinación está siendo
    // robada por otra app/driver con un keyboard hook. RegisterHotKey aceptó pero el WM_HOTKEY
    // no llega — caso clásico de Ctrl+M / Ctrl+letra solo, donde otra app registra la misma
    // combinación con prioridad. Mostramos warning accionable; no auto-cancelamos el demo
    // para que el usuario pueda darle más tiempo si quiere o cancelar con Esc.
    private void ScheduleDemoTimeout()
    {
        _demoTimeoutCts = new CancellationTokenSource();
        var ct = _demoTimeoutCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DemoNoPressTimeout, ct); }
            catch (OperationCanceledException) { return; }

            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (!_isDemoActive || _toastIsSuccess) return;
                ApplyDemoTimeoutWarning();
            });
        });
    }

    private void ApplyDemoTimeoutWarning()
    {
        ToastMessage = "⚠ No detectamos tu press. Otra app puede tener la misma combinación. Probá Ctrl+Alt + letra.";
        ToastIsWarning = true;
        ToastIsSuccess = false;
        _logger.LogWarning("Hotkey demo: timeout sin detectar press — combinación posiblemente en conflicto");
        // El warning desaparece solo (4s); el demo queda terminado para dar feedback final
        // sin obligar al usuario a apretar Esc.
        _demoMode.EndDemoMode();
        ScheduleWarningDismiss();
    }

    private void ScheduleWarningDismiss()
    {
        _toastDismissCts?.Cancel();
        _toastDismissCts = new CancellationTokenSource();
        var ct = _toastDismissCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DemoWarningDismissDelay, ct); }
            catch (OperationCanceledException) { return; }

            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                IsDemoActive = false;
                ToastMessage = string.Empty;
                ToastIsWarning = false;
            });
        });
    }

    private void OnDemoHotkeyDetected(object? sender, EventArgs e)
    {
        // El evento puede llegar desde el thread del WndProc del HotkeyService. CheckAccess
        // nos deja despachar sincrónico cuando ya estamos en el UI thread (caso normal:
        // HwndSource pertenece al mismo dispatcher que esta sección + tests sin dispatcher
        // bombeado), y cae al BeginInvoke solo si por algún motivo el evento llegara desde
        // otro thread.
        if (_dispatcher.CheckAccess())
        {
            ApplyDemoSuccess();
        }
        else
        {
            _dispatcher.BeginInvoke(ApplyDemoSuccess);
        }
    }

    private void ApplyDemoSuccess()
    {
        if (!_isDemoActive) return;
        // El press llegó dentro de la ventana — cancelamos el timeout de "no detectamos press".
        _demoTimeoutCts?.Cancel();
        ToastMessage = "✓ Hotkey detectado";
        ToastIsSuccess = true;
        ToastIsWarning = false;
        _logger.LogInformation("Hotkey demo: combinación detectada por el OS");
        ScheduleToastDismiss();
    }

    private void ScheduleToastDismiss()
    {
        _toastDismissCts?.Cancel();
        _toastDismissCts = new CancellationTokenSource();
        var ct = _toastDismissCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(DemoSuccessDismissDelay, ct); }
            catch (OperationCanceledException) { return; }

            await _dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                IsDemoActive = false;
                ToastMessage = string.Empty;
                ToastIsSuccess = false;
                ToastIsWarning = false;
            });
        });
    }

    // ============ Save ============

    public ICommand SaveCommand { get; }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(IsEditable));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value))
            {
                OnPropertyChanged(nameof(HasSaveError));
            }
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(_saveError);

    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        if (_hotkey is null)
        {
            SaveError = "Capturá una combinación antes de guardar.";
            return false;
        }

        SaveError = string.Empty;
        IsSaving = true;
        try
        {
            await _configWriter.SaveAsync(_hotkey, _mode, ct).ConfigureAwait(true);
            _persistedHotkey = _hotkey;
            _persistedMode = _mode;
            OnPropertyChanged(nameof(HasPendingChanges));
            CommandManager.InvalidateRequerySuggested();
            _logger.LogInformation("Hotkey config persistida desde Settings ({Hotkey} / {Mode})", _hotkey, _mode);
            return true;
        }
        catch (HotkeyRegistrationException ex)
        {
            // CB-7: igual que el onboarding, mensaje literal del ticket.
            _logger.LogWarning(ex, "CB-7 desde Settings: hotkey {Hotkey} ya está en uso", _hotkey);
            SaveError = "Esta combinación está en uso por el sistema o por otra app. Probá otra.";
            return false;
        }
        catch (HotkeyConfigSaveException ex)
        {
            _logger.LogWarning(ex, "Guardado de hotkey config falló desde Settings");
            SaveError = ex.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al guardar hotkey config desde Settings");
            SaveError = "Error inesperado al guardar la configuración. Probá de nuevo.";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    // ============ Precarga ============

    private void LoadFromPersistence()
    {
        var settings = _settingsService.Load();
        if (!settings.Hotkey.TryToRuntime(out var definition, out var mode))
        {
            _logger.LogWarning("settings.json tiene un bloque hotkey inválido — usando defaults V1");
        }

        _hotkey = definition;
        _mode = mode;
        _persistedHotkey = definition;
        _persistedMode = mode;

        OnPropertyChanged(nameof(Hotkey));
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsPushToTalk));
        OnPropertyChanged(nameof(IsToggle));
        NotifyDerivedChanged();
    }

    private void NotifyDerivedChanged()
    {
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(WarningMessage));
        OnPropertyChanged(nameof(HasPendingChanges));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _demoMode.DemoHotkeyDetected -= OnDemoHotkeyDetected;
        _toastDismissCts?.Cancel();
        _toastDismissCts?.Dispose();
        _demoTimeoutCts?.Cancel();
        _demoTimeoutCts?.Dispose();

        if (_isDemoActive)
        {
            _demoMode.EndDemoMode();
        }
    }
}
