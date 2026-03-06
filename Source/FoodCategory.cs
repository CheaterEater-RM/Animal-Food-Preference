namespace AnimalFoodPreference
{
    /// <summary>
    /// Food categories ordered from most-preferred (lowest numeric value)
    /// to least-preferred (highest numeric value) for animal consumption.
    /// The numeric order matters: lower enum value = higher priority.
    /// </summary>
    public enum FoodCategory : int
    {
        /// <summary>Wild grass, tall grass, bushes, dandelions — grows naturally, zero cost.</summary>
        WildPlant = 0,

        /// <summary>Flowers and non-food planted crops (roses, daylilies, etc.).</summary>
        FlowerOrDecor = 1,

        /// <summary>Food crops that can be harvested (corn, rice, potatoes, berries on plant).</summary>
        FoodCrop = 2,

        /// <summary>Corpses — rot if unused, free nutrition.</summary>
        Corpse = 3,

        /// <summary>Hay — cheap dedicated animal feed.</summary>
        Hay = 4,

        /// <summary>Kibble — made for animals, bulk recipe.</summary>
        Kibble = 5,

        /// <summary>Nutrient paste meals — cheapest meal form.</summary>
        MealNutrientPaste = 6,

        /// <summary>Simple meals — low-value cooked meals.</summary>
        MealSimple = 7,

        /// <summary>Fine meals — medium-value cooked meals.</summary>
        MealFine = 8,

        /// <summary>Lavish meals — high-value cooked meals.</summary>
        MealLavish = 9,

        /// <summary>Raw vegetables, meat, and other raw ingestibles.</summary>
        RawFood = 10,

        /// <summary>Pemmican — caravan food, somewhat expensive.</summary>
        Pemmican = 11,

        /// <summary>Insect jelly — valuable, trading commodity.</summary>
        InsectJelly = 12,

        /// <summary>Survival meals — most valuable, never spoils.</summary>
        SurvivalMeal = 13,

        /// <summary>Unknown / unclassified food from other mods. Falls back to vanilla behavior.</summary>
        Other = 14,
    }
}
