internal sealed class CacheDownloadDialog : Form
{
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly VoiceService voice;
    private readonly TextBox manifestUrl = new() { Dock = DockStyle.Fill, PlaceholderText = "https://.../cache-manifest.json" };
    private readonly Button download = new() { Text = "Download cache", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill };
    private CancellationTokenSource? cancellation;

    public CacheDownloadDialog(Settings settings, SettingsStore store, VoiceService voice)
    {
        this.settings = settings; this.store = store; this.voice = voice;
        Text = "Optional voice cache"; ClientSize = new Size(660, 180); MinimumSize = new Size(540, 180); StartPosition = FormStartPosition.CenterParent;
        manifestUrl.Text = settings.Cache.ManifestUrl;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Cache manifest", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); layout.Controls.Add(manifestUrl, 1, 0);
        layout.Controls.Add(new Label { Text = "Status", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); layout.Controls.Add(status, 1, 1);
        layout.SetColumnSpan(progress, 2); layout.Controls.Add(progress, 0, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill }; buttons.Controls.Add(cancel); buttons.Controls.Add(download); layout.SetColumnSpan(buttons, 2); layout.Controls.Add(buttons, 0, 3);
        Label note = new() { Text = "Downloads only missing or changed files. Local cache files are never deleted.", AutoSize = true, ForeColor = SystemColors.GrayText };
        layout.SetColumnSpan(note, 2); layout.Controls.Add(note, 0, 4);
        Controls.Add(layout);
        download.Click += async (_, _) => await DownloadAsync();
        cancel.Click += (_, _) => cancellation?.Cancel();
        FormClosed += (_, _) => cancellation?.Cancel();
    }

    private async Task DownloadAsync()
    {
        settings.Cache.ManifestUrl = manifestUrl.Text.Trim(); store.Save(settings);
        cancellation?.Dispose(); cancellation = new CancellationTokenSource();
        download.Enabled = false; cancel.Enabled = true; manifestUrl.Enabled = false; progress.Value = 0; status.Text = "Loading cache manifest...";
        try
        {
            var updater = new CacheDownloadService(voice.CacheDirectory);
            var reporter = new Progress<CacheDownloadProgress>(UpdateProgress);
            CacheDownloadResult result = await updater.DownloadAsync(settings.Cache.ManifestUrl, reporter, cancellation.Token);
            voice.ReloadDownloadedCache();
            status.Text = result.DownloadedFiles == 0 ? "The optional cache is already up to date." : $"Downloaded {result.DownloadedFiles:N0} files ({FormatBytes(result.DownloadedBytes)}).";
        }
        catch (OperationCanceledException) { status.Text = "Cache download cancelled. Completed files are kept."; }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { download.Enabled = true; cancel.Enabled = false; manifestUrl.Enabled = true; }
    }

    private void UpdateProgress(CacheDownloadProgress value)
    {
        int percent = value.TotalBytes <= 0 ? 0 : (int)Math.Clamp(value.CompletedBytes * 100 / value.TotalBytes, 0, 100);
        progress.Value = percent;
        status.Text = value.TotalFiles == 0 ? value.CurrentFile : $"{value.CompletedFiles:N0}/{value.TotalFiles:N0} files ({percent}%) — {value.CurrentFile}";
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
