using System.Text.Json;
using Xunit.Abstractions;
using static CrowDB.Engine.Tests.TestSupport;

namespace CrowDB.Engine.Tests;

/// <summary>
/// The on-disk contract of Option A (docs/001 §3): one <c>crowdb.log</c>, one
/// envelope per line, old bytes never modified — appends only. The log is the
/// source of truth, so these tests read it directly.
/// </summary>
public class DiskFormatTests(ITestOutputHelper output)
{
    [Fact]
    public void GIVEN_two_puts_WHEN_inspecting_the_log_THEN_each_record_is_one_v1_envelope_line()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.Root, output))
        {
            store.Put("user:1", Bytes("""{"name":"Ada"}"""));
            store.Put("user:2", Bytes("""{"name":"Grace"}"""));
        }

        Assert.True(File.Exists(temp.LogFile), "the log lives at <data directory>/crowdb.log");

        var lines = File.ReadAllLines(temp.LogFile);
        Assert.Equal(2, lines.Length);

        foreach (var line in lines)
        {
            using var envelope = JsonDocument.Parse(line);
            Assert.Equal(1, envelope.RootElement.GetProperty("v").GetInt32());
            Assert.True(envelope.RootElement.TryGetProperty("id", out _));
            Assert.True(envelope.RootElement.TryGetProperty("doc", out _));
        }
    }

    [Fact]
    public void GIVEN_an_overwrite_WHEN_inspecting_the_log_THEN_the_dead_record_stays_and_a_new_one_is_appended()
    {
        using var temp = new TempDirectory();

        using (var store = OpenStore(temp.Root, output))
        {
            store.Put("user:1", Bytes("""{"version":1}"""));
            store.Put("user:1", Bytes("""{"version":2}"""));
        }

        var log = File.ReadAllText(temp.LogFile);
        Assert.Equal(2, File.ReadAllLines(temp.LogFile).Length);
        Assert.Contains("""{"version":1}""", log, StringComparison.Ordinal); // dead, never modified
        Assert.Contains("""{"version":2}""", log, StringComparison.Ordinal); // appended after it

        using var reopened = OpenStore(temp.Root, output);
        Assert.Equal(Bytes("""{"version":2}"""), Read(reopened, "user:1"));
    }

    [Fact]
    public void GIVEN_a_hand_written_log_WHEN_opening_THEN_the_store_replays_it()
    {
        using var temp = new TempDirectory();

        // JSON whitespace and member order are not significant — the reader
        // must be a JSON reader, not a string matcher.
        File.WriteAllText(temp.LogFile, string.Join('\n', new[]
        {
            """{"v":1,"id":"user:1","doc":{"name":"Ada"}}""",
            """{ "v" : 1, "id" : "user:2", "doc" : {"name":"Grace"} }""",
            """{"doc":{"name":"Ada Lovelace"},"id":"user:1","v":1}""",
        }) + "\n");

        using var store = OpenStore(temp.Root, output);

        Assert.Equal(Bytes("""{"name":"Ada Lovelace"}"""), Read(store, "user:1")); // last write wins
        Assert.Equal(Bytes("""{"name":"Grace"}"""), Read(store, "user:2"));
        Assert.Equal(2, store.ListIds().Count);
    }
}
