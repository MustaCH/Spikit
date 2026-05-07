using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Spikit.Views.Onboarding.Steps;

public partial class PruebaStepView : UserControl
{
    public PruebaStepView()
    {
        InitializeComponent();
        // Foco automático al volverse visible (US-1.3 AC: "TextBox vacía con foco").
        // IsVisibleChanged dispara cuando el step se activa; BeginInvoke con prioridad
        // Render aplica el foco después de que el template termine de armarse.
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            DictationTextBox.Focus();
            Keyboard.Focus(DictationTextBox);
        }));
    }
}
