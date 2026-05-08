using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Clip;
using Spikit.Services.History;
using Spikit.Services.Settings;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Historial de Settings (EP-4.8). Coordina cuatro estados visuales:
//   - Off       → privacy.historyEnabled = false. CTA "Ir a Privacidad →".
//   - EmptyOn   → toggle ON pero el store no tiene entries todavía.
//   - Data      → toggle ON con datos: buscador + lista paginada + delete one/all.
//   - NoMatches → SearchQuery no vacío y sin matches: CTA "Limpiar búsqueda".
//
// Paginación:
//   - Page size = 100 (acceptance criteria del ticket). LoadMoreCommand carga el próximo
//     bloque cuando el usuario scrollea al final.
//   - HasMorePages se calcula contra el Count del store (sin search) o el size del
//     SearchSnapshot (con search).
//
// Search:
//   - Inline en el textbox del topo. Setter de SearchQuery dispara la query y resetea la
//     paginación. Si la query queda vacía, volvemos al modo paginated normal.
//   - Sin debounce en V1 — el dataset es chico (≤100 inicial) y la búsqueda es synchronous
//     contains. Si el usuario tipea rápido, el cost es trivial.
//
// Navegación a Privacidad:
//   - El empty-state OFF tiene un botón "Ir a Privacidad". El VM no conoce el shell;
//     emite el evento NavigateRequested(SettingsSection.Privacy) y el SettingsViewModel
//     lo escucha para hacer el nav. Misma idea que Action callbacks pero más explícito.
public sealed class HistorySectionViewModel : ViewModelBase
{
    public const int PageSize = 100;

    private readonly ILogger<HistorySectionViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IHistoryStore _store;
    private readonly IClipboardService _clipboard;
    private readonly IConfirmationDialogService _confirmationDialog;

    // Snapshot de la búsqueda actual. Cuando SearchQuery está vacío, _searchSnapshot es
    // null y la paginación va contra _store.LoadPage. Cuando hay query, _searchSnapshot
    // contiene los matches y la paginación slicea sobre él (sin reconsultar el store).
    private IReadOnlyList<HistoryEntry>? _searchSnapshot;

    private string _searchQuery = string.Empty;
    private Guid? _expandedEntryId;
    private bool _historyEnabled;
    private bool _suppressSearchEffects;
    private string? _operationFeedback;
    private bool _operationFeedbackIsError;

    public HistorySectionViewModel(
        ILogger<HistorySectionViewModel> logger,
        ISettingsService settingsService,
        IHistoryStore store,
        IClipboardService clipboard,
        IConfirmationDialogService confirmationDialog)
    {
        _logger = logger;
        _settingsService = settingsService;
        _store = store;
        _clipboard = clipboard;
        _confirmationDialog = confirmationDialog;

        VisibleEntries = new ObservableCollection<HistoryEntryViewModel>();

        LoadMoreCommand = new RelayCommand(LoadMore, () => HasMorePages);
        ClearSearchCommand = new RelayCommand(ClearSearch);
        DeleteAllCommand = new RelayCommand(DeleteAll, () => HasResults);
        GoToPrivacyCommand = new RelayCommand(() => RequestNavigation(SettingsSection.Privacy));
        ToggleExpandCommand = new RelayCommand<Guid>(ToggleExpand);
        CopyTextCommand = new RelayCommand<Guid>(CopyText);
        DeleteOneCommand = new RelayCommand<Guid>(DeleteOne);

        Refresh();
    }

    // El SettingsViewModel se suscribe acá para hacer el nav cuando el usuario aprieta
    // "Ir a Privacidad →" desde el empty state OFF.
    public event EventHandler<SettingsSection>? NavigateRequested;

    public ObservableCollection<HistoryEntryViewModel> VisibleEntries { get; }

    // ============ Estados ============

    public bool IsHistoryOff => !_historyEnabled;
    public bool IsHistoryOn => _historyEnabled;

    // En modo ON sin query: empty si el store está vacío.
    // En modo ON con query: NO empty (mostrar empty-search si no hay matches).
    public bool IsEmptyOn => IsHistoryOn
                              && string.IsNullOrEmpty(_searchQuery)
                              && _store.Count() == 0;

    public bool HasResults => IsHistoryOn && VisibleEntries.Count > 0;

    public bool HasNoSearchMatches => IsHistoryOn
                                       && !string.IsNullOrEmpty(_searchQuery)
                                       && (_searchSnapshot is null || _searchSnapshot.Count == 0);

    public bool HasMorePages
    {
        get
        {
            if (!IsHistoryOn) return false;
            if (_searchSnapshot is not null)
            {
                return VisibleEntries.Count < _searchSnapshot.Count;
            }
            return VisibleEntries.Count < _store.Count();
        }
    }

