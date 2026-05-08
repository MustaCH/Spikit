using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Clip;
using Spikit.Services.History;
using Spikit.Services.Settings;
using Spikit.ViewModels.Settings;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class HistorySectionViewModelTests
{
    private static (HistorySectionViewModel vm,
                    FakeSettingsService settings,
                    FakeHistoryStore store,
                    FakeClipboard clipboard,
                    FakeConfirmationDialog dialog) MakeVm(
        bool historyEnabled = true,
        IEnumerable<HistoryEntry>? seed = null)
    {
        var settings = new FakeSettingsService
        {
            Saved = new AppSettings { Privacy = new PrivacySettings { HistoryEnabled = historyEnabled } },
        };
        var store = new FakeHistoryStore();
        if (seed is not null)
        {
            foreach (var e in seed)
            {
                store.Append(e);
            }
        }
        var clipboard = new FakeClipboard();
        var dialog = new FakeConfirmationDialog();

        var vm = new HistorySectionViewModel(
            NullLogger<HistorySectionViewModel>.Instance,
            settings,
            store,
            clipboard,
            dialog);
        return (vm, settings, store, clipboard, dialog);
    }

    private static HistoryEntry MakeEntry(string text = "x", DateTimeOffset? ts = null, string proc = "cursor.exe")
        => new()
        {
            Id = Guid.NewGuid(),
            Text = text,
            Timestamp = ts ?? DateTimeOffset.UtcNow,
            TargetProcessName = proc,
            DurationMs = 12345,
        };

    // ===== Estados =====

    [Fact]
    public void When_history_off_state_is_off()
    {
        var (vm, _, _, _, _) = MakeVm(historyEnabled: false);

        Assert.True(vm.IsHistoryOff);
        Assert.False(vm.IsHistoryOn);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void When_history_on_and_no_data_state_is_empty()
    {
        var (vm, _, _, _, _) = MakeVm(historyEnabled: true);

        Assert.True(vm.IsHistoryOn);
        Assert.True(vm.IsEmptyOn);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void When_history_on_with_data_state_has_results()
    {
        var (vm, _, _, _, _) = MakeVm(seed: new[] { MakeEntry(text: "hola") });

        Assert.False(vm.IsEmptyOn);
        Assert.True(vm.HasResults);
        Assert.Single(vm.VisibleEntries);
    }

    // ===== Paginación =====

    [Fact]
    public void First_page_loads_up_to_PageSize_entries()
    {
        var seeds = Enumerable.Range(0, HistorySectionViewModel.PageSize + 50)
            .Select(i => MakeEntry(text: $"e{i}", ts: DateTimeOffset.UtcNow.AddSeconds(i)));
        var (vm, _, _, _, _) = MakeVm(seed: seeds);

        Assert.Equal(HistorySectionViewModel.PageSize, vm.VisibleEntries.Count);
        Assert.True(vm.HasMorePages);
    }

    [Fact]
    public void LoadMore_appends_next_page_and_disables_when_exhausted()
    {
        var total = HistorySectionViewModel.PageSize + 25;
        var seeds = Enumerable.Range(0, total)
            .Select(i => MakeEntry(text: $"e{i}", ts: DateTimeOffset.UtcNow.AddSeconds(i)));
        var (vm, _, _, _, _) = MakeVm(seed: seeds);

        vm.LoadMoreCommand.Execute(null);

        Assert.Equal(total, vm.VisibleEntries.Count);
        Assert.False(vm.HasMorePages);
    }

    // ===== Búsqueda =====

    [Fact]
    public void Setting_search_query_filters_by_text_contains()
    {
        var (vm, _, _, _, _) = MakeVm(seed: new[]
        {
            MakeEntry(text: "Refactor del módulo auth"),
            MakeEntry(text: "Otro dictado"),
            MakeEntry(text: "REFACTOR de la lista"),
        });

        vm.SearchQuery = "refactor";

        Assert.Equal(2, vm.VisibleEntries.Count);
        Assert.False(vm.HasNoSearchMatches);
    }

    [Fact]
    public void Search_with_no_matches_sets_HasNoSearchMatches()
    {
        var (vm, _, _, _, _) = MakeVm(seed: new[] { MakeEntry(text: "hola mundo") });

        vm.SearchQuery = "xyzzy";

        Assert.True(vm.HasNoSearchMatches);
        Assert.False(vm.HasResults);
        Assert.Empty(vm.VisibleEntries);
    }

    [Fact]
    public void Clearing_search_restores_full_pagination()
    {
        var (vm, _, _, _, _) = MakeVm(seed: new[]
        {
            MakeEntry(text: "uno"),
            MakeEntry(text: "dos"),
        });

        vm.SearchQuery = "uno";
        Assert.Single(vm.VisibleEntries);

        vm.ClearSearchCommand.Execute(null);

        Assert.Equal(string.Empty, vm.SearchQuery);
        Assert.Equal(2, vm.VisibleEntries.Count);
    }

    // ===== Expand =====

    [Fact]
    public void Toggle_expand_marks_only_one_entry_as_expanded()
    {
        var e1 = MakeEntry(text: "a");
        var e2 = MakeEntry(text: "b");
        var (vm, _, _, _, _) = MakeVm(seed: new[] { e1, e2 });

        vm.ToggleExpandCommand.Execute(e1.Id);

        Assert.Equal(e1.Id, vm.ExpandedEntryId);
        Assert.True(vm.VisibleEntries.First(v => v.Id == e1.Id).IsExpanded);
        Assert.False(vm.VisibleEntries.First(v => v.Id == e2.Id).IsExpanded);
    }

    [Fact]
    public void Toggle_expand_same_id_collapses()
    {
        var e1 = MakeEntry();
        var (vm, _, _, _, _) = MakeVm(seed: new[] { e1 });

        vm.ToggleExpandCommand.Execute(e1.Id);
        vm.ToggleExpandCommand.Execute(e1.Id);

        Assert.Null(vm.ExpandedEntryId);
    }

    // ===== Copy =====

    [Fact]
    public void CopyText_writes_full_text_to_clipboard()
    {
        var entry = MakeEntry(text: "lorem ipsum");
        var (vm, _, _, clipboard, _) = MakeVm(seed: new[] { entry });

        vm.CopyTextCommand.Execute(entry.Id);

        Assert.Equal("lorem ipsum", clipboard.LastText);
        Assert.True(vm.HasOperationFeedback);
        Assert.False(vm.OperationFeedbackIsError);
    }

    // ===== Delete one =====

    [Fact]
    public void DeleteOne_when_user_cancels_keeps_entry()
    {
        var entry = MakeEntry();
        var (vm, _, store, _, dialog) = MakeVm(seed: new[] { entry });
        dialog.NextResult = false;

        vm.DeleteOneCommand.Execute(entry.Id);

        Assert.Single(vm.VisibleEntries);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void DeleteOne_when_user_confirms_removes_from_store_and_view()
    {
        var entry = MakeEntry();
        var (vm, _, store, _, dialog) = MakeVm(seed: new[] { entry });
        dialog.NextResult = true;

        vm.DeleteOneCommand.Execute(entry.Id);

        Assert.Empty(vm.VisibleEntries);
        Assert.Equal(0, store.Count());
        Assert.True(vm.HasOperationFeedback);
    }

    // ===== Delete all =====

    [Fact]
    public void DeleteAll_when_user_confirms_clears_everything()
    {
        var seeds = new[] { MakeEntry(text: "a"), MakeEntry(text: "b") };
        var (vm, _, store, _, dialog) = MakeVm(seed: seeds);
        dialog.NextResult = true;

        vm.DeleteAllCommand.Execute(null);

        Assert.Empty(vm.VisibleEntries);
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void DeleteAll_when_user_cancels_no_op()
    {
        var (vm, _, store, _, dialog) = MakeVm(seed: new[] { MakeEntry() });
        dialog.NextResult = false;

        vm.DeleteAllCommand.Execute(null);

        Assert.Equal(1, store.Count());
        Assert.Single(vm.VisibleEntries);
    }

    [Fact]
    public void Delete_dialog_marks_action_as_destructive()
    {
        var entry = MakeEntry();
        var (vm, _, _, _, dialog) = MakeVm(seed: new[] { entry });

        vm.DeleteOneCommand.Execute(entry.Id);

        Assert.NotNull(dialog.LastRequest);
        Assert.True(dialog.LastRequest!.IsDestructive);
    }

    // ===== Refresh y nav =====

    [Fact]
    public void Refresh_after_toggle_changed_off_clears_visible_entries()
    {
        var (vm, settings, _, _, _) = MakeVm(seed: new[] { MakeEntry() });
        Assert.Single(vm.VisibleEntries);

        settings.Saved!.Privacy.HistoryEnabled = false;
        vm.Refresh();

        Assert.True(vm.IsHistoryOff);
        Assert.Empty(vm.VisibleEntries);
    }

    [Fact]
    public void GoToPrivacy_emits_navigate_request()
    {
        var (vm, _, _, _, _) = MakeVm();
        SettingsSection? captured = null;
        vm.NavigateRequested += (_, section) => captured = section;

        vm.GoToPrivacyCommand.Execute(null);

        Assert.Equal(SettingsSection.Privacy, captured);
    }

    // ===== Formato del HistoryEntryViewModel =====

    [Fact]
    public void HistoryEntryViewModel_formats_header_and_preview()
    {
        var entry = new HistoryEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = new DateTimeOffset(2026, 4, 28, 18, 42, 0, TimeSpan.Zero).ToLocalTime(),
            DurationMs = 18_000,
            Text = "Refactorizá la función\ncalculateTotal",
            TargetProcessName = "cursor.exe",
        };

        var vm = new HistoryEntryViewModel(entry);

        Assert.Contains("cursor.exe", vm.Header);
        Assert.Contains("0:18", vm.Header);
        Assert.DoesNotContain("\n", vm.Preview);
    }

    [Fact]
    public void HistoryEntryViewModel_unknown_process_falls_back_to_label()
    {
        var entry = new HistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Text = "x",
            TargetProcessName = string.Empty,
            DurationMs = 1000,
        };

        var vm = new HistoryEntryViewModel(entry);

        Assert.Contains("(desconocido)", vm.Header);
    }

    // ===== Fakes =====

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Saved { get; set; }
        public event EventHandler? SettingsChanged;
        public AppSettings Load() => Saved ?? new AppSettings();
        public void Save(AppSettings settings)
        {
            Saved = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeHistoryStore : IHistoryStore
    {
        private readonly List<HistoryEntry> _entries = new();

        public void Append(HistoryEntry entry)
        {
            if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
            _entries.Add(entry);
        }

        public IReadOnlyList<HistoryEntry> LoadPage(int skip, int take)
        {
            if (take <= 0) return Array.Empty<HistoryEntry>();
            if (skip < 0) skip = 0;
            return _entries.OrderByDescending(e => e.Timestamp).Skip(skip).Take(take).ToList();
        }

        public int Count() => _entries.Count;

        public IReadOnlyList<HistoryEntry> Search(string query)
        {
            if (string.IsNullOrEmpty(query)) return LoadPage(0, int.MaxValue);
            return _entries
                .Where(e => e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Timestamp)
                .ToList();
        }

        public void DeleteOne(Guid id) => _entries.RemoveAll(e => e.Id == id);
        public void DeleteAll() => _entries.Clear();
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? LastText { get; private set; }
        public void SetText(string text) => LastText = text;
    }

    private sealed class FakeConfirmationDialog : IConfirmationDialogService
    {
        public bool NextResult { get; set; }
        public ConfirmationRequest? LastRequest { get; private set; }
        public bool Confirm(ConfirmationRequest request)
        {
            LastRequest = request;
            return NextResult;
        }
    }
}
