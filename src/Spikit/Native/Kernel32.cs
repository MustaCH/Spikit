using System.Runtime.InteropServices;

namespace Spikit.Native;

internal static class Kernel32
{
    // V1 no necesita firmas de kernel32 — el archivo existe para mantener la convención
    // "Native/ es la única capa que importa System.Runtime.InteropServices" (architecture.md).
    // Cuando un service de EP-2+ requiera, por ejemplo, CreateMutex para single-instance,
    // se agregan acá las firmas correspondientes.
}
