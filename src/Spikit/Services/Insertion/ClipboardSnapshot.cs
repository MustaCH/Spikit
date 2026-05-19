using System.Windows;

namespace Spikit.Services.Insertion;

// Estado del clipboard del usuario justo antes de que insertemos la transcripción.
// Distingue los 3 escenarios que el restore tiene que tratar distinto (RN-4):
//   - HasContent: había datos y los capturamos → restaurar al final.
//   - Empty:      el clipboard estaba vacío   → limpiar tras el paste (si no, la
//                                               transcripción queda residual).
//   - Unknown:    el snapshot falló (COM error u otro) → no tocar nada, porque
//                                               borrar podría destruir datos del
//                                               usuario que no pudimos snapshotear.
public sealed class ClipboardSnapshot
{
    public enum Kind
    {
        HasContent,
        Empty,
        Unknown,
    }

    private ClipboardSnapshot(Kind state, IDataObject? data)
    {
        State = state;
        Data = data;
    }

    public Kind State { get; }
    public IDataObject? Data { get; }

    public static ClipboardSnapshot Empty { get; } = new(Kind.Empty, data: null);
    public static ClipboardSnapshot Unknown { get; } = new(Kind.Unknown, data: null);

    public static ClipboardSnapshot FromData(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new ClipboardSnapshot(Kind.HasContent, data);
    }

    // Clasifica el resultado de Clipboard.GetDataObject() — null o sin formatos
    // significa que el clipboard estaba vacío. Las excepciones se tratan en el
    // call site (Unknown), no acá.
    public static ClipboardSnapshot FromDataObject(IDataObject? dataObject)
    {
        if (dataObject is null) return Empty;
        var formats = dataObject.GetFormats();
        return formats.Length == 0 ? Empty : FromData(dataObject);
    }
}
