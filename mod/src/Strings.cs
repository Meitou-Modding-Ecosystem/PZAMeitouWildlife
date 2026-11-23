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
        public const string CarrionHint = "Birds come down to corpses, land beside them and peck, and burst up when someone comes within about 20 m. One ready-made look, nothing to tune.";
        public const string LangLabel = "Language";
        public const string LangHint = "auto = the game's language. Needs a restart of the game.";
    }

    public static readonly (string Key, string En)[] Table =
    [
        ("mod.name", "PZA's Wildlife"),
        ("set.carrion", En.CarrionLabel),
        ("set.carrion.hint", En.CarrionHint),
        ("set.lang", En.LangLabel),
        ("set.lang.hint", En.LangHint),
        ("debug.toggle", "Wildlife: birds at carrion on / off"),
    ];

    public static readonly PZA.MeitouUi.PzaStrings Text = new(Table);
    public static string T(string key) => Text.T(key);
}
