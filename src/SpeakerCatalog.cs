using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

internal enum SpeakerGender { Male, Female }

internal sealed class SpeakerSeedCollection { public List<SpeakerSeed> Speakers { get; set; } = []; }
internal sealed class SpeakerSeed
{
    public string CanonicalName { get; set; } = "";
    public SpeakerGender Gender { get; set; }
    public string? PreferredVoice { get; set; }
    // Applied locally at playback time. It never changes the cached PCM data.
    public float? VolumeMultiplier { get; set; }
    public string? SpeechInstructions { get; set; }
    public List<string> Aliases { get; set; } = [];
}
internal sealed class UnknownSpeakerProfileCollection { public List<UnknownSpeakerSeed> Profiles { get; set; } = []; }
internal sealed class UnknownSpeakerSeed
{
    // This is deliberately separate from SpeakerSeed. A text fingerprint may
    // identify an unnamed person, but it must never make a near-matching name
    // resolve to a named character's profile.
    public string Id { get; set; } = "";
    public UnknownSpeakerMatch Match { get; set; } = new();
    public SpeakerGender Gender { get; set; }
    public string? Voice { get; set; }
    public string? Instructions { get; set; }
    public string? Evidence { get; set; }
}
internal sealed class UnknownSpeakerMatch
{
    public List<string> ExactTexts { get; set; } = [];
    // The live reader does not expose the UE asset path. These are persisted
    // provenance for curating the fingerprint and for a future asset-aware reader.
    public List<string> AssetHints { get; set; } = [];
}
internal sealed record SpeakerProfile(string DisplayName, string CanonicalName, string Voice, string Instructions, SpeakerGender Gender, bool IsKnown, float VolumeMultiplier);

internal sealed class SpeakerCatalog
{
    private const string MomCanonicalName = "Mom";
    private const string DadCanonicalName = "Dad";
    private const string NoneCanonicalName = "None";
    private const string MrAlanCanonicalName = "Mr. Alan";
    private const string MomExclusiveVoice = "shimmer";
    private const string DadExclusiveVoice = "ash";
    private const string AnimatedFemaleDelivery = "Use a noticeably higher-pitched, youthful Beverly Hills girl style. Keep reactions and phrasing animated with exaggerated expression and varied inflection. Never read in a monotone, calm, flat, or bored voice; even restrained lines should retain an alert, lively cadence. Keep every word intelligible and follow the line's specific emotion.";
    private static readonly IReadOnlyDictionary<string, float> VoiceVolumeMultipliers = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
    {
        ["alloy"] = 1f, ["ash"] = 1f, ["ballad"] = 2f, ["coral"] = 2f, ["echo"] = 1f,
        ["sage"] = 20f, ["shimmer"] = 1f, ["verse"] = 1f, ["marin"] = 1f, ["cedar"] = 1f
    };
    private readonly Settings settings;
    private readonly SpeakerSeedCollection seeds;
    private readonly Dictionary<string, SpeakerSeed> lookup = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UnknownSpeakerSeed> unknownProfileByExactText = new(StringComparer.Ordinal);
    private readonly HashSet<string> ambiguousUnknownTextKeys = new(StringComparer.Ordinal);

    public SpeakerCatalog(Settings settings, SpeakerSeedCollection seeds, UnknownSpeakerProfileCollection? unknownProfiles = null)
    {
        this.settings = settings;
        this.seeds = seeds;
        RebuildLookup();
        RebuildUnknownProfileLookup(unknownProfiles ?? new());
    }
    public IReadOnlyList<SpeakerSeed> Seeds => seeds.Speakers;

    public SpeakerProfile Resolve(string rawName, string? subtitle = null, SpeakerGender? unknownGenderOverride = null)
    {
        string normalized = NormalizeLookup(rawName);
        // A performance profile is character-specific. Never attach one on a
        // similarity guess: an unrecognised on-screen name is safer as unknown.
        SpeakerSeed? seed = lookup.TryGetValue(normalized, out SpeakerSeed? exact) ? exact : null;
        if (seed is null)
        {
            if (TryResolveUnknownProfile(rawName, subtitle, out SpeakerProfile anonymousProfile)) return anonymousProfile;
            // UE uses ?, ??, ???, and None as unnamed-speaker placeholders.
            // Treat every variation as one safe cache and voice-context identity.
            string safeName = IsQuestionOnlyPlaceholder(rawName) ? "Unknown" : rawName.Trim();
            SpeakerGender gender = unknownGenderOverride ?? (LooksFeminine(safeName) ? SpeakerGender.Female : SpeakerGender.Male);
            string fallback = gender == SpeakerGender.Female
                ? SelectUnknownFeminineVoice(safeName)
                : SelectUnknownMaleVoice();
            string canonicalName = IsQuestionOnlyPlaceholder(rawName) ? "Unknown " + gender : safeName;
            string unknownInstructions = gender == SpeakerGender.Male ? SelectUnknownMaleInstructions() : settings.OpenAi.Instructions;
            return new(safeName, canonicalName, fallback, ApplyPerformanceDirectives(unknownInstructions, gender, canonicalName), gender, false, ResolveVolume(null, fallback));
        }
        string preferredVoice = string.Equals(seed.CanonicalName, "Alisa", StringComparison.OrdinalIgnoreCase)
            ? settings.OpenAi.AlisaVoice
            : seed.PreferredVoice ?? string.Empty;
        string voice = SelectVoice(seed.CanonicalName, seed.Gender, preferredVoice);
        string instructions = string.IsNullOrWhiteSpace(seed.SpeechInstructions) ? settings.OpenAi.Instructions : seed.SpeechInstructions;
        return new(rawName, seed.CanonicalName, voice, ApplyPerformanceDirectives(instructions, seed.Gender, seed.CanonicalName), seed.Gender, true, ResolveVolume(seed.VolumeMultiplier, voice));
    }

