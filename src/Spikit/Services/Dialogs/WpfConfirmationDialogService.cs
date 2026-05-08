using System.Windows;
using Spikit.ViewModels.Settings.Sections;
using Spikit.Views.Common;

namespace Spikit.Services.Dialogs;

// Implementación WPF del IConfirmationDialogService: instancia ConfirmDialog, lo asocia
// a la owner correcta (la SettingsWindow activa) y devuelve el resultado del ShowDialog().
//
// La elección de owner: privilegiar la window activa (típicamente SettingsWindow cuando
// se invoca el modal). Si no hay ninguna activa, dejamos owner=null — el ConfirmDialog
// cae a CenterScreen sin owner. La app no tiene ventana persistente visible (solo tray
// icon + pill flotante, esta última no debería ser owner de un modal).
//
// Singleton en DI: el servicio es stateless. Cada Confirm() crea su propia instancia de
// ConfirmDialog (transient por construcción).
public sealed class WpfConfirmationDialogService : IConfirmationDialogService
{
    public bool Confirm(ConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dialog = new ConfirmDialog
        {
            Title = request.Title,
            MessageText = request.Message,
            ConfirmLabel = request.ConfirmLabel,
            CancelLabel = request.CancelLabel,
            IsDestructive = request.IsDestructive,
            Owner = ResolveOwner(),
        };

        return dialog.ShowDialog() == true;
    }

    private static Window? ResolveOwner()
    {
        if (Application.Current is null) return null;

        // Buscamos la window activa (la que el usuario tiene en foco — típicamente
        // SettingsWindow cuando se invoca el modal). Si ninguna está activa devolvemos
        // null y ConfirmDialog cae a CenterScreen sin owner.
        foreach (Window w in Application.Current.Windows)
        {
            if (w.IsActive) return w;
        }
        return null;
    }
}
