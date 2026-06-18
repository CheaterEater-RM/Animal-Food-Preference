using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Persisted mod settings.
    ///
    /// Stores two things:
    /// 1. <see cref="tierOrder"/> — the player's preferred ordering of food categories
    ///    (a list of FoodCategory values, index 0 = most preferred).
    /// 2. <see cref="defOverrides"/> — per-ThingDef category overrides (defName → FoodCategory).
    ///
    /// Score offsets are derived from tierOrder: the first tier gets the highest offset,
    /// each subsequent tier is spaced by <see cref="TierSpacing"/> points.
    /// </summary>
    public class AnimalFoodPreferenceSettings : ModSettings
    {
        // ── Serialized state ─────────────────────────────────────────

        /// <summary>
        /// Ordered list of food categories. Index 0 = most preferred.
        /// Initialized to default order on first load.
        /// </summary>
        public List<FoodCategory> tierOrder;

        /// <summary>
        /// Per-ThingDef overrides. Key = ThingDef.defName, Value = assigned FoodCategory.
        /// Only stores defs the player has explicitly changed.
        /// </summary>
        public Dictionary<string, FoodCategory> defOverrides = new Dictionary<string, FoodCategory>();

        // ── User-configurable settings ───────────────────────────────

        /// <summary>
        /// Points between adjacent tiers. Must dominate vanilla's ~30-40 pt preferability spread.
        /// Default is 100. Vanilla is approximately 40.
        /// </summary>
        public float tierSpacing = 100f;

        /// <summary>
        /// Multiplier applied to the distance penalty inside FoodOptimality.
        /// 1.0 = vanilla weighting (1 point per cell). Higher values make nearby food
        /// more competitive against far-away better-tier food.
        /// Range: 0.5 – 5.0. Default: 1.0.
        /// </summary>
        public float distanceMultiplier = 1f;

        /// <summary>
        /// Optional extra cap (in cells, Manhattan) on how far a tame animal will look
        /// for food. The search is ALWAYS bounded to ~100 nearby regions (vanilla-style),
        /// so it never scans the whole map; this value only tightens that further.
        /// 0 = no extra distance cap (region-bounded only). Default: 0.
        /// </summary>
        public int maxSearchDistance = 0;

        // ── Constants ────────────────────────────────────────────────

        /// <summary>
        /// Base offset for the top tier. Each lower tier subtracts tierSpacing.
        /// </summary>
        public const float BaseOffset = 800f;

        // ── Derived cache ────────────────────────────────────────────

        /// <summary>
        /// FoodCategory → score offset. Rebuilt whenever tierOrder changes.
        /// </summary>
        private static Dictionary<FoodCategory, float> categoryScores =
            new Dictionary<FoodCategory, float>();

        /// <summary>
        /// Singleton reference, set by AnimalFoodPreference_Mod on construction.
        /// </summary>
        public static AnimalFoodPreferenceSettings Instance =>
            AnimalFoodPreference_Mod.Settings;

        // ── Default order ────────────────────────────────────────────

        public static readonly List<FoodCategory> DefaultTierOrder = new List<FoodCategory>
        {
            FoodCategory.WildPlant,
            FoodCategory.FlowerOrDecor,
            FoodCategory.FoodCrop,
            FoodCategory.Corpse,
            FoodCategory.Hay,
            FoodCategory.Kibble,
            FoodCategory.MealNutrientPaste,
            FoodCategory.MealSimple,
            FoodCategory.MealFine,
            FoodCategory.MealLavish,
            FoodCategory.RawFood,
            FoodCategory.Pemmican,
            FoodCategory.InsectJelly,
            FoodCategory.SurvivalMeal,
            FoodCategory.Other,
        };

        // ── Init & serialization ─────────────────────────────────────

        public AnimalFoodPreferenceSettings()
        {
            ResetToDefaults();
        }

        public void ResetToDefaults()
        {
            tierOrder = new List<FoodCategory>(DefaultTierOrder);
            defOverrides.Clear();
            tierSpacing = 100f;
            distanceMultiplier = 1f;
            maxSearchDistance = 0;
            RebuildScores();
            FoodClassifier.ClearCache();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref tierSpacing, "tierSpacing", 100f);
            Scribe_Values.Look(ref distanceMultiplier, "distanceMultiplier", 1f);
            Scribe_Values.Look(ref maxSearchDistance, "maxSearchDistance", 0);

            // Serialize tier order as list of strings (enum names for readability)
            List<string> tierNames = tierOrder?.Select(c => c.ToString()).ToList();
            Scribe_Collections.Look(ref tierNames, "tierOrder", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars && tierNames != null)
            {
                tierOrder = new List<FoodCategory>();
                foreach (string name in tierNames)
                {
                    if (Enum.TryParse(name, out FoodCategory cat))
                        tierOrder.Add(cat);
                }
                // Add any new categories that might have been added in an update
                foreach (FoodCategory cat in DefaultTierOrder)
                {
                    if (!tierOrder.Contains(cat))
                        tierOrder.Add(cat);
                }
            }

            // Serialize per-def overrides
            // Scribe_Collections doesn't directly support Dictionary<string, enum>,
            // so we serialize as parallel lists.
            List<string> overrideKeys = null;
            List<string> overrideValues = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                overrideKeys = defOverrides.Keys.ToList();
                overrideValues = defOverrides.Values.Select(c => c.ToString()).ToList();
            }
            Scribe_Collections.Look(ref overrideKeys, "overrideKeys", LookMode.Value);
            Scribe_Collections.Look(ref overrideValues, "overrideValues", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                defOverrides = new Dictionary<string, FoodCategory>();
                if (overrideKeys != null && overrideValues != null)
                {
                    for (int i = 0; i < overrideKeys.Count && i < overrideValues.Count; i++)
                    {
                        if (Enum.TryParse(overrideValues[i], out FoodCategory cat))
                            defOverrides[overrideKeys[i]] = cat;
                    }
                }
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (tierOrder == null || tierOrder.Count == 0)
                    tierOrder = new List<FoodCategory>(DefaultTierOrder);
                RebuildScores();
            }
        }

        // ── Score API ────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the category → score mapping from the current tierOrder.
        /// Must be called after any change to tierOrder or tierSpacing.
        /// </summary>
        public void RebuildScores()
        {
            categoryScores.Clear();
            for (int i = 0; i < tierOrder.Count; i++)
            {
                categoryScores[tierOrder[i]] = BaseOffset - (i * tierSpacing);
            }
            FoodClassifier.ClearCache();
        }

        /// <summary>
        /// Returns the optimality offset for a given FoodCategory.
        /// </summary>
        public static float GetScoreOffset(FoodCategory category)
        {
            return categoryScores.TryGetValue(category, out float score) ? score : 0f;
        }

        /// <summary>
        /// Checks if a player override exists for the given ThingDef.
        /// </summary>
        public static bool TryGetOverride(ThingDef def, out FoodCategory category)
        {
            if (Instance != null && Instance.defOverrides.TryGetValue(def.defName, out category))
                return true;
            category = FoodCategory.Other;
            return false;
        }

        /// <summary>
        /// Sets a per-def override. Pass null to remove the override.
        /// </summary>
        public void SetOverride(ThingDef def, FoodCategory? category)
        {
            if (category.HasValue)
                defOverrides[def.defName] = category.Value;
            else
                defOverrides.Remove(def.defName);
            FoodClassifier.ClearCache();
        }

        /// <summary>
        /// Swaps two tiers in the order. Returns false if indices are out of range.
        /// </summary>
        public bool SwapTiers(int indexA, int indexB)
        {
            if (indexA < 0 || indexA >= tierOrder.Count || indexB < 0 || indexB >= tierOrder.Count)
                return false;
            FoodCategory temp = tierOrder[indexA];
            tierOrder[indexA] = tierOrder[indexB];
            tierOrder[indexB] = temp;
            RebuildScores();
            return true;
        }
    }
}