    // ============ Búsqueda ============

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value ?? string.Empty)) return;
            if (_suppressSearchEffects) return;

            ApplySearchAndReload();
        }
    }

    // ============ Expand inline ============

    public Guid? ExpandedEntryId
    {
        get => _expandedEntryId;
        private set
        {
            if (SetProperty(ref _expandedEntryId, value))
            {
                foreach (var entry in VisibleEntries)
                {
                    entry.IsExpanded = entry.Id == value;
                }
            }
        }
    }

    // ============ Operation feedback (post delete) ============

    // Mensaje inline tras DeleteOne / DeleteAll. Mismo patrón que PrivacySectionVM.
    // Null = sin feedback.
    public string? OperationFeedback
    {
        get => _operationFeedback;
        private set
        {
            if (SetProperty(ref _operationFeedback, value))
            {
                OnPropertyChanged(nameof(HasOperationFeedback));
            }
        }
    }

    public bool HasOperationFeedback => !string.IsNullOrEmpty(_operationFeedback);

    public bool OperationFeedbackIsError
    {
        get => _operationFeedbackIsError;
        private set => SetProperty(ref _operationFeedbackIsError, value);
    }

    // ============ Commands ============

    public ICommand LoadMoreCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand GoToPrivacyCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand CopyTextCommand { get; }
    public ICommand DeleteOneCommand { get; }

    // Llamado por SettingsViewModel cada vez que el usuario navega a Historial. Releé
    // el toggle de Privacy y la primera página. Importante porque el toggle puede haber
    // cambiado en otra sección sin que este VM lo sepa (no estamos suscritos a
    // SettingsChanged en V1 — se cubre en EP-4.10 si hace falta reactividad cross-section).
    public void Refresh()
    {
        var settings = _settingsService.Load();
        var newEnabled = settings.Privacy.HistoryEnabled;

        var enabledChanged = newEnabled != _historyEnabled;
        _historyEnabled = newEnabled;

        // Si pasó de ON a OFF, limpiamos la lista visible y la búsqueda.
        if (!_historyEnabled)
        {
            VisibleEntries.Clear();
            _searchSnapshot = null;
            _suppressSearchEffects = true;
            try { SearchQuery = string.Empty; }
            finally { _suppressSearchEffects = false; }
        }
        else
        {
            // ON: refresh de la primera página. Si había una búsqueda activa, re-ejecutarla.
            ApplySearchAndReload();
        }

        // Limpieza del feedback: siempre que el usuario navega de vuelta a Historial,
        // arrancamos sin "✓ Borrado" residual de la última vez.
        OperationFeedback = null;
        OperationFeedbackIsError = false;

        NotifyAllStatesChanged();
        if (enabledChanged)
        {
            _logger.LogDebug("Historial → toggle privacy ahora {Enabled}", _historyEnabled);
        }
    }

    private void ApplySearchAndReload()
    {
        VisibleEntries.Clear();
        ExpandedEntryId = null;

        if (string.IsNullOrEmpty(_searchQuery))
        {
            _searchSnapshot = null;
            // Primer page del store
            foreach (var entry in _store.LoadPage(skip: 0, take: PageSize))
            {
                VisibleEntries.Add(ToViewModel(entry));
            }
        }
        else
        {
            _searchSnapshot = _store.Search(_searchQuery);
            // Para search no paginamos lazy (ver comentario en LoadMore). Cargamos hasta
            // PageSize del snapshot y dejamos que LoadMore traiga el resto si hay.
            foreach (var entry in _searchSnapshot.Take(PageSize))
            {
                VisibleEntries.Add(ToViewModel(entry));
            }
        }

        NotifyAllStatesChanged();
    }

    private void LoadMore()
    {
        if (!HasMorePages) return;

        IEnumerable<HistoryEntry> next;
        if (_searchSnapshot is not null)
        {
            // Slice del snapshot: ya está ordenado DESC, salteamos lo que se mostró.
            next = _searchSnapshot.Skip(VisibleEntries.Count).Take(PageSize);
        }
        else
        {
            next = _store.LoadPage(skip: VisibleEntries.Count, take: PageSize);
        }

        foreach (var entry in next)
        {
            VisibleEntries.Add(ToViewModel(entry));
        }

        NotifyAllStatesChanged();
    }

    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    private void ToggleExpand(Guid id)
    {
        ExpandedEntryId = ExpandedEntryId == id ? null : id;
    }

    private void CopyText(Guid id)
    {
        var entry = VisibleEntries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;

        try
        {
            _clipboard.SetText(entry.FullText);
            OperationFeedbackIsError = false;
            OperationFeedback = "Texto copiado al portapapeles.";
            _logger.LogDebug("Historial: copiada entry {Id}", id);
        }
        catch (Exception ex)
        {
            OperationFeedbackIsError = true;
            OperationFeedback = "No se pudo copiar al portapapeles. Probá de nuevo.";
            _logger.LogWarning(ex, "Historial: error copiando entry");
        }
    }

    private void DeleteOne(Guid id)
    {
        var entry = VisibleEntries.FirstOrDefault(e => e.Id == id);
        if (entry is null) return;

        var confirmed = _confirmationDialog.Confirm(new ConfirmationRequest(
            Title: "Borrar este dictado",
            Message: "Esta acción no se puede deshacer. ¿Continuar?",
            ConfirmLabel: "Borrar",
            CancelLabel: "Cancelar",
            IsDestructive: true));
        if (!confirmed) return;

        try
        {
            _store.DeleteOne(id);
            VisibleEntries.Remove(entry);
            // Si el snapshot de search está activo, sacamos también la entry de él para
            // que la paginación HasMorePages no quede inconsistente.
            if (_searchSnapshot is not null)
            {
                _searchSnapshot = _searchSnapshot.Where(e => e.Id != id).ToList();
            }
            if (ExpandedEntryId == id) ExpandedEntryId = null;
            OperationFeedbackIsError = false;
            OperationFeedback = "Dictado borrado.";
        }
        catch (Exception ex)
        {
            OperationFeedbackIsError = true;
            OperationFeedback = "No se pudo borrar el dictado. Probá de nuevo.";
            _logger.LogWarning(ex, "Historial: error borrando entry {Id}", id);
        }

        NotifyAllStatesChanged();
    }

    private void DeleteAll()
    {
        var confirmed = _confirmationDialog.Confirm(new ConfirmationRequest(
            Title: "Borrar todo el historial",
            Message: "Vas a borrar todos tus dictados guardados. Esta acción no se puede deshacer. ¿Continuar?",
            ConfirmLabel: "Borrar todo",
            CancelLabel: "Cancelar",
            IsDestructive: true));
        if (!confirmed) return;

        try
        {
            _store.DeleteAll();
            VisibleEntries.Clear();
            _searchSnapshot = null;
            ExpandedEntryId = null;
            _suppressSearchEffects = true;
            try { SearchQuery = string.Empty; }
            finally { _suppressSearchEffects = false; }
            OperationFeedbackIsError = false;
            OperationFeedback = "Historial borrado.";
        }
        catch (Exception ex)
        {
            OperationFeedbackIsError = true;
            OperationFeedback = "No se pudo borrar el historial. Probá de nuevo.";
            _logger.LogWarning(ex, "Historial: error borrando todo");
        }

        NotifyAllStatesChanged();
    }

    private void RequestNavigation(SettingsSection section)
    {
        NavigateRequested?.Invoke(this, section);
    }

    private static HistoryEntryViewModel ToViewModel(HistoryEntry entry) => new(entry);

    private void NotifyAllStatesChanged()
    {
        OnPropertyChanged(nameof(IsHistoryOff));
        OnPropertyChanged(nameof(IsHistoryOn));
        OnPropertyChanged(nameof(IsEmptyOn));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoSearchMatches));
        OnPropertyChanged(nameof(HasMorePages));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}

