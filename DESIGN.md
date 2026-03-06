# Animal Food Preference — Design Document

## Overview

Animals in RimWorld choose food using an optimality score system (`FoodUtility.FoodOptimality`). By default, animals will happily eat your lavish meals or survival rations instead of the free grass growing everywhere. This mod adds a large tier-based offset to the optimality score so animals strongly prefer cheap, renewable food sources first.

## Core Mechanics

### Tier-Based Food Priority

Every food item is classified into one of 15 categories (see `FoodCategory` enum). The player orders these categories by priority in Mod Settings. The top-priority category gets a +800 optimality offset, with each subsequent tier dropping by 100 points (configurable via Tier Spacing). This 100-point spacing dominates vanilla's ~30–40 point preferability spread, ensuring tier ordering is respected.

Default 15 tiers span from +800 (WildPlant, index 0) to -600 (Other, index 14).

### Auto-Classification

Food items are classified automatically via `FoodClassifier`:

1. **Specific well-known defs** — hay, kibble, pemmican, survival meal, insect jelly (cheapest checks first)
2. **Corpses** — detected via `typeof(Corpse).IsAssignableFrom(def.thingClass)`
3. **Plants** — three sub-tiers based on purpose and harvest:
   - `WildPlant` — grass, tall grass, dandelions, bushes (no edible harvest, not Beauty/Health purpose)
   - `FlowerOrDecor` — roses, daylilies, healroot, cotton (Beauty/Health purpose or non-food harvest)
   - `FoodCrop` — corn, rice, potatoes, berries (has edible `harvestedThingDef`)
4. **Meals and raw food** — classified by `FoodPreferability` enum
5. **Everything else** → `Other` (neutral offset)

Results are cached per `ThingDef`. The cache is invalidated only when settings change.

### Per-Def Overrides

Players can override the auto-classification for any specific food item via the settings UI. Overrides are stored as `Dictionary<string, FoodCategory>` keyed by `defName` for safe serialization. Using `defName` (string) rather than direct `ThingDef` reference ensures safe serialization. Only explicit player overrides are stored — auto-classified defs aren't persisted. Clearing an override returns the item to auto-classification.

## Architecture

### Why Two Patches Are Needed

In vanilla RimWorld, `BestFoodSourceOnMap` has two branches:
- **Humanlike pawns**: Uses `SpawnedFoodSearchInnerScan`, which calls `FoodOptimality` to score every food candidate and picks the highest-scoring one.
- **Animals**: Uses `GenClosest.ClosestThingReachable`, which picks the **nearest** valid food by distance — it **never calls `FoodOptimality`**.

Simply patching `FoodOptimality` is insufficient because the animal code path never invokes it. The `BestFoodSourceOnMap` prefix redirects animals to use optimality-scored selection, and the `FoodOptimality` postfix adds the tier-based offsets to those scores.

### Harmony Patches

| Patch Target | Type | Purpose |
|---|---|---|
| `FoodUtility.BestFoodSourceOnMap` | Prefix | Redirects animal food search to use optimality-scored selection instead of distance-based |
| `FoodUtility.FoodOptimality` | Postfix | Adds tier-based offset to optimality score for non-humanlike eaters |

### Data Flow

```
Animal wants food
  → RimWorld calls BestFoodSourceOnMap(getter, eater, ...)
  → BestFoodSourceOnMap_Patch.Prefix intercepts (animals only):
      1. Builds food validator (same checks as vanilla)
      2. Filters out food other nearby animals are eating
      3. Iterates all candidate foods, scoring each by FoodOptimality
         → FoodOptimality_Patch.Postfix runs on each score:
            a. Fast-exit if eater is humanlike
            b. FoodClassifier.Classify(def)
               - Check player overrides (Dictionary<string, FoodCategory>)
               - Check auto-classification cache (Dictionary<ThingDef, FoodCategory>)
               - If uncached: run classification logic, cache result
            c. Settings.GetScoreOffset(category) → Dictionary<FoodCategory, float>
            d. __result += offset
      4. Picks the highest-scoring reachable food
      5. If nothing found, retries with relaxed (desperate) validator
  → Animal eats the best-scored food
```

### File Layout

