using System.Windows;
using System.Windows.Input;
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
