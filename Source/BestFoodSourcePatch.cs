using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AnimalFoodPreference
{
    /// <summary>
    /// Prefix on FoodUtility.BestFoodSourceOnMap for non-humanlike pawns.
    ///
    /// In vanilla, animals choose food via GenClosest.ClosestThingReachable which
    /// picks the nearest valid food by distance — FoodOptimality is never called.
    /// This makes our FoodOptimality postfix (tier offsets) completely ineffective.
    ///
    /// This prefix intercepts the animal food search and replaces it with an
    /// optimality-scored search (equivalent to SpawnedFoodSearchInnerScan),
    /// so FoodOptimality — and our tier offset postfix — actually runs.
    ///
    /// For humanlike/mech pawns, the original method runs unmodified.
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.BestFoodSourceOnMap),
        new Type[]
        {
            typeof(Pawn), typeof(Pawn), typeof(bool), typeof(ThingDef),
            typeof(FoodPreferability), typeof(bool), typeof(bool), typeof(bool),
            typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
            typeof(bool), typeof(bool), typeof(bool), typeof(FoodPreferability),
            typeof(float?), typeof(bool)
        },
        new ArgumentType[]
        {
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal
        })]
    public static class BestFoodSourceOnMap_Patch
    {
        // Pooled to avoid per-search allocations. Safe because RimWorld's game loop is
        // single-threaded and the validators called during FindBestByOptimality never
        // re-enter this Prefix. If that assumption changes, switch back to a local allocation.
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
                // BUT exempt corpses when they are explicitly allowed — corpse ThingDefs
                // carry DesperateOnly preferability at the def level even though animals
                // can and should eat them. Without this exception, corpses are never
                // scored in the normal pass and raw meat wins by default.
                bool isAllowedCorpse = allowCorpse && t is Corpse;
                if (!isAllowedCorpse &&
                    !(t is Building_NutrientPasteDispenser) &&
                    t.def.ingestible.preferability <= FoodPreferability.DesperateOnlyForHumanlikes)
                    return false;
                return !t.IsNotFresh();
            };

            // ── Determine search set ─────────────────────────────────────
            ThingRequest thingRequest = (
                (eater.RaceProps.foodType & (FoodTypeFlags.Plant | FoodTypeFlags.Tree)) != 0
                && allowPlant)
                ? ThingRequest.ForGroup(ThingRequestGroup.FoodSource)
                : ThingRequest.ForGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);

            List<Thing> searchSet = getter.Map.listerThings.ThingsMatching(thingRequest);

            // ── Score candidates by FoodOptimality and pick the best ─────
            AnimalFoodPreferenceSettings settings = AnimalFoodPreferenceSettings.Instance;
            float distMult = settings?.distanceMultiplier ?? 1f;
            float maxDist = settings?.maxSearchDistance ?? 0;

            Thing bestThing = FindBestByOptimality(eater, getter, searchSet, animalValidator, distMult, maxDist);

            // ── Desperate fallback (relax extra animal filters) ──────────
            if (bestThing == null)
            {
                bestThing = FindBestByOptimality(eater, getter, searchSet, foodValidator, distMult, maxDist);
            }

            if (bestThing != null)
            {
                foodDef = FoodUtility.GetFinalIngestibleDef(bestThing);
            }

            __result = bestThing;
            return false;
        }

        /// <summary>
        /// Iterates all candidates, scoring each by FoodOptimality (which includes
        /// our tier offsets via the FoodOptimality_Patch postfix). Returns the
        /// highest-scoring reachable food that passes the validator.
        ///
        /// distanceMultiplier scales how strongly distance penalises a candidate.
        /// 1.0 matches vanilla weighting. Higher values favour closer food.
        ///
        /// maxSearchDistance (>0) skips candidates beyond that many cells,
        /// cutting the candidate set and reducing per-search CPU cost.
        ///
        /// Equivalent to the private SpawnedFoodSearchInnerScan method that
        /// vanilla uses for humanlike pawns.
        /// </summary>
        private static Thing FindBestByOptimality(
            Pawn eater, Pawn getter, List<Thing> searchSet, Predicate<Thing> validator,
            float distanceMultiplier, float maxSearchDistance)
        {
            if (searchSet == null)
                return null;

            Thing result = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < searchSet.Count; i++)
            {
                Thing thing = searchSet[i];
                if (!thing.Spawned)
                    continue;

                float dist = (getter.Position - thing.Position).LengthManhattan;

                // Skip candidates beyond the configured search radius
                if (maxSearchDistance > 0 && dist > maxSearchDistance)
                    continue;

                ThingDef thingFoodDef = FoodUtility.GetFinalIngestibleDef(thing);
                // Pass scaled distance so FoodOptimality applies our distance weight.
                // Vanilla formula: score -= dist. With multiplier: score -= dist * multiplier.
                float score = FoodUtility.FoodOptimality(eater, thing, thingFoodDef, dist * distanceMultiplier);

                if (score <= bestScore)
                    continue;

                // Expensive checks only for potential winners
                if (!getter.Map.reachability.CanReach(
                    getter.Position, thing, PathEndMode.OnCell,
                    TraverseParms.For(getter)))
                    continue;

                if (validator != null && !validator(thing))
                    continue;

                result = thing;
                bestScore = score;
            }

            return result;
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
