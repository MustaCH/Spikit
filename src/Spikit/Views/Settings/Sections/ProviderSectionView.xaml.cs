using System.Windows;
using System.Windows.Controls;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Views.Settings.Sections;

public partial class ProviderSectionView : UserControl
{
    // Mismo patrón que el ProviderStepView del onboarding: PasswordBox.Password no es
    // DependencyProperty, así que sincronizamos manualmente con el VM.ApiKey y suprimimos
    // el feedback loop cuando el seed es desde código.
    private bool _suppressPasswordSync;

    public ProviderSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ProviderSectionViewModel vm)
        {
            // Al precargar el VM ya está construido con la key existente cacheada en _existingKey
            // (no en ApiKey). El PasswordBox arranca vacío salvo que el usuario haya entrado
            // en modo "Reemplazar" antes de que se mueva el DataContext (escenario raro).
            _suppressPasswordSync = true;
            try { ApiKeyPasswordBox.Password = vm.ApiKey; }
            finally { _suppressPasswordSync = false; }

            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ProviderSectionViewModel vm) return;

        // Cuando el user aprieta "Reemplazar" o cuando el Save exitoso vuelve al estado masked,
        // el VM resetea ApiKey a string.Empty. Sincronizamos el PasswordBox para que no quede
        // con el contenido del intento previo.
        if (e.PropertyName == nameof(ProviderSectionViewModel.ApiKey)
            && vm.ApiKey != ApiKeyPasswordBox.Password)
        {
            _suppressPasswordSync = true;
            try { ApiKeyPasswordBox.Password = vm.ApiKey; }
            finally { _suppressPasswordSync = false; }
        }
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPasswordSync) return;
        if (DataContext is ProviderSectionViewModel vm)
        {
            vm.ApiKey = ApiKeyPasswordBox.Password;
        }
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ProviderSectionViewModel vm) return;

        if (vm.IsApiKeyVisible)
        {
            // Estábamos en TextBox visible; volvemos al masked → sincronizamos PasswordBox.
            _suppressPasswordSync = true;
            try { ApiKeyPasswordBox.Password = vm.ApiKey; }
            finally { _suppressPasswordSync = false; }
        }

        vm.IsApiKeyVisible = !vm.IsApiKeyVisible;
    }
}
