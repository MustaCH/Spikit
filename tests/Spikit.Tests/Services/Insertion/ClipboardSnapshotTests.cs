using System.Windows;
using Spikit.Services.Insertion;

namespace Spikit.Tests.Services.Insertion;

// Cubre los 3 casos que el restore tiene que tratar distinto (RN-4 + ticket 86ahdt78n):
//   - Empty (clipboard estaba vacío)        → tras el paste se limpia.
//   - HasContent (había datos)              → tras el paste se restauran.
//   - Unknown (snapshot falló por excepción)→ no se toca el clipboard.
//
// Los tests trabajan sobre el factory ClipboardSnapshot.FromDataObject, que es la
// pieza pura del flow (clasifica el resultado de Clipboard.GetDataObject()). El
// switch que ejecuta la acción dentro de ClipboardPasteService.TryRestoreClipboardAsync
// es trivial 1-a-1 sobre el Kind; se verifica con smoke manual (Win+V → Limpiar todo
// → dictar → Ctrl+V en otra ventana debe no pegar nada).
public class ClipboardSnapshotTests
{
    [Fact]
    public void FromDataObject_null_classifies_as_Empty()
    {
        var snapshot = ClipboardSnapshot.FromDataObject(null);

        Assert.Equal(ClipboardSnapshot.Kind.Empty, snapshot.State);
        Assert.Null(snapshot.Data);
    }

    [Fact]
    public void FromDataObject_with_no_formats_classifies_as_Empty()
    {
        // Cuando el usuario tiene el clipboard vacío (Win+V → Limpiar todo), WPF
        // devuelve un DataObject sin formatos en lugar de null.
        var empty = new DataObject();

        var snapshot = ClipboardSnapshot.FromDataObject(empty);

        Assert.Equal(ClipboardSnapshot.Kind.Empty, snapshot.State);
        Assert.Null(snapshot.Data);
    }

    [Fact]
    public void FromDataObject_with_text_classifies_as_HasContent_and_preserves_data()
    {
        var withText = new DataObject(DataFormats.UnicodeText, "hola");

        var snapshot = ClipboardSnapshot.FromDataObject(withText);

        Assert.Equal(ClipboardSnapshot.Kind.HasContent, snapshot.State);
        Assert.Same(withText, snapshot.Data);
    }

    [Fact]
    public void FromDataObject_with_arbitrary_format_classifies_as_HasContent()
    {
        // Imagen, archivo, custom format — cualquier cosa con formats > 0 es HasContent.
        // El restore va a delegar en SetDataObject(data) sin importar el tipo.
        var withFiles = new DataObject(DataFormats.FileDrop, new[] { @"C:\algo.txt" });

        var snapshot = ClipboardSnapshot.FromDataObject(withFiles);

        Assert.Equal(ClipboardSnapshot.Kind.HasContent, snapshot.State);
        Assert.Same(withFiles, snapshot.Data);
    }

    [Fact]
    public void Empty_sentinel_has_Empty_state_and_no_data()
    {
        Assert.Equal(ClipboardSnapshot.Kind.Empty, ClipboardSnapshot.Empty.State);
        Assert.Null(ClipboardSnapshot.Empty.Data);
    }

    [Fact]
    public void Unknown_sentinel_has_Unknown_state_and_no_data()
    {
        Assert.Equal(ClipboardSnapshot.Kind.Unknown, ClipboardSnapshot.Unknown.State);
        Assert.Null(ClipboardSnapshot.Unknown.Data);
    }

    [Fact]
    public void FromData_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ClipboardSnapshot.FromData(null!));
    }
}
