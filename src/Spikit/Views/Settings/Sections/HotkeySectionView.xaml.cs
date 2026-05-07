using System.Windows.Controls;
using System.Windows.Input;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Views.Settings.Sections;

public partial class HotkeySectionView : UserControl
{
    public HotkeySectionView()
    {
        InitializeComponent();
        // Esc cancela el demo cuando está activo. Lo manejamos a nivel del UserControl
        // como PreviewKeyDown porque el botón Probar pierde foco después del click y la
        // ventana de Settings no tiene un handler propio de Esc — sin esto, Esc no haría
        // nada visible y el modo demo quedaría activo hasta que el usuario apriete su
        // hotkey. PreviewKeyDown captura antes del routing, así que funciona aunque el
        // foco esté en otro hijo de la sección.
        PreviewKeyDown += OnPreviewKeyDown;

        // Cuando el HotkeyCaptureField entra/sale de Capturing, el VM suspende/resume el
        // hotkey global activo. Sin esto, recapturar la combinación que ya está activa
        // hace que Win32 se trague el press antes de que llegue al control como KeyDown,
        // y la app empieza a grabar en lugar de capturar la nueva combo.
        CaptureField.CaptureStarted += OnCaptureFieldStarted;
        CaptureField.CaptureEnded += OnCaptureFieldEnded;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not HotkeySectionViewModel vm) return;
        if (!vm.IsDemoActive) return;

        if (vm.CancelTestCommand.CanExecute(null))
        {
            vm.CancelTestCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnCaptureFieldStarted(object? sender, EventArgs e)
    {
        if (DataContext is HotkeySectionViewModel vm) vm.BeginCapture();
    }

    private void OnCaptureFieldEnded(object? sender, EventArgs e)
    {
        if (DataContext is HotkeySectionViewModel vm) vm.EndCapture();
    }
}
