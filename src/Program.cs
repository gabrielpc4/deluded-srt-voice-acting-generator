using System.Text.Json;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        string settingsPath = FindProjectFile("appsettings.json");
        string speakerSeedsPath = FindProjectFile("speaker-seeds.json");
        string unknownSpeakerProfilesPath = FindProjectFile("unknown-speaker-profiles.json");
        Settings settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? new();
        settings.OpenAi.ApiKey = ApiKeyStore.Load();
        SettingsStore store = new(settingsPath, speakerSeedsPath, unknownSpeakerProfilesPath);
        Application.Run(new MainForm(settings, store, new SpeakerCatalog(settings, store.LoadSeeds(), store.LoadUnknownSpeakerProfiles())));
    }

    private static string FindProjectFile(string fileName)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(Path.Combine(directory.FullName, "DeludedAIVoiceGeneration.csproj")) && File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, fileName);
    }
}

internal static class ApiKeyStore
{
    private const string FileName = "openai-api-key.txt";

    public static string Load()
    {
        string path = Path;
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }

    public static void Save(string key) => File.WriteAllText(Path, key.Trim());

    private static string Path
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "DeludedAIVoiceGeneration.csproj")))
                    return System.IO.Path.Combine(directory.FullName, FileName);
                directory = directory.Parent;
            }
            return System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
        }
    }
}

internal sealed class Settings
{
    public OpenAiSettings OpenAi { get; set; } = new();
    public ReaderSettings Reader { get; set; } = new();
    public CacheSettings Cache { get; set; } = new();
}
internal sealed class ReaderSettings { public string ProcessName { get; set; } = "SRTE-Win64-Shipping.exe"; public int SubtitleStartDelayMilliseconds { get; set; } = 250; }
internal sealed class CacheSettings
{
    /// <summary>A public HTTPS URL for cache-manifest.json.  The manifest is
    /// intentionally separate from the app release so it can be updated at
    /// any time without distributing a new executable.</summary>
    public string ManifestUrl { get; set; } = "https://drive.usercontent.google.com/download?id=1NgK_Me_UeHmsScbZQPjSFn8E-of8BJXY&export=download&confirm=t";
}
internal sealed class OpenAiSettings { public string ApiKey { get; set; } = ""; public string RealtimeModel { get; set; } = "gpt-realtime-1.5"; public string AlisaVoice { get; set; } = "marin"; public string UnknownVoice { get; set; } = "cedar"; public double SpeechSpeed { get; set; } = 1.0; public string Instructions { get; set; } = "Speak naturally and clearly."; public List<string> FemaleVoices { get; set; } = ["marin", "shimmer", "coral", "sage"]; public List<string> MaleVoices { get; set; } = ["cedar", "echo", "ash", "alloy", "verse", "ballad"]; public bool PersistentSessions { get; set; } = true; }
