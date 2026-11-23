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

    /// <summary>auto = the game's language.</summary>
    [Setting(Strings.En.LangLabel, Hint = Strings.En.LangHint, Options = ["auto", "en", "ru", "zh", "ja"])]
    public string Language { get; set; } = "auto";
}
