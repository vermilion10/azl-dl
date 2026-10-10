using System.Text.RegularExpressions;

namespace AzlDl.Core.Audio;

// Where a voice line's recording is: the bank (cv-10706, cv-10706-battle, ...) and the cue in it.
public sealed record VoiceCue(string Bank, string Cue);

// Finds and decodes ship voice lines. A ship group's lines are in three banks under cue/: cv-<group>.b (port lines),
// cv-<group>-battle.b and cv-<group>-gift.b. A line's cue is its character_voice resource_key, with "_<group_index>"
// appended for a skin other than the default one; when a skin has no recording of its own, the default skin's is used.
public sealed partial class VoiceLibrary(string assetFolder)
{
    private static readonly string[] BankSuffixes = ["", "-battle", "-gift"];

    private readonly Dictionary<string, AcbFile> banks = new(StringComparer.Ordinal);
    private readonly object gate = new();

    public static IReadOnlyList<string> BankPaths(long shipGroup) =>
        BankSuffixes.Select(suffix => $"cue/cv-{shipGroup}{suffix}.b").ToList();

    // Bank names and cue names only use these characters, so a request can't name anything else on disk.
    public static bool IsValidName(string bank, string cue) => BankName().IsMatch(bank) && CueName().IsMatch(cue);

    public VoiceCue? Find(long shipGroup, int groupIndex, string resourceKey)
    {
        string[] cues = groupIndex > 0 ? [$"{resourceKey}_{groupIndex}", resourceKey] : [resourceKey];
        foreach (var cue in cues)
        {
            foreach (var suffix in BankSuffixes)
            {
                var bank = $"cv-{shipGroup}{suffix}";
                if (Bank(bank)?.HasCue(cue) == true)
                {
                    return new VoiceCue(bank, cue);
                }
            }
        }

        return null;
    }

    public byte[]? ReadWav(VoiceCue voice) =>
        IsValidName(voice.Bank, voice.Cue) ? Bank(voice.Bank)?.ReadWav(voice.Cue) : null;

    private AcbFile? Bank(string name)
    {
        lock (gate)
        {
            // Missing banks aren't remembered, so a bank downloaded later is picked up.
            if (!banks.TryGetValue(name, out var bank))
            {
                var path = Path.Combine(assetFolder, "cue", name + ".b");
                if (!File.Exists(path))
                {
                    return null;
                }

                banks[name] = bank = AcbFile.Read(path);
            }

            return bank;
        }
    }

    [GeneratedRegex(@"^cv-\d+(-battle|-gift)?$")]
    private static partial Regex BankName();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex CueName();
}