// Wrapper UI-friendly de HistoryEntry. Expone strings ya formateadas y un flag IsExpanded
// para el binding del expand-on-click. Mantenemos la lógica de formateo acá (en lugar
// de IValueConverters) porque concentra el "cómo se muestra una entry" en un solo lugar
// testeable.
public sealed class HistoryEntryViewModel : ViewModelBase
{
    public HistoryEntryViewModel(HistoryEntry entry)
    {
        Id = entry.Id;
        FullText = entry.Text;
        Header = FormatHeader(entry);
        Preview = OneLinePreview(entry.Text);
    }

    public Guid Id { get; }
    public string FullText { get; }
    public string Header { get; }
    public string Preview { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    // Formato spec: "{fecha} · {hora} · {duración} · {procesoTarget}". La fecha se muestra
    // en TZ local (no UTC) — el usuario ve cuando dictó "para él", no en Greenwich.
    private static string FormatHeader(HistoryEntry entry)
    {
        var local = entry.Timestamp.ToLocalTime();
        var date = local.ToString("yyyy-MM-dd");
        var time = local.ToString("HH:mm");
        var duration = FormatDuration(entry.DurationMs);
        var process = string.IsNullOrEmpty(entry.TargetProcessName) ? "(desconocido)" : entry.TargetProcessName;
        return $"{date} · {time} · {duration} · {process}";
    }

    private static string FormatDuration(long ms)
    {
        var totalSeconds = (long)Math.Round(ms / 1000.0);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }

    private static string OneLinePreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(sin contenido)";
        // Aplastamos newlines a espacios. El TextTrimming del XAML pone el "…" al final
        // si excede el ancho.
        return text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
