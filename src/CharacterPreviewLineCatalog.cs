/// <summary>
/// Built-in, cache-independent character samples for the Cast editor. These
/// are intentionally short real dialogue lines chosen from the local cache
/// corpus during development; previewing never needs an installed WAV cache.
/// </summary>
internal sealed class CharacterPreviewLineCatalog
{
    private static readonly Dictionary<string, string> Lines = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Alisa"] = "There's a man in there. Is he asleep? Oh God, what if he hears me? Maybe I can just get in the back and warm up a bit... and then wake him later?",
        ["Mom"] = "Heh, you need a hand? Poor thing, I know the forbidden fruit is sweet, but no. Go deal with what you need to deal with.",
        ["Dad"] = "The wealthiest place left is Serpengate, which gets more trade and all thanks to the port access and shady dealings, those corrupt bastards!",
        ["Stuart"] = "Do you know anything about horse powers? Just imagine 300 spartans pulling a big rock up the mountain... They would've been slower.",
        ["Luis"] = "Soo... The key is to not show fear or make eye contact. Don't show teeth when smiling. Or is this a dog thing?",
        ["Violet"] = "How could I not make it? You're my cuddlebug. You've been ignoring me lately, but unlike some people, I don't forget my friends.",
        ["Ava"] = "Did you take classes to learn how to play such a convincing fool? I couldn't do it that well. And I'm an actress!",
        ["Mr. Alan"] = "By the way, if your daddy wants to chase me away, this gazebo is technically not your property. I'm familiar with the house plan!",
        ["Adelina"] = "House with a voice is a house with a past. Listen, Alisa, what does it want to tell you?",
        ["Agatha"] = "We order it beforehand, so I was surprised about the box. I think it was something different. I bought treatment!",
        ["Bob"] = "Sorry, dolly, it's a business meeting, employees only. Next time I'll make you claw the walls.",
        ["Macy"] = "No! I mean, sort of... Listen, this one is bad. This one and the other green ones.",
        ["Dr. Victor"] = "Due to technical difficulties, the movie screening has been put on hold. The cinema apologizes for any inconvenience caused.",
        ["Victor"] = "Due to technical difficulties, the movie screening has been put on hold. The cinema apologizes for any inconvenience caused.",
        ["Jerome"] = "Al, you should meet with Thomas when the opportunity comes up. He's our local technician. Dude is head over heels with mechanisms.",
        ["Mia"] = "Are you sure this will fix me? They're getting worse, not better...",
        ["Maria"] = "Don't touch anything here! For a guy who doesn't know a damn thing about us, about magic, your desire to paw everything is simply suicidal!",
        ["Unknown girl"] = "Flatterer! Oh, yes! How can I resist your tenderness? Not fair!",
        ["Librarian Girl"] = "Wonderful! Then I can introduce myself too. My name is Cruella Castellier; you can just call me Cruella.",
        ["Jane"] = "You gonna keep acting like you don't want this when you're literally soaking?",
        ["Sophie"] = "And what, the cheer captain just has to go out with the most popular guy in school?",
        ["Chair"] = "What are you looking at? Can't you see I'm exhausted? I can't move my legs.",
        ["David"] = "Where do you live, girl?",
        ["Robert"] = "What are you trying to achieve?",
        ["Driver"] = "Who are you?",
        ["Librarian"] = "Wonderful! Then I can introduce myself too. My name is Cruella Castellier; you can just call me Cruella."
    };

    public string? Find(string speaker) => Lines.TryGetValue(speaker.Trim(), out string? line) ? line : null;
}
