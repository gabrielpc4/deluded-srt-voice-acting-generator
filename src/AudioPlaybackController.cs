using NAudio.Wave;

/// <summary>
/// The game is the source of truth for ordering. A newly visible dialogue line
/// cancels stale playback rather than letting an old queue talk over it.
/// </summary>
internal sealed class AudioPlaybackController : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? activeCancellation;

    public async Task<bool> PlayLatestAsync(byte[] pcm, float volumeMultiplier = 1f)
    {
        CancellationTokenSource cancellation;
        lock (gate)
        {
            activeCancellation?.Cancel();
            activeCancellation?.Dispose();
            activeCancellation = cancellation = new CancellationTokenSource();
        }

        bool completed = false;
        try
        {
            using WaveOutEvent output = new();
            byte[] playbackPcm = AmplifyPcm(pcm, volumeMultiplier);
            using RawSourceWaveStream stream = new(new MemoryStream(playbackPcm), new WaveFormat(24000, 16, 1));
            using CancellationTokenRegistration stopRegistration = cancellation.Token.Register(output.Stop);
            output.Init(stream);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing && !cancellation.IsCancellationRequested) await Task.Delay(20, cancellation.Token);
            completed = !cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeCancellation, cancellation)) activeCancellation = null;
            }
            cancellation.Dispose();
        }
        return completed;
    }

    public void Stop()
    {
        lock (gate) activeCancellation?.Cancel();
    }
    public void Dispose() => Stop();

    private static byte[] AmplifyPcm(byte[] pcm, float multiplier)
    {
        multiplier = Math.Clamp(multiplier, 0.25f, 2f);
        if (Math.Abs(multiplier - 1f) < 0.001f || pcm.Length < 2) return pcm;

        byte[] output = (byte[])pcm.Clone();
        for (int index = 0; index + 1 < output.Length; index += 2)
        {
            short sample = (short)(output[index] | output[index + 1] << 8);
            int amplified = (int)MathF.Round(sample * multiplier);
            short clipped = (short)Math.Clamp(amplified, short.MinValue, short.MaxValue);
            output[index] = (byte)clipped;
            output[index + 1] = (byte)(clipped >> 8);
        }
        return output;
    }
}
