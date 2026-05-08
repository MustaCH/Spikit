using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.History;

namespace Spikit.Tests.Services.History;

public class JsonHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public JsonHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "Spikit.HistoryTests." + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "history.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. Si el test holding del archivo lo bloquea, el OS limpia /tmp.
        }
    }

    private JsonHistoryStore MakeStore() =>
        new(_filePath, NullLogger<JsonHistoryStore>.Instance);

    private static HistoryEntry MakeEntry(
        DateTimeOffset? timestamp = null,
        string text = "test text",
        string process = "cursor.exe",
        long durationMs = 1234)
        => new()
        {
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Text = text,
            TargetProcessName = process,
            DurationMs = durationMs,
        };

    // ===== Bootstrap / load =====

    [Fact]
    public void Count_returns_zero_when_file_missing()
    {
        var store = MakeStore();

        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void LoadPage_returns_empty_when_file_missing()
    {
        var store = MakeStore();

        Assert.Empty(store.LoadPage(0, 100));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_empty()
    {
        File.WriteAllText(_filePath, "not valid json {");
        var store = MakeStore();

        Assert.Equal(0, store.Count());
        Assert.Empty(store.LoadPage(0, 100));
    }

    // ===== Append =====

    [Fact]
    public void Append_persists_entry_atomically()
    {
        var store = MakeStore();
        var entry = MakeEntry(text: "hola mundo");

        store.Append(entry);

        Assert.True(File.Exists(_filePath));
        var fresh = MakeStore();
        Assert.Equal(1, fresh.Count());
        Assert.Equal("hola mundo", fresh.LoadPage(0, 10)[0].Text);
    }

    [Fact]
    public void Append_assigns_id_when_empty_guid()
    {
        var store = MakeStore();
        var entry = MakeEntry();
        entry.Id = Guid.Empty;

        store.Append(entry);

        var loaded = store.LoadPage(0, 10).Single();
        Assert.NotEqual(Guid.Empty, loaded.Id);
    }

    [Fact]
    public void Append_preserves_id_when_provided()
    {
        var store = MakeStore();
        var entry = MakeEntry();
        entry.Id = Guid.NewGuid();
        var expected = entry.Id;

        store.Append(entry);

        Assert.Equal(expected, store.LoadPage(0, 10).Single().Id);
    }

    [Fact]
    public void Append_multiple_round_trips_via_disk()
    {
        var store = MakeStore();
        store.Append(MakeEntry(text: "uno"));
        store.Append(MakeEntry(text: "dos"));
        store.Append(MakeEntry(text: "tres"));

        var fresh = MakeStore();

        Assert.Equal(3, fresh.Count());
    }

    // ===== Orden DESC =====

    [Fact]
    public void LoadPage_orders_by_timestamp_descending()
    {
        var store = MakeStore();
        var older = MakeEntry(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10), text: "older");
        var newer = MakeEntry(timestamp: DateTimeOffset.UtcNow, text: "newer");
        store.Append(older);
        store.Append(newer);

        var page = store.LoadPage(0, 10);

        Assert.Equal("newer", page[0].Text);
        Assert.Equal("older", page[1].Text);
    }

    // ===== Paginación =====

    [Fact]
    public void LoadPage_skip_and_take_work()
    {
        var store = MakeStore();
        var baseTime = DateTimeOffset.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            store.Append(MakeEntry(timestamp: baseTime.AddSeconds(i), text: $"entry-{i}"));
        }

        var page2 = store.LoadPage(skip: 3, take: 4);

        // DESC ordering: entry-9 (idx 0), entry-8 (idx 1), ..., entry-0 (idx 9).
        // skip 3 take 4 → entry-6, entry-5, entry-4, entry-3.
        Assert.Equal(4, page2.Count);
        Assert.Equal("entry-6", page2[0].Text);
        Assert.Equal("entry-3", page2[3].Text);
    }

    [Fact]
    public void LoadPage_take_zero_returns_empty()
    {
        var store = MakeStore();
        store.Append(MakeEntry());

        Assert.Empty(store.LoadPage(0, 0));
    }

    [Fact]
    public void LoadPage_skip_negative_clamps_to_zero()
    {
        var store = MakeStore();
        store.Append(MakeEntry(text: "only"));

        Assert.Single(store.LoadPage(-5, 10));
    }

    // ===== Search =====

    [Fact]
    public void Search_is_case_insensitive_contains_on_text()
    {
        var store = MakeStore();
        store.Append(MakeEntry(text: "Refactorizá la función calculateTotal"));
        store.Append(MakeEntry(text: "Otro dictado sin matches"));
        store.Append(MakeEntry(text: "REFACTOR de auth"));

        var matches = store.Search("refactor");

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void Search_does_not_match_process_name()
    {
        // El AC define búsqueda solo sobre el texto. Si el día de mañana queremos buscar
        // por proceso, lo extendemos — pero sin esperar al usuario.
        var store = MakeStore();
        store.Append(MakeEntry(text: "hola", process: "cursor.exe"));

        Assert.Empty(store.Search("cursor"));
    }

    [Fact]
    public void Search_with_empty_query_returns_all_desc()
    {
        var store = MakeStore();
        store.Append(MakeEntry(timestamp: DateTimeOffset.UtcNow.AddMinutes(-1), text: "a"));
        store.Append(MakeEntry(timestamp: DateTimeOffset.UtcNow, text: "b"));

        var all = store.Search("");

        Assert.Equal(2, all.Count);
        Assert.Equal("b", all[0].Text);
    }

    [Fact]
    public void Search_no_matches_returns_empty()
    {
        var store = MakeStore();
        store.Append(MakeEntry(text: "lorem ipsum"));

        Assert.Empty(store.Search("xyzzy"));
    }

    // ===== Delete =====

    [Fact]
    public void DeleteOne_removes_entry_and_persists()
    {
        var store = MakeStore();
        var keep = MakeEntry(text: "keep");
        var bye = MakeEntry(text: "bye");
        store.Append(keep);
        store.Append(bye);

        store.DeleteOne(bye.Id);

        var fresh = MakeStore();
        Assert.Equal(1, fresh.Count());
        Assert.Equal("keep", fresh.LoadPage(0, 10)[0].Text);
    }

    [Fact]
    public void DeleteOne_unknown_id_is_idempotent()
    {
        var store = MakeStore();
        store.Append(MakeEntry(text: "still here"));

        store.DeleteOne(Guid.NewGuid());

        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void DeleteAll_removes_file()
    {
        var store = MakeStore();
        store.Append(MakeEntry());
        Assert.True(File.Exists(_filePath));

        store.DeleteAll();

        Assert.False(File.Exists(_filePath));
        Assert.Equal(0, store.Count());
    }

    [Fact]
    public void DeleteAll_when_file_missing_is_idempotent()
    {
        var store = MakeStore();

        store.DeleteAll();

        Assert.Equal(0, store.Count());
    }
}
