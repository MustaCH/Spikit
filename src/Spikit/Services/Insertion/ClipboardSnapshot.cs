using System.Windows;
using Microsoft.Extensions.Logging;

namespace Spikit.Services.Insertion;

// Estado del clipboard del usuario justo antes de que insertemos la transcripción.
// Distingue los 3 escenarios que el restore tiene que tratar distinto (RN-4):
//   - HasContent: había datos y los capturamos → restaurar al final.
//   - Empty:      el clipboard estaba vacío   → limpiar tras el paste (si no, la
//                                               transcripción queda residual).
//   - Unknown:    el snapshot falló (COM error o no se pudo extraer ningún
//                                               formato) → no tocar nada, porque
//                                               borrar podría destruir datos del
//                                               usuario que no pudimos snapshotear.
//
// FromDataObject EXTRAE los datos del IDataObject original a una copia local
// (formato por formato). El IDataObject que devuelve Clipboard.GetDataObject() es
// un proxy COM al ex-dueño del clipboard; cuando spikit asume ownership con la
// transcripción intermedia (paso 2 del flow de paste), ese proxy puede quedar
// inválido y restaurarlo después falla. La copia local sobrevive a ese cambio
// de ownership porque los datos viven en nuestro proceso.
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

    // Clasifica el resultado de Clipboard.GetDataObject() y extrae los datos a una
    // copia local que sobreviva al cambio de ownership intermedio:
    //   - null o sin formatos → Empty
    //   - todos los formatos fallaron al extraer → Unknown (preferimos dejar
    //     residual antes que destruir datos que no pudimos snapshotear)
    //   - al menos un formato extraído OK → HasContent con DataObject local
    // Las excepciones que tira Clipboard.GetDataObject() en sí (COM locked, etc.)
    // se tratan en el call site → Unknown directo, no llega acá.
    public static ClipboardSnapshot FromDataObject(IDataObject? dataObject, ILogger? logger = null)
    {
        if (dataObject is null) return Empty;
        var formats = dataObject.GetFormats(autoConvert: false);
        if (formats.Length == 0) return Empty;

        var localCopy = new DataObject();
        var extracted = 0;
        foreach (var format in formats)
        {
            try
            {
                var data = dataObject.GetData(format, autoConvert: false);
                if (data is null) continue;
                localCopy.SetData(format, data);
                extracted++;
            }
            catch (Exception ex)
            {
                // Formato COM-only, delayed-render que falló, o serialización
                // no soportada — skipeamos ese formato y seguimos con los demás.
                logger?.LogDebug(ex,
                    "Formato {Format} del clipboard no se pudo extraer al snapshot — skip",
                    format);
            }
        }

        // Si ningún formato pudo extraerse, no tenemos nada confiable para restaurar.
        // Caer en Unknown evita que el restore final pise el clipboard con un
        // DataObject vacío que destruiría la transcripción residual sin poner nada
        // útil en su lugar.
        return extracted == 0 ? Unknown : FromData(localCopy);
    }
}
