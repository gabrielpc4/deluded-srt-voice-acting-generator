internal sealed class SettingsDialog : Form
{
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly TextBox model = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown speed = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Increment = .05m, Minimum = .25m, Maximum = 2m };
    private readonly ComboBox alisa = VoiceCombo();
    private readonly ComboBox unknown = VoiceCombo();
    private readonly TextBox instructions = new() { Dock = DockStyle.Fill, Multiline = true, Height = 80 };
    private readonly CheckBox persistentSessions = new()
    {
        Text = "Send the entire conversation to AI on every request\r\n(Increases tone consistency for the next sentence, but it also increases the chances of AI refusing if it infers inappropriate content.)",
        AutoSize = false,
        Dock = DockStyle.Fill,
        Height = 42
    };

    public SettingsDialog(Settings settings, SettingsStore store)
    {
        this.settings = settings; this.store = store;
        Text = "Voice settings"; ClientSize = new Size(600, 380); StartPosition = FormStartPosition.CenterParent;
        model.Text = settings.OpenAi.RealtimeModel; speed.Value = (decimal)settings.OpenAi.SpeechSpeed; alisa.SelectedItem = settings.OpenAi.AlisaVoice; unknown.SelectedItem = settings.OpenAi.UnknownVoice; instructions.Text = settings.OpenAi.Instructions; persistentSessions.Checked = settings.OpenAi.PersistentSessions;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(layout, 0, "Realtime model", model); Add(layout, 1, "Speech speed", speed); Add(layout, 2, "Alisa voice", alisa); Add(layout, 3, "Unknown voice", unknown); Add(layout, 4, "Default instructions", instructions); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.Controls.Add(persistentSessions, 1, 5);
        Button save = new() { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true }; Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true }; var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 1, 6); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        save.Click += (_, _) => { settings.OpenAi.RealtimeModel = model.Text.Trim(); settings.OpenAi.SpeechSpeed = (double)speed.Value; settings.OpenAi.AlisaVoice = alisa.Text; settings.OpenAi.UnknownVoice = unknown.Text; settings.OpenAi.Instructions = instructions.Text.Trim(); settings.OpenAi.PersistentSessions = persistentSessions.Checked; store.Save(settings); };
    }
    private static ComboBox VoiceCombo() { ComboBox box = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList }; box.Items.AddRange(["alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"]); return box; }
    private static void Add(TableLayoutPanel layout, int row, string label, Control control) { layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); layout.Controls.Add(control, 1, row); }
}

