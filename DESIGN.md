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
  → BestFoodSourceOnMap_Patch.Prefix intercepts (player non-humanlike eaters only):
      1. Builds food validator (same checks as vanilla)
      2. Filters out food other nearby animals are eating
      3. FAST PATH: scans only the animal's current region; if the best valid candidate
         there is the TOP tier (offset == BaseOffset, i.e. tierOrder[0]), returns it
         immediately. Covers the very common "grazer standing in a grassy pen" case and
         skips the full scan. Safe because no other region can hold a better tier.
      4. Otherwise runs a BOUNDED region BFS (GenClosest.RegionwiseBFSWorker) seeded with
         a cheap priority function instead of calling FoodUtility.FoodOptimality:
            priority(t) = Settings.GetScoreOffset(FoodClassifier.Classify(def))
                          − dist × distanceMultiplier
         The BFS reuses vanilla's pooled region machinery: it picks the highest-priority
         reachable candidate (nearest as tie-break), bounded by maxRegions (≈100, like
         vanilla's GetMaxRegionsToScan). minRegions is
         set equal to maxRegions so it scans the whole bounded neighbourhood rather than
         stopping at the nearest (vanilla's behaviour, which would ignore tiers).
      5. If nothing found, sets desperate=true and retries with the relaxed base
         validator (also allows rotting-but-not-dessicated food, matching vanilla).
  → Animal eats the best-tier reachable food

FoodOptimality_Patch.Postfix is retained but no longer runs in the hot search loop.
It still applies the same tier offset (and neutralises vanilla biases) for the
secondary callers that score animal food via FoodOptimality directly — notably
FoodUtility.TryFindBestFoodSourceFor's inventory-vs-map comparison — so tier
preference stays consistent there.
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
| Distance multiplier | Float (0.5–5.0) | Scales the distance penalty in the priority score. 1.0 = vanilla weighting; higher favours closer food. Default 1.0. |
| Max search distance | Integer (cells) | Optional extra cap on search radius. 0 = no extra cap (search is always region-bounded, ~100 regions, never whole-map). Default 0. |
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

- **BestFoodSourceOnMap prefix**: Uses vanilla's bounded region BFS
  (`GenClosest.RegionwiseBFSWorker`), so cost is bounded by nearby regions
  (≈100) and does **not** scale with total map
  size — critical for large herds on grassy maps where the old whole-map scan walked
  every plant on the map. The expensive validator (WillEat / CanReserve / reachability)
  only runs for candidates that beat the current best priority; the per-candidate work
  is just a cached classification lookup and a distance subtraction.
- **Current-region fast path**: before the full scan, the prefix checks only the animal's
  own region and short-circuits if the best valid candidate there is the top tier. The
  common grazing case (animal already among its preferred food) costs a single-region
  scan instead of a full bounded BFS.
- **No per-candidate `FoodOptimality`**: the hot loop scores via cheap tier offset minus
  scaled distance. `FoodUtility.FoodOptimality` (which does a `CompRottable` lookup,
  allocates a thoughts list, and loops traits) is no longer called per candidate. The
  trade-off is that vanilla's small within-tier nudges (e.g. the +12 "about to rot" bonus,
  ≈0 mood/trait offsets for animals) are not applied — negligible because the tier spacing
  dominates them.
- **FoodOptimality postfix**: retained for secondary callers only; 1 bool check + 2–3
  dictionary lookups + 1 float add. Zero allocations.
- **Classification cache**: `Dictionary<ThingDef, FoodCategory>`, ~200 entries in a modded game. Populated lazily, invalidated only on settings change.
- **Score cache**: `Dictionary<FoodCategory, float>`, 15 entries. Rebuilt only on tier reorder.
- **No per-tick cost**: only runs when an animal actively searches for food.
- **Profiling**: `Analyzer.xml` (dev-only, git-ignored) adds an `AnimalFoodPreference`
  tab to Dubs Performance Analyzer wired to the patch types and the patched host methods.

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

### Vanilla-Alignment Behaviors

- **Preferability cutoff**: the animal validator excludes `preferability <= DesperateOnly` (2) in
  the normal pass, matching vanilla's `<= 2`, with an explicit exemption for allowed corpses (which
  carry `DesperateOnly` at the def level but are a normal-tier choice here by design). Earlier the
  cutoff was one notch stricter (`<= DesperateOnlyForHumanlikes`), which wrongly excluded raw
  human/insect meat from the normal pass.
- **Desperate fallback**: when the normal pass finds nothing, the fallback sets `desperate=true`
  before re-scanning, so rotting (not dessicated) food becomes eligible for a merely-Hungry animal —
  same as vanilla's internal second pass. Dessicated food is always excluded.
- **MealTerrible**: classified as the cheapest meal tier (`MealNutrientPaste` category) rather than
  falling through to `Other`. Covers Biotech baby food and modded "terrible" meals.

## Build

```
cd Source
dotnet build -c Release
```
Output: `1.6/Assemblies/AnimalFoodPreference.dll`

For 1.5 compat: copy the DLL to `1.5/Assemblies/` (same .NET 4.8 target, should work unless APIs differ).
