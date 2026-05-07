using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spikit.Models;
using Spikit.Native;

namespace Spikit.Views.Controls;

// UserControl para capturar combinaciones de hotkey (modificadores + tecla principal).
//
// Flujo:
//   1) Click o foco → IsCapturing=true → muestra "Apretá tu combinación…".
//   2) Cualquier press con AT MENOS UNA tecla NO modificadora se acepta:
//      Hotkey = (modifiers, vk) y IsCapturing=false.
//      Si el press es solo modificadores (Ctrl/Alt/Shift/Win solos) seguimos esperando.
//   3) Esc en modo Capturing cancela y deja la Hotkey anterior intacta.
//
// La Hotkey expuesta es DependencyProperty two-way bindable, default Ctrl+Alt+M (HotkeyDefinition.Default).
public partial class HotkeyCaptureField : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty HotkeyProperty =
        DependencyProperty.Register(
            nameof(Hotkey),
            typeof(HotkeyDefinition),
            typeof(HotkeyCaptureField),
            new FrameworkPropertyMetadata(
                HotkeyDefinition.Default,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnHotkeyChanged));

    private bool _isCapturing;

    public HotkeyCaptureField()
    {
        InitializeComponent();
        // Click sobre el control entra a Capturing. Sin esto el usuario tendría que
        // tabbear para enfocar — peor UX que el clásico "click en el input".
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        // KeyDown llega cuando el control está focuseado. Manejamos en PreviewKeyDown
        // para atrapar Tab/Esc antes de que WPF los routee a otro lado.
        PreviewKeyDown += OnPreviewKeyDown;
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public HotkeyDefinition? Hotkey
    {
        get => (HotkeyDefinition?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    // True mientras esperamos input. La XAML hace data-trigger sobre esta propiedad.
    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (_isCapturing == value) return;
            _isCapturing = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowHotkey));
            OnPropertyChanged(nameof(ShowEditButton));
            OnPropertyChanged(nameof(ShowEmptyPlaceholder));
        }
    }

    // Texto a mostrar: ej. "Ctrl + Alt + M". El padre (HotkeyStepViewModel) usa el mismo
    // formato pero acá lo computamos desde la DP para que el control sea autocontenido.
    public string HotkeyDisplay => Hotkey is null ? string.Empty : Hotkey.ToString().Replace("+", " + ");

    public bool ShowHotkey => !IsCapturing && Hotkey is not null;

    public bool ShowEmptyPlaceholder => !IsCapturing && Hotkey is null;

    public bool ShowEditButton => !IsCapturing && Hotkey is not null;

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyCaptureField self)
        {
            self.OnPropertyChanged(nameof(HotkeyDisplay));
            self.OnPropertyChanged(nameof(ShowHotkey));
            self.OnPropertyChanged(nameof(ShowEditButton));
            self.OnPropertyChanged(nameof(ShowEmptyPlaceholder));
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartCapture();
        e.Handled = true;
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        StartCapture();
        e.Handled = true;
    }

    private void StartCapture()
    {
        Focus();
        Keyboard.Focus(this);
        IsCapturing = true;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Si el usuario click-out mientras estábamos capturando, cancelar la captura
        // sin tocar la Hotkey actual. Mantiene comportamiento "lo que viste es lo
        // que tenés guardado" del input.
        if (IsCapturing) IsCapturing = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsCapturing) return;

        // IME/dead keys → ignorar.
        if (e.Key == Key.ImeProcessed) return;

        // Cuando Alt está apretada, e.Key se reporta como System y la tecla real
        // viaja en e.SystemKey. Pasa con cualquier combinación que use Alt.
        var actualKey = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc cancela la captura y restaura el placeholder al estado previo.
        if (actualKey == Key.Escape)
        {
            IsCapturing = false;
            e.Handled = true;
            return;
        }

        // Si la tecla apretada es solo una modificadora (Ctrl/Alt/Shift/Win), seguimos
        // esperando — el press no está completo todavía.
        if (IsModifierOnlyKey(actualKey))
        {
            e.Handled = true;
            return;
        }

        var modifiers = MapModifiers(Keyboard.Modifiers);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(actualKey);

        Hotkey = new HotkeyDefinition(modifiers, virtualKey);
        IsCapturing = false;
        e.Handled = true;
    }

    private static bool IsModifierOnlyKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin or
        // Capslock + num/scroll lock: no son modificadoras de hotkey en Windows pero
        // tampoco las queremos como tecla principal — el usuario raramente quiere bindear
        // a CapsLock y RegisterHotKey no las acepta limpias.
        Key.Capital or Key.NumLock or Key.Scroll;

    private static HotkeyModifiers MapModifiers(ModifierKeys wpf)
    {
        var result = HotkeyModifiers.None;
        if (wpf.HasFlag(ModifierKeys.Control)) result |= HotkeyModifiers.Control;
        if (wpf.HasFlag(ModifierKeys.Alt)) result |= HotkeyModifiers.Alt;
        if (wpf.HasFlag(ModifierKeys.Shift)) result |= HotkeyModifiers.Shift;
        if (wpf.HasFlag(ModifierKeys.Windows)) result |= HotkeyModifiers.Win;
        return result;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
