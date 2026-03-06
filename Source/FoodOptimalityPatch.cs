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
    /// Also neutralises two vanilla biases that would otherwise override the
    /// player's tier ordering:
    ///   - optimalityOffsetFeedingAnimals (e.g. kibble +100, lavish meals -100)
    ///   - DesperateOnly / DesperateOnlyForHumanlikes penalties (-150 each)
    /// These vanilla offsets are subtracted back out so that the ONLY
    /// food-type-specific factor is our tier offset.
    ///
    /// IMPORTANT: Vanilla only calls FoodOptimality for humanlike pawns during map
    /// food search (via SpawnedFoodSearchInnerScan). Animals use distance-based
    /// selection instead. The companion BestFoodSourceOnMap_Patch prefix redirects
    /// the animal search to use optimality scoring, making this postfix effective.
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

            // ── Neutralise vanilla biases ─────────────────────────────
            // Vanilla FoodOptimality applies these before our postfix:
            //   • DesperateOnly:              -150
            //   • DesperateOnlyForHumanlikes:  -150 (for humanlike eaters only, but we guard anyway)
            //   • optimalityOffsetFeedingAnimals: varies per def (kibble +100, meals -25 to -100, etc.)
            // These compete with our tier offsets and can flip the ordering
            // at lower tier-spacing values. Subtract them out so the tier
            // offset is the sole food-type factor.
            if (def.ingestible != null)
            {
                // Undo optimalityOffsetFeedingAnimals (vanilla added it for IsAnimal eaters)
                if (eater.IsAnimal)
                    __result -= def.ingestible.optimalityOffsetFeedingAnimals;

                // Undo the DesperateOnly penalty (-150) that vanilla applied
                switch (def.ingestible.preferability)
                {
                    case FoodPreferability.DesperateOnly:
                        __result += 150f;
                        break;
                    case FoodPreferability.DesperateOnlyForHumanlikes:
                        // Vanilla only penalises humanlike eaters for this,
                        // but since we only run for non-humanlike, nothing
                        // to undo here. Listed for clarity.
                        break;
                }
            }

            // ── Apply tier offset ─────────────────────────────────────
            FoodCategory category = FoodClassifier.Classify(def);
            __result += AnimalFoodPreferenceSettings.GetScoreOffset(category);
        }
    }
}
