using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Mod settings window with two sections:
    /// 1. Tier Order — reorderable list of FoodCategories with ▲/▼ buttons
    /// 2. Per-Def Overrides — searchable list of all food ThingDefs showing their
    ///    auto-classified tier and allowing dropdown override
    ///
    /// Performance note: The ThingDef list is built once and cached. Only the
    /// visible portion is drawn each frame (virtualized via scrolling).
    /// </summary>
    public static class SettingsUI
    {
        // ── Layout constants ─────────────────────────────────────────
        private const float RowHeight = 30f;
        private const float ButtonWidth = 30f;
        private const float Margin = 4f;
        private const float SectionGap = 16f;
        private const float LabelWidth = 200f;
        private const float DropdownWidth = 160f;
        private const float SearchBarHeight = 28f;
        private const float BottomPadding = 10f;

        // ── Cached state ─────────────────────────────────────────────
        private static Vector2 tierScrollPos;
        private static Vector2 defScrollPos;
        private static string searchText = "";
        private static string tierSpacingBuffer = "100";
        private static string maxSearchDistanceBuffer = "0";
        private static string distanceMultiplierBuffer = "1.00";
        private static bool _buffersInitialized;
        private static List<ThingDef> allFoodDefs;
        private static List<ThingDef> filteredFoodDefs;

        // ── Human-readable names for categories ──────────────────────
        private static readonly Dictionary<FoodCategory, string> CategoryLabels =
            new Dictionary<FoodCategory, string>
        {
            { FoodCategory.WildPlant,        "Wild Plants (grass, bushes)" },
            { FoodCategory.FlowerOrDecor,    "Flowers / Decorative Crops" },
            { FoodCategory.FoodCrop,         "Food Crops" },
            { FoodCategory.Corpse,           "Corpses" },
            { FoodCategory.Hay,              "Hay" },
            { FoodCategory.Kibble,           "Kibble" },
            { FoodCategory.MealNutrientPaste,"Nutrient Paste Meals" },
            { FoodCategory.MealSimple,       "Simple Meals" },
            { FoodCategory.MealFine,         "Fine Meals" },
            { FoodCategory.MealLavish,       "Lavish Meals" },
            { FoodCategory.RawFood,          "Raw Food (veggies, meat)" },
            { FoodCategory.Pemmican,         "Pemmican" },
            { FoodCategory.InsectJelly,      "Insect Jelly" },
            { FoodCategory.SurvivalMeal,     "Survival Meals" },
            { FoodCategory.Other,            "Other / Unclassified" },
        };

        public static string GetCategoryLabel(FoodCategory cat)
        {
            return CategoryLabels.TryGetValue(cat, out string label) ? label : cat.ToString();
        }

        // ── Build food def list (once) ───────────────────────────────

        private static void EnsureFoodDefList()
        {
            if (allFoodDefs != null)
                return;

            allFoodDefs = new List<ThingDef>();
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                // Include anything an animal could conceivably eat
                bool isFood = def.IsNutritionGivingIngestible || def.IsCorpse;
                bool isPlant = def.plant != null && def.IsNutritionGivingIngestible;
                if (isFood || isPlant)
                    allFoodDefs.Add(def);
            }
            allFoodDefs.SortBy(d => d.label ?? d.defName);
            filteredFoodDefs = new List<ThingDef>(allFoodDefs);
        }

        private static void UpdateFilter()
        {
            if (string.IsNullOrEmpty(searchText))
            {
                filteredFoodDefs = new List<ThingDef>(allFoodDefs);
                return;
            }
            string lower = searchText.ToLowerInvariant();
            filteredFoodDefs = allFoodDefs
                .Where(d => (d.label != null && d.label.ToLowerInvariant().Contains(lower))
                         || d.defName.ToLowerInvariant().Contains(lower)
                         || GetCategoryLabel(FoodClassifier.ClassifyAuto(d)).ToLowerInvariant().Contains(lower))
                .ToList();
        }

        // ── Main draw ────────────────────────────────────────────────
        // Layout strategy: use Listing_Standard ONLY for the config controls at
        // the top, then End() it and place the two scroll sections using raw rects.
        // Mixing scroll views inside a Listing_Standard causes coordinate confusion
        // and overlapping elements.

        public static void DoSettingsWindow(Rect inRect, AnimalFoodPreferenceSettings settings)
        {
            // The vanilla mod-settings dialog is a fixed 900×700, whose content area cannot
            // hold the config controls plus all 15 tier rows. Grow the live dialog once so the
            // whole tier list fits; re-centre only when the size actually differs so we don't
            // fight a player drag. Converges after one frame.
            var win = Find.WindowStack?.WindowOfType<RimWorld.Dialog_ModSettings>();
            if (win != null)
            {
                float tw = 920f;
                float th = Mathf.Min(UI.screenHeight - 35f, 1040f);
                if (Mathf.Abs(win.windowRect.width - tw) > 1f || Mathf.Abs(win.windowRect.height - th) > 1f)
                {
                    win.windowRect.width = tw;
                    win.windowRect.height = th;
                    win.windowRect.x = (UI.screenWidth - tw) / 2f;
                    win.windowRect.y = (UI.screenHeight - th) / 2f;
                }
            }

            EnsureFoodDefList();
            if (!_buffersInitialized)
            {
                tierSpacingBuffer = ((int)settings.tierSpacing).ToString();
                maxSearchDistanceBuffer = settings.maxSearchDistance.ToString();
                distanceMultiplierBuffer = settings.distanceMultiplier.ToString("0.00");
                _buffersInitialized = true;
            }

            // ── Phase 1: Config controls (Listing_Standard, no scroll views) ──
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            ls.Label("Tier Spacing — points between adjacent tiers. Higher = stricter priority. (Vanilla ≈ 40)");
            float oldSpacing = settings.tierSpacing;
            Rect tierSpacingRow = ls.GetRect(RowHeight);
            tierSpacingBuffer = Widgets.TextField(tierSpacingRow.LeftPartPixels(120f), tierSpacingBuffer);
            if (int.TryParse(tierSpacingBuffer, out int parsedSpacing))
                settings.tierSpacing = Mathf.Clamp(parsedSpacing, 0, 999);
            if (settings.tierSpacing != oldSpacing)
                settings.RebuildScores();
            ls.Gap(Margin);

            ls.Label("Distance Multiplier — how strongly distance penalises far-away food. 1.0 = vanilla.");
            Rect distRow = ls.GetRect(RowHeight);
            // The slider is authoritative while being dragged; the value box only writes
            // back when the player actually edits its text (it has its own buffer). Feeding
            // the box the live value every frame — the old approach — silently clobbered the
            // drag, which is why the slider appeared frozen. Snaps to 0.05 via roundTo.
            Rect sliderRect = new Rect(distRow.x, distRow.y, distRow.width - 70f, distRow.height);
            Rect distValRect = new Rect(distRow.xMax - 60f, distRow.y, 60f, distRow.height);

            float sliderVal = Widgets.HorizontalSlider(
                sliderRect, settings.distanceMultiplier, 0.5f, 5f,
                middleAlignment: true, leftAlignedLabel: "0.5×", rightAlignedLabel: "5.0×",
                roundTo: 0.05f);
            if (!Mathf.Approximately(sliderVal, settings.distanceMultiplier))
            {
                settings.distanceMultiplier = sliderVal;
                distanceMultiplierBuffer = sliderVal.ToString("0.00");
            }

            string typedDist = Widgets.TextField(distValRect, distanceMultiplierBuffer);
            if (typedDist != distanceMultiplierBuffer)
            {
                distanceMultiplierBuffer = typedDist;
                if (float.TryParse(typedDist, out float parsedDist))
                    settings.distanceMultiplier = Mathf.Clamp(parsedDist, 0.5f, 5f);
            }
            ls.Gap(Margin);

            ls.Label("Max Search Distance — optional extra cap (cells) on how far animals look for food. " +
                "The search is always bounded to ~100 nearby regions (vanilla-style) and never scans the " +
                "whole map; 0 = no extra cap (region-bounded only). Raise to tighten the search further.");
            Rect maxDistRow = ls.GetRect(RowHeight);
            maxSearchDistanceBuffer = Widgets.TextField(maxDistRow.LeftPartPixels(120f), maxSearchDistanceBuffer);
            if (int.TryParse(maxSearchDistanceBuffer, out int parsedMaxDist))
                settings.maxSearchDistance = Mathf.Max(0, parsedMaxDist);
            ls.Gap(Margin);

            if (ls.ButtonText("Reset to Defaults", widthPct: 0.22f))
            {
                settings.ResetToDefaults();
                allFoodDefs = null;
                _buffersInitialized = false;
            }
            ls.Gap(SectionGap);

            // Capture how tall the config section is, then close the listing.
            float configBottom = inRect.y + ls.CurHeight;
            ls.End();

            // ── Phase 2: Tier Order section ───────────────────────────────────
            float y = configBottom;
            float remaining = inRect.yMax - y;

            // Section header
            DrawSectionHeader(ref y, inRect, "Food Category Priority Order",
                "Higher in the list = animals prefer it first. Use ▲▼ to reorder.");

            // Tier scroll view — sized to show ALL rows when there's room (the list is a
            // fixed 15 categories), reserving a minimum scrollable block for the override
            // list below. Only scrolls internally on very short screens.
            float tierContentH = settings.tierOrder.Count * RowHeight;
            float minOverrideBlock = SearchBarHeight + Margin + 4 * RowHeight;
            float tierScrollH  = Mathf.Clamp(tierContentH, 3 * RowHeight,
                                     Mathf.Max(3 * RowHeight, remaining - minOverrideBlock));
            Rect tierOuter = new Rect(inRect.x, y, inRect.width, tierScrollH);
            Rect tierInner = new Rect(0f, 0f, inRect.width - 16f, tierContentH);
            Widgets.BeginScrollView(tierOuter, ref tierScrollPos, tierInner);
            DrawTierList(tierInner, settings);
            Widgets.EndScrollView();
            y += tierScrollH + SectionGap;

            // ── Phase 3: Per-Def Overrides section ───────────────────────────
            DrawSectionHeader(ref y, inRect, "Individual Food Overrides",
                "Override the auto-classified category for specific items. Useful for modded foods.");

            // Search bar
            string newSearch = Widgets.TextField(new Rect(inRect.x, y, inRect.width, SearchBarHeight), searchText);
            if (newSearch != searchText) { searchText = newSearch; UpdateFilter(); }
            y += SearchBarHeight + Margin;

            // Override scroll view — fills everything that remains, leaving
            // padding so rows don't overlap with the dialog's Close button.
            // Clamp to available space (never extend past inRect).
            float availableH = inRect.yMax - y - BottomPadding;
            float defScrollH = Mathf.Max(availableH, 0f);
            Rect defOuter = new Rect(inRect.x, y, inRect.width, defScrollH);
            Rect defInner = new Rect(0f, 0f, inRect.width - 16f, filteredFoodDefs.Count * RowHeight);
            Widgets.BeginScrollView(defOuter, ref defScrollPos, defInner);
            DrawDefList(defInner, settings);
            Widgets.EndScrollView();
        }

        // Draws a bold-style section header (title + separator line + subtitle) and
        // advances y by the height consumed.
        private static void DrawSectionHeader(ref float y, Rect inRect,
            string title, string subtitle)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), title);
            y += 22f;
            GUI.color = new Color(1f, 1f, 1f, 0.4f);
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            GUI.color = Color.white;
            y += 5f;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 22f), subtitle);
            GUI.color = Color.white;
            y += 22f + Margin;
        }

        // ── Tier list drawing ────────────────────────────────────────

        private static void DrawTierList(Rect viewRect, AnimalFoodPreferenceSettings settings)
        {
            float y = 0f;
            for (int i = 0; i < settings.tierOrder.Count; i++)
            {
                Rect rowRect = new Rect(0f, y, viewRect.width, RowHeight);

                // Zebra stripe
                if (i % 2 == 0)
                    Widgets.DrawLightHighlight(rowRect);

                // Rank number
                Rect rankRect = new Rect(rowRect.x + Margin, rowRect.y, 24f, RowHeight);
                Widgets.Label(rankRect, (i + 1).ToString());

                // Representative icon
                float iconSize = RowHeight - 6f;
                Rect iconRect = new Rect(rankRect.xMax + Margin, rowRect.y + 3f, iconSize, iconSize);
                CategoryIcons.Draw(iconRect, settings.tierOrder[i]);

                // Category label
                float labelX = iconRect.xMax + Margin;
                Rect labelRect = new Rect(labelX, rowRect.y, rowRect.width - labelX - (ButtonWidth * 2 + Margin * 3), RowHeight);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, GetCategoryLabel(settings.tierOrder[i]));
                Text.Anchor = TextAnchor.UpperLeft;

                // ▲ button
                float btnX = rowRect.xMax - (ButtonWidth * 2 + Margin);
                Rect upRect = new Rect(btnX, rowRect.y + 2f, ButtonWidth, RowHeight - 4f);
                if (i > 0)
                {
                    if (Widgets.ButtonText(upRect, "▲"))
                    {
                        settings.SwapTiers(i, i - 1);
                    }
                }

                // ▼ button
                Rect downRect = new Rect(upRect.xMax + Margin, rowRect.y + 2f, ButtonWidth, RowHeight - 4f);
                if (i < settings.tierOrder.Count - 1)
                {
                    if (Widgets.ButtonText(downRect, "▼"))
                    {
                        settings.SwapTiers(i, i + 1);
                    }
                }

                y += RowHeight;
            }
        }

        // ── Per-def list drawing ─────────────────────────────────────

        private static void DrawDefList(Rect viewRect, AnimalFoodPreferenceSettings settings)
        {
            float y = 0f;
            for (int i = 0; i < filteredFoodDefs.Count; i++)
            {
                ThingDef def = filteredFoodDefs[i];
                Rect rowRect = new Rect(0f, y, viewRect.width, RowHeight);

                if (i % 2 == 0)
                    Widgets.DrawLightHighlight(rowRect);

                // Def label
                Rect labelRect = new Rect(rowRect.x + Margin, rowRect.y, LabelWidth, RowHeight);
                Text.Anchor = TextAnchor.MiddleLeft;
                string label = def.label?.CapitalizeFirst() ?? def.defName;
                Widgets.Label(labelRect, label);

                // Auto-classified category (dimmed)
                FoodCategory autoCat = FoodClassifier.ClassifyAuto(def);
                bool hasOverride = settings.defOverrides.ContainsKey(def.defName);
                FoodCategory currentCat = hasOverride
                    ? settings.defOverrides[def.defName]
                    : autoCat;

                float catX = labelRect.xMax + Margin;
                Rect autoCatRect = new Rect(catX, rowRect.y, DropdownWidth, RowHeight);

                if (!hasOverride)
                {
                    GUI.color = new Color(0.7f, 0.7f, 0.7f);
                }
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(autoCatRect, GetCategoryLabel(currentCat));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;

                // Override button
                float btnX = autoCatRect.xMax + Margin;
                float btnW = 90f;
                Rect overrideRect = new Rect(btnX, rowRect.y + 2f, btnW, RowHeight - 4f);

                if (hasOverride)
                {
                    // Show "Clear" button to remove override
                    if (Widgets.ButtonText(overrideRect, "Clear"))
                    {
                        settings.SetOverride(def, null);
                    }
                }
                else
                {
                    if (Widgets.ButtonText(overrideRect, "Override"))
                    {
                        OpenCategoryDropdown(def, settings);
                    }
                }

                // Tooltip with defName for modders
                TooltipHandler.TipRegion(labelRect, $"defName: {def.defName}\nAuto-category: {GetCategoryLabel(autoCat)}\nFrom: {def.modContentPack?.Name ?? "Core"}");

                y += RowHeight;
            }
        }

        private static void OpenCategoryDropdown(ThingDef def, AnimalFoodPreferenceSettings settings)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (FoodCategory cat in settings.tierOrder)
            {
                FoodCategory captured = cat;
                options.Add(new FloatMenuOption(GetCategoryLabel(cat), () =>
                {
                    settings.SetOverride(def, captured);
                }));
            }
            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
