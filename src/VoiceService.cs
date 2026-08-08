using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using NAudio.Wave;

internal enum AudioState { Waiting, CacheHit, Generating, Ready, Failed, Unavailable }
internal enum AudioRequestRole { Current, Prefetch }
internal sealed record AudioStatus(AudioState State, string Detail);

internal sealed class VoiceService : IDisposable
{
    private readonly Settings settings;
    private readonly IndexedAudioCache indexedCache = new();
    private readonly Dictionary<string, Task<byte[]?>> inFlight = new();
    // Holds the most recently needed PCM clips so a prefetched line promotes
    // without a second disk read when its subtitle becomes current.
    private readonly Dictionary<string, ReadyAudio> readyPcmByFile = new();
    private readonly Queue<string> readyPcmOrder = new();
    private const int MaximumReadyPcmEntries = 24;
    // Failure is sticky only for this running process. It prevents a timer poll
    // from repeatedly calling the API after an invalid key, model, or outage.
    private readonly Dictionary<string, string> failureByFile = new();
    private readonly HashSet<string> conversationRecordedKeys = new(StringComparer.Ordinal);
    // Character identity, rather than voice name, is the key: two people may
    // share a configured voice but must never share dialogue context.
    private readonly ConcurrentDictionary<string, PersistentRealtimeVoiceSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private string? gateOwner;
    public event EventHandler<string>? LogGenerated;

    public VoiceService(Settings settings) => this.settings = settings;

    public string CacheDirectory => indexedCache.DirectoryPath;

    public void ReloadDownloadedCache()
    {
        indexedCache.Reload();
        LogGenerated?.Invoke(this, "Optional cache updated.");
    }

    public AudioStatus Status(SpeakerProfile speaker, string text)
    {
        if (!IsSpeakable(text)) return new(AudioState.Unavailable, "Skipped: no spoken words.");
        IndexedAudioLookup lookup = indexedCache.CreateLookup(speaker, text);
        if (!lookup.IsValid) return new(AudioState.Unavailable, "Skipped: no cacheable words.");
        using (EnterGate("status", lookup.Key, null, text))
        {
            if (readyPcmByFile.ContainsKey(lookup.Key)) return new(AudioState.Ready, "Ready in memory");
            if (failureByFile.TryGetValue(lookup.Key, out string? error)) return new(AudioState.Failed, $"Failed (retry disabled): {error}");
            if (inFlight.ContainsKey(lookup.Key)) return new(AudioState.Generating, "Generating with OpenAI Realtime...");
        }
        return indexedCache.TryRead(lookup, out _) ? new(AudioState.CacheHit, "Indexed WAV cache; warming memory") : new(AudioState.Waiting, "Cache miss; waiting");
    }

    public bool RetryFailed(SpeakerProfile speaker, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        IndexedAudioLookup lookup = indexedCache.CreateLookup(speaker, text);
        using (EnterGate("retry_failed", lookup.Key, null, text)) return lookup.IsValid && failureByFile.Remove(lookup.Key);
    }

