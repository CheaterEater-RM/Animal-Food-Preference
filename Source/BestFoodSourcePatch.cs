using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Prefix on FoodUtility.BestFoodSourceOnMap for player-owned non-humanlike pawns.
    ///
    /// In vanilla, animals choose food via GenClosest.ClosestThingReachable which
    /// picks the NEAREST valid food by distance — FoodOptimality is never called,
    /// so our tier offsets would have no effect on the animal path.
    ///
    /// This prefix intercepts the animal food search and replaces it with a
    /// tier-scored search that still honours vanilla's performance profile: it reuses
    /// vanilla's own bounded region BFS (GenClosest.RegionwiseBFSWorker) and supplies
    /// a cheap priority function (tier offset minus scaled distance). That keeps the
    /// scan bounded to nearby regions instead of walking every food/plant on the map,
    /// and avoids the per-candidate cost of FoodUtility.FoodOptimality entirely.
    ///
    /// For humanlike/mech getters feeding themselves, the original method runs unmodified.
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap))]
    public static class BestFoodSourceOnMap_Patch
    {
        // Reused across calls to avoid per-search GC pressure.
        // Safe because RimWorld's game loop is single-threaded.
        private static readonly HashSet<Thing> nearbyAnimalFood = new HashSet<Thing>();

        public static bool Prefix(
            ref Thing __result,
            Pawn getter,
            Pawn eater,
            bool desperate,
            ref ThingDef foodDef,
            FoodPreferability maxPref,
            bool allowPlant,
            bool allowDrug,
            bool allowCorpse,
            bool allowDispenserFull,
            bool allowDispenserEmpty,
            bool allowForbidden,
            bool allowSociallyImproper,
            bool allowHarvest,
            bool forceScanWholeMap,
            bool ignoreReservations,
            bool calculateWantedStackCount,
            FoodPreferability minPrefOverride,
            float? minNutrition,
            bool allowVenerated)
        {
            // Only intercept for tame animals — let wild animals, humanlike, and mechs use vanilla logic
            if (eater == null || eater.RaceProps == null ||
                eater.RaceProps.Humanlike || getter.IsColonyMechPlayerControlled ||
                getter.Faction != Faction.OfPlayer)
            {
                return true;
            }

            foodDef = null;
            bool getterCanManipulate = getter.RaceProps.ToolUser &&
                getter.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation);

            if (!getterCanManipulate && getter != eater)
            {
                Log.Error(string.Concat(getter, " tried to find food to bring to ", eater,
                    " but ", getter, " is incapable of Manipulation."));
                __result = null;
                return false;
            }

            // Minimum food preferability — for animals, default is NeverForNutrition
            FoodPreferability minPref = (minPrefOverride == FoodPreferability.Undefined)
                ? FoodPreferability.NeverForNutrition
                : minPrefOverride;

            // ── Base food validator (same checks as vanilla) ─────────────
            Predicate<Thing> foodValidator = delegate(Thing t)
            {
                if (t is Building_NutrientPasteDispenser npd)
                {
                    if (!allowDispenserFull || !getterCanManipulate ||
                        (int)ThingDefOf.MealNutrientPaste.ingestible.preferability < (int)minPref ||
                        (int)ThingDefOf.MealNutrientPaste.ingestible.preferability > (int)maxPref ||
                        !eater.WillEat(ThingDefOf.MealNutrientPaste, getter, true, allowVenerated) ||
                        (t.Faction != getter.Faction && t.Faction != getter.HostFaction) ||
                        (!allowForbidden && t.IsForbidden(getter)) ||
                        !npd.powerComp.PowerOn ||
                        (!allowDispenserEmpty && !npd.HasEnoughFeedstockInHoppers()) ||
                        !t.InteractionCell.Standable(t.Map) ||
                        !IsFoodSociallyProper(t, getter, eater, allowSociallyImproper) ||
                        !getter.Map.reachability.CanReachNonLocal(
                            getter.Position,
                            new TargetInfo(t.InteractionCell, t.Map),
                            PathEndMode.OnCell,
                            TraverseParms.For(getter, Danger.Some)))
                    {
                        return false;
                    }
                    IntVec3 pos = npd.InteractionCell;
                    return !getter.roping.IsRoped || pos.InHorDistOf(getter.roping.RopedTo.Cell, 8f);
                }
                else
                {
                    if ((int)t.def.ingestible.preferability < (int)minPref ||
                        (int)t.def.ingestible.preferability > (int)maxPref ||
                        !eater.WillEat(t, getter, true, allowVenerated) ||
                        !t.def.IsNutritionGivingIngestible ||
                        !t.IngestibleNow ||
                        (!allowCorpse && t is Corpse) ||
                        (!allowDrug && t.def.IsDrug) ||
                        (!allowForbidden && t.IsForbidden(getter)) ||
                        (!desperate && t.IsNotFresh()) ||
                        t.IsDessicated() ||
                        !IsFoodSociallyProper(t, getter, eater, allowSociallyImproper) ||
                        (!getter.AnimalAwareOf(t) && !forceScanWholeMap))
                    {
                        return false;
                    }
                    int stackCount = 1;
                    float singleNutrition = FoodUtility.NutritionForEater(eater, t);
                    if (minNutrition.HasValue)
                        stackCount = FoodUtility.StackCountForNutrition(minNutrition.Value, singleNutrition);
                    else if (calculateWantedStackCount)
                        stackCount = FoodUtility.WillIngestStackCountOf(eater, t.def, singleNutrition);

                    if (!ignoreReservations && !getter.CanReserve(t, 10, stackCount))
                        return false;

                    IntVec3 pos = t.PositionHeld;
                    return !getter.roping.IsRoped || pos.InHorDistOf(getter.roping.RopedTo.Cell, 8f);
                }
            };

            // ── Filter out food other nearby animals are eating ──────────
            nearbyAnimalFood.Clear();
            foreach (Thing item in GenRadial.RadialDistinctThingsAround(
                getter.Position, getter.Map, 2f, useCenter: true))
            {
                if (item is Pawn p && p != getter && p.IsAnimal &&
                    p.CurJob != null && p.CurJob.def == JobDefOf.Ingest &&
                    p.CurJob.GetTarget(TargetIndex.A).HasThing)
                {
                    nearbyAnimalFood.Add(p.CurJob.GetTarget(TargetIndex.A).Thing);
                }
            }

            // ── Animal-specific validator (wraps foodValidator) ──────────
            Predicate<Thing> animalValidator = delegate(Thing t)
            {
                if (!foodValidator(t))
                    return false;
                if (nearbyAnimalFood.Contains(t))
                    return false;
                // Filter out desperate-only food in the normal (non-desperate) pass,
                // matching vanilla's `preferability <= DesperateOnly` (2) cutoff,
                // BUT exempt corpses when they are explicitly allowed — corpse ThingDefs
                // carry DesperateOnly preferability at the def level even though animals
                // can and should eat them. Without this exception, corpses are never
                // scored in the normal pass and raw meat wins by default.
                bool isAllowedCorpse = allowCorpse && t is Corpse;
                if (!isAllowedCorpse &&
                    !(t is Building_NutrientPasteDispenser) &&
                    t.def.ingestible.preferability <= FoodPreferability.DesperateOnly)
                    return false;
                return !t.IsNotFresh();
            };

            // ── Determine search set ─────────────────────────────────────
            ThingRequest thingRequest = (
                (eater.RaceProps.foodType & (FoodTypeFlags.Plant | FoodTypeFlags.Tree)) != 0
                && allowPlant)
                ? ThingRequest.ForGroup(ThingRequestGroup.FoodSource)
                : ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);

            // ── Search parameters ────────────────────────────────────────
            AnimalFoodPreferenceSettings settings = AnimalFoodPreferenceSettings.Instance;
            float distMult = settings?.distanceMultiplier ?? 1f;
            int maxSearch = settings?.maxSearchDistance ?? 0;

            // The configured distance cap is a grazing-performance knob: only apply it
            // when an animal is feeding itself. When a colonist is hauling food to an
            // animal (taming/feeding), search freely like vanilla so far-away animals
            // can still be fed.
            float maxDist = (getter == eater && maxSearch > 0) ? maxSearch : 9999f;

            int maxRegions = GetMaxRegionsToScan(getter, forceScanWholeMap);

            // Mirror vanilla's region-skip optimisation / area restriction handling.
            bool ignoreForbiddenRegions = !allowForbidden &&
                ForbidUtility.CaresAboutForbidden(getter, cellTarget: true) &&
                getter.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap != null;

            // ── Fast path: top-tier food in the animal's current region ──
            // Very common (e.g. grazers standing in a grassy pen): if the most-preferred
            // tier is already in the animal's own region, take it and skip the full bounded
            // region scan. Only triggers when the best local candidate is the TOP tier, so
            // it can never make the animal settle for a worse tier than the full scan would
            // reach. (It may pick a top-tier item a few cells farther than the global nearest,
            // which is irrelevant — the animal still eats its most-preferred food.)
            Thing quickPick = TryGetTopTierFoodInRegion(getter, thingRequest, animalValidator, distMult, maxDist);
            if (quickPick != null)
            {
                foodDef = FoodUtility.GetFinalIngestibleDef(quickPick);
                __result = quickPick;
                return false;
            }

            // ── Bounded region scan, scored by tier (normal pass) ────────
            Thing bestThing = FindBestFood(
                getter, thingRequest, animalValidator, distMult, maxDist, maxRegions, ignoreForbiddenRegions);

            // ── Desperate fallback (relax extra animal filters) ──────────
            // Set desperate=true so the base validator also allows not-fresh (rotting,
            // not dessicated) food, matching vanilla's internal second pass.
            if (bestThing == null)
            {
                desperate = true;
                bestThing = FindBestFood(
                    getter, thingRequest, foodValidator, distMult, maxDist, maxRegions, ignoreForbiddenRegions);
            }

            if (bestThing != null)
            {
                foodDef = FoodUtility.GetFinalIngestibleDef(bestThing);
            }

            __result = bestThing;
            return false;
        }

        /// <summary>
        /// Fast path for the very common case of an animal standing in a region that
        /// already contains its most-preferred food tier (e.g. a grazer in a grassy pen).
        ///
        /// Scans only the getter's current region for the best-scoring valid candidate.
        /// Returns it ONLY if that candidate is the top preferred tier — i.e. its tier
        /// offset equals the maximum (BaseOffset, always tierOrder[0]). In that case no
        /// other region can hold a better tier, so the full bounded scan is unnecessary.
        /// If the local best is not top tier, returns null so the caller falls through to
        /// the full bounded scan (a better tier may lie further out).
        ///
        /// Things in the getter's own region are reachable by construction, so no extra
        /// reachability check is needed beyond the validator.
        /// </summary>
        private static Thing TryGetTopTierFoodInRegion(
            Pawn getter, ThingRequest req, Predicate<Thing> validator, float distanceMultiplier, float maxDistance)
        {
            Region region = getter.Position.GetRegion(getter.Map);
            if (region == null)
                return null;

            IntVec3 root = getter.Position;
            List<Thing> things = region.ListerThings.ThingsMatching(req);
            Thing best = null;
            float bestPrio = float.MinValue;

            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (!t.Spawned)
                    continue;
                float dist = (root - t.Position).LengthManhattan;
                if (dist > maxDistance)
                    continue;
                ThingDef fd = FoodUtility.GetFinalIngestibleDef(t);
                float prio = AnimalFoodPreferenceSettings.GetScoreOffset(FoodClassifier.Classify(fd))
                             - dist * distanceMultiplier;
                if (prio <= bestPrio)
                    continue;
                if (validator != null && !validator(t))
                    continue;
                best = t;
                bestPrio = prio;
            }

            if (best == null)
                return null;

            // Only short-circuit when the local winner is the top preferred tier.
            ThingDef bestDef = FoodUtility.GetFinalIngestibleDef(best);
            if (AnimalFoodPreferenceSettings.GetScoreOffset(FoodClassifier.Classify(bestDef))
                >= AnimalFoodPreferenceSettings.BaseOffset)
            {
                return best;
            }
            return null;
        }

        /// <summary>
        /// Finds the highest-tier reachable food using vanilla's bounded region BFS.
        ///
        /// Reuses GenClosest.RegionwiseBFSWorker (public, pooled, zero-alloc) with a
        /// cheap priority function: <c>tierOffset(category) − dist × distanceMultiplier</c>.
        /// The BFS picks the highest-priority reachable candidate (nearest as a tie-break),
        /// honouring per-thing region-local reachability, the distance cap, and the region
        /// cap. minRegions is set equal to maxRegions so the scan does NOT early-terminate
        /// on the first valid candidate — it must examine the whole bounded neighbourhood to
        /// respect the tier ordering (vanilla stops at the nearest, which we explicitly do not want).
        ///
        /// Replaces the old whole-map iteration that called FoodUtility.FoodOptimality on
        /// every candidate (the dominant large-herd cost).
        /// </summary>
        private static Thing FindBestFood(
            Pawn getter, ThingRequest req, Predicate<Thing> validator,
            float distanceMultiplier, float maxDistance, int maxRegions, bool ignoreForbiddenRegions)
        {
            IntVec3 root = getter.Position;
            Map map = getter.Map;

            Func<Thing, float> priorityGetter = delegate(Thing t)
            {
                ThingDef fd = FoodUtility.GetFinalIngestibleDef(t);
                float dist = (root - t.Position).LengthManhattan;
                return AnimalFoodPreferenceSettings.GetScoreOffset(FoodClassifier.Classify(fd))
                       - dist * distanceMultiplier;
            };

            return GenClosest.RegionwiseBFSWorker(
                root, map, req, PathEndMode.OnCell, TraverseParms.For(getter),
                validator, priorityGetter,
                minRegions: maxRegions, maxRegions: maxRegions, maxDistance: maxDistance,
                regionsSeen: out _,
                traversableRegionTypes: RegionType.Set_Passable,
                ignoreEntirelyForbiddenRegions: ignoreForbiddenRegions);
        }

        /// <summary>
        /// Replica of FoodUtility.GetMaxRegionsToScan (private) for the player-animal case.
        /// A humanlike getter (hauling food to an animal) or forceScanWholeMap searches
        /// without a region bound; a roaming penned animal is bounded to its pen; otherwise
        /// the bound is 100 regions — independent of map size.
        /// </summary>
        private static int GetMaxRegionsToScan(Pawn getter, bool forceScanWholeMap)
        {
            if (getter.RaceProps.Humanlike || forceScanWholeMap)
                return 999999;

            if (getter.Roamer && AnimalPenUtility.GetFixedAnimalFilter().Allows(getter))
            {
                CompAnimalPenMarker pen = AnimalPenUtility.GetCurrentPenOf(getter, allowUnenclosedPens: false);
                if (pen != null)
                    return Math.Min(pen.PenState.ConnectedRegions.Count, 100);
            }

            return 100;
        }

        /// <summary>
        /// Inlined from FoodUtility.IsFoodSourceOnMapSociallyProper (private).
        /// </summary>
        private static bool IsFoodSociallyProper(
            Thing t, Pawn getter, Pawn eater, bool allowSociallyImproper)
        {
            if (!allowSociallyImproper)
            {
                bool animalsCare = !getter.IsAnimal;
                if (!t.IsSociallyProper(getter) &&
                    !t.IsSociallyProper(eater, eater.IsPrisonerOfColony, animalsCare))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
