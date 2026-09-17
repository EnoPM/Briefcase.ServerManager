namespace Briefcase.ServerManager.Core;

public sealed record BalanceOptionCustomization(
    string? Label = null,
    decimal? Minimum = null,
    decimal? Maximum = null,
    decimal? Increment = null,
    IReadOnlyList<decimal>? AllowedValues = null);

public static class BalanceSchemaKeys
{
    public static string Variant(string group, BalanceCategory category, int variant) =>
        $"{group}/{category}/{variant}";

    public static string Row(string table, string row) => $"{table}/{row}";
}

public sealed class BalanceSchemaCustomization
{
    public static BalanceSchemaCustomization Default { get; } = new();

    // These dictionaries are the code-first customization surface. Stable option keys use
    // "Table/Row/Field"; stable row keys use BalanceSchemaKeys.Row(table, row).
    private static readonly IReadOnlyDictionary<string, string> GroupLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ["Ace"] = "Agent Ace"
            ["Socialite"] = "Red"
        };

    private static readonly IReadOnlyDictionary<BalanceCategory, string> CategoryLabels =
        new Dictionary<BalanceCategory, string>
        {
            [BalanceCategory.Weapons] = "Weapons",
            [BalanceCategory.Passives] = "Passives",
            [BalanceCategory.Expertises] = "Expertises",
            [BalanceCategory.Gadgets] = "Gadgets",
            [BalanceCategory.Npcs] = "NPCs",
            [BalanceCategory.Shared] = "Shared"
        };

    private static readonly IReadOnlyDictionary<string, string> VariantLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // [BalanceSchemaKeys.Variant("Ace", BalanceCategory.Weapons, 1)] = "First weapon"
        };

    private static readonly IReadOnlyDictionary<string, string> ComponentLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ads"] = "ADS",
            ["hitscan-ammunition"] = "Hitscan and ammunition",
            ["ads-weapon-charge"] = "ADS weapon and charge",
            ["expertise-weapon"] = "Expertise weapon",
            ["projectile-weapon-charge"] = "Projectile weapon and charge",
            ["split-projectile-motion"] = "Split projectile motion",
            ["regular-projectile-motion"] = "Regular projectile motion",
            ["projectile-motion"] = "Projectile motion",
            ["split-projectile-damage"] = "Split projectile damage",
            ["regular-projectile-damage"] = "Regular projectile damage",
            ["projectile-damage"] = "Projectile damage",
            ["charge"] = "Charge",
            ["melee"] = "Melee",
            ["weapon-ability"] = "Weapon ability",
            ["gadget-settings"] = "Gadget settings",
            ["expertise-cooldown"] = "Expertise cooldown",
            ["health-pool"] = "Health pool",
            ["status-effect"] = "Status effect",
            ["general"] = "General"
        };

    private static readonly IReadOnlyDictionary<string, string> RowLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // [BalanceSchemaKeys.Row("DT_Balancing_HitscanWeapons", "Ace_Weapon_Base")] = "Weapon 1"
        };

    // Example entry:
    // ["DT_Balancing_HitscanWeapons/Ace_Weapon_Base/Damage"] =
    //     new(Label: "Weapon 1 damage", Minimum: 0, Maximum: 100, Increment: .5m)
    private static readonly IReadOnlyDictionary<string, BalanceOptionCustomization> Options =
        new Dictionary<string, BalanceOptionCustomization>(StringComparer.Ordinal)
        {
        };

    public string GroupLabel(string key, string fallback) => Get(GroupLabels, key, fallback);

    public string CategoryLabel(BalanceCategory category) =>
        CategoryLabels.TryGetValue(category, out var label) ? label : category.ToString();

    public string VariantLabel(string group, BalanceCategory category, int variant)
    {
        var fallback = variant == 0
            ? category switch
            {
                BalanceCategory.Passives => "Shared data",
                BalanceCategory.Expertises => "Additional data",
                _ => "Other data"
            }
            : $"{CategoryLabel(category).TrimEnd('s')} {variant}";
        return Get(VariantLabels, BalanceSchemaKeys.Variant(group, category, variant), fallback);
    }

    public string ComponentLabel(string key) => Get(ComponentLabels, key, key);

    public string RowLabel(string table, string row, string fallback) =>
        Get(RowLabels, BalanceSchemaKeys.Row(table, row), fallback);

    public (string Label, BalanceValueConstraint Constraint) Option(
        string id, string fallbackLabel, BalanceValueConstraint inferred)
    {
        if (!Options.TryGetValue(id, out var customization)) return (fallbackLabel, inferred);
        var minimum = NarrowMinimum(inferred.Minimum, customization.Minimum);
        var maximum = NarrowMaximum(inferred.Maximum, customization.Maximum);
        if (minimum is not null && maximum is not null && minimum > maximum)
            throw new InvalidDataException($"Invalid balancing bounds for '{id}'.");
        var allowedValues = customization.AllowedValues?
            .Where(x => (minimum is null || x >= minimum) && (maximum is null || x <= maximum))
            .Distinct()
            .Order()
            .ToArray() ?? inferred.AllowedValues;
        return (customization.Label ?? fallbackLabel,
            new BalanceValueConstraint(minimum, maximum,
                customization.Increment ?? inferred.Increment, allowedValues));
    }

    private static decimal? NarrowMinimum(decimal? inferred, decimal? customized) =>
        inferred is null ? customized : customized is null ? inferred : Math.Max(inferred.Value, customized.Value);

    private static decimal? NarrowMaximum(decimal? inferred, decimal? customized) =>
        inferred is null ? customized : customized is null ? inferred : Math.Min(inferred.Value, customized.Value);

    private static string Get(IReadOnlyDictionary<string, string> source, string key, string fallback) => source.GetValueOrDefault(key, fallback);
}
