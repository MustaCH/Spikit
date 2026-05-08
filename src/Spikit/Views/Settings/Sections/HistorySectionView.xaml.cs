using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Views.Settings.Sections;

// Code-behind del HistorySectionView (EP-4.8). Dos responsabilidades:
//
//  1. Click en una fila del historial → ToggleExpandCommand del VM. Lo manejamos en
//     code-behind porque (a) Border no acepta Command nativo y (b) usar un Button como
//     row trigger dejaría al sub-botón "Copiar" como nested-button con bubbling raro.
//     El handler chequea que el originalSource NO sea un Button (delete / copy) — esos
//     ya manejan su propio click y NO debe disparar el expand.
//
//  2. Scroll-infinite: al loaded buscamos el ScrollViewer ancestor (el del SettingsWindow)
//     y nos suscribimos a su ScrollChanged. Cuando el offset + viewport se acerca al final
//     (umbral 100px), disparamos LoadMoreCommand. Unsuscripción en Unloaded.
public partial class HistorySectionView : UserControl
{
    // Cuántos px antes del final disparamos la próxima página. 100 es suficiente para que
    // el usuario no vea el "Cargar más" — la próxima página ya está agregada cuando llega.
    private const double LoadMoreThresholdPx = 100;

    private ScrollViewer? _hostScrollViewer;

    public HistorySectionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    // Click en una fila → toggle expand. Si el click vino de un Button hijo (delete /
    // copy), salimos: el Button ya manejó su propio Click y no queremos efectos colaterales.
    private void HistoryRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
        {
            return;
        }

        if (sender is FrameworkElement fe
            && fe.DataContext is HistoryEntryViewModel entry
            && DataContext is HistorySectionViewModel vm
            && vm.ToggleExpandCommand.CanExecute(entry.Id))
        {
            vm.ToggleExpandCommand.Execute(entry.Id);
        }
    }

    private static bool IsInsideButton(DependencyObject node)
    {
        var current = node;
        while (current is not null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    // Cuando el SettingsWindow swap-ea de sección, el VM debería refrescar (toggle Privacy
    // pudo haber cambiado). El SettingsViewModel.NavigateTo ya lo dispara, pero acá hacemos
    // un fallback: cuando esta view se vuelve visible, pedimos un Refresh defensivo para
    // que la lista esté sincronizada con el toggle actual.
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is HistorySectionViewModel vm)
        {
            vm.Refresh();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostScrollViewer = FindAncestor<ScrollViewer>(this);
        if (_hostScrollViewer is not null)
        {
            _hostScrollViewer.ScrollChanged += HostScrollViewer_ScrollChanged;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostScrollViewer is not null)
        {
            _hostScrollViewer.ScrollChanged -= HostScrollViewer_ScrollChanged;
            _hostScrollViewer = null;
        }
    }

    private void HostScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not HistorySectionViewModel vm) return;
        if (!IsVisible) return;
        if (!vm.HasMorePages) return;

        var distanceToBottom = sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight);
        if (distanceToBottom <= LoadMoreThresholdPx && vm.LoadMoreCommand.CanExecute(null))
        {
            vm.LoadMoreCommand.Execute(null);
        }
    }

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
