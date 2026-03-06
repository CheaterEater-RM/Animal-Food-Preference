using HarmonyLib;
using UnityEngine;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Mod entry point for settings UI. Loads very early — do NOT reference Defs here.
    /// </summary>
    public class AnimalFoodPreference_Mod : Mod
    {
        public static AnimalFoodPreferenceSettings Settings { get; private set; }

        public AnimalFoodPreference_Mod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<AnimalFoodPreferenceSettings>();
        }

        public override string SettingsCategory() => "Animal Food Preference";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            SettingsUI.DoSettingsWindow(inRect, Settings);
        }
    }

    /// <summary>
    /// Harmony patch entry point. Fires after all Defs are loaded.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class AnimalFoodPreference_Init
    {
        static AnimalFoodPreference_Init()
        {
            var harmony = new Harmony("com.cheatereater.animalfoodpreference");
            harmony.PatchAll();
            Log.Message("[Animal Food Preference] Harmony patches applied.");
        }
    }
}
