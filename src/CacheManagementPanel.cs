internal sealed class CacheManagementPanel : UserControl
{
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly VoiceService voice;
    private readonly AudioPlaybackController previewPlayback = new();
    private readonly TextBox manifestUrl = new() { Dock = DockStyle.Fill, PlaceholderText = "Cache manifest URL", MinimumSize = new Size(80, 0) };
    private readonly TextBox filter = new() { Dock = DockStyle.Fill, PlaceholderText = "Filter cached sounds..." };
    private readonly ListBox entries = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly Button download = new() { Text = "Download cache", AutoSize = true };
    private readonly Button cancelDownload = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button play = new() { Text = "Play", AutoSize = true, Enabled = false };
    private readonly Button delete = new() { Text = "Delete", AutoSize = true, Enabled = false };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100, Visible = false };
    private readonly Label progressText = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
    private List<CachedAudioEntry> allEntries = [];
    private CancellationTokenSource? downloadCancellation;
    private int displayedCompletedFiles;
    private long displayedCompletedBytes;
    private int displayedTotalFiles;
    private long displayedTotalBytes;
    private int lastListRefreshCompletedFiles;
    private long lastListRefreshTick;

    public CacheManagementPanel(Settings settings, SettingsStore store, VoiceService voice)
    {
        this.settings = settings; this.store = store; this.voice = voice;
        Dock = DockStyle.Fill; Padding = new Padding(8, 0, 0, 0);
        manifestUrl.Text = settings.Cache.ManifestUrl;

        var sourceRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true };
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sourceRow.Controls.Add(manifestUrl, 0, 0); sourceRow.Controls.Add(download, 1, 0); sourceRow.Controls.Add(cancelDownload, 2, 0);
        var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        actionRow.Controls.Add(play); actionRow.Controls.Add(delete);

        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.Controls.Add(sourceRow, 0, 0); layout.Controls.Add(progress, 0, 1); layout.Controls.Add(progressText, 0, 2);
        layout.Controls.Add(filter, 0, 3); layout.Controls.Add(entries, 0, 4); layout.Controls.Add(actionRow, 0, 5);
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
        if (!IsDisposed) BeginInvoke(RefreshEntries);
    }
    private void RefreshEntries()
    {
        string? selectedKey = (entries.SelectedItem as CachedAudioEntry)?.Key;
        allEntries = voice.ListCachedAudio().ToList();
        ApplyFilter(selectedKey);
    }
    private void ApplyFilter(string? preferredKey = null)
    {
        string query = filter.Text.Trim();
        IEnumerable<CachedAudioEntry> filtered = allEntries;
        if (query.Length > 0) filtered = filtered.Where(entry => entry.Speaker.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        entries.BeginUpdate(); entries.Items.Clear(); entries.Items.AddRange(filtered.Cast<object>().ToArray()); entries.EndUpdate();
        if (preferredKey is not null)
            for (int index = 0; index < entries.Items.Count; index++)
                if (((CachedAudioEntry)entries.Items[index]).Key == preferredKey) { entries.SelectedIndex = index; break; }
        UpdateSelectionButtons();
    }
    private void UpdateSelectionButtons()
    {
        bool selected = entries.SelectedItem is CachedAudioEntry;
        play.Enabled = selected; delete.Enabled = selected;
    }
    private void SetDownloadUi(bool downloading)
    {
        download.Enabled = !downloading; cancelDownload.Enabled = downloading; manifestUrl.Enabled = !downloading;
        filter.Visible = !downloading; progress.Visible = downloading; progressText.Visible = downloading;
        layout.RowStyles[1].Height = downloading ? 22 : 0;
        layout.RowStyles[2].Height = downloading ? 22 : 0;
    }
    private async Task DownloadAsync()
    {
        settings.Cache.ManifestUrl = manifestUrl.Text.Trim(); store.Save(settings);
        downloadCancellation?.Dispose(); downloadCancellation = new CancellationTokenSource();
        displayedCompletedFiles = 0; displayedCompletedBytes = 0; displayedTotalFiles = 0; displayedTotalBytes = 0;
        lastListRefreshCompletedFiles = 0; lastListRefreshTick = Environment.TickCount64;
        progress.Value = 0; progressText.Text = "Loading cache manifest..."; SetDownloadUi(true);
        try
        {
            CacheDownloadResult result = await new CacheDownloadService(voice.CacheDirectory).DownloadAsync(settings.Cache.ManifestUrl, new Progress<CacheDownloadProgress>(UpdateProgress), downloadCancellation.Token);
            voice.ReloadDownloadedCache(); RefreshEntries();
            progress.Value = 100;
            progressText.Text = result.DownloadedFiles == 0 ? "Cache is already up to date." : $"Downloaded {result.DownloadedFiles:N0} files ({FormatBytes(result.DownloadedBytes)}).";
        }
        catch (OperationCanceledException)
        {
            // Completed WAVs were atomically moved into place before the
            // cancellation reached the remaining downloads.
            voice.ReloadDownloadedCache(); RefreshEntries();
            progressText.Text = $"Download cancelled; {displayedCompletedFiles:N0} completed files were kept.";
        }
        catch (Exception exception) { progressText.Text = exception.Message; }
        finally
        {
            // Keep the result readable briefly; the cache controls never reflow while progress changes.
            await Task.Delay(1800);
            if (!IsDisposed) SetDownloadUi(false);
        }
    }
    private void UpdateProgress(CacheDownloadProgress value)
    {
        // Progress<T> posts updates to the UI queue. Parallel downloads can
        // arrive out of order, so only allow the displayed state to advance.
        displayedTotalFiles = Math.Max(displayedTotalFiles, value.TotalFiles);
        displayedTotalBytes = Math.Max(displayedTotalBytes, value.TotalBytes);
        displayedCompletedFiles = Math.Max(displayedCompletedFiles, value.CompletedFiles);
        displayedCompletedBytes = Math.Max(displayedCompletedBytes, value.CompletedBytes);
        int percent = displayedTotalBytes <= 0 ? 0 : (int)Math.Clamp(displayedCompletedBytes * 100 / displayedTotalBytes, 0, 100);
        progress.Value = percent;
        progressText.Text = displayedTotalFiles == 0
            ? value.CurrentFile
            : $"Downloading {displayedCompletedFiles:N0} complete of {displayedTotalFiles:N0} ({percent}%) — {value.CurrentFile}";

        // The index is available first, then WAVs appear as their atomic
        // moves complete. Throttle list rebuilds so thousands of downloads do
        // not make the UI less responsive.
        long now = Environment.TickCount64;
        if ((value.CurrentFile.Equals("audio-cache-index.json", StringComparison.OrdinalIgnoreCase) ||
             displayedCompletedFiles >= lastListRefreshCompletedFiles + 8 ||
             now - lastListRefreshTick >= 500) && displayedCompletedFiles > 0)
        {
            if (value.CurrentFile.Equals("audio-cache-index.json", StringComparison.OrdinalIgnoreCase)) voice.ReloadDownloadedCache();
            RefreshEntries();
            lastListRefreshCompletedFiles = displayedCompletedFiles;
            lastListRefreshTick = now;
        }
    }
    private async Task PlaySelectedAsync()
    {
        if (entries.SelectedItem is not CachedAudioEntry entry) return;
        if (!voice.TryReadCachedAudio(entry.Key, out byte[] pcm)) { RefreshEntries(); return; }
        play.Enabled = false;
        try { await previewPlayback.PlayLatestAsync(pcm); }
        finally { if (!IsDisposed) UpdateSelectionButtons(); }
    }
    private void DeleteSelected()
    {
        if (entries.SelectedItem is not CachedAudioEntry entry) return;
        DialogResult answer = MessageBox.Show(this, $"Delete this local cached sound?\r\n\r\n{entry.Speaker}: {entry.Subtitle}\r\n\r\nIt can be downloaded again later if it is still in the optional cache.", "Delete cached sound", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        previewPlayback.Stop(); voice.DeleteCachedAudio(entry.Key); RefreshEntries();
    }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{bytes / 1024d:0.0} KB";
}
