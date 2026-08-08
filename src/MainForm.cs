using System.Text.Json;

internal sealed class MainForm : Form
{
    private const int UnknownMaleHotkeyId = 2;
    private const int UnknownFemaleHotkeyId = 3;
    private const int WmHotkey = 0x0312;
    private const uint VirtualKeyM = 0x4D;
    private const uint VirtualKeyF = 0x46;
    private const int SubtitleStabilityMilliseconds = 100;
    private const string UnknownSpeakerQuestion = "Is this speaker male or female? Press 'M' for male or 'F' for female.";
    private static readonly SpeakerProfile UnknownSpeakerQuestionProfile = new("Speaker choice", "Unknown speaker choice", "coral", "Speak clearly and briefly.", SpeakerGender.Female, true, 1f);
    private readonly GameMemoryReader reader;
    private readonly VoiceService voice;
    private readonly SpeakerCatalog speakers;
    private readonly Settings settings;
    private readonly SettingsStore settingsStore;
    private readonly AudioPlaybackController playback = new();
    private readonly System.Windows.Forms.Timer timer = new();
    private readonly TextBox currentText = TextFor();
    private readonly TextBox nextText = TextFor();
    private readonly TextBox apiKeyText = new() { Dock = DockStyle.Fill, PlaceholderText = "OpenAI API key" };
    private readonly Button apiKeyButton = new() { AutoSize = true, Margin = new Padding(7, 0, 0, 0) };
    private readonly NumericUpDown subtitleStartDelay = new() { Minimum = 0, Maximum = 10_000, Increment = 25, Width = 72 };
    private readonly Button subtitleStartDelayButton = new() { AutoSize = true, Text = "Save delay", Margin = new Padding(7, 0, 0, 0) };
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro, Font = new Font("Consolas", 9) };
    private readonly ActivityLog activityLog = new();
    private string lastPlaybackKey = string.Empty;
    // A rapid polling cycle can observe the same exact next subtitle several
    // times before the first prefetch has warmed its PCM. Queue it once.
    private readonly HashSet<string> prefetchedSubtitleKeys = new(StringComparer.Ordinal);
    private bool busy;
    private DateTime? noDialogueSinceUtc;
    private bool dialogueContextCleared;
    private bool dialogueActive;
    private string? lastSpokenTextKey;
    private int lastSpokenNode;
    private SubtitleSnapshot? latestSnapshot;
    private SpeakerGender? unknownSpeakerGender;
    private int? pendingUnknownSpeakerNode;
    private bool unknownChoicePromptInFlight;
    private int unknownChoiceRequestId;
    private IReadOnlyList<string> activeChoiceOptions = [];
    private string activeChoiceOptionsSignature = "";
    private string selectedChoiceSignature = "";
    private string lastMenuDiagnosticKey = "";
    private readonly bool[] choiceNumberWasDown = new bool[9];
    private Task choiceNarrationTask = Task.CompletedTask;
    private bool eWasDown;
    private DateTime? eDialogueCheckDueUtc;
    private bool rWasDown;
    private bool apiKeyEditing;

    public MainForm(Settings settings, SettingsStore settingsStore, SpeakerCatalog speakers)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        this.settings = settings;
        this.settingsStore = settingsStore;
        this.speakers = speakers;
        reader = new GameMemoryReader(settings.Reader.ProcessName);
        reader.Diagnostic += (_, message) => AppendLog(message);
        voice = new VoiceService(settings);
        Text = "Deluded Voice Acting Generator";
        ClientSize = new Size(1760, 720);
        MinimumSize = new Size(1000, 560);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 14), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(ApiKeyPanel(), 0, 0);
        var subtitles = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        subtitles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        subtitles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        subtitles.Controls.Add(SubtitlePanel("Current Subtitle", currentText), 0, 0);
        subtitles.Controls.Add(SubtitlePanel("Next Subtitle", nextText), 1, 0);
        root.Controls.Add(subtitles, 0, 1);
        root.Controls.Add(log, 0, 2);
        MenuStrip menu = CreateMenus();
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        menu.Dock = DockStyle.Fill;
        shell.Controls.Add(menu, 0, 0);
        shell.Controls.Add(root, 0, 1);
        Controls.Add(shell);

        voice.LogGenerated += (_, message) => AppendLog(message);
        KeyPreview = true;
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.M && TryChooseUnknownSpeakerGender(SpeakerGender.Male, "keyboard")) eventArgs.Handled = true;
            else if (eventArgs.KeyCode == Keys.F && TryChooseUnknownSpeakerGender(SpeakerGender.Female, "keyboard")) eventArgs.Handled = true;
        };
        // The memory reader is event-free and inexpensive on the inventory
        // path; manual recovery is bound to R while the game has focus.
        timer.Interval = 1;
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
        FormClosed += (_, _) => { timer.Stop(); UnregisterUnknownChoiceHotkeys(); playback.Dispose(); reader.Dispose(); voice.Dispose(); };
        AppendLog($"Companion started. File log: {activityLog.CurrentPath}");
    }

    private Task TickAsync()
    {
        bool gameIsForeground = reader.IsGameWindowForeground();
        PollManualRecoveryInput(gameIsForeground);
        PollChoiceSelectionInput(gameIsForeground);
        if (busy) return Task.CompletedTask;
        busy = true;
        try
        {
            SubtitleSnapshot? snapshot = reader.TryRead();
            if (snapshot is null)
            {
                HandleNoDialogue();
                return Task.CompletedTask;
            }

            bool startsConversation = !dialogueActive;
            if (startsConversation)
            {
                lastSpokenTextKey = null;
                lastSpokenNode = 0;
                prefetchedSubtitleKeys.Clear();
                unknownSpeakerGender = null;
                pendingUnknownSpeakerNode = null;
                UnregisterUnknownChoiceHotkeys();
            }
            dialogueActive = true;
            noDialogueSinceUtc = null;
            dialogueContextCleared = false;

            latestSnapshot = snapshot;
            bool transientChoicePlaceholder = IsTransientChoicePlaceholder(snapshot.Text);
            bool currentIsMenu = snapshot.IsChoiceMenu || IsPossibleReplies(snapshot.Text) || transientChoicePlaceholder;
            UpdateActiveChoiceOptions(currentIsMenu ? snapshot.ChoiceOptions : []);
            if (currentIsMenu)
            {
                CancelUnknownSpeakerChoice("choice_menu");
                LogMenuStateOnce(snapshot, transientChoicePlaceholder);
            }
            bool currentIsUnknownSpeaker = SpeakerCatalog.IsQuestionOnlyPlaceholder(snapshot.Speaker);
            bool currentHasUnknownProfile = speakers.HasUnknownProfileMatch(snapshot.Speaker, snapshot.Text);
            if (!currentIsUnknownSpeaker)
                CancelUnknownSpeakerChoice("speaker_resolved");
            if (pendingUnknownSpeakerNode is int pendingNode && pendingNode != snapshot.NodeId)
                ChooseUnknownSpeakerGender(SpeakerGender.Female, "advanced_default");
            SpeakerProfile currentProfile = ResolveSubtitleProfile(snapshot.Speaker, snapshot.Text);
            bool suppressCurrent = currentIsMenu || !snapshot.Text.Any(char.IsLetterOrDigit);
            if (currentIsUnknownSpeaker && !currentHasUnknownProfile && unknownSpeakerGender is null && !suppressCurrent)
            {
                AudioStatus unknownStatus = voice.Status(speakers.Resolve(snapshot.Speaker, snapshot.Text, SpeakerGender.Female), snapshot.Text);
                if (unknownStatus.State == AudioState.Waiting)
                {
                    currentText.Text = snapshot.Text;
                    nextText.Text = "Choose speaker: M = male, F = female. Advancing defaults to female.";
                    BeginUnknownSpeakerChoice(snapshot);
                    return Task.CompletedTask;
                }
                ChooseUnknownSpeakerGender(SpeakerGender.Female, "cached_default");
                currentProfile = ResolveSubtitleProfile(snapshot.Speaker, snapshot.Text);
            }
            AudioStatus currentStatus = suppressCurrent
                ? new(AudioState.Unavailable, "Skipped: not a spoken subtitle.")
                : voice.Status(currentProfile, snapshot.Text);
            currentText.Text = suppressCurrent ? string.Empty : snapshot.Text;
            if (startsConversation) AppendLog(DiagnosticEvent.Create("conversation.start", ("node", snapshot.NodeId), ("speaker", currentProfile.CanonicalName), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));

            SubtitleCandidate? prefetchCandidate = null;
            SpeakerProfile? prefetchProfile = null;
            AudioStatus? prefetchStatus = null;
            if (snapshot.Next is { IsExact: true } next)
            {
                bool nextIsUnknownSpeaker = SpeakerCatalog.IsQuestionOnlyPlaceholder(next.Speaker);
                bool nextHasUnknownProfile = speakers.HasUnknownProfileMatch(next.Speaker, next.Text);
                if (nextIsUnknownSpeaker && !nextHasUnknownProfile && unknownSpeakerGender is null)
                {
                    nextText.Text = "Unknown speaker: choice will be requested when this line appears.";
                    goto SkipPrefetch;
                }
                SpeakerProfile nextProfile = ResolveSubtitleProfile(next.Speaker, next.Text);
                nextText.Text = next.Text;
                AudioStatus nextStatus = voice.Status(nextProfile, next.Text);
                if (IsPossibleReplies(next.Text)) AppendLog(DiagnosticEvent.Create("menu.possible_replies.predicted", ("fromNode", snapshot.NodeId), ("text", next.Text), ("textHash", TextHash(next.Text))));
                prefetchCandidate = next;
                prefetchProfile = nextProfile;
                prefetchStatus = nextStatus;
            }
            else
            {
                nextText.Text = snapshot.Next?.Detail ?? "No predicted next subtitle.";
            }

        SkipPrefetch:

            // UE can rewrite the active dialogue-node id several times while
            // Slate still displays exactly the same line. The rendered speaker
            // and text are the stable identity for scheduling narration.
            string playbackKey = PlaybackKey(currentProfile, snapshot.Text);
            if (!string.Equals(playbackKey, lastPlaybackKey, StringComparison.Ordinal))
            {
                lastPlaybackKey = playbackKey;
                AppendLog(DiagnosticEvent.Create("subtitle.detected",
                    ("node", snapshot.NodeId), ("speakerRaw", snapshot.Speaker), ("speaker", currentProfile.CanonicalName),
                    ("voice", currentProfile.Voice), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)),
                    ("playbackKey", playbackKey), ("nextExact", prefetchCandidate is not null),
                    ("nextSpeaker", prefetchProfile?.CanonicalName), ("nextText", prefetchCandidate?.Text),
                    ("nextTextHash", prefetchCandidate is null ? null : TextHash(prefetchCandidate.Text))));
                if (prefetchProfile is not null && !string.Equals(currentProfile.CanonicalName, prefetchProfile.CanonicalName, StringComparison.OrdinalIgnoreCase))
                    AppendLog(DiagnosticEvent.Create("context.isolated", ("currentSpeaker", currentProfile.CanonicalName), ("nextSpeaker", prefetchProfile.CanonicalName), ("currentVoice", currentProfile.Voice), ("nextVoice", prefetchProfile.Voice)));
                if (suppressCurrent)
                {
                    playback.Stop();
                    AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", currentIsMenu ? "possible_replies" : "non_spoken_current"), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));
                }
                else
                {
                    bool applySubtitleStartDelay = currentStatus.State is AudioState.CacheHit or AudioState.Ready;
                    AppendLog(DiagnosticEvent.Create("play.queued", ("node", snapshot.NodeId), ("speaker", currentProfile.CanonicalName), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)), ("playbackKey", playbackKey), ("subtitleStartDelayMilliseconds", applySubtitleStartDelay ? Math.Max(0, settings.Reader.SubtitleStartDelayMilliseconds) : 0), ("subtitleStartDelayApplied", applySubtitleStartDelay)));
                    _ = PlayCurrentAsync(snapshot, currentProfile, applySubtitleStartDelay);
                }
            }
            // Start current first. This establishes the correct character
            // history before an exact-next prefetch can use the same session.
            if (prefetchCandidate is not null && prefetchProfile is not null && prefetchStatus!.State is (AudioState.Waiting or AudioState.CacheHit))
            {
                bool waitForCurrentCharacterContext = !suppressCurrent &&
                    string.Equals(currentProfile.CanonicalName, prefetchProfile.CanonicalName, StringComparison.OrdinalIgnoreCase);
                QueuePrefetchOnce(prefetchCandidate, prefetchProfile, waitForCurrentCharacterContext);
            }
        }
        catch (Exception exception)
        {
            AppendLog($"Memory reader error: {exception.Message}");
        }
        finally { busy = false; }
        return Task.CompletedTask;
    }

    private void QueuePrefetchOnce(SubtitleCandidate candidate, SpeakerProfile profile, bool waitForCurrentCharacterContext)
    {
        string key = string.Concat(profile.CanonicalName, "\u001f", profile.Voice, "\u001f", candidate.Text);
        if (!prefetchedSubtitleKeys.Add(key)) return;
        _ = PrefetchAsync(candidate, profile, waitForCurrentCharacterContext);
    }

    private async Task PrefetchAsync(SubtitleCandidate candidate, SpeakerProfile profile, bool waitForCurrentCharacterContext = false)
    {
        try
        {
            // PlayCurrentAsync deliberately waits for Slate state to settle.
            // Give it time to enter the same character's line into its
            // persistent session before preparing the next line.
            if (waitForCurrentCharacterContext) await Task.Delay(SubtitleStabilityMilliseconds + 25);
            await voice.EnsureAsync(profile, candidate.Text, AudioRequestRole.Prefetch, CancellationToken.None);
        }
        catch { /* VoiceService reports the single latched generation failure. */ }
    }

    private SpeakerProfile ResolveSubtitleProfile(string rawSpeaker, string subtitle)
    {
        SpeakerGender? selectedGender = SpeakerCatalog.IsQuestionOnlyPlaceholder(rawSpeaker) ? unknownSpeakerGender : null;
        return speakers.Resolve(rawSpeaker, subtitle, selectedGender);
    }

    private void BeginUnknownSpeakerChoice(SubtitleSnapshot snapshot)
    {
        if (pendingUnknownSpeakerNode == snapshot.NodeId || unknownChoicePromptInFlight) return;
        pendingUnknownSpeakerNode = snapshot.NodeId;
        unknownChoicePromptInFlight = true;
        RegisterUnknownChoiceHotkeys();
        playback.Stop();
        AppendLog(DiagnosticEvent.Create("unknown_speaker.choice.requested", ("node", snapshot.NodeId), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)), ("default", "female")));
        int requestId = ++unknownChoiceRequestId;
        _ = PlayUnknownSpeakerQuestionAsync(snapshot.NodeId, requestId);
    }

    private async Task PlayUnknownSpeakerQuestionAsync(int expectedNode, int requestId)
    {
        try
        {
            byte[]? pcm = await voice.EnsureAsync(UnknownSpeakerQuestionProfile, UnknownSpeakerQuestion, AudioRequestRole.Current, CancellationToken.None);
            if (pcm is not null && IsUnknownSpeakerChoiceStillPending(expectedNode, requestId))
            {
                AppendLog(DiagnosticEvent.Create("unknown_speaker.choice.play", ("node", expectedNode)));
                await playback.PlayLatestAsync(pcm);
            }
        }
        catch (Exception exception) { AppendLog(DiagnosticEvent.Create("unknown_speaker.choice.failed", ("error", exception.Message))); }
        finally { unknownChoicePromptInFlight = false; }
    }

    private bool IsUnknownSpeakerChoiceStillPending(int expectedNode, int requestId) =>
        pendingUnknownSpeakerNode == expectedNode &&
        unknownChoiceRequestId == requestId &&
        unknownSpeakerGender is null &&
        latestSnapshot is { } snapshot &&
        snapshot.NodeId == expectedNode &&
        SpeakerCatalog.IsQuestionOnlyPlaceholder(snapshot.Speaker) &&
        !snapshot.IsChoiceMenu &&
        !IsPossibleReplies(snapshot.Text) &&
        !IsTransientChoicePlaceholder(snapshot.Text);

    private void CancelUnknownSpeakerChoice(string reason)
    {
        if (pendingUnknownSpeakerNode is not int node) return;
        pendingUnknownSpeakerNode = null;
        unknownChoiceRequestId++;
        UnregisterUnknownChoiceHotkeys();
        AppendLog(DiagnosticEvent.Create("unknown_speaker.choice.cancelled", ("node", node), ("reason", reason)));
    }

    private bool TryChooseUnknownSpeakerGender(SpeakerGender gender, string source)
    {
        if (pendingUnknownSpeakerNode is null || unknownSpeakerGender is not null) return false;
        ChooseUnknownSpeakerGender(gender, source);
        _ = TickAsync();
        return true;
    }

    private void ChooseUnknownSpeakerGender(SpeakerGender gender, string source)
    {
        if (unknownSpeakerGender is not null) return;
        unknownSpeakerGender = gender;
        int? node = pendingUnknownSpeakerNode;
        pendingUnknownSpeakerNode = null;
        UnregisterUnknownChoiceHotkeys();
        playback.Stop();
        lastPlaybackKey = string.Empty;
        AppendLog(DiagnosticEvent.Create("unknown_speaker.choice.selected", ("node", node), ("gender", gender.ToString().ToLowerInvariant()), ("source", source)));
    }

    private void HandleNoDialogue()
    {
        noDialogueSinceUtc ??= DateTime.UtcNow;
        if (dialogueContextCleared || DateTime.UtcNow - noDialogueSinceUtc < TimeSpan.FromSeconds(2)) return;
        dialogueContextCleared = true;
        dialogueActive = false;
        lastSpokenTextKey = null;
        lastSpokenNode = 0;
        _ = ClearDialogueContextAsync();
    }

    private async Task ClearDialogueContextAsync()
    {
        if (await voice.EndConversationAsync()) AppendLog(DiagnosticEvent.Create("conversation.end", ("reason", "no_subtitle_for_2_seconds")));
    }

    private async Task PlayCurrentAsync(SubtitleSnapshot snapshot, SpeakerProfile profile, bool applySubtitleStartDelay = false)
    {
        try
        {
            string expectedPlaybackKey = PlaybackKey(profile, snapshot.Text);
            int subtitleStartDelayMilliseconds = Math.Max(0, settings.Reader.SubtitleStartDelayMilliseconds);
            Task subtitleStartDelay = applySubtitleStartDelay && subtitleStartDelayMilliseconds > 0 ? Task.Delay(subtitleStartDelayMilliseconds) : Task.CompletedTask;
            AppendLog(DiagnosticEvent.Create("play.settle.begin", ("node", snapshot.NodeId), ("expectedPlaybackKey", expectedPlaybackKey)));
            // UE/Slate exposes short-lived intermediate values while a dialogue
            // node changes. Wait for the state to settle before an old line can
            // be replayed under the new node id.
            await Task.Delay(SubtitleStabilityMilliseconds);
            AppendLog(DiagnosticEvent.Create("play.settle.end", ("node", snapshot.NodeId), ("expectedPlaybackKey", expectedPlaybackKey), ("activePlaybackKey", lastPlaybackKey)));
            if (!string.Equals(expectedPlaybackKey, lastPlaybackKey, StringComparison.Ordinal))
            {
                AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", "subtitle_state_changed_during_settle"), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)), ("expectedPlaybackKey", expectedPlaybackKey), ("activePlaybackKey", lastPlaybackKey)));
                return;
            }
            byte[]? pcm = await voice.EnsureAsync(profile, snapshot.Text, AudioRequestRole.Current, CancellationToken.None);
            if (pcm is null) return;
            if (!string.Equals(expectedPlaybackKey, lastPlaybackKey, StringComparison.Ordinal))
            {
                AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", "superseded_before_audio_ready"), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)), ("expectedPlaybackKey", expectedPlaybackKey), ("activePlaybackKey", lastPlaybackKey)));
                return;
            }
            string spokenTextKey = profile.CanonicalName + "|" + NormalizeSpokenText(snapshot.Text);
            if (string.Equals(spokenTextKey, lastSpokenTextKey, StringComparison.Ordinal))
            {
                AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", "duplicate_text_immediately_repeated"), ("lastPlayedNode", lastSpokenNode), ("speaker", profile.CanonicalName), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));
                return;
            }
            // A numbered reply is deliberately narrated first. The next game
            // subtitle may arrive in the same frame as the selection.
            await choiceNarrationTask;
            if (!string.Equals(expectedPlaybackKey, lastPlaybackKey, StringComparison.Ordinal))
            {
                AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", "superseded_after_choice_narration"), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));
                return;
            }
            await subtitleStartDelay;
            if (!string.Equals(expectedPlaybackKey, lastPlaybackKey, StringComparison.Ordinal))
            {
                AppendLog(DiagnosticEvent.Create("play.suppressed", ("node", snapshot.NodeId), ("reason", "superseded_during_subtitle_start_delay"), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));
                return;
            }
            // Reserve the line before handing it to the playback device. UE
            // can briefly expose the same text under a new node while the
            // prior clip is still finishing; waiting for completion would let
            // that duplicate start a second copy.
            lastSpokenTextKey = spokenTextKey;
            lastSpokenNode = snapshot.NodeId;
            string sanitizedText = RealtimeTextSanitizer.ReplaceBlockedWords(snapshot.Text, out _);
            AppendLog(DiagnosticEvent.Create("play.start", ("node", snapshot.NodeId), ("speaker", profile.CanonicalName), ("voice", profile.Voice), ("text", sanitizedText), ("textHash", TextHash(sanitizedText))));
            if (await playback.PlayLatestAsync(pcm, profile.VolumeMultiplier))
            {
                AppendLog(DiagnosticEvent.Create("play.completed", ("node", snapshot.NodeId), ("speaker", profile.CanonicalName), ("text", sanitizedText), ("textHash", TextHash(sanitizedText))));
            }
            else
            {
                AppendLog(DiagnosticEvent.Create("play.interrupted", ("node", snapshot.NodeId), ("speaker", profile.CanonicalName), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text))));
            }
        }
        catch (Exception exception)
        {
            // A generation failure was already logged by VoiceService. Only
            // report independent playback/device failures here.
            if (!exception.Message.StartsWith("Retry disabled for this app session:", StringComparison.Ordinal))
                AppendLog(exception.Message);
        }
    }
    private static string PlaybackKey(SpeakerProfile profile, string text) =>
        string.Concat(profile.CanonicalName, "\u001f", text);

    private void PollManualRecoveryInput(bool gameIsForeground)
    {
        bool eDown = (Native.GetAsyncKeyState(0x45) & 0x8000) != 0;
        bool ePressed = eDown && !eWasDown;
        eWasDown = eDown;
        if (ePressed && gameIsForeground)
        {
            eDialogueCheckDueUtc = DateTime.UtcNow.AddMilliseconds(80);
            AppendLog(DiagnosticEvent.Create("reader.input.e_pressed", ("checkDelayMilliseconds", 80)));
        }
        if (eDialogueCheckDueUtc is { } due && DateTime.UtcNow >= due)
        {
            eDialogueCheckDueUtc = null;
            reader.WarmUpDiscovery();
            AppendLog(DiagnosticEvent.Create("reader.input.e_checked", ("found", reader.HasValidWidget)));
        }

        bool rDown = (Native.GetAsyncKeyState(0x52) & 0x8000) != 0;
        bool activated = rDown && !rWasDown;
        rWasDown = rDown;
        if (!activated || !gameIsForeground) return;
        _ = ResetAndRecoverAsync();
    }

    private void UpdateActiveChoiceOptions(IReadOnlyList<string> options)
    {
        string signature = string.Join("\n", options);
        if (string.Equals(signature, activeChoiceOptionsSignature, StringComparison.Ordinal)) return;
        activeChoiceOptionsSignature = signature;
        activeChoiceOptions = options;
        selectedChoiceSignature = "";
        if (options.Count == 0) return;
        AppendLog(DiagnosticEvent.Create("choice.options.ready", ("options", options)));
        SpeakerProfile profile = speakers.Resolve("Alisa", options[0], null);
        foreach (string option in options.Where(option => !ShouldSkipChoiceNarration(option))) _ = PrefetchAsync(new SubtitleCandidate(-1, "Alisa", option, "Choice option.", true), profile);
    }

    private void LogMenuStateOnce(SubtitleSnapshot snapshot, bool transientPlaceholder)
    {
        string key = snapshot.NodeId + "|" + snapshot.Text + "|" + string.Join("\n", snapshot.ChoiceOptions);
        if (string.Equals(key, lastMenuDiagnosticKey, StringComparison.Ordinal)) return;
        lastMenuDiagnosticKey = key;
        AppendLog(DiagnosticEvent.Create("menu.possible_replies.detected", ("node", snapshot.NodeId), ("text", snapshot.Text), ("textHash", TextHash(snapshot.Text)), ("transientPlaceholder", transientPlaceholder), ("options", snapshot.ChoiceOptions)));
    }

    private void PollChoiceSelectionInput(bool gameIsForeground)
    {
        for (int index = 0; index < choiceNumberWasDown.Length; index++)
        {
            bool down = (Native.GetAsyncKeyState(0x31 + index) & 0x8000) != 0;
            bool pressed = down && !choiceNumberWasDown[index];
            choiceNumberWasDown[index] = down;
            if (!pressed || !gameIsForeground || index >= activeChoiceOptions.Count) continue;
            string option = activeChoiceOptions[index];
            string signature = activeChoiceOptionsSignature + "|" + index;
            if (string.Equals(signature, selectedChoiceSignature, StringComparison.Ordinal)) continue;
            selectedChoiceSignature = signature;
            choiceNarrationTask = NarrateSelectedChoiceAsync(index + 1, option);
        }
    }

    private async Task NarrateSelectedChoiceAsync(int number, string option)
    {
        if (ShouldSkipChoiceNarration(option))
        {
            AppendLog(DiagnosticEvent.Create("choice.selection.skipped", ("number", number), ("text", option), ("reason", IsLeaveChoice(option) ? "leave" : "action_marker")));
            return;
        }
        SpeakerProfile profile = speakers.Resolve("Alisa", option, null);
        AppendLog(DiagnosticEvent.Create("choice.selection.detected", ("source", "number_key"), ("number", number), ("speaker", profile.CanonicalName), ("text", option)));
        try
        {
            byte[]? pcm = await voice.EnsureAsync(profile, option, AudioRequestRole.Current, CancellationToken.None);
            if (pcm is null) return;
            // Some branches repeat the selected reply as Alisa's immediate
            // subtitle. Reserve only narrated choices; a skipped action must
            // never suppress the following subtitle.
            lastSpokenTextKey = profile.CanonicalName + "|" + NormalizeSpokenText(option);
            lastSpokenNode = -number;
            AppendLog(DiagnosticEvent.Create("choice.selection.play", ("number", number), ("text", option)));
            await playback.PlayLatestAsync(pcm, profile.VolumeMultiplier);
        }
        catch (Exception exception) { AppendLog(DiagnosticEvent.Create("choice.selection.failed", ("number", number), ("text", option), ("error", exception.Message))); }
    }

    private async Task ResetAndRecoverAsync()
    {
        SubtitleSnapshot? snapshot = latestSnapshot;
        playback.Stop();
        lastPlaybackKey = string.Empty;
        lastSpokenTextKey = null;
        lastSpokenNode = 0;
        CancelUnknownSpeakerChoice("manual_reset");
        bool deletedAudio = false;
        if (snapshot is not null && snapshot.Text.Any(char.IsLetterOrDigit) && !snapshot.IsChoiceMenu && !IsPossibleReplies(snapshot.Text))
        {
            SpeakerProfile profile = ResolveSubtitleProfile(snapshot.Speaker, snapshot.Text);
            deletedAudio = await voice.ResetAudioForRegenerationAsync(profile, snapshot.Text);
        }
        else
        {
            await voice.EndConversationAsync();
        }
        reader.ResetForManualRecovery();
        reader.RequestPriorityDiscovery();
        System.Media.SystemSounds.Beep.Play();
        AppendLog(DiagnosticEvent.Create("reader.recovery.requested", ("source", "r"), ("audioDeleted", deletedAudio), ("sessionReset", true)));
        await TickAsync();
    }

    private void RetryVisibleAudio()
    {
        SubtitleSnapshot? snapshot = latestSnapshot;
        if (snapshot is null) { AppendLog("Retry ignored: no active subtitle."); return; }
        if (SpeakerCatalog.IsQuestionOnlyPlaceholder(snapshot.Speaker) && !speakers.HasUnknownProfileMatch(snapshot.Speaker, snapshot.Text) && unknownSpeakerGender is null)
        {
            AppendLog("Retry ignored: choose the unknown speaker's gender first.");
            return;
        }

        SpeakerProfile currentProfile = ResolveSubtitleProfile(snapshot.Speaker, snapshot.Text);
        bool retried = voice.RetryFailed(currentProfile, snapshot.Text);
        SubtitleCandidate? retryNext = null;
        SpeakerProfile? retryNextProfile = null;
        if (snapshot.Next is { IsExact: true } next)
        {
            bool nextNeedsChoice = SpeakerCatalog.IsQuestionOnlyPlaceholder(next.Speaker) && !speakers.HasUnknownProfileMatch(next.Speaker, next.Text) && unknownSpeakerGender is null;
            if (!nextNeedsChoice)
            {
                SpeakerProfile nextProfile = ResolveSubtitleProfile(next.Speaker, next.Text);
                retried |= voice.RetryFailed(nextProfile, next.Text);
                retryNext = next;
                retryNextProfile = nextProfile;
            }
        }

        if (!retried) { AppendLog("Retry ignored: current and next lines have no failed request."); return; }
        AppendLog("Retry requested for the visible failed audio.");
        _ = PlayCurrentAsync(snapshot, currentProfile);
        if (retryNext is not null && retryNextProfile is not null) _ = PrefetchAsync(retryNext, retryNextProfile);
    }

    private MenuStrip CreateMenus()
    {
        MenuStrip menu = new();
        ToolStripMenuItem settingsMenu = new("Configure");
        ToolStripMenuItem voiceSettings = new("Voice and reader settings...");
        voiceSettings.Click += (_, _) => { using SettingsDialog dialog = new(settings, settingsStore); if (dialog.ShowDialog(this) == DialogResult.OK) { timer.Interval = 1; speakers.RebuildLookup(); AppendLog("Settings saved."); } };
        ToolStripMenuItem castSettings = new("Cast voice profiles...");
        castSettings.Click += (_, _) => { using CastDialog dialog = new(speakers, settingsStore, voice); dialog.ShowDialog(this); AppendLog("Cast profiles updated."); };
        settingsMenu.DropDownItems.Add(voiceSettings); settingsMenu.DropDownItems.Add(castSettings); menu.Items.Add(settingsMenu);
        MainMenuStrip = menu;
        return menu;
    }

    private void RegisterUnknownChoiceHotkeys()
    {
        if (!Native.RegisterHotKey(Handle, UnknownMaleHotkeyId, 0, VirtualKeyM)) AppendLog("Could not register global M unknown-speaker choice.");
        if (!Native.RegisterHotKey(Handle, UnknownFemaleHotkeyId, 0, VirtualKeyF)) AppendLog("Could not register global F unknown-speaker choice.");
    }
    private void UnregisterUnknownChoiceHotkeys()
    {
        Native.UnregisterHotKey(Handle, UnknownMaleHotkeyId);
        Native.UnregisterHotKey(Handle, UnknownFemaleHotkeyId);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam.ToInt32() == UnknownMaleHotkeyId) TryChooseUnknownSpeakerGender(SpeakerGender.Male, "global_hotkey");
        else if (m.Msg == WmHotkey && m.WParam.ToInt32() == UnknownFemaleHotkeyId) TryChooseUnknownSpeakerGender(SpeakerGender.Female, "global_hotkey");
        base.WndProc(ref m);
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
        if (!DiagnosticEvent.IsJsonObject(message)) message = DiagnosticEvent.Create("app.message", ("message", message));
        activityLog.Write(message);
        string? displayMessage = FormatLogForDisplay(message);
        if (displayMessage is not null) log.AppendText(displayMessage + Environment.NewLine);
        const int maximumUiCharacters = 20_000;
        if (log.TextLength > maximumUiCharacters) log.Text = log.Text[^maximumUiCharacters..];
        log.SelectionStart = log.TextLength;
        log.ScrollToCaret();
    }

    // Keep the file log lossless JSON while making the live console easy to scan.
    private static string? FormatLogForDisplay(string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            if (!document.RootElement.TryGetProperty("ts", out JsonElement timestamp) ||
                !DateTimeOffset.TryParse(timestamp.GetString(), out DateTimeOffset parsed)) return message;

            JsonElement root = document.RootElement;
            string eventName = root.TryGetProperty("event", out JsonElement eventValue) ? eventValue.GetString() ?? "event" : "event";
            string clock = parsed.LocalDateTime.ToString("HH:mm:ss.fff");
            if (eventName is "play.settle.begin" or "play.settle.end"
                or "subtitle.detected" or "play.queued" or "play.completed"
                or "reader.widget.invalid"
                or "reader.widget_cache.invalid" or "reader.candidate.state") return null;
            if (eventName == "app.message")
            {
                string appMessage = GetLogString(root, "message");
                if (appMessage.StartsWith("------ SANITIZED TEXT:", StringComparison.Ordinal)) return null;
                if (appMessage.StartsWith("Companion started. File log:", StringComparison.Ordinal)) appMessage = "Companion started.";
                return $"{clock} {appMessage}";
            }

            if (eventName is "audio.cache.hit" or "audio.generate.start" or "audio.ready")
            {
                string action = eventName switch
                {
                    "audio.cache.hit" => "Cache hit",
                    "audio.generate.start" => "Generating",
                    _ => "Generated"
                };
                string role = GetLogString(root, "role");
                string shortText = ShortLogText(GetLogString(root, "text"));
                string target = role == "prefetch" ? " next" : string.Empty;
                return string.IsNullOrWhiteSpace(shortText) ? $"{clock} {action}{target}." : $"{clock} {action}{target}: {shortText}";
            }

            if (eventName == "play.start")
            {
                string playingSpeaker = GetLogString(root, "speaker");
                string playingText = GetLogString(root, "text");
                return string.IsNullOrWhiteSpace(playingSpeaker) ? $"{clock} Playing: {playingText}" : $"{clock} Playing: {playingSpeaker}: {playingText}";
            }

            if (eventName == "play.suppressed")
            {
                string shortText = ShortLogText(GetLogString(root, "text"));
                return string.IsNullOrWhiteSpace(shortText) ? $"{clock} Playback skipped." : $"{clock} Playback skipped: {shortText}";
            }

            string label = eventName switch
            {
                "reader.attach.ok" => "Game attached",
                "reader.widget_cache.hit" => "Dialogue widget restored",
                "reader.widget_cache.saved" => "Dialogue widget saved",
                "reader.discovery.found" => "Dialogue widget found",
                "conversation.start" => "Conversation started",
                "conversation.end" => "Conversation ended",
                "play.interrupted" => "Playback interrupted",
                "audio.request.failed" => "Audio request failed",
                _ => eventName.Replace('.', ' ')
            };
            string speaker = GetLogString(root, "speaker");
            string text = GetLogString(root, "text");
            if (eventName == "conversation.start") return $"{clock} {label}.";
            if (!string.IsNullOrWhiteSpace(text))
                return string.IsNullOrWhiteSpace(speaker) ? $"{clock} {label}: {text}" : $"{clock} {label}: {speaker}: {text}";
            return $"{clock} {label}";
        }
        catch (JsonException) { return message; }
    }

    private static string GetLogString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string ShortLogText(string text)
    {
        string normalized = string.Join(' ', text.Trim().Trim('\"', '\u201c', '\u201d').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return string.Empty;
        string[] words = normalized.Split(' ');
        return words.Length <= 7 ? normalized : string.Join(' ', words.Take(7)) + "...";
    }

    private static TextBox TextFor() => new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 8), BackColor = SystemColors.Window, Margin = new Padding(0, 0, 7, 0) };
    private Control ApiKeyPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 8, RowCount = 1, Margin = new Padding(0, 3, 0, 3) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        apiKeyButton.Click += async (_, _) =>
        {
            if (!apiKeyEditing)
            {
                SetApiKeyEditing(true);
                return;
            }
            try
            {
                settings.OpenAi.ApiKey = apiKeyText.Text.Trim();
                ApiKeyStore.Save(settings.OpenAi.ApiKey);
                await voice.EndConversationAsync();
                AppendLog("API key saved.");
                SetApiKeyEditing(false);
            }
            catch (Exception exception) { AppendLog("Could not save API key: " + exception.Message); }
        };
        subtitleStartDelay.Value = Math.Clamp(settings.Reader.SubtitleStartDelayMilliseconds, (int)subtitleStartDelay.Minimum, (int)subtitleStartDelay.Maximum);
        subtitleStartDelayButton.Click += (_, _) =>
        {
            try
            {
                int delayMilliseconds = Decimal.ToInt32(subtitleStartDelay.Value);
                settings.Reader.SubtitleStartDelayMilliseconds = delayMilliseconds;
                settingsStore.SaveSubtitleStartDelay(delayMilliseconds);
                AppendLog($"Subtitle start delay saved: {delayMilliseconds} ms.");
            }
            catch (Exception exception) { AppendLog("Could not save subtitle start delay: " + exception.Message); }
        };
        panel.Controls.Add(new Label { Text = "Open API Key:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 7, 0) }, 0, 0);
        panel.Controls.Add(apiKeyText, 1, 0);
        panel.Controls.Add(apiKeyButton, 2, 0);
        panel.Controls.Add(new Label { Text = "Start speaking delay:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(18, 0, 7, 0) }, 3, 0);
        panel.Controls.Add(subtitleStartDelay, 4, 0);
        panel.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(7, 0, 0, 0) }, 5, 0);
        panel.Controls.Add(subtitleStartDelayButton, 6, 0);
        SetApiKeyEditing(string.IsNullOrWhiteSpace(settings.OpenAi.ApiKey));
        return panel;
    }

    private void SetApiKeyEditing(bool editing)
    {
        apiKeyEditing = editing;
        apiKeyText.Enabled = editing;
        apiKeyText.Text = editing ? settings.OpenAi.ApiKey : ObscureApiKey(settings.OpenAi.ApiKey);
        apiKeyButton.Text = editing ? "Save" : "Edit";
        if (editing)
        {
            apiKeyText.Focus();
            apiKeyText.SelectionStart = apiKeyText.TextLength;
        }
    }

    private static string ObscureApiKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        if (key.Length <= 12) return "...";
        return key[..7] + "..." + key[^5..];
    }
    private static Control SubtitlePanel(string label, TextBox text)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 7, 0) };
        // AutoSize is essential at high Windows DPI: a fixed logical height
        // can clip the label before the subtitle text box begins.
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = new Font("Segoe UI", 9), Margin = new Padding(0) }, 0, 0);
        text.Margin = new Padding(0);
        panel.Controls.Add(text, 0, 1);
        return panel;
    }
    private static string SubtitlePreview(string text)
    {
        string normalized = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 100 ? '"' + normalized + '"' : '"' + normalized[..97] + "...\"";
    }
    private static string TextHash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    private static bool IsPossibleReplies(string text) => text.TrimStart().StartsWith("Possible replies:", StringComparison.OrdinalIgnoreCase);
    private static bool IsLeaveChoice(string text) => string.Equals(NormalizeSpokenText(text), "leave", StringComparison.Ordinal);
    private static bool ShouldSkipChoiceNarration(string text) => IsLeaveChoice(text) || text.Any(character => character is '*' or '★' or '☆' or '✦' or '✧' or '✩' or '✪' or '✫' or '✬' or '✭' or '✮' or '✯' or '✰');
    // Slate briefly exposes each choice label as "a" before its rich text and
    // speaker fields settle. It is never a line that should be narrated.
    private static bool IsTransientChoicePlaceholder(string text)
    {
        string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length >= 2 && lines.All(line => string.Equals(line, "a", StringComparison.OrdinalIgnoreCase));
    }
    private static string NormalizeSpokenText(string text)
    {
        var normalized = new System.Text.StringBuilder();
        bool previousSpace = false;
        foreach (char character in text.Normalize(System.Text.NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(character)) { normalized.Append(char.ToLowerInvariant(character)); previousSpace = false; }
            else if (char.IsWhiteSpace(character) && normalized.Length > 0 && !previousSpace) { normalized.Append(' '); previousSpace = true; }
        }
        return normalized.ToString().Trim();
    }
}
