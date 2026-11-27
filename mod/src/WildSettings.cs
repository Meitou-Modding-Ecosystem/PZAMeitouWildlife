// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// The Settings > Mods group (MeitouClient docs/modding.md "Settings"): one switch and the language. The behaviour is one ready-made preset
// (Preset, "Carrion"), no sliders. The page sets these on the interface thread; the bird thread reads Driver.On (volatile).
using Meitou.Modding;

namespace PZA.Wildlife.Meitou;

public sealed class WildSettings
{
    bool _carrion = true;

    /// <summary>Birds come to corpses, land, peck and fly off from people.</summary>
    [Setting(Strings.En.CarrionLabel, Hint = Strings.En.CarrionHint)]
    public bool Carrion
    {
        get => _carrion;
        set
        {
            _carrion = value;
            Driver.On = value;
        }
    }

    bool _blood = true;

    /// <summary>A small blood mist where a landed bird bites at the body (display only; needs Meitou's code-mod effects).</summary>
    [Setting(Strings.En.BloodLabel, Hint = Strings.En.BloodHint, VisibleWhen = nameof(Carrion))]
    public bool Blood
    {
        get => _blood;
        set
        {
            _blood = value;
            Driver.Core.Bites = value;
        }
    }

    bool _fireflies = true;

    /// <summary>Warm glowing fireflies drifting over the Swamp at dusk and night (render-side only, never saved).</summary>
    [Setting(Strings.En.FirefliesLabel, Hint = Strings.En.FirefliesHint)]
    public bool Fireflies
    {
        get => _fireflies;
        set
        {
            _fireflies = value;
            Driver.Fireflies.Enabled = value;
        }
    }

    /// <summary>Night sounds: the biome's night ambience from dusk (19:30) instead of 23:00, and its calls a little louder (sound only).</summary>
    [Setting(Strings.En.NightSoundsLabel, Hint = Strings.En.NightSoundsHint)]
    public bool NightSounds { get; set; } = true;

    bool _frogs = true;

    /// <summary>Frogs at the Swamp's water edges that leap into the water when someone comes near (display only).</summary>
    [Setting(Strings.En.FrogsLabel, Hint = Strings.En.FrogsHint)]
    public bool Frogs
    {
        get => _frogs;
        set
        {
            _frogs = value;
            Driver.Frogs.Enabled = value;
        }
    }

    bool _froxlotls = true;

    /// <summary>Froxlotls (TheIvoryDisaster's model) resting at the Swamp's water edges; they run into the water when someone comes near.</summary>
    [Setting(Strings.En.FroxLabel, Hint = Strings.En.FroxHint)]
    public bool Froxlotls
    {
        get => _froxlotls;
        set
        {
            _froxlotls = value;
            Driver.Frox.Enabled = value;
        }
    }

    /// <summary>auto = the game's language.</summary>
    [Setting(Strings.En.LangLabel, Hint = Strings.En.LangHint, Options = ["auto", "en", "ru", "zh", "ja"])]
    public string Language { get; set; } = "auto";
}