    /// <summary>Produces one temporary voice sample for the Cast editor. It is
    /// deliberately independent of the cache and character conversation
    /// sessions, so auditioning a voice never changes game playback state.</summary>
    public async Task<byte[]> PreviewAsync(SpeakerProfile speaker, string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAi.ApiKey))
            throw new InvalidOperationException("OpenAI API key is not configured. Enter it in the application window.");
        RealtimeAudioResult result = await RealtimeAsync(speaker, text, token);
        if (result.PcmAudio.Length == 0) throw new InvalidOperationException("Realtime returned no audio for the preview.");
        return result.PcmAudio;
    }

    /// <summary>For a deliberate manual re-take: forget the clip in memory and
    /// on disk, then close every character session so the next request is a
    /// genuinely fresh realtime performance.</summary>
    public async Task<bool> ResetAudioForRegenerationAsync(SpeakerProfile speaker, string text)
    {
        IndexedAudioLookup lookup = indexedCache.CreateLookup(speaker, text);
        if (!lookup.IsValid) return false;
        using (EnterGate("manual_regenerate_reset", lookup.Key, null, text))
        {
            readyPcmByFile.Remove(lookup.Key);
            failureByFile.Remove(lookup.Key);
            conversationRecordedKeys.Remove(lookup.Key);
        }
        indexedCache.Remove(lookup, deleteAudioFile: true);
        await EndConversationAsync();
        return true;
    }

    public Task<byte[]?> EnsureAsync(SpeakerProfile speaker, string text, AudioRequestRole role, CancellationToken token)
    {
        if (!IsSpeakable(text))
        {
            Log(text.TrimStart().StartsWith("Possible replies:", StringComparison.OrdinalIgnoreCase) ? "audio.skip.menu" : "audio.skip.unspeakable", ("role", role.ToString().ToLowerInvariant()), ("speaker", speaker.CanonicalName), ("text", text));
            return Task.FromResult<byte[]?>(null);
        }
        IndexedAudioLookup lookup = indexedCache.CreateLookup(speaker, text);
        if (!lookup.IsValid) return Task.FromResult<byte[]?>(null);
        using (EnterGate("ensure_before_cache", lookup.Key, role, text))
        {
            if (readyPcmByFile.TryGetValue(lookup.Key, out ReadyAudio? ready))
            {
                Log("audio.cache.hit", ("source", "memory"), ("key", lookup.Key), ("role", role.ToString().ToLowerInvariant()), ("speaker", speaker.CanonicalName), ("text", text));
                return PrepareCachedAudioUnsafe(lookup, speaker, text, ready.PcmAudio, token);
            }
            if (failureByFile.TryGetValue(lookup.Key, out string? failure))
                return Task.FromException<byte[]?>(new InvalidOperationException($"Retry disabled for this app session: {failure}"));
            if (inFlight.TryGetValue(lookup.Key, out Task<byte[]?>? existing)) return existing;
        }

        // Disk I/O is deliberately outside the service state lock. A prefetch
        // must never keep the current subtitle from reaching playback simply
        // because a cache file is slow, locked, or malformed.
        bool hasIndexedCache = indexedCache.TryRead(lookup, out IndexedCacheHit cached);

        using (EnterGate("ensure_after_cache", lookup.Key, role, text))
        {
            // A competing current/prefetch request may have completed while
            // this request was reading from disk. Reuse that result instead of
            // opening a duplicate realtime request.
            if (readyPcmByFile.TryGetValue(lookup.Key, out ReadyAudio? ready))
            {
                Log("audio.cache.hit", ("source", "memory"), ("key", lookup.Key), ("role", role.ToString().ToLowerInvariant()), ("speaker", speaker.CanonicalName), ("text", text));
                return PrepareCachedAudioUnsafe(lookup, speaker, text, ready.PcmAudio, token);
            }
            if (failureByFile.TryGetValue(lookup.Key, out string? failure))
                return Task.FromException<byte[]?>(new InvalidOperationException($"Retry disabled for this app session: {failure}"));
            if (inFlight.TryGetValue(lookup.Key, out Task<byte[]?>? existing)) return existing;

            if (hasIndexedCache)
            {
                byte[] cachedPcm = ApplySageVolumeGainOnce(lookup, speaker, cached.PcmAudio);
                RememberReadyPcm(lookup.Key, cachedPcm);
                Log("audio.cache.hit", ("source", "indexed"), ("key", lookup.Key), ("role", role.ToString().ToLowerInvariant()), ("speaker", speaker.CanonicalName), ("text", text));
                return PrepareCachedAudioUnsafe(lookup, speaker, text, cachedPcm, token);
            }
            if (string.IsNullOrWhiteSpace(settings.OpenAi.ApiKey))
            {
                const string noKey = "OpenAI API key is not configured. Enter it in the application window.";
                failureByFile[lookup.Key] = noKey;
                LogGenerated?.Invoke(this, noKey);
                return Task.FromException<byte[]?>(new InvalidOperationException(noKey));
            }

            Log("audio.generate.start", ("key", lookup.Key), ("role", role.ToString().ToLowerInvariant()), ("speaker", speaker.CanonicalName), ("voice", speaker.Voice), ("text", text));
            return StartTaskUnsafe(lookup.Key, text, role, GenerateAndSaveAsync(lookup, speaker, text, token));
        }
    }

    private Task<byte[]?> StartTaskUnsafe(string key, string text, AudioRequestRole role, Task<byte[]?> task)
    {
        inFlight[key] = task;
        _ = task.ContinueWith(completed => CompleteRequest(key, text, role, completed), TaskScheduler.Default);
        return task;
    }

    private Task<byte[]?> PrepareCachedAudioUnsafe(IndexedAudioLookup lookup, SpeakerProfile speaker, string text, byte[] pcm, CancellationToken token)
    {
        // Playback from an on-disk/memory cache is entirely local.  Restoring
        // the spoken line into the Realtime session is optional context work;
        // it must never make cached audio wait for a reachable network.
        if (settings.OpenAi.PersistentSessions && !string.IsNullOrWhiteSpace(settings.OpenAi.ApiKey))
            _ = RecordCachedAudioContextAsync(lookup.Key, speaker, text, pcm, token);

        return Task.FromResult<byte[]?>(pcm);
    }

    private async Task<byte[]?> RecordCachedAudioContextAsync(string key, SpeakerProfile speaker, string text, byte[] pcm, CancellationToken token)
    {
        using (EnterGate("record_cached_context", key, null, text)) if (!conversationRecordedKeys.Add(key)) return pcm;
        try
        {
            await GetSession(speaker).RecordSpokenLineAsync(settings.OpenAi, RealtimePromptBuilder.BuildSpeechInstructions(speaker.Instructions), text, token);
        }
        catch (Exception exception)
        {
            // Cached playback is still useful. Do not turn a context-only issue
            // into a cache miss or an API retry loop.
            LogGenerated?.Invoke(this, "Could not restore voice context: " + exception.Message);
        }
        return pcm;
    }

    private void CompleteRequest(string file, string text, AudioRequestRole role, Task<byte[]?> completed)
    {
        if (completed.IsFaulted)
        {
            string error = completed.Exception?.GetBaseException().Message ?? "Unknown generation failure.";
            using (EnterGate("complete_failed_request", file, role, text))
            {
                // A prefetch has never been needed for playback yet. Do not
                // let its transient socket/API failure prevent one normal
                // attempt when the subtitle actually becomes current.
                if (role == AudioRequestRole.Current) failureByFile[file] = error;
                else failureByFile.Remove(file);
                inFlight.Remove(file);
            }
            Log("audio.request.failed", ("key", file), ("role", role.ToString().ToLowerInvariant()), ("text", text), ("error", error), ("retryWhenCurrent", role == AudioRequestRole.Prefetch));
            return;
        }

        using (EnterGate("complete_request", file, role, text))
        {
            inFlight.Remove(file);
            if (completed.Result is { Length: > 0 } pcm && !readyPcmByFile.ContainsKey(file))
                RememberReadyPcm(file, pcm);
        }
        Log("audio.ready", ("key", file), ("role", role.ToString().ToLowerInvariant()), ("text", text), ("pcmBytes", completed.Result?.Length ?? 0));
    }

    private async Task<byte[]?> GenerateAndSaveAsync(IndexedAudioLookup lookup, SpeakerProfile speaker, string text, CancellationToken token)
    {
        RealtimeAudioResult result = await GeneratePcmAsync(speaker, text, token);
        if (result.PcmAudio.Length == 0) return null;
        indexedCache.Save(lookup, result.PcmAudio, result.Transcript);
        if (settings.OpenAi.PersistentSessions) using (EnterGate("record_generated_context", lookup.Key, null, text)) conversationRecordedKeys.Add(lookup.Key);
        return ApplySageVolumeGainOnce(lookup, speaker, result.PcmAudio);
    }

    private byte[] ApplySageVolumeGainOnce(IndexedAudioLookup lookup, SpeakerProfile speaker, byte[] pcm)
    {
        if (!string.Equals(speaker.Voice, "sage", StringComparison.OrdinalIgnoreCase)) return pcm;
        byte[] prepared = indexedCache.ApplySageVolumeGainOnce(lookup, pcm, out bool reencoded);
        if (reencoded) Log("audio.sage.reencoded", ("key", lookup.Key), ("speaker", speaker.CanonicalName), ("text", lookup.Subtitle), ("volumeMultiplier", 2));
        return prepared;
    }

    private async Task<RealtimeAudioResult> RealtimeAsync(SpeakerProfile speaker, string text, CancellationToken token)
    {
        string textToSpeak = RealtimeTextSanitizer.ReplaceBlockedWords(text, out bool replaced);
        if (replaced) LogGenerated?.Invoke(this, "Text replacement applied.");
        using ClientWebSocket ws = new();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + settings.OpenAi.ApiKey);
        await ws.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(settings.OpenAi.RealtimeModel)), token);
        await ReceiveUntilAsync(ws, "session.created", token);
        string voice = speaker.Voice;
        // GA /v1/realtime uses nested audio configuration. The former
        // OpenAI-Beta header, modalities, *_audio_format, and root voice fields
        // are beta-only and are rejected with beta_api_shape_disabled.
        await SendAsync(ws, new
        {
            type = "session.update",
            session = new
            {
                type = "realtime",
                output_modalities = new[] { "audio" },
                audio = new
                {
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        voice,
                        speed = settings.OpenAi.SpeechSpeed
                    }
                },
                instructions = RealtimePromptBuilder.BuildSpeechInstructions(speaker.Instructions)
            }
        }, token);
        await SendAsync(ws, new { type = "conversation.item.create", item = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = RealtimePromptBuilder.BuildPromptText(textToSpeak) } } } }, token);
        await SendAsync(ws, new { type = "response.create" }, token);
        using MemoryStream output = new();
        while (true)
        {
            using JsonDocument document = JsonDocument.Parse(await ReceiveAsync(ws, token));
            JsonElement root = document.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            if (type is "response.output_audio.delta" or "response.audio.delta") output.Write(Convert.FromBase64String(root.GetProperty("delta").GetString() ?? ""));
            else if (type == "error") throw new InvalidOperationException(DescribeOpenAiError(root));
            else if (type == "response.done") return new RealtimeAudioResult(output.ToArray(), RealtimeTranscript.Extract(root));
        }
    }

    private Task<RealtimeAudioResult> GeneratePcmAsync(SpeakerProfile speaker, string text, CancellationToken token)
    {
        string textToSpeak = RealtimeTextSanitizer.ReplaceBlockedWords(text, out bool replaced);
        if (replaced) LogGenerated?.Invoke(this, "Text replacement applied.");
        if (!settings.OpenAi.PersistentSessions) return GenerateWithoutPersistentSessionAsync(speaker, textToSpeak, token);
        return GenerateWithPersistentSessionAsync(speaker, textToSpeak, token);
    }

    private async Task<RealtimeAudioResult> GenerateWithoutPersistentSessionAsync(SpeakerProfile speaker, string text, CancellationToken token)
    {
        return await RealtimeAsync(speaker, text, token);
    }

    private async Task<RealtimeAudioResult> GenerateWithPersistentSessionAsync(SpeakerProfile speaker, string text, CancellationToken token)
    {
        PersistentRealtimeVoiceSession session = GetSession(speaker);
        return await session.GenerateAsync(settings.OpenAi, RealtimePromptBuilder.BuildSpeechInstructions(speaker.Instructions), text, token);
    }

    private PersistentRealtimeVoiceSession GetSession(SpeakerProfile speaker)
    {
        string sessionKey = speaker.CanonicalName + "|" + speaker.Voice;
        if (sessions.TryGetValue(sessionKey, out PersistentRealtimeVoiceSession? existing)) return existing;
        var created = new PersistentRealtimeVoiceSession(speaker.Voice, speaker.CanonicalName);
        if (sessions.TryAdd(sessionKey, created))
        {
            Log("context.open", ("speaker", speaker.CanonicalName), ("voice", speaker.Voice), ("sessionKey", sessionKey));
            return created;
        }
        return sessions[sessionKey];
    }

    public async Task<bool> EndConversationAsync()
    {
        KeyValuePair<string, PersistentRealtimeVoiceSession>[] active = sessions.ToArray();
        if (active.Length == 0) return false;
        foreach (KeyValuePair<string, PersistentRealtimeVoiceSession> pair in active) sessions.TryRemove(pair.Key, out _);
        using (EnterGate("end_conversation", null, null, null)) conversationRecordedKeys.Clear();
        await Task.WhenAll(active.Select(async pair => await pair.Value.DisposeAsync()));
        return true;
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken token) { byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)); await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
    private static async Task ReceiveUntilAsync(ClientWebSocket ws, string wanted, CancellationToken token)
    {
        while (true)
        {
            using JsonDocument document = JsonDocument.Parse(await ReceiveAsync(ws, token));
            JsonElement root = document.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";
            if (type == wanted) return;
            if (type == "error") throw new InvalidOperationException(DescribeOpenAiError(root));
        }
    }
    private static async Task<string> ReceiveAsync(ClientWebSocket ws, CancellationToken token) { byte[] bytes = new byte[16384]; using MemoryStream stream = new(); WebSocketReceiveResult result; do { result = await ws.ReceiveAsync(bytes, token); if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Realtime socket closed."); stream.Write(bytes, 0, result.Count); } while (!result.EndOfMessage); return Encoding.UTF8.GetString(stream.ToArray()); }
    private static string DescribeOpenAiError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out JsonElement error)) return "OpenAI Realtime returned an error without details.";
        string message = error.TryGetProperty("message", out JsonElement messageValue) ? messageValue.GetString() ?? "Unknown error" : "Unknown error";
        string type = error.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() ?? "unknown_type" : "unknown_type";
        string code = error.TryGetProperty("code", out JsonElement codeValue) ? codeValue.ToString() : "none";
        return $"[{type}, code={code}]: {message}";
    }
    private static string Key(string speaker, string text) { string normalized = string.Concat(speaker, "|", text).Normalize(NormalizationForm.FormKC).ToLowerInvariant(); return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant(); }
    private void RememberReadyPcm(string file, byte[] pcm)
    {
        if (readyPcmByFile.ContainsKey(file)) { readyPcmByFile[file] = new ReadyAudio(pcm); return; }
        readyPcmByFile[file] = new ReadyAudio(pcm);
        readyPcmOrder.Enqueue(file);
        while (readyPcmOrder.Count > MaximumReadyPcmEntries)
        {
            string oldest = readyPcmOrder.Dequeue();
            readyPcmByFile.Remove(oldest);
        }
    }
    private sealed record ReadyAudio(byte[] PcmAudio);
    private static bool IsSpeakable(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        !text.TrimStart().StartsWith("Possible replies:", StringComparison.OrdinalIgnoreCase) &&
        text.Any(char.IsLetterOrDigit);
    private static string SubtitlePreview(string text)
    {
        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 100 ? '"' + normalized + '"' : '"' + normalized[..97] + "...\"";
    }
    private void Log(string eventName, params (string Key, object? Value)[] fields) => LogGenerated?.Invoke(this, DiagnosticEvent.Create(eventName, fields.Where(field => field.Value is not null).ToArray()));
    private GateLease EnterGate(string operation, string? key, AudioRequestRole? role, string? text)
    {
        if (!Monitor.TryEnter(gate))
        {
            Log("audio.lock.contended", ("requestedOperation", operation), ("ownerOperation", Volatile.Read(ref gateOwner) ?? "unknown"), ("key", key), ("role", role?.ToString().ToLowerInvariant()), ("text", text));
            Monitor.Enter(gate);
        }
        string? previousOwner = gateOwner;
        gateOwner = operation;
        return new GateLease(gate, previousOwner, value => gateOwner = value);
    }
    private sealed class GateLease : IDisposable
    {
        private readonly object sync; private readonly string? previousOwner; private readonly Action<string?> restoreOwner; private bool disposed;
        public GateLease(object sync, string? previousOwner, Action<string?> restoreOwner) { this.sync = sync; this.previousOwner = previousOwner; this.restoreOwner = restoreOwner; }
        public void Dispose() { if (disposed) return; disposed = true; restoreOwner(previousOwner); Monitor.Exit(sync); }
    }
    public void Dispose()
    {
        foreach (PersistentRealtimeVoiceSession session in sessions.Values) _ = session.DisposeAsync();
        sessions.Clear();
    }
}
