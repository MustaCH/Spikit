using System.Runtime.InteropServices;
using System.Windows;
using Spikit.Services.Insertion;

namespace Spikit.Tests.Services.Insertion;

// Cubre los 3 casos que el restore tiene que tratar distinto (RN-4 + ticket 86ahdt78n):
//   - Empty (clipboard estaba vacío)            → tras el paste se limpia.
//   - HasContent (había datos)                  → tras el paste se restauran.
//   - Unknown (snapshot falló o no extraíble)   → no se toca el clipboard.
//
// Verifican que FromDataObject EXTRAE los datos a un DataObject local (no guarda
// el proxy del clipboard), porque ese proxy queda inválido cuando spikit asume
// ownership del clipboard con la transcripción intermedia. El switch del restore
// en ClipboardPasteService.TryRestoreClipboardAsync es trivial 1-a-1 sobre el
// Kind y se verifica con smoke manual end-to-end.
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
    public void FromDataObject_with_text_copies_data_to_local_object()
    {
        var withText = new DataObject(DataFormats.UnicodeText, "hola");

        var snapshot = ClipboardSnapshot.FromDataObject(withText);

        Assert.Equal(ClipboardSnapshot.Kind.HasContent, snapshot.State);
        // La copia debe ser un DataObject distinto del original: si guardáramos
        // la referencia, el proxy COM se invalidaría tras el paste.
        Assert.NotNull(snapshot.Data);
        Assert.NotSame(withText, snapshot.Data);
        Assert.True(snapshot.Data!.GetDataPresent(DataFormats.UnicodeText));
        Assert.Equal("hola", snapshot.Data.GetData(DataFormats.UnicodeText));
    }

    [Fact]
    public void FromDataObject_with_arbitrary_format_copies_data_to_local_object()
    {
        // Imagen, archivo, custom format — cualquier cosa con formatos extraíbles
        // se copia byte-a-byte a un DataObject local; el restore va a delegar en
        // SetDataObject(data, copy:true) sin importar el tipo.
        var withFiles = new DataObject(DataFormats.FileDrop, new[] { @"C:\algo.txt" });

        var snapshot = ClipboardSnapshot.FromDataObject(withFiles);

        Assert.Equal(ClipboardSnapshot.Kind.HasContent, snapshot.State);
        Assert.NotNull(snapshot.Data);
        Assert.NotSame(withFiles, snapshot.Data);
        var files = (string[])snapshot.Data!.GetData(DataFormats.FileDrop)!;
        Assert.Equal(new[] { @"C:\algo.txt" }, files);
    }

    [Fact]
    public void FromDataObject_with_multiple_formats_preserves_each_one()
    {
        // El clipboard real suele exponer el mismo contenido en varios formatos
        // (texto plano + unicode + html). FromDataObject tiene que copiarlos todos.
        var multi = new DataObject();
        multi.SetData(DataFormats.UnicodeText, "hola");
        multi.SetData(DataFormats.Text, "hola");

        var snapshot = ClipboardSnapshot.FromDataObject(multi);

        Assert.Equal(ClipboardSnapshot.Kind.HasContent, snapshot.State);
        Assert.True(snapshot.Data!.GetDataPresent(DataFormats.UnicodeText));
        Assert.True(snapshot.Data.GetDataPresent(DataFormats.Text));
    }

    [Fact]
    public void FromDataObject_classifies_as_Unknown_when_all_formats_fail_to_extract()
    {
        // Si el IDataObject reporta formatos pero todos tiran al extraer (ej. proxy
        // COM ya invalidado, formatos delayed-render que fallan), no podemos
        // restaurar nada confiablemente → Unknown, mejor no tocar el clipboard final.
        var snapshot = ClipboardSnapshot.FromDataObject(new ThrowingDataObject());

        Assert.Equal(ClipboardSnapshot.Kind.Unknown, snapshot.State);
        Assert.Null(snapshot.Data);
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

    private sealed class ThrowingDataObject : IDataObject
    {
        public string[] GetFormats() => new[] { "FakeFormat" };
        public string[] GetFormats(bool autoConvert) => new[] { "FakeFormat" };
        public object GetData(string format) =>
            throw new COMException("simulated COM failure");
        public object GetData(string format, bool autoConvert) =>
            throw new COMException("simulated COM failure");
        public object GetData(Type format) =>
            throw new COMException("simulated COM failure");
        public bool GetDataPresent(string format) => true;
        public bool GetDataPresent(string format, bool autoConvert) => true;
        public bool GetDataPresent(Type format) => true;
        public void SetData(string format, object data) =>
            throw new NotSupportedException();
        public void SetData(string format, object data, bool autoConvert) =>
            throw new NotSupportedException();
        public void SetData(Type format, object data) =>
            throw new NotSupportedException();
        public void SetData(object data) =>
            throw new NotSupportedException();
    }
}
