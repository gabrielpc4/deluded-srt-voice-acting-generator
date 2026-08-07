using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>One serialized GA Realtime WebSocket per character during an active
/// dialogue. Normal conversation items retain that character's prior lines.</summary>
internal sealed class PersistentRealtimeVoiceSession : IAsyncDisposable
{
    private readonly string voice;
    private readonly string characterName;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClientWebSocket? socket;
    private string connectedModel = "";
    private string appliedInstructions = "";
    private double appliedSpeed = -1;
    private bool voiceApplied;
    public PersistentRealtimeVoiceSession(string voice, string characterName)
    {
        this.voice = voice;
        this.characterName = characterName;
    }

    public async Task<RealtimeAudioResult> GenerateAsync(OpenAiSettings settings, string instructions, string text, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await GenerateCoreAsync(settings, instructions, text, token); }
        finally { gate.Release(); }
    }

    /// <summary>Records a cache-hit line as an assistant turn without asking the
    /// model to generate it again, so later lines retain the spoken context.</summary>
    public async Task RecordSpokenLineAsync(OpenAiSettings settings, string instructions, string text, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ClientWebSocket active = await EnsureConnectedAsync(settings, instructions, token);
            await SendAsync(active, new
            {
                type = "conversation.item.create",
                item = new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } }
            }, token);
        }
        catch { await ResetAsync(); throw; }
        finally { gate.Release(); }
    }

    private async Task<RealtimeAudioResult> GenerateCoreAsync(OpenAiSettings settings, string instructions, string text, CancellationToken token)
    {
        ClientWebSocket active = await EnsureConnectedAsync(settings, instructions, token);
        try
        {
            await SendAsync(active, new
            {
                type = "conversation.item.create",
                item = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = RealtimePromptBuilder.BuildPromptText(text) } } }
            }, token);
            await SendAsync(active, new { type = "response.create" }, token);
            return await ReceiveAudioAsync(active, token);
        }
        catch { await ResetAsync(); throw; }
    }

    private async Task<ClientWebSocket> EnsureConnectedAsync(OpenAiSettings settings, string instructions, CancellationToken token)
    {
        if (socket is null || socket.State != WebSocketState.Open || connectedModel != settings.RealtimeModel)
        {
            await ResetAsync();
            socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + settings.ApiKey);
            await socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(settings.RealtimeModel)), token);
            await ReceiveUntilAsync(socket, "session.created", token);
            connectedModel = settings.RealtimeModel; appliedInstructions = ""; appliedSpeed = -1;
        }

        if (appliedInstructions != instructions || appliedSpeed != settings.SpeechSpeed)
        {
            object output = voiceApplied
                ? new { format = new { type = "audio/pcm", rate = 24000 }, speed = settings.SpeechSpeed }
                : new { format = new { type = "audio/pcm", rate = 24000 }, voice, speed = settings.SpeechSpeed };
            await SendAsync(socket, new
            {
                type = "session.update",
                session = new
                {
                    type = "realtime", output_modalities = new[] { "audio" },
                    audio = new { output },
                    instructions
                }
            }, token);
            await ReceiveUntilAsync(socket, "session.updated", token);
            appliedInstructions = instructions; appliedSpeed = settings.SpeechSpeed; voiceApplied = true;
        }
        return socket;
    }

    private async Task<RealtimeAudioResult> ReceiveAudioAsync(ClientWebSocket socket, CancellationToken token)
    {
        using MemoryStream output = new();
        while (true)
        {
            using JsonDocument document = JsonDocument.Parse(await ReceiveAsync(socket, token)); JsonElement root = document.RootElement; string type = root.GetProperty("type").GetString() ?? "";
            if (type == "response.output_audio.delta") output.Write(Convert.FromBase64String(root.GetProperty("delta").GetString() ?? ""));
            else if (type == "error") throw new InvalidOperationException(DescribeError(root));
            else if (type == "response.done") return new RealtimeAudioResult(output.ToArray(), RealtimeTranscript.Extract(root));
        }
    }
    private async Task ReceiveUntilAsync(ClientWebSocket socket, string wanted, CancellationToken token)
    {
        while (true) { using JsonDocument document = JsonDocument.Parse(await ReceiveAsync(socket, token)); JsonElement root = document.RootElement; string type = root.GetProperty("type").GetString() ?? ""; if (type == wanted) return; if (type == "error") throw new InvalidOperationException(DescribeError(root)); }
    }
    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken token) { byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)); await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
    private async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken token) { byte[] bytes = new byte[16384]; using MemoryStream stream = new(); WebSocketReceiveResult result; do { result = await socket.ReceiveAsync(bytes, token); if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException($"{characterName} voice-context socket closed."); stream.Write(bytes, 0, result.Count); } while (!result.EndOfMessage); return Encoding.UTF8.GetString(stream.ToArray()); }
    private static string DescribeError(JsonElement root) { if (!root.TryGetProperty("error", out JsonElement error)) return "Realtime returned an error without details."; string message = error.TryGetProperty("message", out JsonElement value) ? value.GetString() ?? "Unknown error" : "Unknown error"; string type = error.TryGetProperty("type", out JsonElement typeValue) ? typeValue.GetString() ?? "unknown_type" : "unknown_type"; string code = error.TryGetProperty("code", out JsonElement codeValue) ? codeValue.ToString() : "none"; return $"[{type}, code={code}]: {message}"; }
    private async Task ResetAsync() { if (socket is not null) { try { if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { } socket.Dispose(); socket = null; } connectedModel = ""; appliedInstructions = ""; appliedSpeed = -1; voiceApplied = false; }
    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { await ResetAsync(); } finally { gate.Release(); gate.Dispose(); } }
}
