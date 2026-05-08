using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Spikit.Views.Common;

// Modal de confirmación bloqueante reusable. Caller pattern:
//
//   var dialog = new ConfirmDialog
//   {
//       Owner = settingsWindow, // o null si no hay ventana visible activa
//       Title = "Borrar API key del sistema",
//       MessageText = "Vas a tener que reconfigurar tu provider. ¿Continuar?",
//       ConfirmLabel = "Borrar",
//       CancelLabel = "Cancelar",
//       IsDestructive = true,
//   };
//   if (dialog.ShowDialog() == true) { /* usuario confirmó */ }
//
// Decisiones:
//   - Cuando hay Owner, el scrim se dimensiona al área de la owner (Width/Height/Left/Top
//     copiados de Owner en Loaded). El scrim NO cubre toda la pantalla, solo la owner —
//     coherente con UX clásica de "la app está bloqueada, no el OS".
//   - Sin Owner (caso defensivo, ej. tests headless / scenarios edge), caemos a CenterScreen
//     y un tamaño cómodo del scrim (450×360) — la dialog box queda centrada igual.
//   - Foco inicial en CancelButton (heurística #5 del design-system §9.10).
//   - Esc cancela vía IsCancel="True" del CancelButton.
//   - Click en scrim cancela. Click en la box NO cancela (handler que come el evento).
//   - Cuando IsDestructive=true, el botón Confirm cambia a un destructive style declarado
//     inline (brand solid es muy parecido al state.error.fg porque la marca de Spikit es
//     roja, así que en la práctica los dos quedan igual; el override está por si en V2 se
//     diferencia el destructive del primary).
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        // PreviewKeyDown para que el Esc pase aunque el foco esté en el botón Confirm
        // (donde IsCancel del CancelButton podría no recibir el evento dependiendo del
        // routing). IsCancel cubre el caso normal; este preview es belt-and-suspenders.
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public string MessageText
    {
        get => MessageBlock.Text;
        set => MessageBlock.Text = value;
    }

    public string ConfirmLabel
    {
        get => ConfirmLabelText.Text;
        set => ConfirmLabelText.Text = value;
    }

    public string CancelLabel
    {
        get => CancelLabelText.Text;
        set => CancelLabelText.Text = value;
    }

    private bool _isDestructive;
    public bool IsDestructive
    {
        get => _isDestructive;
        set
        {
            _isDestructive = value;
            ApplyDestructiveStyle();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleText.Text = Title;

        if (Owner is { } owner)
        {
            // Cubrimos exactamente el área de la Owner. Si la owner está minimizada, ShowDialog
            // ya la habrá restaurado (WPF default). Si en runtime el usuario mueve la owner
            // mientras el modal está abierto, queda desfasado — no es un caso V1 prioritario
            // (el modal cierra rápido, está modal-bloqueando la owner).
            Left = owner.Left;
            Top = owner.Top;
            Width = owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width;
            Height = owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height;
        }
        else
        {
            // Sin owner: tamaño cómodo + centrado en pantalla. Cubre el caso defensivo.
            Width = 480;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        // Foco inicial en Cancelar — el destructive nunca tiene foco automático.
        CancelButton.Focus();
        Keyboard.Focus(CancelButton);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DialogBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Comemos el click sobre la caja para que NO burbujee al scrim y cierre la dialog.
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void ApplyDestructiveStyle()
    {
        // El brand color de Spikit es state.error.fg (#FF3B30) — así que el SpkPrimaryButton
        // ya pinta destructive de fábrica. Si en V2 se diferencian los tokens (state.error.fg
        // ≠ bg.brand.solid), este método hace el switch del Background del ConfirmButton.
        if (_isDestructive)
        {
            // Hoy es no-op: SpkPrimaryButtonStyle ya usa SpkBgBrandSolidBrush = #FF3B30 = error.
            // Lo dejamos como hook explícito para que el call-site comunique intención.
            ConfirmButton.Foreground = Brushes.White;
        }
    }
}
