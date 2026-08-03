using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FH6Mod.Cheats.Scan;

/// <summary>
/// Persists discovered pointer chains to %APPDATA%\FH6AllInOneTrainer\saved_pointers.json.
/// Uses System.Text.Json DOM (trim-safe for single-file self-contained publish).
/// Each saved chain is a permanent, ASLR-safe address for one in-game value.
/// </summary>
public static class SavedPointerStore
{
    public sealed class Entry
    {
        public string Label = "";
        public long RootOffset;
        public int[] Offsets = Array.Empty<int>();
        public string SavedUtc = "";
    }

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FH6AllInOneTrainer",
        "saved_pointers.json");

    public static List<Entry> Load()
    {
        var list = new List<Entry>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            var json = File.ReadAllText(FilePath);
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var e = new Entry();
                if (el.TryGetProperty("Label", out var l) && l.ValueKind == JsonValueKind.String) e.Label = l.GetString() ?? "";
                if (el.TryGetProperty("RootOffset", out var r) && r.ValueKind == JsonValueKind.Number) e.RootOffset = r.GetInt64();
                if (el.TryGetProperty("SavedUtc", out var s) && s.ValueKind == JsonValueKind.String) e.SavedUtc = s.GetString() ?? "";
                if (el.TryGetProperty("Offsets", out var o) && o.ValueKind == JsonValueKind.Array)
                {
                    var tmp = new List<int>();
                    foreach (var x in o.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.Number) tmp.Add(x.GetInt32());
                    e.Offsets = tmp.ToArray();
                }
                list.Add(e);
            }
        }
        catch { /* malformed -> empty */ }
        return list;
    }

    public static void Save(List<Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartArray();
                foreach (var e in entries)
                {
                    w.WriteStartObject();
                    w.WriteString("Label", e.Label);
                    w.WriteNumber("RootOffset", e.RootOffset);
                    w.WriteStartArray("Offsets");
                    foreach (var o in e.Offsets) w.WriteNumberValue(o);
                    w.WriteEndArray();
                    w.WriteString("SavedUtc", e.SavedUtc);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            File.WriteAllText(FilePath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { /* disk full / locked -> silent */ }
    }

    public static Entry ToEntry(PointerChain c, string label)
        => new() { Label = label, RootOffset = c.RootOffset, Offsets = c.Offsets, SavedUtc = DateTime.UtcNow.ToString("o") };

    public static PointerChain ToChain(Entry e)
        => new() { RootOffset = e.RootOffset, Offsets = e.Offsets, Label = e.Label };
}
