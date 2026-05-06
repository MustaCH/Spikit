namespace Spikit.Services.Insertion;

public interface ITextInsertionService
{
    // Inserta `text` en la ventana identificada por `targetHwnd`. La ventana debe
    // tener un campo editable con foco implícito (la app target la enfoca al recibir
    // SetForegroundWindow). Comportamiento exacto en docs/architecture.md § "Flow de dictado" → paso 5.
    Task<InsertionResult> InsertIntoForegroundAsync(string text, IntPtr targetHwnd);
}

public enum InsertionResult
{
    Pasted,       // Ctrl+V despachado al target.
    TargetGone,   // El HWND ya no apunta a una ventana viva.
    Failed,       // No se pudo despachar el paste (clipboard, foreground o SendInput falló).
}
