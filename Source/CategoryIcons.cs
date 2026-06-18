using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Resolves and caches a representative icon for each <see cref="FoodCategory"/>, drawn
    /// next to the category label in the settings tier list.
    ///
    /// Most categories pull a real item/plant icon from a representative ThingDef via
    /// <see cref="Widgets.GetIconFor(Thing, Vector2, Rot4?, bool, out float, out float, out Vector2, out Color, out Material)"/>
    /// — the same path Material Filter uses. <see cref="FoodCategory.Corpse"/> uses a bundled
    /// custom texture instead, because vanilla has no generic corpse icon (corpse defs render
    /// as the living creature, and there is no Core skull/meat item to stand in).
    ///
    /// Icons resolve lazily on first draw — the settings window only opens in-play, after
    /// Defs and textures have loaded — and are cached for the session. A null cache entry
    /// means "resolved, but no icon" so we don't retry every frame.
    /// </summary>
    public static class CategoryIcons
    {
        private const string CorpseTexturePath = "UI/AFP/CorpseIcon";

        // Representative ThingDef defName(s) per category. The first that resolves wins, so a
        // DLC-gated def can list a Core fallback after it (all current entries are Core).
        private static readonly Dictionary<FoodCategory, string[]> DefNames =
            new Dictionary<FoodCategory, string[]>
        {
            { FoodCategory.WildPlant,         new[] { "Plant_Grass" } },
            { FoodCategory.FlowerOrDecor,     new[] { "Plant_Dandelion" } },
            { FoodCategory.FoodCrop,          new[] { "Plant_Corn" } },
            // Corpse is handled via the custom texture, not a def.
            { FoodCategory.Hay,               new[] { "Hay" } },
            { FoodCategory.Kibble,            new[] { "Kibble" } },
            { FoodCategory.MealNutrientPaste, new[] { "MealNutrientPaste" } },
            { FoodCategory.MealSimple,        new[] { "MealSimple" } },
            { FoodCategory.MealFine,          new[] { "MealFine" } },
            { FoodCategory.MealLavish,        new[] { "MealLavish" } },
            { FoodCategory.RawFood,           new[] { "RawCorn" } },
            { FoodCategory.Pemmican,          new[] { "Pemmican" } },
            { FoodCategory.InsectJelly,       new[] { "InsectJelly" } },
            { FoodCategory.SurvivalMeal,      new[] { "MealSurvivalPack" } },
            { FoodCategory.Other,             new[] { "Chocolate" } },
        };

        private sealed class IconData
        {
            internal Texture Texture;
            internal Material Material;
            internal Color Color = Color.white;
            internal float Angle;
            internal Vector2 Proportions = Vector2.one;
        }

        private static readonly Dictionary<FoodCategory, IconData> cache =
            new Dictionary<FoodCategory, IconData>();

        /// <summary>Draws the category icon into <paramref name="rect"/>. No-op if none resolved.</summary>
        public static void Draw(Rect rect, FoodCategory cat)
        {
            if (!cache.TryGetValue(cat, out IconData data))
            {
                data = Resolve(cat, rect.size);
                cache[cat] = data;
            }
            if (data?.Texture == null || data.Texture == BaseContent.BadTex)
                return;

            Color prev = GUI.color;
            GUI.color = data.Color;
            Widgets.DrawTextureFitted(rect, data.Texture, 1f, data.Proportions,
                new Rect(0f, 0f, 1f, 1f), data.Angle, data.Material);
            GUI.color = prev;
        }

        /// <summary>Drops cached icons. Call if representative defs/textures might have changed.</summary>
        public static void ClearCache() => cache.Clear();

        private static IconData Resolve(FoodCategory cat, Vector2 size)
        {
            try
            {
                if (cat == FoodCategory.Corpse)
                {
                    Texture2D tex = ContentFinder<Texture2D>.Get(CorpseTexturePath, reportFailure: false);
                    return tex != null ? new IconData { Texture = tex } : null;
                }

                if (!DefNames.TryGetValue(cat, out string[] names))
                    return null;

                foreach (string name in names)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
                    if (def == null)
                        continue;
                    IconData data = ResolveFromDef(def, size);
                    if (data != null)
                        return data;
                }
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning($"[AnimalFoodPreference] Failed to resolve icon for category {cat}: {ex}");
                return null;
            }
        }

        private static IconData ResolveFromDef(ThingDef def, Vector2 size)
        {
            Thing preview = ThingMaker.MakeThing(def);
            if (preview is Plant plant)
            {
                // Show the mature plant graphic — some crops (e.g. corn) have a distinct
                // immature sprite that a freshly-made plant would otherwise render.
                plant.Growth = 1f;
            }
            else
            {
                preview.stackCount = Math.Max(1, def.stackLimit);
            }

            Texture tex = Widgets.GetIconFor(preview, size, null, false,
                out _, out float angle, out Vector2 proportions, out Color color, out Material material);

            if (tex == null || tex == BaseContent.BadTex)
            {
                // Fallback for defs whose live graphic doesn't resolve a UI texture.
                Texture fallback = def.uiIcon;
                if (fallback == null || fallback == BaseContent.BadTex)
                    fallback = def.graphic?.MatSingle?.mainTexture;
                if (fallback == null || fallback == BaseContent.BadTex)
                    return null;
                return new IconData { Texture = fallback };
            }

            return new IconData
            {
                Texture = tex,
                Material = material,
                Color = color,
                Angle = angle,
                Proportions = proportions,
            };
        }
    }
}
