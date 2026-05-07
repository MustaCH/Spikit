using System.ComponentModel;
using System.Windows;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Views.Onboarding;

public partial class OnboardingWindow : Window
{
    private readonly OnboardingViewModel _viewModel;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.OnboardingCompleted += OnOnboardingCompleted;
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
        base.OnClosing(e);
    }

    private void OnOnboardingCompleted(object? sender, EventArgs e)
    {
        // El cableado de "qué pasa después de completar" lo hace EP-3.8 (bootstrap gate
        // + flag onboardingCompleted persistido). Por ahora cerramos la ventana — Window.OnClosing
        // ve IsCompleted=true y deja pasar sin confirm.
        Close();
    }
}
