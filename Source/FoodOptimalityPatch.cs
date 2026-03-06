using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Postfix on FoodUtility.FoodOptimality.
    ///
    /// For non-humanlike pawns (animals), adjusts the optimality score based on
    /// the food's classified category and the player's tier ordering.
    ///
    /// IMPORTANT: Vanilla only calls FoodOptimality for humanlike pawns during map
    /// food search (via SpawnedFoodSearchInnerScan). Animals use distance-based
    /// selection instead. The companion BestFoodSourceOnMap_Patch prefix redirects
    /// the animal search to use optimality scoring, making this postfix effective.
    ///
    /// Hot path per call:
    ///   1. Null check + bool check (eater.RaceProps.Humanlike) → fast exit for colonists
    ///   2. FoodClassifier.Classify → one Dictionary lookup (cached)
    ///   3. GetScoreOffset → one Dictionary lookup
    ///   4. Float addition
    ///
    /// Total: ~3 dictionary lookups in the worst case, zero allocations.
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.FoodOptimality),
        new Type[] { typeof(Pawn), typeof(Thing), typeof(ThingDef), typeof(float), typeof(bool) })]
    public static class FoodOptimality_Patch
    {
        public static void Postfix(ref float __result, Pawn eater, Thing foodSource, ThingDef foodDef)
        {
            // Fast-exit: only adjust for animals
            if (eater == null || eater.RaceProps.Humanlike)
                return;

            ThingDef def = foodDef ?? foodSource?.def;
            if (def == null)
                return;

            FoodCategory category = FoodClassifier.Classify(def);
            __result += AnimalFoodPreferenceSettings.GetScoreOffset(category);
        }
    }
}
