using System.Security.Cryptography;
using System.Text.Json;

internal sealed class CacheDownloadService
{
    private const string IndexFileName = "audio-cache-index.json";
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly string cacheDirectory;

    public CacheDownloadService(string cacheDirectory) => this.cacheDirectory = cacheDirectory;

    public async Task<CacheDownloadResult> DownloadAsync(string manifestUrl, IProgress<CacheDownloadProgress>? progress, CancellationToken token)
    {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Enter a public HTTPS link to cache-manifest.json.");

        using HttpResponseMessage response = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using Stream manifestStream = await response.Content.ReadAsStreamAsync(token);
        CacheManifest? manifest = await JsonSerializer.DeserializeAsync<CacheManifest>(manifestStream, cancellationToken: token);
        ValidateManifest(manifest);

        Directory.CreateDirectory(cacheDirectory);
        List<CacheManifestFile> pending = [];
        foreach (CacheManifestFile file in manifest!.Files)
            if (!await MatchesLocalFileAsync(file, token)) pending.Add(file);
        long totalBytes = pending.Sum(file => file.SizeBytes);
        long completedBytes = 0;
        int completedFiles = 0;
        progress?.Report(new CacheDownloadProgress(0, pending.Count, 0, totalBytes, "Checking optional cache..."));

        // Keep the index until every WAV is safely in place: a running reader
        // never observes an index entry pointing to an unfinished download.
        object progressGate = new();
        async Task DownloadWavAsync(CacheManifestFile file)
        {
            long lastReported = 0;
            await DownloadFileAsync(file, value =>
            {
                lock (progressGate)
                {
                    completedBytes += value - lastReported;
                    lastReported = value;
                    progress?.Report(new CacheDownloadProgress(completedFiles, pending.Count, completedBytes, totalBytes, file.FileName));
                }
            }, token);
            lock (progressGate)
            {
                completedFiles++;
                progress?.Report(new CacheDownloadProgress(completedFiles, pending.Count, completedBytes, totalBytes, file.FileName));
            }
        }
        // A small bounded parallelism makes thousands of short Drive downloads
        // practical, without flooding either the connection or Drive.
        using (SemaphoreSlim slots = new(4))
        {
            Task[] downloads = pending.Where(file => !IsIndex(file)).Select(async file =>
            {
                await slots.WaitAsync(token);
                try { await DownloadWavAsync(file); }
                finally { slots.Release(); }
            }).ToArray();
            await Task.WhenAll(downloads);
        }
        foreach (CacheManifestFile file in pending.Where(IsIndex))
        {
            await DownloadWavAsync(file);
        }
        progress?.Report(new CacheDownloadProgress(completedFiles, pending.Count, completedBytes, totalBytes, pending.Count == 0 ? "Optional cache is already up to date." : "Optional cache updated."));
        return new CacheDownloadResult(manifest.Version, pending.Count, totalBytes);
    }

    private async Task DownloadFileAsync(CacheManifestFile file, Action<long> reportBytes, CancellationToken token)
    {
        if (!Uri.TryCreate(file.DownloadUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Invalid download URL for {file.FileName}.");
        string destination = SafeDestination(file.FileName);
        string temporary = destination + ".download-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using Stream input = await response.Content.ReadAsStreamAsync(token);
            await using FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, useAsync: true);
            byte[] buffer = new byte[131072]; long written = 0; int read;
            while ((read = await input.ReadAsync(buffer, token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token);
                written += read; reportBytes(written);
            }
            await output.FlushAsync(token);
            if (written != file.SizeBytes) throw new InvalidOperationException($"Incomplete download for {file.FileName}.");
            if (!string.Equals(await HashFileAsync(temporary, token), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Integrity check failed for {file.FileName}.");
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<bool> MatchesLocalFileAsync(CacheManifestFile file, CancellationToken token)
    {
        string path = SafeDestination(file.FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes) return false;
        return string.Equals(await HashFileAsync(path, token), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private string SafeDestination(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !(fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || fileName == IndexFileName))
            throw new InvalidOperationException("The cache manifest contains an unsafe file name.");
        return Path.Combine(cacheDirectory, fileName);
    }

    private static bool IsIndex(CacheManifestFile file) => string.Equals(file.FileName, IndexFileName, StringComparison.OrdinalIgnoreCase);
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    private static void ValidateManifest(CacheManifest? manifest)
    {
        if (manifest is null || manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Version) || manifest.Files.Count == 0)
            throw new InvalidOperationException("The cache manifest is invalid.");
        if (manifest.Files.Count(file => IsIndex(file)) != 1 || manifest.Files.Any(file => file.SizeBytes < 0 || file.Sha256.Length != 64 || string.IsNullOrWhiteSpace(file.DownloadUrl)))
            throw new InvalidOperationException("The cache manifest is incomplete.");
        if (manifest.Files.Select(file => file.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new InvalidOperationException("The cache manifest has duplicate file names.");
    }
}

internal sealed class CacheManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public List<CacheManifestFile> Files { get; set; } = [];
}
internal sealed class CacheManifestFile
{
    public string FileName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
}
internal readonly record struct CacheDownloadProgress(int CompletedFiles, int TotalFiles, long CompletedBytes, long TotalBytes, string CurrentFile);
internal readonly record struct CacheDownloadResult(string Version, int DownloadedFiles, long DownloadedBytes);
