using System.Windows;
using System.Windows.Input;
using Spikit.Native;
using Spikit.ViewModels;

namespace Spikit.Views;

public partial class FloatingResultWindow : Window
{
    private readonly FloatingResultViewModel _viewModel;

    public FloatingResultWindow(FloatingResultViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.CloseRequested += OnCloseRequested;
        KeyDown += OnKeyDown;
    }

    // Win11 polish: dark title bar + corners redondeados nativos + Mica como backdrop.
    // Mismo patrón que SettingsWindow/OnboardingWindow (EP-6.4). En Win10/Win11 < 22H2
    // las llamadas degradan silenciosamente al solid bg.canvas del XAML.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ApplyRoundedCorners(this, DwmWindowCornerPreference.Round);
        DwmHelper.ApplyDarkTitleBar(this);
        DwmHelper.ApplyBackdrop(this, DwmSystemBackdropType.MainWindow);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnCloseRequested(object? sender, EventArgs e)
    {
        // El VM puede emitir CloseRequested desde un timer en background — marshalear.
        Dispatcher.BeginInvoke(Close);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.CloseRequested -= OnCloseRequested;
        KeyDown -= OnKeyDown;
        base.OnClosed(e);
    }
}
