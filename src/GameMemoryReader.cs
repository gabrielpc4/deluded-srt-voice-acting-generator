using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal sealed record SubtitleCandidate(int NodeId, string Speaker, string Text, string Detail, bool IsExact);
internal sealed record SubtitleSnapshot(int NodeId, string Speaker, string Text, SubtitleCandidate? Next, string Status, bool IsChoiceMenu, IReadOnlyList<string> ChoiceOptions);
internal sealed record WidgetAddressCache(long TextBlock, long SpeakerBlock, long Owner);

internal sealed class GameMemoryReader : IDisposable
{
    // Deluded 0.5.0, SRTE-Win64-Shipping.exe. Re-validate these per game build.
    private const long ObjArray = 0x5BD4368, NamePool = 0x5DA5E80, STextVtable = 0x4F3FA28;
    private const long GenArg = 0x4EB6AD8, GenBase = 0x4EB30A0, Localized = 0x4EB30F0, StringTable = 0x4EB3140;
    private IntPtr handle; private long imageBase; private long textBlock, speakerBlock, owner; private readonly string processName;
    private Process? process; private IntPtr gameWindowHandle; private DateTime nextProcessRefreshUtc;
    private long discoveryChunks;
    private int discoveryCount, discoveryFrontIndex, discoveryBackIndex;
    private const int PriorityObjectsPerPoll = 1024;
    private int discoveryBudget = 32;
    private bool discoveryRequested;
    private bool fullFallbackRequested;
    private bool widgetValid;
    private int textNameMatches, textClassMatches, slateMatches, ownerMatches, nextProgressAt;
    private string lastDiagnostic = "";
    private string lastChoiceTrace = "";
    private int discoveryReadFailures, firstDiscoveryReadFailureWin32;
    private long firstDiscoveryReadFailureAddress;
    private readonly Dictionary<int, byte[]> discoveryItemChunks = new();
    private readonly Dictionary<long, bool> textBlockClassCache = new();
    private readonly Dictionary<uint, string> fNameCache = new();
    private readonly Dictionary<int, string> candidateStates = new();
    private readonly string widgetCachePath;
    private bool cachedWidgetWasInvalid;
    private int discoveryScannedCount;
    private bool discoveryTakeFront;
    private WidgetAddressCache? persistedWidgetCache;
    private bool persistedWidgetCacheLoaded;
    // Object names are stable enough for the lifetime of a process/load. An
    // inventory turns normal detection into a handful of validations instead
    // of walking every UObject entry on the UI timer.
    private readonly HashSet<int> dialogueTextCandidateIndices = new();
    private readonly HashSet<int> replyTextCandidateIndices = new();
    private Task<ObjectInventoryResult>? objectInventoryTask;
    private int inventoryProcessId;
    // A complete inventory takes a noticeable amount of process-memory I/O.
    // When the dialogue widget is absent, keep the cadence tied to the start
    // of the previous inventory, rather than waiting its duration plus a delay.
    private long nextObjectInventoryStartTimestamp;
    private bool replyInventoryComplete;
    private string lastReplyTextSignature = "";
    public event EventHandler<string>? Diagnostic;
    public GameMemoryReader(string processName)
    {
        this.processName = Path.GetFileNameWithoutExtension(processName);
        widgetCachePath = Path.Combine(FindCacheDirectory(), "reader-widget-address.json");
    }
    public void Dispose() { if (handle != IntPtr.Zero) Native.CloseHandle(handle); handle = IntPtr.Zero; process?.Dispose(); process = null; gameWindowHandle = IntPtr.Zero; }
    public bool IsGameWindowForeground()
    {
        RefreshProcessIfNeeded();
        return gameWindowHandle != IntPtr.Zero && Native.GetForegroundWindow() == gameWindowHandle;
    }
    public void RequestPriorityDiscovery()
    {
        if (!EnsureAttached()) return;
        UpdateObjectInventory();
        fullFallbackRequested = true;
        // Let a live inventory finish before paying for the explicit fallback
        // walk. The pending request is retained, so one R press is enough.
        if (objectInventoryTask is not null)
        {
            Report("reader.discovery.waiting_for_inventory", ("candidateCount", dialogueTextCandidateIndices.Count));
            return;
        }
        if (discoveryRequested)
        {
            discoveryBudget = Math.Max(discoveryBudget, PriorityObjectsPerPoll);
            fullFallbackRequested = false;
            Report("reader.discovery.priority_retained", ("objectsPerPoll", discoveryBudget));
            return;
        }
        BeginExplicitFallbackIfRequested();
    }
    /// <summary>
    /// Starts a clean manual recovery without deleting the persisted pointer.
    /// The next read reattaches, validates the pointer again, and rebuilds the
    /// object inventory if necessary.
    /// </summary>
    public void ResetForManualRecovery()
    {
        if (handle != IntPtr.Zero) Native.CloseHandle(handle);
        handle = IntPtr.Zero;
        textBlock = speakerBlock = owner = 0;
        widgetValid = false;
        discoveryRequested = false;
        fullFallbackRequested = false;
        ResetDiscovery();
        dialogueTextCandidateIndices.Clear();
        replyTextCandidateIndices.Clear();
        replyInventoryComplete = false;
        inventoryProcessId = 0;
        textBlockClassCache.Clear();
        fNameCache.Clear();
        persistedWidgetCache = null;
        persistedWidgetCacheLoaded = false;
        cachedWidgetWasInvalid = false;
        nextProcessRefreshUtc = DateTime.MinValue;
        Report("reader.recovery.reset");
    }
    public bool HasValidWidget => widgetValid && textBlock != 0;
    /// <summary>Attaches and discovers the widget without reading dialogue or triggering narration.</summary>
    public void WarmUpDiscovery()
    {
        if (!EnsureAttached()) return;
        UpdateObjectInventory();
        RevalidateCachedWidgetIfDue();
        if (textBlock == 0) TryKnownDialogueCandidates();
        BeginExplicitFallbackIfRequested();
        if (textBlock == 0 && discoveryRequested) DiscoverSlice();
    }
    public SubtitleSnapshot? TryRead()
    {
        if (!EnsureAttached()) return null;
        UpdateObjectInventory();
        RevalidateCachedWidgetIfDue();
        if (textBlock == 0) TryKnownDialogueCandidates();
        BeginExplicitFallbackIfRequested();
        if (textBlock == 0 && discoveryRequested) DiscoverSlice();
        if (textBlock == 0) return null;
        string? text = ReadTextBlock(textBlock), speaker = ReadTextBlock(speakerBlock) ?? "";
        if (text is null) { Report("reader.widget.invalid", ("reason", "text_read_failed"), ("textBlock", $"0x{textBlock:x}")); textBlock = speakerBlock = owner = 0; widgetValid = false; ResetDiscovery(); discoveryRequested = false; return null; }
        widgetValid = true;
        long dialogue = ReadPtr(owner + 0x310); int id = ReadI32(owner + 0x3F8);
        List<Node>? nodes = dialogue == 0 ? null : Nodes(dialogue);
        Node? activeNode = nodes?.FirstOrDefault(node => node.Id == id);
        IReadOnlyList<string> choiceOptions = ReadReplyOptions();
        bool isChoiceMenu = activeNode?.Type == 8 || choiceOptions.Count > 0 || LooksLikeChoiceMenu(text);
        TraceChoiceMenu(activeNode, nodes, text, speaker);
        SubtitleCandidate? next = dialogue == 0 ? null : Predict(dialogue, id, text, speaker);
        return new(id, speaker, text, next, next?.Detail ?? "Dialogue state unavailable.", isChoiceMenu, choiceOptions);
    }

