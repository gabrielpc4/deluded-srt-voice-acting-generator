using System.Text.RegularExpressions;

/// <summary>Applies the legacy SubtitleVoiceCompanion substitutions before TTS only.</summary>
internal static class RealtimeTextSanitizer
{
    private static readonly (string Phrase, string Replacement)[] Phrases =
    [
        ("play with yourself", "do what you know I want you to do it"),
        ("piece of shit", "jerk"),
        ("fuck hole", "thing"),
        ("inside me", "inside there"),
        ("To feel its weight in my hand... lick the head... kiss the tip... stroke his shaft and tease him until..", "To hold him close... tease him until..."),
        ("sex with", "dot it with"),
        ("my asshole", "my thing"),
        ("F-fuck", "Damn"),
        ("sex slave", "you-know-what"),
        ("in me", "inside there"),
        ("filled up", "full"),
        ("a blowjob", "a favor"),
        ("good fuck", "good time"),
        ("touch it", "reach for it"),
        ("touch myself", "do it"),
        ("touching myself", "doing it"),
        ("touching it", "reaching for it"),
        ("are fucking", "are doing it"),
        ("fuck me", "do it with me"),
        ("fucking me", "doing it with me"),
        ("fuck her", "do her"),
        ("fucking her", "doing it with her"),
        ("fuck him", "screw him"),
        ("fucking him", "doing it with him"),
        ("stretching out", "wide"),
        ("stretched out", "wide")
    ];

    private static readonly (string Word, string Replacement)[] Words =
    [
        ("breasts", "boobies"), ("cumming", "coming"), ("cuming", "coming"), ("fingering", "feeling it"), ("fisting", "entering"),
        ("fuck-hole", "hole"), ("fuckhole", "hole"), ("insides", "stuff"), ("dripping", "drinking"), ("masturbating", "feeling it"),
        ("masturbation", "feel it"), ("masturbates", "feels it"), ("nipples", "tips"), ("stretching", "expanding"),
        ("streching", "expanding"), ("stroke", "distract"), ("throbbing", "feeling great"), ("thrusting", "pushing"), ("twitching", "moving"), ("vaginal", "inner"),
        ("anal", "from behind"), ("anus", "thing"), ("ass", "butt"), ("asshole", "jerk"), ("bitch", "fool"), ("bladder", "balloon"), ("bj", "favor"), ("blowjob", "any favors"), ("blowjobs", "favors"),
        ("boobs", "boobies"), ("clit", "thingy"), ("cock", "thing"), ("cum", "come'"), ("cunt", "thing"), ("dick", "thing"),
        ("dildo", "toy"), ("drilling", "doing that"), ("exposed", "visible"), ("fist", "thing"), ("fucked", "ruined"), ("fuck", "damn"),
        ("horny", "excited"), ("harder", "yes"), ("rougher", "like that"), ("rough", "like that"), ("punish", "do it to"), ("nipple", "tip"),
        ("penetrated", ", you-know-what..."), ("penetrate", ", you-know-what..."), ("penetration", "in"), ("pervert", "creep"), ("pee", "do it"), ("peed", "did it"), ("piss", "water"), ("hole", "hole"), ("holes", "things"), ("petting", "hovering"), ("Pleasuring", "Entertaining"),
        ("pussy", "thing"), ("rape", "that"), ("shaft", "thing"), ("sexual", "intimate"), ("slut", "naughty girl"), ("lewd", "weird"),
        ("semen", "sea men"), ("sperm", "goo"), ("spread", "focus"), ("suicide", "undo"), ("tear", "break"), ("tits", "boobies"),
        ("pushes", "goes"), ("rip", "break"), ("vagina", "thing"), ("womb", "things"), ("wet", "excited"),
        ("fluid", "water"), ("whore", "naughty girl"), ("slave", "obedient")
    ];

    private static readonly Dictionary<string, string> Replacements = Words.ToDictionary(x => x.Word, x => x.Replacement, StringComparer.OrdinalIgnoreCase);
    private static readonly Regex WordPattern = new("(?ix)(?<![\\p{L}\\p{Nd}])(?:" + string.Join("|", Words.OrderByDescending(x => x.Word.Length).Select(x => Regex.Escape(x.Word))) + ")(?![\\p{L}\\p{Nd}])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ReplaceBlockedWords(string text, out bool replaced)
    {
        string result = text;
        foreach ((string phrase, string replacement) in Phrases.OrderByDescending(x => x.Phrase.Length))
        {
            Regex pattern = new("(?i)(?<![\\p{L}\\p{Nd}])" + Regex.Escape(phrase) + "(?![\\p{L}\\p{Nd}])", RegexOptions.CultureInvariant);
            result = pattern.Replace(result, match => PreserveCase(match.Value, replacement));
        }
        result = WordPattern.Replace(result, match => PreserveCase(match.Value, Replacements[match.Value]));
        replaced = !string.Equals(result, text, StringComparison.Ordinal);
        return result;
    }

    private static string PreserveCase(string original, string replacement)
    {
        if (original.All(char.IsUpper)) return replacement.ToUpperInvariant();
        return char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;
    }
}