internal sealed class CastDialog : Form
{
    private readonly SpeakerCatalog catalog;
    private readonly SettingsStore store;
    private readonly VoiceService voiceService;
    private readonly CharacterPreviewLineCatalog previewLines = new();
    private readonly AudioPlaybackController previewPlayback = new();
    private readonly ListBox names = new() { Dock = DockStyle.Fill };
    private readonly ComboBox voice = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown volume = new() { Dock = DockStyle.Top, DecimalPlaces = 2, Increment = .05m, Minimum = .25m, Maximum = 2m, Value = 1m };
    private readonly TextBox instructions = new() { Dock = DockStyle.Fill, Multiline = true };
    private readonly TextBox previewLine = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, Height = 52, BackColor = SystemColors.Window };
    private readonly Label previewStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button preview = new() { Text = "Preview selected voice", AutoSize = true };
    private readonly Button stopPreview = new() { Text = "Stop", AutoSize = true, Enabled = false };
    private CancellationTokenSource? previewCancellation;

    public CastDialog(SpeakerCatalog catalog, SettingsStore store, VoiceService voiceService)
    {
        this.catalog = catalog; this.store = store; this.voiceService = voiceService; Text = "Cast voice profiles"; ClientSize = new Size(980, 600); MinimumSize = new Size(700, 480); StartPosition = FormStartPosition.CenterParent;
        voice.Items.AddRange(["", "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"]);
        names.Items.AddRange(catalog.Seeds.OrderBy(seed => seed.CanonicalName).Cast<object>().ToArray()); names.DisplayMember = nameof(SpeakerSeed.CanonicalName); names.SelectedIndexChanged += (_, _) => LoadSeed();
        preview.Click += async (_, _) => await PreviewAsync();
        stopPreview.Click += (_, _) => { previewCancellation?.Cancel(); previewPlayback.Stop(); };
        var previewControls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        previewControls.Controls.Add(preview); previewControls.Controls.Add(stopPreview); previewControls.Controls.Add(previewStatus);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 10 };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 58)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(new Label { Text = "Voice", AutoSize = true }, 0, 0); right.Controls.Add(voice, 0, 1); right.Controls.Add(new Label { Text = "Character preview line", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, 2); right.Controls.Add(previewLine, 0, 3); right.Controls.Add(previewControls, 0, 4); right.Controls.Add(new Label { Text = "Playback volume", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, 5); right.Controls.Add(volume, 0, 6); right.Controls.Add(new Label { Text = "Performance instructions", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, 0, 7); right.Controls.Add(instructions, 0, 8); Button save = new() { Text = "Save selected profile", AutoSize = true, Margin = new Padding(0, 8, 0, 0) }; save.Click += (_, _) => SaveSeed(); right.Controls.Add(save, 0, 9);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 }; split.Panel1.Controls.Add(names); split.Panel2.Controls.Add(right); Controls.Add(split);
        Shown += (_, _) =>
        {
            // SplitContainer validates min sizes immediately. Apply them only
            // after WinForms has given the dialog its real DPI-scaled bounds.
            split.Panel1MinSize = 150;
            split.Panel2MinSize = 400;
            int maximumLeftWidth = split.ClientSize.Width - split.SplitterWidth - split.Panel2MinSize;
            split.SplitterDistance = Math.Clamp(split.ClientSize.Width / 5, split.Panel1MinSize, maximumLeftWidth);
        };
        FormClosed += (_, _) => { previewCancellation?.Cancel(); previewCancellation?.Dispose(); previewPlayback.Dispose(); };
        if (names.Items.Count > 0) names.SelectedIndex = 0;
    }
    private SpeakerSeed? Selected => names.SelectedItem as SpeakerSeed;
    private void LoadSeed()
    {
        if (Selected is not { } seed) return;
        previewCancellation?.Cancel(); previewPlayback.Stop();
        voice.SelectedItem = seed.PreferredVoice ?? ""; volume.Value = (decimal)Math.Clamp(seed.VolumeMultiplier ?? 1f, .25f, 2f); instructions.Text = seed.SpeechInstructions ?? "";
        previewLine.Text = previewLines.Find(seed.CanonicalName) ?? "No bundled character preview line is available for this profile yet.";
        previewStatus.Text = string.Empty;
        preview.Enabled = !string.IsNullOrWhiteSpace(previewLines.Find(seed.CanonicalName));
    }
    private void SaveSeed() { if (Selected is not { } seed) return; seed.PreferredVoice = string.IsNullOrWhiteSpace(voice.Text) ? null : voice.Text; seed.VolumeMultiplier = (float)volume.Value; seed.SpeechInstructions = string.IsNullOrWhiteSpace(instructions.Text) ? null : instructions.Text.Trim(); catalog.RebuildLookup(); store.SaveSeeds(new SpeakerSeedCollection { Speakers = catalog.Seeds.ToList() }); }
    private async Task PreviewAsync()
    {
        if (Selected is not { } seed) return;
        string line = previewLines.Find(seed.CanonicalName) ?? "";
        if (string.IsNullOrWhiteSpace(line)) return;
        string selectedVoice = voice.Text.Trim();
        if (selectedVoice.Length == 0) { previewStatus.Text = "Choose a voice first."; return; }
        previewCancellation?.Cancel(); previewCancellation?.Dispose();
        previewCancellation = new CancellationTokenSource();
        CancellationToken token = previewCancellation.Token;
        preview.Enabled = false; stopPreview.Enabled = true; previewStatus.Text = "Generating preview...";
        try
        {
            SpeakerProfile profile = new(seed.CanonicalName, seed.CanonicalName, selectedVoice, instructions.Text.Trim(), seed.Gender, true, (float)volume.Value);
            byte[] pcm = await voiceService.PreviewAsync(profile, line, token);
            previewStatus.Text = "Playing preview...";
            await previewPlayback.PlayLatestAsync(pcm, profile.VolumeMultiplier);
            previewStatus.Text = "Preview complete.";
        }
        catch (OperationCanceledException) { previewStatus.Text = "Preview stopped."; }
        catch (Exception exception) { previewStatus.Text = exception.Message; }
        finally { preview.Enabled = true; stopPreview.Enabled = false; }
    }
}