    // The game represents an option screen as type 8. Record its direct
    // graph links once so we can verify the exact choice-to-branch mapping
    // before narrating selections.
    private void TraceChoiceMenu(Node? activeNode, List<Node>? nodes, string displayText, string speaker)
    {
        if (activeNode?.Type != 8 || nodes is null) { lastChoiceTrace = ""; return; }
        Node?[] linked = activeNode.Links.Select(id => nodes.FirstOrDefault(node => node.Id == id)).ToArray();
        string signature = activeNode.Id + "|" + string.Join('|', linked.Select(node => node?.Id + ":" + node?.Type + ":" + node?.Text));
        if (string.Equals(signature, lastChoiceTrace, StringComparison.Ordinal)) return;
        lastChoiceTrace = signature;
        Report("reader.choice_menu.active",
            ("node", activeNode.Id), ("speaker", speaker), ("displayText", displayText),
            ("links", linked.Select(node => new { node = node?.Id, type = node?.Type, text = node?.Text }).ToArray()));
    }
    private bool EnsureAttached()
    {
        try
        {
            if (handle != IntPtr.Zero && ReadPtr(imageBase + ObjArray) != 0) return true;
            if (handle != IntPtr.Zero) Report("reader.attach.lost", ("imageBase", $"0x{imageBase:x}"), ("objectArrayAddress", $"0x{imageBase + ObjArray:x}"));
            if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            bool requestWasPending = discoveryRequested;
            handle = IntPtr.Zero; textBlock = speakerBlock = owner = 0; ResetDiscovery(); ResetObjectInventory(); widgetValid = false; discoveryRequested = requestWasPending;
            RefreshProcessIfNeeded(force: true);
            if (process is null) { Report("reader.attach.failed", ("reason", "process_not_found"), ("process", processName)); return false; }
            imageBase = process.MainModule?.BaseAddress.ToInt64() ?? 0;
            handle = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, process.Id);
            textBlockClassCache.Clear();
            fNameCache.Clear();
            bool attached = handle != IntPtr.Zero && imageBase != 0;
            Report(attached ? "reader.attach.ok" : "reader.attach.failed", ("processId", process.Id), ("imageBase", $"0x{imageBase:x}"), ("handle", $"0x{handle.ToInt64():x}"));
            if (attached) { RestoreCachedWidget(); StartObjectInventoryIfNeeded(); }
            return attached;
        }
        catch (Exception exception)
        {
            Report("reader.attach.failed", ("reason", "exception"), ("error", exception.Message));
            if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            handle = IntPtr.Zero; textBlock = speakerBlock = owner = 0; ResetDiscovery(); ResetObjectInventory(); widgetValid = false;
            return false;
        }
    }
    private void RefreshProcessIfNeeded(bool force = false)
    {
        if (!force && DateTime.UtcNow < nextProcessRefreshUtc && process is not null && !process.HasExited) return;
        nextProcessRefreshUtc = DateTime.UtcNow.AddSeconds(1);
        Process[] candidates = Process.GetProcessesByName(processName);
        if (candidates.Length > 1)
        {
            Process newest = candidates.OrderByDescending(candidate => candidate.StartTime).First();
            foreach (Process older in candidates.Where(candidate => candidate.Id != newest.Id))
            {
                try
                {
                    DateTime started = older.StartTime;
                    older.Kill();
                    older.WaitForExit(2_000);
                    Report("reader.process.older_terminated", ("processId", older.Id), ("started", started.ToUniversalTime().ToString("O")), ("keptProcessId", newest.Id));
                }
                catch (Exception exception) { Report("reader.process.older_termination_failed", ("processId", older.Id), ("error", exception.Message)); }
                finally { older.Dispose(); }
            }
            if (process is null || process.Id != newest.Id)
            {
                if (handle != IntPtr.Zero) Native.CloseHandle(handle);
                handle = IntPtr.Zero; textBlock = speakerBlock = owner = 0; widgetValid = false;
                ResetDiscovery(); ResetObjectInventory();
                process?.Dispose();
                process = newest;
            }
            else newest.Dispose();
        }
        else
        {
            Process? discovered = candidates.FirstOrDefault();
            if (process is null || process.HasExited || discovered is null || process.Id != discovered.Id)
            {
                process?.Dispose();
                process = discovered;
            }
            else discovered?.Dispose();
        }
        gameWindowHandle = process?.MainWindowHandle ?? IntPtr.Zero;
    }
    private bool DiscoverSlice()
    {
        if (discoveryChunks == 0)
        {
            discoveryChunks = ReadPtr(imageBase + ObjArray + 0x10);
            discoveryCount = ReadI32(imageBase + ObjArray + 0x24);
            discoveryFrontIndex = 0; discoveryBackIndex = discoveryCount - 1;
            if (discoveryChunks == 0 || discoveryCount <= 0 || discoveryCount > 10_000_000) { Report("reader.discovery.failed", ("reason", "invalid_object_array"), ("chunks", $"0x{discoveryChunks:x}"), ("count", discoveryCount)); ResetDiscovery(); discoveryRequested = false; return false; }
            nextProgressAt = 16_384;
            discoveryScannedCount = 0;
            discoveryTakeFront = false;
            Report("reader.discovery.started", ("count", discoveryCount), ("chunks", $"0x{discoveryChunks:x}"), ("objectsPerPoll", discoveryBudget), ("strategy", "bidirectional_full"));
        }
        // R explicitly requested the one remaining expensive recovery path.
        // Alternate ends so newly allocated and older persistent widgets get
        // equal priority without maintaining a second search state machine.
        int scanned = 0;
        while (scanned < discoveryBudget && discoveryFrontIndex <= discoveryBackIndex)
        {
            bool fromFront = discoveryTakeFront;
            discoveryTakeFront = !discoveryTakeFront;
            int objectIndex = fromFront ? discoveryFrontIndex++ : discoveryBackIndex--;
            scanned++;
            discoveryScannedCount++;
            if (TryCandidate(objectIndex, fromFront ? "front" : "back")) return true;
        }
        if (discoveryScannedCount >= nextProgressAt) { Report("reader.discovery.progress", ("scanned", discoveryScannedCount), ("count", discoveryCount), ("frontIndex", discoveryFrontIndex), ("backIndex", discoveryBackIndex), ("textNameMatches", textNameMatches), ("textClassMatches", textClassMatches), ("slateMatches", slateMatches), ("ownerMatches", ownerMatches), ("readFailures", discoveryReadFailures)); nextProgressAt += 16_384; }
        if (discoveryFrontIndex > discoveryBackIndex) { Report("reader.discovery.exhausted", ("count", discoveryCount), ("textNameMatches", textNameMatches), ("textClassMatches", textClassMatches), ("slateMatches", slateMatches), ("ownerMatches", ownerMatches), ("readFailures", discoveryReadFailures), ("firstReadFailureAddress", firstDiscoveryReadFailureAddress == 0 ? null : $"0x{firstDiscoveryReadFailureAddress:x}"), ("firstReadFailureWin32", firstDiscoveryReadFailureWin32 == 0 ? null : firstDiscoveryReadFailureWin32)); ResetDiscovery(); discoveryRequested = false; }
        return false;
    }
    private bool TryCandidate(int objectIndex, string direction)
    {
        return TryCandidateObject(objectIndex, ObjectAt(objectIndex), direction);
    }
    private bool TryKnownDialogueCandidates()
    {
        if (dialogueTextCandidateIndices.Count == 0) return false;
        long chunks = ReadPtr(imageBase + ObjArray + 0x10);
        int count = ReadI32(imageBase + ObjArray + 0x24);
        if (chunks == 0 || count <= 0) return false;
        foreach (int index in dialogueTextCandidateIndices.ToArray())
        {
            if (index >= count) { dialogueTextCandidateIndices.Remove(index); continue; }
            if (TryCandidateObject(index, ObjectAt(chunks, count, index), "inventory")) return true;
        }
        return false;
    }
    private bool TryCandidateObject(int objectIndex, long obj, string direction)
    {
        bool reportState = direction == "inventory";
        if (obj == 0) { ReportCandidateState(objectIndex, "object_null", reportState); return false; }
        long classObject = ReadPtr(obj + 0x10);
        if (classObject == 0) { ReportCandidateState(objectIndex, "class_null", reportState); return false; }
        if (!textBlockClassCache.TryGetValue(classObject, out bool isTextBlock))
        {
            isTextBlock = Name(classObject) == "TextBlock";
            textBlockClassCache[classObject] = isTextBlock;
        }
        if (!isTextBlock) { ReportCandidateState(objectIndex, "class_not_text_block", reportState); return false; }
        textClassMatches++;
        if (Name(obj) != "TB_Dialogue_Text") { ReportCandidateState(objectIndex, "name_changed", reportState); return false; }
        textNameMatches++;
        long slate = ReadPtr(obj + 0x298); if (slate == 0 || ReadPtr(slate) != imageBase + STextVtable) { ReportCandidateState(objectIndex, "slate_not_live", reportState); return false; }
        slateMatches++;
        long tree = ReadPtr(obj + 0x20), candidateOwner = tree == 0 ? 0 : ReadPtr(tree + 0x20); if (candidateOwner == 0 || !Name(candidateOwner).StartsWith("W_FLIXXX_DIALOGUE_G_SHPAKUS_C", StringComparison.Ordinal) || Name(ReadPtr(candidateOwner + 0x10)) != "W_FLIXXX_DIALOGUE_G_SHPAKUS_C") { ReportCandidateState(objectIndex, "owner_not_dialogue_widget", reportState); return false; }
        ownerMatches++;
        long candidateSpeaker = ReadPtr(candidateOwner + 0x2F0); if (candidateSpeaker == 0 || Name(candidateSpeaker) != "TB_InterlocutorName_Text") { ReportCandidateState(objectIndex, "speaker_not_found", reportState); return false; }
        candidateStates.Remove(objectIndex);
        textBlock = obj; speakerBlock = candidateSpeaker; owner = candidateOwner; SaveCachedWidget(); Report("reader.discovery.found", ("objectIndex", objectIndex), ("direction", direction), ("textBlock", $"0x{textBlock:x}"), ("owner", $"0x{owner:x}")); ResetDiscovery(); widgetValid = true; discoveryRequested = false; fullFallbackRequested = false; return true;
    }
    private void ReportCandidateState(int objectIndex, string state, bool enabled)
    {
        if (!enabled || (candidateStates.TryGetValue(objectIndex, out string? previous) && previous == state)) return;
        candidateStates[objectIndex] = state;
        Report("reader.candidate.state", ("objectIndex", objectIndex), ("state", state));
    }
    private long ObjectAt(int objectIndex)
    {
        int chunkIndex = objectIndex / 0x10000;
        if (!discoveryItemChunks.TryGetValue(chunkIndex, out byte[]? items))
        {
            long chunk = ReadPtr(discoveryChunks + chunkIndex * 8L);
            int itemsInChunk = Math.Min(0x10000, discoveryCount - chunkIndex * 0x10000);
            items = chunk == 0 ? [] : ReadBytes(chunk, itemsInChunk * 0x18);
            discoveryItemChunks[chunkIndex] = items;
        }
        int offset = (objectIndex % 0x10000) * 0x18;
        return items.Length >= offset + 8 ? BitConverter.ToInt64(items, offset) : 0;
    }
    private long ObjectAt(long chunks, int count, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= count) return 0;
        long chunk = ReadPtr(chunks + objectIndex / 0x10000 * 8L);
        return chunk == 0 ? 0 : ReadPtr(chunk + objectIndex % 0x10000 * 0x18L);
    }
    private void BeginDiscovery(int objectsPerPoll, string eventName)
    {
        textBlock = speakerBlock = owner = 0;
        ResetDiscovery();
        widgetValid = false;
        discoveryRequested = true;
        discoveryBudget = objectsPerPoll;
        Report(eventName, ("objectsPerPoll", discoveryBudget));
    }
    private void BeginExplicitFallbackIfRequested()
    {
        if (!fullFallbackRequested || HasValidWidget || discoveryRequested || objectInventoryTask is not null) return;
        fullFallbackRequested = false;
        BeginDiscovery(PriorityObjectsPerPoll, "reader.discovery.priority_requested");
    }
    private void ResetObjectInventory()
    {
        dialogueTextCandidateIndices.Clear(); replyTextCandidateIndices.Clear(); candidateStates.Clear();
        objectInventoryTask = null; inventoryProcessId = 0;
        nextObjectInventoryStartTimestamp = 0;
        replyInventoryComplete = false; lastReplyTextSignature = "";
    }
    private void StartObjectInventoryIfNeeded()
    {
        if (objectInventoryTask is not null || (HasValidWidget && replyInventoryComplete) || process is null || handle == IntPtr.Zero) return;
        // A live widget can need its first reply-widget inventory immediately.
        // Otherwise, poll the expensive full inventory at a fixed one-second
        // cadence from each start, independent of how long the prior pass ran.
        long now = Stopwatch.GetTimestamp();
        if (!HasValidWidget && now < nextObjectInventoryStartTimestamp) return;
        int currentCount = ReadI32(imageBase + ObjArray + 0x24);
        if (currentCount <= 0 || currentCount > 10_000_000) return;
        inventoryProcessId = process.Id;
        int processId = process.Id; long baseAddress = imageBase;
        if (!HasValidWidget) nextObjectInventoryStartTimestamp = now + Stopwatch.Frequency;
        objectInventoryTask = Task.Run(() => ObjectInventoryResult.Scan(processId, baseAddress, 0, currentCount));
    }
    private void UpdateObjectInventory()
    {
        bool completedNow = false;
        if (objectInventoryTask is { IsCompleted: true } completed)
        {
            completedNow = true;
            objectInventoryTask = null;
            try
            {
                ObjectInventoryResult result = completed.GetAwaiter().GetResult();
                if (result.ProcessId == inventoryProcessId && result.ImageBase == imageBase)
                {
                    dialogueTextCandidateIndices.Clear();
                    foreach (int index in result.DialogueTextIndices) dialogueTextCandidateIndices.Add(index);
                    replyTextCandidateIndices.Clear();
                    foreach (int index in result.ReplyTextIndices) replyTextCandidateIndices.Add(index);
                    replyInventoryComplete = true;
                }
            }
            catch (Exception exception) { Report("reader.inventory.failed", ("error", exception.Message)); }
        }
        // Give the newly catalogued candidates one reader tick to validate
        // before starting another full pass.
        if (!completedNow) StartObjectInventoryIfNeeded();
    }

    private IReadOnlyList<string> ReadReplyOptions()
    {
        if (replyTextCandidateIndices.Count == 0) { lastReplyTextSignature = ""; return []; }
        var entries = new List<(int Number, int Index, string Name, string Text)>();
        long chunks = ReadPtr(imageBase + ObjArray + 0x10);
        int count = ReadI32(imageBase + ObjArray + 0x24);
        if (chunks == 0 || count <= 0) return [];
        foreach (int index in replyTextCandidateIndices)
        {
            long block = ObjectAt(chunks, count, index);
            if (block == 0) continue;
            string? value = ReadTextBlock(block);
            if (string.IsNullOrWhiteSpace(value)) continue;
            Match match = Regex.Match(value, @"^\s*(\d+)\.\s*(.+?)\s*$", RegexOptions.Singleline);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int number)) continue;
            entries.Add((number, index, Name(block), match.Groups[2].Value));
        }
        var ordered = entries.OrderBy(entry => entry.Number).ToArray();
        string signature = JsonSerializer.Serialize(ordered);
        if (ordered.Length == 0) { lastReplyTextSignature = ""; return []; }
        if (string.Equals(signature, lastReplyTextSignature, StringComparison.Ordinal)) return ordered.Select(entry => entry.Text).ToArray();
        lastReplyTextSignature = signature;
        Report("reader.reply_widget.active", ("entries", ordered.Select(entry => new { number = entry.Number, objectIndex = entry.Index, name = entry.Name, text = entry.Text }).ToArray()));
        return ordered.Select(entry => entry.Text).ToArray();
    }
    private void RevalidateCachedWidgetIfDue()
    {
        if (HasValidWidget) return;
        RestoreCachedWidget();
    }
    private bool RestoreCachedWidget()
    {
        try
        {
            WidgetAddressCache? cached = LoadPersistedWidgetCache();
            if (cached is null || cached.TextBlock == 0 || cached.SpeakerBlock == 0 || cached.Owner == 0) return false;
            bool valid = Name(cached.TextBlock) == "TB_Dialogue_Text"
                && Name(ReadPtr(cached.TextBlock + 0x10)) == "TextBlock"
                && ReadTextBlock(cached.TextBlock) is not null
                && Name(cached.SpeakerBlock) == "TB_InterlocutorName_Text"
                && ReadTextBlock(cached.SpeakerBlock) is not null
                && Name(cached.Owner).StartsWith("W_FLIXXX_DIALOGUE_G_SHPAKUS_C", StringComparison.Ordinal)
                && ReadPtr(cached.Owner + 0x2F0) == cached.SpeakerBlock;
            if (!valid)
            {
                if (!cachedWidgetWasInvalid) Report("reader.widget_cache.invalid");
                cachedWidgetWasInvalid = true;
                return false;
            }
            textBlock = cached.TextBlock; speakerBlock = cached.SpeakerBlock; owner = cached.Owner;
            ResetDiscovery(); widgetValid = true; discoveryRequested = false;
            cachedWidgetWasInvalid = false;
            Report("reader.widget_cache.hit", ("textBlock", $"0x{textBlock:x}"), ("owner", $"0x{owner:x}"));
            return true;
        }
        catch (Exception exception)
        {
            if (!cachedWidgetWasInvalid) Report("reader.widget_cache.invalid", ("error", exception.Message));
            cachedWidgetWasInvalid = true;
            return false;
        }
    }
    private void SaveCachedWidget()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(widgetCachePath)!);
            string temporaryPath = widgetCachePath + ".tmp";
            persistedWidgetCache = new WidgetAddressCache(textBlock, speakerBlock, owner);
            persistedWidgetCacheLoaded = true;
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(persistedWidgetCache));
            File.Move(temporaryPath, widgetCachePath, true);
            cachedWidgetWasInvalid = false;
            Report("reader.widget_cache.saved", ("textBlock", $"0x{textBlock:x}"));
        }
        catch (Exception exception) { Report("reader.widget_cache.save_failed", ("error", exception.Message)); }
    }
    private WidgetAddressCache? LoadPersistedWidgetCache()
    {
        if (persistedWidgetCacheLoaded) return persistedWidgetCache;
        persistedWidgetCacheLoaded = true;
        if (!File.Exists(widgetCachePath)) return null;
        persistedWidgetCache = JsonSerializer.Deserialize<WidgetAddressCache>(File.ReadAllText(widgetCachePath));
        return persistedWidgetCache;
    }
    private static string FindCacheDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeludedAIVoiceGeneration.csproj"))) return Path.Combine(directory.FullName, "cache");
            directory = directory.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "cache");
    }
    private void ResetDiscovery()
    {
        discoveryChunks = 0; discoveryCount = discoveryFrontIndex = 0; discoveryBackIndex = -1;
        discoveryScannedCount = 0;
        discoveryItemChunks.Clear();
        textNameMatches = textClassMatches = slateMatches = ownerMatches = nextProgressAt = discoveryReadFailures = firstDiscoveryReadFailureWin32 = 0;
        firstDiscoveryReadFailureAddress = 0;
    }
    private SubtitleCandidate? Predict(long dialogue, int currentId, string display, string currentSpeaker)
    {
        List<Node>? nodes = Nodes(dialogue); Node? current = nodes?.FirstOrDefault(n => n.Id == currentId); if (current is null || !Equivalent(display, current.Text)) return null;
        string interlocutor = FName(ReadU32(dialogue + 0x3C), ReadU32(dialogue + 0x40)); string protagonist = current.Type <= 2 && currentSpeaker.Length > 0 ? currentSpeaker : "Alisa";
        var found = new Dictionary<int, Node>(); var visited = new HashSet<int>(); bool dynamic = false;
        void Walk(int id, int depth) { if (depth > 32 || !visited.Add(id) || found.Count > 12) return; Node? n = nodes!.FirstOrDefault(x => x.Id == id); if (n is null) return; if (n.Type == 8) { dynamic = true; return; } if (n.Type is >= 1 and <= 3 && Meaningful(n.Text)) { found[n.Id] = n; return; } foreach (int link in n.Links) Walk(link, depth + 1); }
        foreach (int link in current.Links) Walk(link, 0);
        if (found.Count != 1) return new(-1, "", "", dynamic ? "Next dialogue is dynamic." : found.Count == 0 ? "End of dialogue." : "Branch unresolved; no audio prefetch.", false);
        Node next = found.Values.Single(); return new(next.Id, Speaker(next, protagonist, interlocutor), next.Text, "Exact meaningful next subtitle.", true);
    }
    private List<Node>? Nodes(long dialogue)
    {
        long data = ReadPtr(dialogue + 0x48); int count = ReadI32(dialogue + 0x50), max = ReadI32(dialogue + 0x54); if (data == 0 || count <= 0 || count > 10000 || max < count) return null; var r = new List<Node>(count);
        for (int i = 0; i < count; i++) { long a = data + i * 0xB8L, links = ReadPtr(a + 0x30); int n = ReadI32(a + 0x38); if (n < 0 || n > 1000) return null; int[] ids = n == 0 ? [] : ReadArray<int>(links, n); r.Add(new(ReadI32(a), ReadByte(a + 4), ReadText(a + 8) ?? "", ReadFString(a + 0x88) ?? "", ids)); } return r;
    }
    private static bool Meaningful(string s) => !s.TrimStart().StartsWith("Possible replies:", StringComparison.OrdinalIgnoreCase) && s.Any(char.IsLetterOrDigit);
    private static bool LooksLikeChoiceMenu(string text)
    {
        return Regex.IsMatch(text, @"(?m)^\s*\d+\.\s*[★☆✦✧*]\s*\S");
    }
    private static bool Equivalent(string a, string b) => string.Join(' ', a.Trim('"').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Equals(string.Join(' ', b.Trim('"').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
    private static string Speaker(Node n, string protagonist, string interlocutor) => n.Override.Length > 0 ? n.Override : n.Type <= 2 ? protagonist : n.Type == 3 ? (interlocutor.Length > 0 ? interlocutor : "NPC") : "";
    private string? ReadTextBlock(long block) { long slate = ReadPtr(block + 0x298); return slate == 0 || ReadPtr(slate) != imageBase + STextVtable ? null : ReadText(slate + 0x2B0); }
    private string? ReadText(long a) { long d = ReadPtr(a); if (d == 0) return ""; long rva = ReadPtr(d) - imageBase; if (rva == GenArg) return ReadFString(d + 0x48); if (rva == GenBase) return ReadFString(d + 0x38); if (rva == Localized) return ReadFString(ReadPtr(d + 8)); if (rva == StringTable) { long refs = ReadPtr(d + 0x18), entry = refs == 0 ? 0 : ReadPtr(refs + 0x38); return entry == 0 ? null : ReadFString(ReadPtr(entry + 0x20)); } return null; }
    private string? ReadFString(long a) { long chars = ReadPtr(a); int n = ReadI32(a + 8), max = ReadI32(a + 12); if (n == 0) return ""; if (chars == 0 || n < 1 || n > 32768 || max < n || max > 1048576) return null; byte[] b = ReadBytes(chars, n * 2); return b.Length == n * 2 && BitConverter.ToUInt16(b, b.Length - 2) == 0 ? Encoding.Unicode.GetString(b, 0, b.Length - 2) : null; }
    private string Name(long obj) => obj == 0 ? "" : FName(ReadU32(obj + 0x18), ReadU32(obj + 0x1C));
    private string FName(uint index, uint number)
    {
        if (!fNameCache.TryGetValue(index, out string? s))
        {
            uint block = index >> 16, offset = index & 0xffff; long b = block >= 8192 ? 0 : ReadPtr(imageBase + NamePool + 0x10 + block * 8L); if (b == 0) return "";
            long e = b + offset * 2L; ushort h = ReadU16(e); int n = h >> 6; if (n < 1 || n > 1023) return "";
            byte[] bytes = ReadBytes(e + 2, n * ((h & 1) != 0 ? 2 : 1)); s = (h & 1) != 0 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
            fNameCache[index] = s;
        }
        return number == 0 ? s : s + "_" + (number - 1);
    }
    private long ReadPtr(long a) => BitConverter.ToInt64(ReadBytes(a, 8)); private int ReadI32(long a) => BitConverter.ToInt32(ReadBytes(a, 4)); private uint ReadU32(long a) => BitConverter.ToUInt32(ReadBytes(a, 4)); private byte ReadByte(long a) => ReadBytes(a, 1)[0]; private ushort ReadU16(long a) => BitConverter.ToUInt16(ReadBytes(a, 2));
    private T[] ReadArray<T>(long a, int n) where T : unmanaged { if (a == 0) return []; byte[] b = ReadBytes(a, n * Marshal.SizeOf<T>()); return MemoryMarshal.Cast<byte, T>(b).ToArray(); }
    private byte[] ReadBytes(long a, int n)
    {
        byte[] b = new byte[n];
        bool complete = Native.ReadProcessMemory(handle, (IntPtr)a, b, n, out IntPtr got) && got.ToInt64() == n;
        if (!complete)
        {
            discoveryReadFailures++;
            if (firstDiscoveryReadFailureAddress == 0) { firstDiscoveryReadFailureAddress = a; firstDiscoveryReadFailureWin32 = Marshal.GetLastWin32Error(); }
        }
        return complete ? b : new byte[n];
    }
    private void Report(string eventName, params (string Key, object? Value)[] fields)
    {
        string signature = eventName + "|" + string.Join("|", fields.Select(field => field.Key + "=" + field.Value));
        if (signature == lastDiagnostic) return;
        lastDiagnostic = signature;
        Diagnostic?.Invoke(this, DiagnosticEvent.Create(eventName, fields));
    }
    private sealed record Node(int Id, byte Type, string Text, string Override, int[] Links);
    private sealed record ObjectInventoryResult(int ProcessId, long ImageBase, int StartIndex, int EndIndex, int[] DialogueTextIndices, int[] ReplyTextIndices, long ElapsedMilliseconds)
    {
        public static ObjectInventoryResult Scan(int processId, long imageBase, int startIndex, int endIndex)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            IntPtr inventoryHandle = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, processId);
            if (inventoryHandle == IntPtr.Zero) throw new InvalidOperationException("Unable to open the game for the object inventory.");
            try
            {
                long chunks = ReadPtr(inventoryHandle, imageBase + ObjArray + 0x10);
                if (chunks == 0) throw new InvalidOperationException("The UE object array is unavailable for the inventory.");
                var dialogue = new List<int>(); var textBlocks = new List<(int Index, long Object, long Tree)>(); var replyWidgets = new HashSet<long>(); var names = new Dictionary<uint, string>();
                for (int chunkIndex = startIndex / 0x10000; chunkIndex <= (endIndex - 1) / 0x10000; chunkIndex++)
                {
                    int chunkStart = Math.Max(startIndex, chunkIndex * 0x10000), chunkEnd = Math.Min(endIndex, (chunkIndex + 1) * 0x10000);
                    long chunk = ReadPtr(inventoryHandle, chunks + chunkIndex * 8L); if (chunk == 0) continue;
                    byte[] items = ReadBytes(inventoryHandle, chunk + (chunkStart - chunkIndex * 0x10000) * 0x18L, (chunkEnd - chunkStart) * 0x18);
                    for (int index = chunkStart; index < chunkEnd; index++)
                    {
                        int offset = (index - chunkStart) * 0x18;
                        if (items.Length < offset + 8) continue;
                        long obj = BitConverter.ToInt64(items, offset); if (obj == 0) continue;
                        string name = Name(inventoryHandle, imageBase, obj, names);
                        if (name == "TB_Dialogue_Text") dialogue.Add(index);
                        if (name.StartsWith("W_FLIXXX_DIALOGUE_G_REPLY_C", StringComparison.Ordinal)) replyWidgets.Add(obj);
                        long classObject = ReadPtr(inventoryHandle, obj + 0x10);
                        if (classObject != 0 && Name(inventoryHandle, imageBase, classObject, names) == "TextBlock")
                            textBlocks.Add((index, obj, ReadPtr(inventoryHandle, obj + 0x20)));
                    }
                }
                int[] replyText = textBlocks.Where(block => block.Tree != 0 && replyWidgets.Contains(ReadPtr(inventoryHandle, block.Tree + 0x20))).Select(block => block.Index).ToArray();
                return new(processId, imageBase, startIndex, endIndex, dialogue.ToArray(), replyText, stopwatch.ElapsedMilliseconds);
            }
            finally { Native.CloseHandle(inventoryHandle); }
        }
        private static long ReadPtr(IntPtr handle, long address) => BitConverter.ToInt64(ReadBytes(handle, address, 8));
        private static uint ReadU32(IntPtr handle, long address) => BitConverter.ToUInt32(ReadBytes(handle, address, 4));
        private static ushort ReadU16(IntPtr handle, long address) => BitConverter.ToUInt16(ReadBytes(handle, address, 2));
        private static byte[] ReadBytes(IntPtr handle, long address, int count)
        {
            byte[] buffer = new byte[count];
            return Native.ReadProcessMemory(handle, (IntPtr)address, buffer, count, out IntPtr received) && received.ToInt64() == count ? buffer : [];
        }
        private static string Name(IntPtr handle, long imageBase, long obj, Dictionary<uint, string> names)
        {
            uint index = ReadU32(handle, obj + 0x18), number = ReadU32(handle, obj + 0x1C);
            if (!names.TryGetValue(index, out string? value))
            {
                long block = ReadPtr(handle, imageBase + NamePool + 0x10 + (index >> 16) * 8L); if (block == 0) return "";
                long entry = block + (index & 0xffff) * 2L; ushort header = ReadU16(handle, entry); int length = header >> 6;
                if (length < 1 || length > 1023) return "";
                byte[] bytes = ReadBytes(handle, entry + 2, length * ((header & 1) != 0 ? 2 : 1));
                value = (header & 1) != 0 ? Encoding.Unicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
                names[index] = value;
            }
            return number == 0 ? value : value + "_" + (number - 1);
        }
    }
}
internal static class Native { internal const uint PROCESS_VM_READ = 0x10, PROCESS_QUERY_INFORMATION = 0x400; [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid); [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h); [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out IntPtr got); [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int virtualKey); [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key); [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id); }
