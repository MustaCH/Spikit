using System.Windows;
using System.Windows.Controls;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Views.Onboarding.Steps;

public partial class ProviderStepView : UserControl
{
    // Suprime el feedback loop entre PasswordBox.PasswordChanged y VM.ApiKey:
    // cuando hidrato el PasswordBox desde el VM, el evento PasswordChanged dispararía
    // a su vez una escritura al VM (idempotente, pero ruidosa en logs/animations).
    private bool _suppressPasswordSync;

    public ProviderStepView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Cuando llega el VM (Onboarding inyecta DataContext via Binding al Provider VM),
        // sembrar el PasswordBox con cualquier valor que ya tuviera el VM (típicamente vacío
        // pero sirve si EP-3.4 precarga la key persistida al volver a Settings).
        if (e.NewValue is ProviderStepViewModel vm)
        {
            _suppressPasswordSync = true;
            try { ApiKeyPasswordBox.Password = vm.ApiKey; }
            finally { _suppressPasswordSync = false; }
        }
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordSync) return;
        if (DataContext is ProviderStepViewModel vm)
        {
            vm.ApiKey = ApiKeyPasswordBox.Password;
        }
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProviderStepViewModel vm) return;

        // Antes de cambiar la visibilidad, sincronizamos para que ambos controles tengan
        // el mismo valor (el TextBox usa two-way binding al VM, el PasswordBox no porque
        // PasswordBox.Password no es DependencyProperty).
        if (vm.IsApiKeyVisible)
        {
            // Estábamos viendo el TextBox; volvemos a ocultar — pasar valor al PasswordBox.
            _suppressPasswordSync = true;
            try { ApiKeyPasswordBox.Password = vm.ApiKey; }
            finally { _suppressPasswordSync = false; }
        }
        else
        {
            // Estábamos ocultos; el VM ya tiene el último valor del PasswordBox vía
            // PasswordChanged, el TextBox lo va a mostrar al activarse el binding.
        }

        vm.IsApiKeyVisible = !vm.IsApiKeyVisible;
    }
}