    public bool HasUnknownProfileMatch(string rawName, string? subtitle) =>
        TryResolveUnknownProfile(rawName, subtitle, out _);

    public bool TryResolveUnknownProfile(string rawName, string? subtitle, out SpeakerProfile profile)
    {
        profile = default!;
        // Empty labels still use the legacy manual-gender fallback, but only
        // the actual ?, ??, and ??? labels may claim a text fingerprint.
        if (!IsQuestionMarkPlaceholder(rawName)) return false;
        string key = NormalizeSubtitle(subtitle);
        if (key.Length == 0 || ambiguousUnknownTextKeys.Contains(key) || !unknownProfileByExactText.TryGetValue(key, out UnknownSpeakerSeed? seed)) return false;

        string voice = SelectVoice(seed.Id, seed.Gender, seed.Voice);
        string instructions = string.IsNullOrWhiteSpace(seed.Instructions) ? settings.OpenAi.Instructions : seed.Instructions;
        string canonicalName = "Anonymous " + seed.Id;
        profile = new(rawName.Trim(), canonicalName, voice, ApplyPerformanceDirectives(instructions, seed.Gender, canonicalName), seed.Gender, false, ResolveVolume(null, voice));
        return true;
    }

    public void RebuildLookup()
    {
        lookup.Clear();
        foreach (SpeakerSeed seed in seeds.Speakers)
        {
            Add(seed.CanonicalName, seed);
            foreach (string alias in seed.Aliases) Add(alias, seed);
        }
    }

