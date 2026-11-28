// PZA's Wildlife for Meitou. Author: PZA. Licence: GPLv3 or later.
// Every user-visible string: English compiled in, Russian / Chinese / Japanese from lang\meitou.<code>.txt (PZA's Meitou UI's PzaStrings: "key =
// text", {1}..{9} placeholders checked against English). Log lines stay English (they are for us). The Settings page's labels are attribute
// constants and show in English for now, as in PZA's other Meitou mods.
namespace PZA.Wildlife.Meitou;

public static class Strings
{
    public static class En
    {
        public const string CarrionLabel = "Birds at carrion";
        public const string CarrionHint = "Birds come down to corpses, land beside them and peck, and burst up when someone comes within about 20 m. Birds also beat their wings as they fly, land and take off. One ready-made look, nothing to tune.";
        public const string BloodLabel = "Blood while pecking";
        public const string BloodHint = "A small spray of blood where a bird bites at the body. Only drawn, never saved. Needs Meitou 0.22.2-nightly.20261123 or newer.";
        public const string FirefliesLabel = "Swamp fireflies";
        public const string FirefliesHint = "Warm glowing fireflies drift over the Swamp from dusk until dawn. Visual only, never saved.";
        public const string NightSoundsLabel = "Night sounds";
        public const string NightSoundsHint = "The night ambience (the swamp's frogs and insects, the canyons' crickets) starts at dusk instead of 23:00, and its calls are a little louder. Sound only, never saved.";
        public const string FroxLabel = "Froxlotls";
        public const string FroxHint = "Froxlotls rest at the Swamp's water edges in a few bright colours, more at night, crawl about now and then, and dash into the water and swim away under the surface when anyone comes within about 7 m. Model and colours by TheIvoryDisaster. Only drawn, never saved.";
        public const string FrogsLabel = "Swamp frogs";
        public const string FrogsHint = "Frogs sit at the Swamp's water edges, more at night, and leap into the water with a splash when anyone comes within about 7 m. Only drawn, never saved.";
        public const string LangLabel = "Language";
        public const string LangHint = "auto = the game's language. Needs a restart of the game.";
    }

    public static readonly (string Key, string En)[] Table =
    [
        ("mod.name", "PZA's Wildlife"),
        ("set.carrion", En.CarrionLabel),
        ("set.carrion.hint", En.CarrionHint),
        ("set.blood", En.BloodLabel),
        ("set.blood.hint", En.BloodHint),
        ("set.fireflies", En.FirefliesLabel),
        ("set.fireflies.hint", En.FirefliesHint),
        ("set.night", En.NightSoundsLabel),
        ("set.night.hint", En.NightSoundsHint),
        ("set.frogs", En.FrogsLabel),
        ("set.frogs.hint", En.FrogsHint),
        ("set.frox", En.FroxLabel),
        ("set.frox.hint", En.FroxHint),
        ("set.lang", En.LangLabel),
        ("set.lang.hint", En.LangHint),
        ("debug.toggle", "Wildlife: birds at carrion on / off"),
    ];

    public static readonly PZA.MeitouUi.PzaStrings Text = new(Table);
    public static string T(string key) => Text.T(key);
}
