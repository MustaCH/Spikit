using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Spikit.Native;
using Spikit.Services.Orchestration;
using Spikit.ViewModels.Onboarding;
using Spikit.Views;

namespace Spikit.Views.Onboarding;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;
    private readonly DictationPillWindow _pill;
    private readonly DictationOrchestrator _orchestrator;
    private readonly ILogger<OnboardingWindow> _logger;

    private bool _dictationActivated;

    public OnboardingWindow(
        OnboardingViewModel viewModel,
        DictationPillWindow pill,
        DictationOrchestrator orchestrator,
        ILogger<OnboardingWindow> logger)
    {
        _viewModel = viewModel;
        _pill = pill;
        _orchestrator = orchestrator;
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.OnboardingCompleted += OnOnboardingCompleted;
        _viewModel.PruebaStepEntered += OnPruebaStepEntered;
    }

    // Bordes redondeados nativos en Windows 11 (DwmSetWindowAttribute con CornerPreference=Round).
    // En Win10 el atributo se ignora silenciosamente y los bordes quedan square — Spikit
    // está targeteado a Win11+ así que no hay fallback intencional. OnSourceInitialized es el
    // momento correcto: el HWND existe pero la window todavía no se mostró.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int pref = (int)DwmWindowCornerPreference.Round;
        Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference, ref pref, sizeof(int));
    }

    // Alt+F4 (o cualquier intento de cerrar antes de terminar) confirma con el usuario.
    // RN-5: la app no debería entrar en estado utilizable sin onboarding hecho — pero
    // bloquear duro Alt+F4 frustra al usuario que necesita salir. Mejor preguntar.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_viewModel.IsCompleted)
        {
            var result = MessageBox.Show(
                "¿Cerrar sin terminar la configuración?\n\nVas a tener que completarla la próxima vez que abras Spikit para poder dictar.",
                "Spikit — Configuración inicial",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _viewModel.OnboardingCompleted -= OnOnboardingCompleted;
        _viewModel.PruebaStepEntered -= OnPruebaStepEntered;

        // Si llegamos al step Prueba durante esta sesión, el orchestrator + la pill
        // están activos. Limpieza ordenada antes de cerrar para no dejar el hotkey
        // global colgando si la app sigue viva en otro contexto.
        if (_dictationActivated)
        {
            try { _orchestrator.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error al stop del orchestrator durante close del onboarding"); }
            try { _pill.Hide(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error al hide de la pill durante close del onboarding"); }
        }

        base.OnClosing(e);
    }

    private void OnOnboardingCompleted(object? sender, EventArgs e)
    {
        // El cableado de "qué pasa después de completar" lo hace EP-3.8 (bootstrap gate
        // + flag onboardingCompleted persistido). Por ahora cerramos la ventana — Window.OnClosing
        // ve IsCompleted=true y deja pasar sin confirm.
        Close();
    }

    // EP-3.7: el wizard llegó al step Prueba. La hotkey ya está registrada (EP-3.6 lo hizo
    // en el SaveAsync del step anterior). Levantamos la pill flotante (modo idle) y arrancamos
    // el DictationOrchestrator para que reaccione al press del hotkey configurado.
    private void OnPruebaStepEntered(object? sender, EventArgs e)
    {
        if (_dictationActivated) return;
        _dictationActivated = true;

        try
        {
            _pill.Show();
            _orchestrator.Start();
            _logger.LogInformation("Dictation activado para el step Prueba del onboarding");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo activar dictation para el step Prueba");
        }
    }

    // El ✕ del title bar custom dispara Close, que pasa por OnClosing — ahí está la
    // lógica de confirmación si el onboarding no terminó (RN-5).
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
