using System.Text.Json;

internal readonly record struct RealtimeAudioResult(byte[] PcmAudio, string Transcript);

internal static class RealtimeTranscript
{
    public static string Extract(JsonElement responseDoneEvent)
    {
        if (!responseDoneEvent.TryGetProperty("response", out JsonElement response) ||
            !response.TryGetProperty("output", out JsonElement output) ||
            output.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement part in content.EnumerateArray())
                if (part.TryGetProperty("transcript", out JsonElement transcript)) return transcript.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

}
