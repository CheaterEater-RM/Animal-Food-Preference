using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Mod settings window, laid out in two columns:
    /// • Left  — config controls (tier spacing, distance, max search), Reset, and the
    ///           reorderable Food Category Priority Order list (▲/▼).
    /// • Right — a searchable, stockpile-style Individual Food Overrides side-panel.
    ///           Each row shows the item's icon, label, and a number badge (its current
    ///           tier rank); clicking a row opens a numbered category menu.
    ///
    /// Performance note: the ThingDef list is built once and cached, and only the rows
    /// inside the visible scroll band are drawn each frame (virtualized).
    /// </summary>
    public static class SettingsUI
    {
        // ── Layout constants ─────────────────────────────────────────
        private const float RowHeight = 30f;
        private const float ButtonWidth = 30f;
        private const float Margin = 4f;
        private const float SectionGap = 16f;
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
                float tw = 1040f;
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

            // ── Two columns: left = config + tier order, right = overrides panel ──
            const float colGap = 24f;
            float leftW = Mathf.Min(480f, inRect.width * 0.5f);
            Rect leftCol = new Rect(inRect.x, inRect.y, leftW, inRect.height);
            float rightX = leftCol.xMax + colGap;
            Rect rightCol = new Rect(rightX, inRect.y, inRect.xMax - rightX, inRect.height);

            GUI.color = new Color(1f, 1f, 1f, 0.3f);
            Widgets.DrawLineVertical(leftCol.xMax + colGap / 2f, inRect.y, inRect.height);
            GUI.color = Color.white;

            // ── Left column, part 1: Config controls (Listing_Standard) ──
            var ls = new Listing_Standard();
            ls.Begin(leftCol);

            ls.Label("Tier Spacing — points between adjacent tiers. Higher = stricter priority.");
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

            // Compact one-liner; the full explanation lives in the tooltip.
            Rect maxDistLabelRect = ls.Label("Max Search Distance — extra cap in cells; 0 = region-bounded only.");
            TooltipHandler.TipRegion(maxDistLabelRect,
                "Distance cap on how far a tame animal looks for food. The search " +
                "is always bounded to ~100 nearby regions (vanilla-style); " +
                "0 = no cap. This is an additional limit.");
            Rect maxDistRow = ls.GetRect(RowHeight);
            maxSearchDistanceBuffer = Widgets.TextField(maxDistRow.LeftPartPixels(120f), maxSearchDistanceBuffer);
            if (int.TryParse(maxSearchDistanceBuffer, out int parsedMaxDist))
                settings.maxSearchDistance = Mathf.Max(0, parsedMaxDist);
            ls.Gap(Margin);

            if (ls.ButtonText("Reset to Defaults", widthPct: 0.5f))
            {
                settings.ResetToDefaults();
                allFoodDefs = null;
                _buffersInitialized = false;
            }
            ls.Gap(SectionGap);

            float configBottom = leftCol.y + ls.CurHeight;
            ls.End();

            // ── Left column, part 2: Tier Order list (all rows visible) ──
            float y = configBottom;
            DrawSectionHeader(ref y, leftCol, "Food Category Priority Order",
                "Higher = preferred first. Use ▲▼ to reorder.");

            float tierContentH = settings.tierOrder.Count * RowHeight;
            float tierScrollH = Mathf.Min(tierContentH, Mathf.Max(3 * RowHeight, leftCol.yMax - y));
            Rect tierOuter = new Rect(leftCol.x, y, leftCol.width, tierScrollH);
            Rect tierInner = new Rect(0f, 0f, leftCol.width - 16f, tierContentH);
            Widgets.BeginScrollView(tierOuter, ref tierScrollPos, tierInner);
            DrawTierList(tierInner, settings);
            Widgets.EndScrollView();

            // ── Right column: Individual Food Overrides side-panel ──
            DrawOverridesPanel(rightCol, settings);
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

        // ── Overrides side-panel (right column) ──────────────────────
        // Styled like a stockpile / Material-Filter list: a searchable, scrollable
        // list of food items. Each row shows the item's icon, label, and a number
        // badge (its current tier rank — tooltip gives the full name). Clicking a row
        // opens a numbered category menu to set or clear its override.

        private static void DrawOverridesPanel(Rect rect, AnimalFoodPreferenceSettings settings)
        {
            float y = rect.y;
            DrawSectionHeader(ref y, rect, "Individual Food Overrides",
                "Click an item to set its category. The number is its current tier rank.");

            // Search
            Rect searchRect = new Rect(rect.x, y, rect.width, SearchBarHeight);
            string newSearch = Widgets.TextField(searchRect, searchText);
            if (newSearch != searchText) { searchText = newSearch; defScrollPos.y = 0f; UpdateFilter(); }
            y += SearchBarHeight + Margin;

            // Scrollable, menu-section-styled list filling the rest of the column.
            Rect listRect = new Rect(rect.x, y, rect.width, Mathf.Max(RowHeight, rect.yMax - y - BottomPadding));
            Widgets.DrawMenuSection(listRect);
            Rect inner = listRect.ContractedBy(1f);
            Rect viewRect = new Rect(0f, 0f, inner.width - 16f, filteredFoodDefs.Count * RowHeight);
            Widgets.BeginScrollView(inner, ref defScrollPos, viewRect);
            DrawOverrideRows(viewRect, inner.height, settings);
            Widgets.EndScrollView();

            if (filteredFoodDefs.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(inner, "No matching foods.");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }
        }

        private static void DrawOverrideRows(Rect viewRect, float viewportHeight, AnimalFoodPreferenceSettings settings)
        {
            int count = filteredFoodDefs.Count;
            // Virtualize: only build/draw the rows intersecting the visible scroll band.
            int first = Mathf.Max(0, Mathf.FloorToInt(defScrollPos.y / RowHeight));
            int last = Mathf.Min(count, Mathf.CeilToInt((defScrollPos.y + viewportHeight) / RowHeight));

            for (int i = first; i < last; i++)
            {
                ThingDef def = filteredFoodDefs[i];
                Rect rowRect = new Rect(0f, i * RowHeight, viewRect.width, RowHeight);

                if (Mouse.IsOver(rowRect))
                    Widgets.DrawHighlight(rowRect);

                // Item icon. Corpse defs have no UI icon of their own (DefIcon would draw blank),
                // so unwrap to the source pawn/animal and draw that. Humanlike corpses unwrap to a
                // race that also has no static icon (rendered via portraits, not a uiIcon) - fall
                // back to our generic Corpses category texture rather than a blank/placeholder.
                float iconSize = RowHeight - 6f;
                Rect iconRect = new Rect(rowRect.x + Margin, rowRect.y + 3f, iconSize, iconSize);
                if (def.IsCorpse)
                {
                    ThingDef src = def.ingestible?.sourceDef;
                    if (src != null && src.uiIcon != null && src.uiIcon != BaseContent.BadTex)
                        Widgets.DefIcon(iconRect, src);
                    else
                        CategoryIcons.Draw(iconRect, FoodCategory.Corpse);
                }
                else
                {
                    Widgets.DefIcon(iconRect, def, drawPlaceholder: true);
                }

                // Number badge (effective category rank), right-aligned. Dim = auto, bright = overridden.
                FoodCategory effective = FoodClassifier.Classify(def);
                bool hasOverride = settings.defOverrides.ContainsKey(def.defName);
                int rank = settings.tierOrder.IndexOf(effective) + 1;

                const float badgeW = 28f;
                Rect badgeRect = new Rect(rowRect.xMax - badgeW - Margin, rowRect.y + 4f, badgeW, RowHeight - 8f);
                Widgets.DrawBoxSolid(badgeRect, hasOverride
                    ? new Color(0.30f, 0.45f, 0.30f, 0.65f)
                    : new Color(0.30f, 0.30f, 0.30f, 0.35f));
                Text.Anchor = TextAnchor.MiddleCenter;
                if (!hasOverride) GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(badgeRect, rank > 0 ? rank.ToString() : "—");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;

                // Label (between icon and badge)
                float labelX = iconRect.xMax + Margin;
                Rect labelRect = new Rect(labelX, rowRect.y, badgeRect.x - labelX - Margin, RowHeight);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(labelRect, def.LabelCap.Resolve().Truncate(labelRect.width));
                Text.Anchor = TextAnchor.UpperLeft;

                // Tooltip
                FoodCategory autoCat = FoodClassifier.ClassifyAuto(def);
                int autoRank = settings.tierOrder.IndexOf(autoCat) + 1;
                string tip = $"{def.LabelCap}\ndefName: {def.defName}\nFrom: {def.modContentPack?.Name ?? "Core"}\n\n" +
                             $"Auto: {autoRank} — {GetCategoryLabel(autoCat)}\n" +
                             (hasOverride
                                 ? $"Override: {rank} — {GetCategoryLabel(effective)}"
                                 : "(no override — click to set)");
                TooltipHandler.TipRegion(rowRect, tip);

                // Click → numbered category menu
                if (Widgets.ButtonInvisible(rowRect, false))
                    OpenCategoryMenu(def, settings);
            }
        }

        private static void OpenCategoryMenu(ThingDef def, AnimalFoodPreferenceSettings settings)
        {
            var options = new List<FloatMenuOption>();

            FoodCategory autoCat = FoodClassifier.ClassifyAuto(def);
            int autoRank = settings.tierOrder.IndexOf(autoCat) + 1;
            options.Add(new FloatMenuOption(
                $"Auto (default: {autoRank} — {GetCategoryLabel(autoCat)})",
                () => settings.SetOverride(def, null)));

            for (int i = 0; i < settings.tierOrder.Count; i++)
            {
                FoodCategory captured = settings.tierOrder[i];
                int rank = i + 1;
                options.Add(new FloatMenuOption(
                    $"{rank} — {GetCategoryLabel(captured)}",
                    () => settings.SetOverride(def, captured)));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
