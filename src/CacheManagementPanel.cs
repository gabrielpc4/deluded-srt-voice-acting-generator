internal sealed class CacheManagementPanel : UserControl
{
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly VoiceService voice;
    private readonly AudioPlaybackController previewPlayback = new();
    private readonly TextBox manifestUrl = new() { Dock = DockStyle.Fill, PlaceholderText = "Cache manifest URL" };
    private readonly TextBox filter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter cached sounds..." };
    private readonly ListBox entries = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button download = new() { Text = "Download cache", AutoSize = true };
    private readonly Button cancelDownload = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button delete = new() { Text = "Delete", AutoSize = true, Enabled = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, AutoEllipsis = true };
    private List<CachedAudioEntry> allEntries = [];
    private CancellationTokenSource? downloadCancellation;

    public CacheManagementPanel(Settings settings, SettingsStore store, VoiceService voice)
    {
        this.settings = settings; this.store = store; this.voice = voice;
        Dock = DockStyle.Fill; Padding = new Padding(8, 0, 0, 0);
        manifestUrl.Text = settings.Cache.ManifestUrl;
        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, AutoSize = true };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(manifestUrl, 0, 0); sourceRow.Controls.Add(download, 1, 0);
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 3, 0, 0) };
        actionRow.Controls.Add(cancelDownload); actionRow.Controls.Add(play); actionRow.Controls.Add(delete); actionRow.Controls.Add(status);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(sourceRow, 0, 0); layout.Controls.Add(progress, 0, 1); layout.Controls.Add(filter, 0, 2); layout.Controls.Add(entries, 0, 3); layout.Controls.Add(actionRow, 0, 4);
        Controls.Add(layout);
        filter.TextChanged += (_, _) => ApplyFilter();
        entries.SelectedIndexChanged += (_, _) => UpdateSelectionButtons();
        download.Click += async (_, _) => await DownloadAsync();
        cancelDownload.Click += (_, _) => downloadCancellation?.Cancel();
        play.Click += async (_, _) => await PlaySelectedAsync();
        delete.Click += (_, _) => DeleteSelected();
        voice.LogGenerated += OnVoiceLog;
        Disposed += (_, _) => { downloadCancellation?.Cancel(); previewPlayback.Dispose(); voice.LogGenerated -= OnVoiceLog; };
        RefreshEntries();
    }

    private void OnVoiceLog(object? sender, string message)
    {
        if (message is not "Optional cache updated." and not "Cached audio deleted.") return;
        if (IsDisposed) return;
        BeginInvoke(RefreshEntries);
    }

    private void RefreshEntries()
    {
        string? selectedKey = (entries.SelectedItem as CachedAudioEntry)?.Key;
        allEntries = voice.ListCachedAudio().ToList();
        ApplyFilter(selectedKey);
        status.Text = $"{allEntries.Count:N0} cached sounds";
    }
    private void ApplyFilter(string? preferredKey = null)
    {
        string query = filter.Text.Trim();
        IEnumerable<CachedAudioEntry> filtered = allEntries;
        if (query.Length > 0) filtered = filtered.Where(entry => entry.Speaker.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        entries.BeginUpdate(); entries.Items.Clear(); entries.Items.AddRange(filtered.Cast<object>().ToArray()); entries.EndUpdate();
        if (preferredKey is not null)
        {
            for (int index = 0; index < entries.Items.Count; index++)
                if (((CachedAudioEntry)entries.Items[index]).Key == preferredKey) { entries.SelectedIndex = index; break; }
        }
        UpdateSelectionButtons();
    }
    private void UpdateSelectionButtons()
    {
        bool selected = entries.SelectedItem is CachedAudioEntry;
        play.Enabled = selected; delete.Enabled = selected;
    }
    private async Task DownloadAsync()
    {
        settings.Cache.ManifestUrl = manifestUrl.Text.Trim(); store.Save(settings);
        downloadCancellation?.Dispose(); downloadCancellation = new CancellationTokenSource();
        download.Enabled = false; cancelDownload.Enabled = true; manifestUrl.Enabled = false; progress.Value = 0; status.Text = "Loading cache manifest...";
        try
        {
            CacheDownloadResult result = await new CacheDownloadService(voice.CacheDirectory).DownloadAsync(settings.Cache.ManifestUrl, new Progress<CacheDownloadProgress>(UpdateProgress), downloadCancellation.Token);
            voice.ReloadDownloadedCache();
            RefreshEntries();
            status.Text = result.DownloadedFiles == 0 ? "Cache is already up to date." : $"Downloaded {result.DownloadedFiles:N0} files ({FormatBytes(result.DownloadedBytes)}).";
        }
        catch (OperationCanceledException) { status.Text = "Download cancelled; completed files were kept."; }
        catch (Exception exception) { status.Text = exception.Message; }
        finally { download.Enabled = true; cancelDownload.Enabled = false; manifestUrl.Enabled = true; }
    }
    private void UpdateProgress(CacheDownloadProgress value)
    {
        int percent = value.TotalBytes <= 0 ? 0 : (int)Math.Clamp(value.CompletedBytes * 100 / value.TotalBytes, 0, 100);
        progress.Value = percent;
        status.Text = value.TotalFiles == 0 ? value.CurrentFile : $"{value.CompletedFiles:N0}/{value.TotalFiles:N0} ({percent}%) {value.CurrentFile}";
    }
    private async Task PlaySelectedAsync()
    {
        if (entries.SelectedItem is not CachedAudioEntry entry) return;
        if (!voice.TryReadCachedAudio(entry.Key, out byte[] pcm)) { status.Text = "That cache file is no longer available."; RefreshEntries(); return; }
        play.Enabled = false; status.Text = $"Playing: {entry.Speaker}: {entry.Subtitle}";
        try { await previewPlayback.PlayLatestAsync(pcm); }
        finally { if (!IsDisposed) { UpdateSelectionButtons(); status.Text = "Preview complete."; } }
    }
    private void DeleteSelected()
    {
        if (entries.SelectedItem is not CachedAudioEntry entry) return;
        DialogResult answer = MessageBox.Show(this, $"Delete this local cached sound?\r\n\r\n{entry.Speaker}: {entry.Subtitle}\r\n\r\nIt can be downloaded again later if it is still in the optional cache.", "Delete cached sound", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        previewPlayback.Stop();
        status.Text = voice.DeleteCachedAudio(entry.Key) ? "Cached sound deleted." : "The cached sound was already unavailable.";
        RefreshEntries();
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
