using System.Text.Json;

/// <summary>Finds one substantial, character-specific line from the optional
/// cache index. The index is local game-adjacent data and is never modified by
/// the preview feature.</summary>
internal sealed class CharacterPreviewLineCatalog
{
    private readonly Dictionary<string, string> lineBySpeaker = new(StringComparer.OrdinalIgnoreCase);

    public CharacterPreviewLineCatalog()
    {
        string? indexPath = FindIndexPath();
        if (indexPath is null) return;
        try
        {
            CacheIndex? index = JsonSerializer.Deserialize<CacheIndex>(File.ReadAllText(indexPath));
            foreach (IGrouping<string, CacheEntry> group in (index?.Entries ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Speaker)).GroupBy(entry => Normalize(entry.Speaker)))
            {
                CacheEntry? choice = group
                    .Where(entry => IsCandidate(entry.BestText))
                    .OrderByDescending(entry => Score(entry.BestText))
                    .FirstOrDefault();
                if (choice is not null) lineBySpeaker[group.Key] = choice.BestText;
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
    }

    public string? Find(string speaker) => lineBySpeaker.TryGetValue(Normalize(speaker), out string? line) ? line : null;

    private static bool IsCandidate(string text)
    {
        int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 5 && text.Length is >= 28 and <= 260 && !text.TrimStart().StartsWith("Possible replies:", StringComparison.OrdinalIgnoreCase);
    }

    private static int Score(string text)
    {
        int words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int lengthScore = 140 - Math.Abs(140 - Math.Min(text.Length, 240));
        int punctuationScore = text.Contains('!') || text.Contains('?') ? 25 : 0;
        int cadenceScore = text.Contains("...", StringComparison.Ordinal) ? 10 : 0;
        return lengthScore + Math.Min(words, 24) * 4 + punctuationScore + cadenceScore;
    }

    private static string? FindIndexPath()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "cache", "audio-cache-index.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    private sealed class CacheIndex { public List<CacheEntry> Entries { get; set; } = []; }
    private sealed class CacheEntry
    {
        public string Speaker { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string? Transcript { get; set; }
        public string BestText => string.IsNullOrWhiteSpace(Transcript) ? Subtitle : Transcript;
    }
}
