using System.Text;

internal sealed class ActivityLog
{
    private const long MaximumActiveLogBytes = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");

    public string CurrentPath => Path.Combine(logDirectory, "voice-companion-current.log");

    public void Write(string jsonLine)
    {
        string line = jsonLine + Environment.NewLine;
        lock (gate)
        {
            Directory.CreateDirectory(logDirectory);
            RotateIfNeeded(line.Length * sizeof(char));
            File.AppendAllText(CurrentPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(CurrentPath)) return;
        if (new FileInfo(CurrentPath).Length + incomingBytes <= MaximumActiveLogBytes) return;

        string previousPath = Path.Combine(logDirectory, "voice-companion-previous.log");
        if (File.Exists(previousPath)) File.Delete(previousPath);
        File.Move(CurrentPath, previousPath);
    }
}
