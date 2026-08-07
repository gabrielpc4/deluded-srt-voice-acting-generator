internal sealed class SettingsDialog : Form
{
    private readonly Settings settings;
    private readonly SettingsStore store;
    private readonly TextBox model = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown speed = new() { Dock = DockStyle.Fill, DecimalPlaces = 2, Increment = .05m, Minimum = .25m, Maximum = 2m };
    private readonly ComboBox alisa = VoiceCombo();
    private readonly ComboBox unknown = VoiceCombo();
    private readonly TextBox instructions = new() { Dock = DockStyle.Fill, Multiline = true, Height = 80 };
    private readonly CheckBox persistentSessions = new() { Text = "Reuse GA Realtime sessions by voice", AutoSize = true };

    public SettingsDialog(Settings settings, SettingsStore store)
    {
        this.settings = settings; this.store = store;
        Text = "Voice settings"; ClientSize = new Size(600, 380); StartPosition = FormStartPosition.CenterParent;
        model.Text = settings.OpenAi.RealtimeModel; speed.Value = (decimal)settings.OpenAi.SpeechSpeed; alisa.SelectedItem = settings.OpenAi.AlisaVoice; unknown.SelectedItem = settings.OpenAi.UnknownVoice; instructions.Text = settings.OpenAi.Instructions; persistentSessions.Checked = settings.OpenAi.PersistentSessions;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(layout, 0, "Realtime model", model); Add(layout, 1, "Speech speed", speed); Add(layout, 2, "Alisa voice", alisa); Add(layout, 3, "Unknown voice", unknown); Add(layout, 4, "Default instructions", instructions); layout.Controls.Add(persistentSessions, 1, 5);
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
    private readonly ListBox names = new() { Dock = DockStyle.Fill };
    private readonly ComboBox voice = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown volume = new() { Dock = DockStyle.Top, DecimalPlaces = 2, Increment = .05m, Minimum = .25m, Maximum = 2m, Value = 1m };
    private readonly TextBox instructions = new() { Dock = DockStyle.Fill, Multiline = true };
    public CastDialog(SpeakerCatalog catalog, SettingsStore store)
    {
        this.catalog = catalog; this.store = store; Text = "Cast voice profiles"; ClientSize = new Size(760, 430); StartPosition = FormStartPosition.CenterParent;
        voice.Items.AddRange(["", "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"]);
        names.Items.AddRange(catalog.Seeds.OrderBy(seed => seed.CanonicalName).Cast<object>().ToArray()); names.DisplayMember = nameof(SpeakerSeed.CanonicalName); names.SelectedIndexChanged += (_, _) => LoadSeed();
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 7 }; right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); right.RowStyles.Add(new RowStyle(SizeType.AutoSize)); right.Controls.Add(new Label { Text = "Voice", AutoSize = true }, 0, 0); right.Controls.Add(voice, 0, 1); right.Controls.Add(new Label { Text = "Playback volume", AutoSize = true }, 0, 2); right.Controls.Add(volume, 0, 3); right.Controls.Add(instructions, 0, 4); Button save = new() { Text = "Save selected profile", AutoSize = true }; save.Click += (_, _) => SaveSeed(); right.Controls.Add(save, 0, 5);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 220 }; split.Panel1.Controls.Add(names); split.Panel2.Controls.Add(right); Controls.Add(split); if (names.Items.Count > 0) names.SelectedIndex = 0;
    }
    private SpeakerSeed? Selected => names.SelectedItem as SpeakerSeed;
    private void LoadSeed() { if (Selected is not { } seed) return; voice.SelectedItem = seed.PreferredVoice ?? ""; volume.Value = (decimal)Math.Clamp(seed.VolumeMultiplier ?? 1f, .25f, 2f); instructions.Text = seed.SpeechInstructions ?? ""; }
    private void SaveSeed() { if (Selected is not { } seed) return; seed.PreferredVoice = string.IsNullOrWhiteSpace(voice.Text) ? null : voice.Text; seed.VolumeMultiplier = (float)volume.Value; seed.SpeechInstructions = string.IsNullOrWhiteSpace(instructions.Text) ? null : instructions.Text.Trim(); catalog.RebuildLookup(); store.SaveSeeds(new SpeakerSeedCollection { Speakers = catalog.Seeds.ToList() }); }
}