    private void Add(string name, SpeakerSeed seed) { string key = NormalizeLookup(name); if (key.Length > 0) lookup[key] = seed; }
    private void RebuildUnknownProfileLookup(UnknownSpeakerProfileCollection profiles)
    {
        unknownProfileByExactText.Clear();
        ambiguousUnknownTextKeys.Clear();
        foreach (UnknownSpeakerSeed profile in profiles.Profiles ?? [])
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) continue;
            foreach (string exactText in profile.Match?.ExactTexts ?? [])
            {
                string key = NormalizeSubtitle(exactText);
                if (key.Length == 0 || ambiguousUnknownTextKeys.Contains(key)) continue;
                if (unknownProfileByExactText.TryGetValue(key, out UnknownSpeakerSeed? existing) && !ReferenceEquals(existing, profile))
                {
                    unknownProfileByExactText.Remove(key);
                    ambiguousUnknownTextKeys.Add(key);
                    continue;
                }
                unknownProfileByExactText[key] = profile;
            }
        }
    }
    private string SelectVoice(string name, SpeakerGender gender, string? preferred)
    {
        if (IsMom(name)) return MomExclusiveVoice;
        if (IsDad(name)) return DadExclusiveVoice;
        if (IsNone(name)) return DadExclusiveVoice;
        if (IsMrAlan(name)) return settings.OpenAi.UnknownVoice;
        if (!string.IsNullOrWhiteSpace(preferred) && IsAllowedVoice(name, gender, preferred)) return preferred;
        IReadOnlyList<string> voices = AvailableVoices(name, gender);
        if (voices.Count == 0) return gender == SpeakerGender.Female ? "marin" : "cedar";
        return SelectVoiceFrom(name, voices);
    }
    private string SelectUnknownFeminineVoice(string name)
    {
        return "coral";
    }
    private string SelectUnknownMaleVoice()
    {
        // Keep generic unnamed men consistent with the cemetery Driver. Read
        // the seed rather than hard-coding a voice, so Cast settings remain
        // the single source of truth.
        if (lookup.TryGetValue(NormalizeLookup("Driver"), out SpeakerSeed? driver))
            return SelectVoice(driver.CanonicalName, SpeakerGender.Male, driver.PreferredVoice);
        return SelectVoice("Driver", SpeakerGender.Male, "ballad");
    }
    private string SelectUnknownMaleInstructions()
    {
        if (lookup.TryGetValue(NormalizeLookup("Driver"), out SpeakerSeed? driver) && !string.IsNullOrWhiteSpace(driver.SpeechInstructions))
            return driver.SpeechInstructions;
        return settings.OpenAi.Instructions;
    }
    private IReadOnlyList<string> AvailableVoices(string name, SpeakerGender gender) =>
        (gender == SpeakerGender.Female ? settings.OpenAi.FemaleVoices : settings.OpenAi.MaleVoices)
        .Where(voice => IsAllowedVoice(name, gender, voice))
        .ToList();
    private static bool IsAllowedVoice(string name, SpeakerGender gender, string voice) =>
        !(gender == SpeakerGender.Female && string.Equals(voice, MomExclusiveVoice, StringComparison.OrdinalIgnoreCase) && !IsMom(name)) &&
        !(gender == SpeakerGender.Male && string.Equals(voice, DadExclusiveVoice, StringComparison.OrdinalIgnoreCase) && !IsAshSpeaker(name));
    private static bool IsMom(string name) => string.Equals(name, MomCanonicalName, StringComparison.OrdinalIgnoreCase);
    private static bool IsDad(string name) => string.Equals(name, DadCanonicalName, StringComparison.OrdinalIgnoreCase);
    private static bool IsNone(string name) => string.Equals(name, NoneCanonicalName, StringComparison.OrdinalIgnoreCase);
    private static bool IsMrAlan(string name) => string.Equals(name, MrAlanCanonicalName, StringComparison.OrdinalIgnoreCase);
    private static bool IsAshSpeaker(string name) => IsDad(name) || IsNone(name);
    private static string ApplyPerformanceDirectives(string instructions, SpeakerGender gender, string canonicalName) =>
        gender == SpeakerGender.Female && !IsMom(canonicalName) ? $"{instructions.Trim()} {AnimatedFemaleDelivery}" : instructions;
    private static string SelectVoiceFrom(string name, IReadOnlyList<string> voices)
    {
        uint hash = 2166136261; foreach (char c in name) { hash ^= c; hash *= 16777619; }
        return voices[(int)(hash % voices.Count)];
    }
    internal static string NormalizeLookup(string raw)
    {
        StringBuilder builder = new();
        foreach (char c in raw.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) builder.Append(c);
        return builder.ToString();
    }
    internal static string NormalizeSubtitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string normalized = text.Normalize(NormalizationForm.FormKC).Trim();
        StringBuilder builder = new();
        bool previousWasWhitespace = false;
        foreach (char c in normalized)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0) previousWasWhitespace = true;
                continue;
            }
            if (previousWasWhitespace) builder.Append(' ');
            builder.Append(char.ToLowerInvariant(c));
            previousWasWhitespace = false;
        }
        return builder.ToString();
    }
    public static bool IsQuestionOnlyPlaceholder(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value.Trim(), "None", StringComparison.OrdinalIgnoreCase) ||
        value.All(c => c == '?' || char.IsWhiteSpace(c));
    private static bool IsQuestionMarkPlaceholder(string value) =>
        value.Any(c => c == '?') && value.All(c => c == '?' || char.IsWhiteSpace(c));
    private static bool LooksFeminine(string value)
    {
        string letters = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return letters.EndsWith('a') || letters.EndsWith('e');
    }
    private static float ResolveVolume(float? multiplier, string voice)
    {
        if (VoiceVolumeMultipliers.TryGetValue(voice, out float configured)) return configured;
        return Math.Clamp(multiplier ?? 1f, 0.25f, 2f);
    }
}

internal sealed class SettingsStore
{
    private readonly JsonSerializerOptions options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    public string SettingsPath { get; }
    public string SpeakerSeedsPath { get; }
    public string UnknownSpeakerProfilesPath { get; }
    public SettingsStore(string settingsPath, string speakerSeedsPath, string unknownSpeakerProfilesPath) { SettingsPath = settingsPath; SpeakerSeedsPath = speakerSeedsPath; UnknownSpeakerProfilesPath = unknownSpeakerProfilesPath; }
    public SpeakerSeedCollection LoadSeeds() => File.Exists(SpeakerSeedsPath) ? JsonSerializer.Deserialize<SpeakerSeedCollection>(File.ReadAllText(SpeakerSeedsPath), options) ?? new() : new();
    public UnknownSpeakerProfileCollection LoadUnknownSpeakerProfiles() => File.Exists(UnknownSpeakerProfilesPath) ? JsonSerializer.Deserialize<UnknownSpeakerProfileCollection>(File.ReadAllText(UnknownSpeakerProfilesPath), options) ?? new() : new();
    public void Save(Settings settings) => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, options));
    public void SaveSubtitleStartDelay(int delayMilliseconds)
    {
        delayMilliseconds = Math.Max(0, delayMilliseconds);
        JsonObject root = File.Exists(SettingsPath)
            ? JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        JsonObject reader = root["Reader"] as JsonObject ?? new JsonObject();
        root["Reader"] = reader;
        reader["SubtitleStartDelayMilliseconds"] = delayMilliseconds;
        File.WriteAllText(SettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    public void SaveSeeds(SpeakerSeedCollection seeds) => File.WriteAllText(SpeakerSeedsPath, JsonSerializer.Serialize(seeds, options));
}
