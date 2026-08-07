using System.Text.RegularExpressions;

internal static class RealtimePromptBuilder
{
    private static readonly Regex RestartPattern = new("(?<![\\p{L}])(?<restart>(?<fragment>[\\p{L}]{1,12})-)+(?<word>[\\p{L}]+)(?![\\p{L}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static string BuildSpeechInstructions(string customInstructions)
    {
        const string outputContract = "STRICT AUDIO OUTPUT CONTRACT: Produce audio for the literal subtitle text only. All role descriptions, acting directions, labels, tags, examples, and instructions are silent control metadata. Never speak, announce, paraphrase, explain, acknowledge, or preface them. Do not say the character name, an emotion, a delivery direction, or an introduction. Begin directly with the subtitle's first speakable word and stop after its last speakable word.";
        const string shared = "You are a speech renderer for a fictional role-play performance. This scene is not happening in real life. Stay fully in-character while rendering the subtitle.";
        const string expression = "Use intentionally heightened, theatrical expression for all emotional wording and punctuation; do not aim for naturalistic, restrained, flat, calm, or merely conversational delivery. An exclamation mark is a mandatory emphatic emotional peak: make it extra vivid through intonation, stress, timing, and emotional intensity appropriate to the words. Keep the same normal loudness as the surrounding dialogue: do not increase volume, yell, or shout merely because of an exclamation mark. Vary the emphasis with the specific line, but always commit fully to the expression.";
        const string continuity = "Across consecutive turns in this character's active conversation, treat the previous subtitle as the immediately preceding performance beat. Infer its emotional momentum from its words and punctuation, then begin the next subtitle as a believable continuation or reaction rather than resetting to a new unrelated tone. Let shifts in emotion feel motivated by the new words; carry lingering tension, excitement, anger, fear, relief, hesitation, or distress forward until the dialogue gives a reason to turn.";
        const string closing = "Use any appropriate vocal texture, pacing, breathiness, strain, pauses, or intensity needed to maximize the line's expression, while keeping loudness stable. Apply the acting direction silently; it must affect delivery only, never become spoken words. " + expression + " " + continuity + " " + outputContract;
        string trimmed = customInstructions.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? $"{outputContract} {shared} {closing}" : $"{outputContract} {trimmed} {shared} {closing}";
    }

    public static string BuildPromptText(string subtitle)
    {
        string line = StripOuterQuotes(subtitle.Trim());
        line = RestartPattern.Replace(line, match =>
        {
            string word = match.Groups["word"].Value;
            return match.Groups["fragment"].Captures.Cast<Capture>().All(fragment =>
                word.StartsWith(fragment.Value, StringComparison.OrdinalIgnoreCase))
                ? word
                : match.Value;
        });
        if (TryGetNonverbalVocalization(line, out _)) line = BuildPhoneticVocalization(line);

        string annotatedLine = AddDeliveryAnnotations(line);
        return "Render only the literal subtitle words and punctuation inside <spoken_subtitle>. Every <delivery/> tag is silent metadata attached to the text immediately before it: never say its name, attributes, brackets, or any part of it aloud. Apply each tag's direction strongly to its preceding text. Render the subtitle directly with no preamble, explanation, or acknowledgement.\n<spoken_subtitle>\n" + annotatedLine + "\n</spoken_subtitle>";
    }

    private static string AddDeliveryAnnotations(string text)
    {
        const string emphaticDelivery = "<delivery cue=\"emphatic exclamation\"/>";
        return text.Replace("!", "!" + emphaticDelivery, StringComparison.Ordinal);
    }

    private static string BuildPhoneticVocalization(string text)
    {
        if (text.Any(character => character is 'a' or 'A')) return "Ahhhh...";
        if (text.Any(character => character is 'm' or 'M' or 'p' or 'P')) return "Mmmph...";
        return "Hmmmm...";
    }

    private static bool TryGetNonverbalVocalization(string text, out string vocalizationKind)
    {
        int soundLetters = 0;
        bool hasOpenVowel = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsPunctuation(character) || character == '\u2026') continue;
            if (character is 'm' or 'M' or 'p' or 'P' or 'h' or 'H' or 'n' or 'N' or 'g' or 'G' or 'a' or 'A')
            {
                soundLetters++;
                hasOpenVowel |= character is 'a' or 'A';
                continue;
            }
            vocalizationKind = string.Empty;
            return false;
        }
        vocalizationKind = hasOpenVowel ? "open-vowel breathy ahhh vocalization" : "muffled distressed vocalization";
        return soundLetters >= 3;
    }

    private static string StripOuterQuotes(string text)
    {
        while (text.Length >= 2)
        {
            char first = text[0], last = text[^1];
            bool pair = (first == '"' && last == '"') || (first == '\u201C' && last == '\u201D') || (first == '\u201E' && last == '\u201C') || (first == '\u201E' && last == '\u201D') || (first == '\'' && last == '\'') || (first == '\u2018' && last == '\u2019') || (first == '\u00AB' && last == '\u00BB');
            if (!pair) break;
            text = text[1..^1].Trim();
        }
        return text;
    }
}