```
AnimalFoodPreference/
├── About/About.xml
├── 1.5/Assemblies/              # (copy DLL here for 1.5 compat)
├── 1.6/Assemblies/              # Build output
├── Source/
│   ├── AnimalFoodPreference.sln
│   ├── AnimalFoodPreference.csproj
│   ├── ModInit.cs               — Mod class (settings) + [StaticConstructorOnStartup] (Harmony)
│   ├── FoodCategory.cs          — Enum defining all 15 food tiers
│   ├── FoodClassifier.cs        — ThingDef → FoodCategory logic + cache
│   ├── BestFoodSourcePatch.cs   — Prefix: redirects animal food search to optimality scoring
│   ├── FoodOptimalityPatch.cs   — Postfix: adds tier offsets to FoodOptimality scores
│   ├── Settings.cs              — ModSettings: tier order, per-def overrides, serialization
│   └── SettingsUI.cs            — IMGUI settings window (tier reorder + def override list)
└── DESIGN.md
```

## Settings

| Setting | Type | Description |
|---|---|---|
| Tier spacing | Integer (10–200) | Points between adjacent tiers. Higher = stricter priority. Default 100. |
| Tier order | Reorderable list | Drag food categories up/down to change priority |
| Per-def overrides | Per-item dropdown | Override auto-classification for individual food items |

### Serialization Details

- Tier order: serialized as `List<string>` of enum names for readability in save files
- Per-def overrides: serialized as parallel `List<string>` for keys and values (workaround for `Scribe_Collections` not supporting `Dictionary<string, enum>` directly)
- Forward-compatible: new `FoodCategory` values added in updates are appended; unknown enum names in saves are silently skipped; defs from unloaded mods are kept harmlessly

## Compatibility

### Hard Dependencies

- Harmony (runtime patching)

### How Modded Foods Are Handled

1. Specific `ThingDefOf` matches (unlikely for custom foods) → matched directly
2. Corpses from any mod → caught by `typeof(Corpse).IsAssignableFrom`
3. Plants from any mod → classified by harvest + purpose logic
4. Meals from any mod → classified by `FoodPreferability` enum
5. Anything unrecognized → `FoodCategory.Other` (neutral)
6. Players can manually override any item via settings

### Known Interactions

- **VGP Vegetable Garden** — custom plants auto-classify via the plant logic
- **Combat Extended** — no food mechanic changes, no conflicts expected
- **Simple Chains** — custom meals may use non-standard preferability values; players can override
- **Alpha Animals** — custom animal food defs will be classified or can be overridden

## Performance Considerations

- **BestFoodSourceOnMap prefix**: Iterates all food candidates on the map (same as vanilla does for humanlike pawns via `SpawnedFoodSearchInnerScan`). Expensive reachability checks are only performed for candidates that beat the current best score, avoiding unnecessary work.
- **FoodOptimality postfix per call**: 1 bool check + 2–3 dictionary lookups + 1 float add. Zero allocations.
- **Classification cache**: `Dictionary<ThingDef, FoodCategory>`, ~200 entries in a modded game. Populated lazily, invalidated only on settings change.
- **Score cache**: `Dictionary<FoodCategory, float>`, 15 entries. Rebuilt only on tier reorder.
- **No per-tick cost**: only runs when an animal actively searches for food.

## Implementation Notes

### API Gotchas

- `FoodOptimality` receives both `foodSource` (Thing) and `foodDef` (ThingDef) — they can differ (e.g. nutrient paste dispenser yields a meal def). Always use `foodDef ?? foodSource?.def`.
- Corpse ThingDefs are dynamically generated per race (`Corpse_Muffalo`, `Corpse_Human`, etc.) — detect via `thingClass` assignability, not defName.
- `InsectJelly` is not in `ThingDefOf` in all versions — use defName string check with preferability-based fallback. If a mod replaces the defName this would miss it, but fallback classification via preferability still catches it (likely `RawTasty` → `RawFood`).
- `Scribe_Collections.Look` doesn't directly support `Dictionary<string, enum>` — serialize as parallel string lists.
- `IsFoodSourceOnMapSociallyProper` is private in `FoodUtility` — inlined in the BestFoodSourceOnMap patch.

### Plant Classification Edge Cases

- **Haygrass**: `harvestedThingDef = Hay` (nutrition-giving) → FoodCrop. Correct: players grow it deliberately.
- **Berry bushes**: `harvestedThingDef = RawBerries` → FoodCrop. Correct.
- **Healroot**: `harvestedThingDef = MedicineHerbal` (not edible) → FlowerOrDecor. Correct.
- **Ambrosia**: plant with no harvest (plant itself is eaten), purpose Misc → WildPlant. Reasonable — grows wild.
- **Trees**: plant, no edible harvest, purpose Misc → WildPlant. Not normally edible but harmless classification.

## Build

```
cd Source
dotnet build -c Release
```
Output: `1.6/Assemblies/AnimalFoodPreference.dll`

For 1.5 compat: copy the DLL to `1.5/Assemblies/` (same .NET 4.8 target, should work unless APIs differ).
