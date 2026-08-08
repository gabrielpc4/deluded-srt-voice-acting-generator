using System.Text;
using System.Text.Json;
using NAudio.Wave;

internal readonly record struct IndexedAudioLookup(string Key, string Speaker, string Subtitle)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Key);
}
internal readonly record struct IndexedCacheHit(byte[] PcmAudio, string Transcript, bool HasTranscript, bool InspectionAttempted);

/// <summary>The original persistent cache design: normalized speaker/subtitle
/// key, JSON index, and human-readable WAV names.</summary>
internal sealed class IndexedAudioCache
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string indexPath;
    private Dictionary<string, IndexedAudioEntry> entryByKey = new(StringComparer.Ordinal);
    private bool loaded;
    public IndexedAudioCache()
    {
        directory = Path.Combine(FindProjectDirectory(), "cache");
        indexPath = Path.Combine(directory, "audio-cache-index.json");
    }
    public string DirectoryPath => directory;
    public void Reload()
    {
        lock (gate)
        {
            loaded = false;
            entryByKey = new(StringComparer.Ordinal);
        }
    }
    public IndexedAudioLookup CreateLookup(SpeakerProfile profile, string subtitle)
    {
        string speaker = Normalize(profile.DisplayName);
        // A speaker placeholder such as ?, ??, or ??? has no letters after
        // normalization. Its canonical profile carries the chosen gender, so
        // male and female recordings cannot accidentally share a cache entry.
        if (speaker.Length == 0) speaker = Normalize(profile.CanonicalName);
        if (speaker.Length == 0) speaker = "unknown";
        // Generic unnamed men previously shared a voice-agnostic cache with
        // cedar recordings. Their voice now follows the Driver, so retain old
        // files but isolate new recordings by the selected Driver voice.
        if (string.Equals(profile.CanonicalName, "Unknown Male", StringComparison.OrdinalIgnoreCase))
            speaker += "_" + Normalize(profile.Voice);
        string text = Normalize(subtitle);
        return text.Length == 0 ? default : new(speaker + "|" + text, speaker, text);
    }
    public bool TryRead(IndexedAudioLookup lookup, out IndexedCacheHit hit)
    {
        hit = default; if (!lookup.IsValid) return false;
        lock (gate)
        {
            EnsureLoaded();
            if (!entryByKey.TryGetValue(lookup.Key, out IndexedAudioEntry? entry)) return false;
            string path = Path.Combine(directory, entry.AudioFileName);
            if (!File.Exists(path)) { entryByKey.Remove(lookup.Key); SaveIndex(); return false; }
            using WaveFileReader reader = new(path); using MemoryStream output = new(); reader.CopyTo(output); byte[] pcm = output.ToArray();
            hit = new IndexedCacheHit(pcm, entry.Transcript ?? "", entry.HasTranscript, entry.InspectionAttempted);
            return pcm.Length > 0;
        }
    }
    public void Remove(IndexedAudioLookup lookup, bool deleteAudioFile = false)
    {
        if (!lookup.IsValid) return;
        lock (gate)
        {
            EnsureLoaded();
            if (!entryByKey.Remove(lookup.Key, out IndexedAudioEntry? entry)) return;
            if (deleteAudioFile) DeleteAudioFile(entry.AudioFileName);
            SaveIndex();
        }
    }
    public void MarkInspectionAttempted(IndexedAudioLookup lookup, string transcript)
    {
        if (!lookup.IsValid) return;
        lock (gate)
        {
            EnsureLoaded();
            if (!entryByKey.TryGetValue(lookup.Key, out IndexedAudioEntry? entry)) return;
            entry.InspectionAttempted = true;
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                entry.Transcript = transcript;
                entry.HasTranscript = true;
            }
            SaveIndex();
        }
    }
    public void Save(IndexedAudioLookup lookup, byte[] pcm, string transcript, bool inspectionAttempted = true)
    {
        if (!lookup.IsValid || pcm.Length == 0) return;
        lock (gate)
        {
            EnsureLoaded();
            if (entryByKey.TryGetValue(lookup.Key, out IndexedAudioEntry? existing) && File.Exists(Path.Combine(directory, existing.AudioFileName))) return;
            Directory.CreateDirectory(directory);
            string fileName = BuildFileName(lookup, entryByKey.Values.Select(entry => entry.AudioFileName));
            using FileStream stream = File.Create(Path.Combine(directory, fileName));
            using WaveFileWriter writer = new(stream, new WaveFormat(24000, 16, 1));
            writer.Write(pcm, 0, pcm.Length);
            entryByKey[lookup.Key] = new IndexedAudioEntry { Key = lookup.Key, Speaker = lookup.Speaker, Subtitle = lookup.Subtitle, AudioFileName = fileName, Transcript = transcript, HasTranscript = !string.IsNullOrWhiteSpace(transcript), InspectionAttempted = inspectionAttempted };
            SaveIndex();
        }
    }
    public byte[] ApplySageVolumeGainOnce(IndexedAudioLookup lookup, byte[] pcm, out bool reencoded)
    {
        reencoded = false;
        if (!lookup.IsValid || pcm.Length < 2) return pcm;
        lock (gate)
        {
            EnsureLoaded();
            if (!entryByKey.TryGetValue(lookup.Key, out IndexedAudioEntry? entry)) return pcm;
            int appliedGain = entry.SageVolumeGain > 0 ? entry.SageVolumeGain : entry.SageVolumeDoubled ? 2 : 1;
            const int targetGain = 2;
            if (appliedGain == targetGain) return pcm;

            string path = Path.Combine(directory, entry.AudioFileName);
            if (!File.Exists(path)) return pcm;
            byte[] amplified = ScalePcm(pcm, targetGain, appliedGain);
            string temporaryPath = path + ".sage-volume-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = File.Create(temporaryPath))
                using (WaveFileWriter writer = new(stream, new WaveFormat(24000, 16, 1)))
                    writer.Write(amplified, 0, amplified.Length);
                File.Move(temporaryPath, path, overwrite: true);
                entry.SageVolumeDoubled = true;
                entry.SageVolumeGain = targetGain;
                SaveIndex();
                reencoded = true;
                return amplified;
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
    private void DeleteAudioFile(string audioFileName)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(directory, audioFileName));
        if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)) File.Delete(candidate);
    }
    private void EnsureLoaded()
    {
        if (loaded) return; Directory.CreateDirectory(directory);
        if (File.Exists(indexPath))
        {
            try { IndexedAudioIndex? index = JsonSerializer.Deserialize<IndexedAudioIndex>(File.ReadAllText(indexPath)); entryByKey = index?.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.AudioFileName)).ToDictionary(entry => entry.Key, StringComparer.Ordinal) ?? new(StringComparer.Ordinal); }
            catch { entryByKey = new(StringComparer.Ordinal); }
        }
        loaded = true;
    }
    private void SaveIndex()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(indexPath, JsonSerializer.Serialize(new IndexedAudioIndex { Entries = entryByKey.Values.OrderBy(entry => entry.Speaker).ThenBy(entry => entry.Subtitle).ToList() }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static string BuildFileName(IndexedAudioLookup lookup, IEnumerable<string> existing)
    {
        const int maxBase = 230; string name = lookup.Speaker;
        foreach (string word in lookup.Subtitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)) { string candidate = name.Length == 0 ? word : name + "-" + word; if (candidate.Length > maxBase) break; name = candidate; }
        name = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')).Trim('-'); if (name.Length == 0) name = "subtitle";
        string candidateName = name + ".wav"; int suffix = 2;
        HashSet<string> used = new(existing, StringComparer.OrdinalIgnoreCase);
        while (used.Contains(candidateName)) candidateName = name[..Math.Min(name.Length, maxBase - 8)] + "-" + suffix++ + ".wav";
        return candidateName;
    }
    private static string Normalize(string text)
    {
        StringBuilder result = new(); bool space = false;
        foreach (char c in text) { if (char.IsLetterOrDigit(c)) { result.Append(char.ToLowerInvariant(c)); space = false; } else if (char.IsWhiteSpace(c) && result.Length > 0 && !space) { result.Append(' '); space = true; } }
        return result.ToString().Trim();
    }
    private static byte[] ScalePcm(byte[] pcm, int numerator, int denominator)
    {
        byte[] output = (byte[])pcm.Clone();
        for (int index = 0; index + 1 < output.Length; index += 2)
        {
            short sample = (short)(output[index] | output[index + 1] << 8);
            int amplified = (int)Math.Round(sample * (double)numerator / denominator, MidpointRounding.AwayFromZero);
            short clipped = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
            output[index] = (byte)clipped;
            output[index + 1] = (byte)(clipped >> 8);
        }
        return output;
    }
    private static string FindProjectDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null) { if (File.Exists(Path.Combine(dir.FullName, "DeludedAIVoiceGeneration.csproj"))) return dir.FullName; dir = dir.Parent; }
        return AppContext.BaseDirectory;
    }
    private sealed class IndexedAudioIndex { public List<IndexedAudioEntry> Entries { get; set; } = []; }
    private sealed class IndexedAudioEntry { public string Key { get; set; } = ""; public string Speaker { get; set; } = ""; public string Subtitle { get; set; } = ""; public string AudioFileName { get; set; } = ""; public string? Transcript { get; set; } public bool HasTranscript { get; set; } public bool InspectionAttempted { get; set; } public bool SageVolumeDoubled { get; set; } public int SageVolumeGain { get; set; } }
}
