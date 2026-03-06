using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Classifies any ThingDef into a <see cref="FoodCategory"/>.
    ///
    /// Classification logic:
    /// 1. Check explicit ThingDefOf matches (hay, kibble, pemmican, survival meal, insect jelly)
    /// 2. Check thingClass for corpses
    /// 3. For plants: distinguish wild/decorative/food based on purpose + harvestedThingDef
    /// 4. For meals: map via FoodPreferability enum
    /// 5. For raw ingestibles: catch-all via preferability
    /// 6. Everything else → Other
    ///
    /// Results are cached per ThingDef. The cache can be rebuilt if settings change.
    /// </summary>
    public static class FoodClassifier
    {
        private static readonly Dictionary<ThingDef, FoodCategory> cache =
            new Dictionary<ThingDef, FoodCategory>();

        /// <summary>
        /// Gets the food category for a ThingDef. Checks player overrides first,
        /// then falls through to auto-classification with caching.
        /// </summary>
        public static FoodCategory Classify(ThingDef def)
        {
            // Player override takes absolute precedence
            if (AnimalFoodPreferenceSettings.TryGetOverride(def, out FoodCategory overridden))
                return overridden;

            if (cache.TryGetValue(def, out FoodCategory cached))
                return cached;

            FoodCategory result = ClassifyInternal(def);
            cache[def] = result;
            return result;
        }

        /// <summary>
        /// Returns the auto-classified category (ignoring player overrides).
        /// Used by the settings UI to show the "default" classification.
        /// </summary>
        public static FoodCategory ClassifyAuto(ThingDef def)
        {
            if (cache.TryGetValue(def, out FoodCategory cached))
                return cached;

            FoodCategory result = ClassifyInternal(def);
            cache[def] = result;
            return result;
        }

        /// <summary>
        /// Clears the classification cache. Call when settings change.
        /// </summary>
        public static void ClearCache()
        {
            cache.Clear();
        }

        private static FoodCategory ClassifyInternal(ThingDef def)
        {
            // ── 1. Specific well-known defs (cheapest checks) ───────────
            if (def == ThingDefOf.Hay)
                return FoodCategory.Hay;
            if (def == ThingDefOf.Kibble)
                return FoodCategory.Kibble;
            if (def == ThingDefOf.Pemmican)
                return FoodCategory.Pemmican;
            if (def == ThingDefOf.MealSurvivalPack)
                return FoodCategory.SurvivalMeal;

            // Insect jelly — defName check since ThingDefOf may not have it in all versions
            if (def.defName == "InsectJelly")
                return FoodCategory.InsectJelly;

            // ── 2. Corpses ──────────────────────────────────────────────
            if (typeof(Corpse).IsAssignableFrom(def.thingClass))
                return FoodCategory.Corpse;

            // ── 3. Plants ───────────────────────────────────────────────
            if (def.plant != null)
                return ClassifyPlant(def);

            // ── 4. Meals and raw food by preferability ──────────────────
            if (def.ingestible != null)
                return ClassifyByPreferability(def);

            // ── 5. Unknown ──────────────────────────────────────────────
            return FoodCategory.Other;
        }

        /// <summary>
        /// Classifies a plant ThingDef into WildPlant, FlowerOrDecor, or FoodCrop.
        ///
        /// Logic:
        /// - If the plant produces an edible harvest → FoodCrop (corn, rice, strawberries, etc.)
        /// - If the plant's purpose is Health or Beauty → FlowerOrDecor (roses, daylilies, healroot)
        /// - If it's a tree → WildPlant (animals don't really eat trees, but they're zero-value)
        /// - Everything else (grass, tall grass, dandelions, bushes) → WildPlant
        ///
        /// Note: "purpose" is PlantPurpose enum: Food, Health, Beauty, Misc.
        /// Wild grass has purpose=Misc and no harvestedThingDef.
        /// </summary>
        private static FoodCategory ClassifyPlant(ThingDef def)
        {
            var plant = def.plant;

            // Food-producing plants: anything that yields an edible harvest
            if (plant.harvestedThingDef != null)
            {
                var harvest = plant.harvestedThingDef;
                if (harvest.IsNutritionGivingIngestible)
                    return FoodCategory.FoodCrop;

                // Non-food harvest (e.g., cotton, devilstrand, healroot medicine)
                // Healroot and such are "Health" purpose but harvest isn't food
                return FoodCategory.FlowerOrDecor;
            }

            // Beauty/Health purpose plants without edible harvest → decorative
            if (plant.purpose == PlantPurpose.Beauty || plant.purpose == PlantPurpose.Health)
                return FoodCategory.FlowerOrDecor;

            // Everything else: grass, tall grass, wild bushes, dandelions, etc.
            return FoodCategory.WildPlant;
        }

        /// <summary>
        /// Classifies non-plant ingestibles by FoodPreferability.
        /// </summary>
        private static FoodCategory ClassifyByPreferability(ThingDef def)
        {
            switch (def.ingestible.preferability)
            {
                case FoodPreferability.MealAwful:
                    return FoodCategory.MealNutrientPaste;
                case FoodPreferability.MealSimple:
                    return FoodCategory.MealSimple;
                case FoodPreferability.MealFine:
                    return FoodCategory.MealFine;
                case FoodPreferability.MealLavish:
                    return FoodCategory.MealLavish;
                case FoodPreferability.RawBad:
                case FoodPreferability.RawTasty:
                    return FoodCategory.RawFood;
                case FoodPreferability.DesperateOnly:
                case FoodPreferability.DesperateOnlyForHumanlikes:
                    return FoodCategory.Other;
                default:
                    return FoodCategory.Other;
            }
        }
    }
}
