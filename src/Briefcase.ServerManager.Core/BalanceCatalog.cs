using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Core;

public enum BalanceRoot
{
    Characters,
    Game
}

public enum BalanceCategory
{
    Weapons,
    Passives,
    Expertises,
    Gadgets,
    Npcs,
    Shared
}

public sealed record BalancePath(
    BalanceRoot Root,
    string Group,
    BalanceCategory Category,
    int Variant,
    string Component);

public sealed record BalanceEntry(
    string Id,
    string Table,
    string Row,
    string Field,
    string Label,
    string RowLabel,
    string ComponentLabel,
    JsonNode? Saved,
    JsonNode? Active,
    JsonNode? Default,
    bool Editable,
    string AllowedRange,
    BalanceValueType ValueType,
    BalanceValueConstraint Constraint,
    BalancePath Path)
{
    public string SearchText { get; } = string.Join('\n',
        Id, Table, Row, Field, Label, RowLabel, ComponentLabel, AllowedRange,
        Saved?.ToJsonString() ?? "", Active?.ToJsonString() ?? "", Default?.ToJsonString() ?? "");

    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        SearchText.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record BalanceSlice(
    string Key,
    string Label,
    BalanceCategory Category,
    int Variant,
    string? Row,
    int Count);

public sealed class BalanceCatalog
{
    public BalanceCatalog(string group, string groupLabel, BalanceRoot root,
        IReadOnlyList<BalanceEntry> entries, BalanceGroupSchema schema, IReadOnlyList<BalanceSlice> slices)
    {
        Group = group;
        GroupLabel = groupLabel;
        Root = root;
        Entries = entries;
        Schema = schema;
        Slices = slices;
    }

    public string Group { get; }
    public string GroupLabel { get; }
    public BalanceRoot Root { get; }
    public IReadOnlyList<BalanceEntry> Entries { get; }
    public BalanceGroupSchema Schema { get; }
    public IReadOnlyList<BalanceSlice> Slices { get; }

    public IEnumerable<BalanceEntry> Select(BalanceSlice? slice, string query)
    {
        var entries = Entries.AsEnumerable();
        if (string.IsNullOrWhiteSpace(query) && slice is not null)
        {
            entries = Root == BalanceRoot.Characters
                ? entries.Where(x => x.Path.Category == slice.Category && x.Path.Variant == slice.Variant)
                : entries.Where(x => string.Equals(x.Row, slice.Row, StringComparison.Ordinal));
        }
        return entries.Where(x => x.Matches(query));
    }
}

public static class BalanceCatalogBuilder
{
    public static readonly IReadOnlyList<string> Characters =
    [
        "Ace", "Cavaliere", "Chavez", "Hans", "Larcin", "Octo", "Sasori", "Socialite",
        "Squire", "Vigil", "Xiu", "Yumi"
    ];

    public static BalanceCatalog Build(JsonObject response)
    {
        var group = response["group"]?.GetValue<string>() ?? throw new InvalidDataException("Missing balance group.");
        var customization = BalanceSchemaCustomization.Default;
        var groupLabel = customization.GroupLabel(group,
            response["groupLabels"]?[group]?["displayName"]?.GetValue<string>() ?? HumanizeGroup(group));
        var root = Characters.Contains(group, StringComparer.Ordinal) ? BalanceRoot.Characters : BalanceRoot.Game;
        var entries = new List<BalanceEntry>();
        foreach (var node in response["entries"]?.AsArray() ?? [])
        {
            if (node is not JsonObject entry) continue;
            var table = Required(entry, "table");
            var row = Required(entry, "row");
            var field = Required(entry, "field");
            var id = Required(entry, "id");
            var category = Category(group, table, row, root);
            var component = Component(table, row);
            var path = new BalancePath(root, group, category, Variant(row), component);
            var saved = entry["saved"]?.DeepClone();
            var allowedRange = entry["allowedRange"]?.GetValue<string>() ?? "";
            var valueType = ValueType(saved);
            var inferred = BalanceConstraintParser.Parse(allowedRange, saved);
            var fallbackLabel = entry["presentation"]?["displayName"]?.GetValue<string>() ?? Humanize(field);
            var option = customization.Option(id, fallbackLabel, inferred);
            entries.Add(new BalanceEntry(
                id, table, row, field, option.Label,
                customization.RowLabel(table, row,
                    entry["rowPresentation"]?["displayName"]?.GetValue<string>() ?? Humanize(row)),
                customization.ComponentLabel(component),
                saved, entry["active"]?.DeepClone(), entry["default"]?.DeepClone(),
                entry["editable"]?.GetValue<bool>() == true,
                allowedRange, valueType, option.Constraint, path));
        }

        var schema = BalanceSchemaGenerator.Generate(group, groupLabel, root, entries);
        var slices = root == BalanceRoot.Characters
            ? CharacterSlices(schema)
            : GameSlices(entries);
        return new BalanceCatalog(group, groupLabel, root, entries, schema, slices);
    }

    public static string CategoryLabel(BalanceCategory category) =>
        BalanceSchemaCustomization.Default.CategoryLabel(category);

    public static string VariantLabel(string group, BalanceCategory category, int variant) =>
        BalanceSchemaCustomization.Default.VariantLabel(group, category, variant);

    private static IReadOnlyList<BalanceSlice> CharacterSlices(BalanceGroupSchema schema) =>
        schema.Categories.SelectMany(category => category.Variants.Select(variant =>
                new BalanceSlice($"{category.Category}:{variant.Number}", variant.Label,
                    category.Category, variant.Number, null, variant.OptionCount)))
            .ToArray();

    private static IReadOnlyList<BalanceSlice> GameSlices(IReadOnlyList<BalanceEntry> entries) =>
        entries.GroupBy(x => new { x.Path.Category, x.Row }, x => x)
            .OrderBy(x => CategoryOrder(x.Key.Category)).ThenBy(x => x.First().RowLabel, StringComparer.OrdinalIgnoreCase)
            .Select(x => new BalanceSlice($"{x.Key.Category}:{x.Key.Row}", x.First().RowLabel,
                x.Key.Category, 0, x.Key.Row, x.Count()))
            .ToArray();

    private static BalanceCategory Category(string group, string table, string row, BalanceRoot root)
    {
        if (root == BalanceRoot.Game)
            return group switch
            {
                "Gadgets" => BalanceCategory.Gadgets,
                "PNJ" or "NPCs" => BalanceCategory.Npcs,
                _ => BalanceCategory.Shared
            };
        if (Contains(table, "Passive") || Token(row, "Passive")) return BalanceCategory.Passives;
        if (Contains(table, "Active") || Token(row, "Active")) return BalanceCategory.Expertises;
        return BalanceCategory.Weapons;
    }

    private static int Variant(string row)
    {
        if (Contains(row, "Mod2")) return 3;
        if (Contains(row, "Mod1")) return 2;
        if (Contains(row, "Base")) return 1;
        if (row.EndsWith("Active1", StringComparison.Ordinal)) return 2;
        if (row.EndsWith("Active", StringComparison.Ordinal) ||
            row.EndsWith("ActiveProjectile", StringComparison.Ordinal)) return 1;
        return 0;
    }

    private static string Component(string table, string row)
    {
        if (table == "DT_Balancing_HitscanWeapons")
            return Contains(row, "ADS") ? "ads" : "hitscan-ammunition";
        if (table == "DT_Balancing_SpawnerWeapons")
            return Contains(row, "ADS") ? "ads-weapon-charge" :
                Token(row, "Active") ? "expertise-weapon" : "projectile-weapon-charge";
        if (table == "DT_Balancing_Projectiles")
            return Contains(row, "Split") ? "split-projectile-motion" :
                Contains(row, "Regular") ? "regular-projectile-motion" : "projectile-motion";
        if (table == "DT_Projectiles_Balancing")
            return Contains(row, "Split") ? "split-projectile-damage" :
                Contains(row, "Regular") ? "regular-projectile-damage" : "projectile-damage";
        if (Contains(table, "ChargeTime")) return "charge";
        if (Contains(table, "MeleeDamage")) return "melee";
        if (Contains(table, "WeaponsAbilities") || Contains(table, "WeaponsAbiltiies")) return "weapon-ability";
        if (table == "DT_Gadgets_Balancing") return "gadget-settings";
        if (Contains(table, "ActiveCooldown")) return "expertise-cooldown";
        if (Contains(table, "HealthPool")) return "health-pool";
        if (Contains(table, "StatusEffects")) return "status-effect";
        return "general";
    }

    private static BalanceValueType ValueType(JsonNode? value)
    {
        if (value is JsonValue json && json.TryGetValue<bool>(out _)) return BalanceValueType.Boolean;
        return BalanceValueConstraint.TryDecimal(value, out _) ? BalanceValueType.Number : BalanceValueType.Unsupported;
    }

    private static int CategoryOrder(BalanceCategory category) => category switch
    {
        BalanceCategory.Gadgets => 0,
        BalanceCategory.Npcs => 1,
        BalanceCategory.Shared => 2,
        _ => 3
    };

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static bool Token(string text, string value) => text.Split('_').Any(x =>
        x.StartsWith(value, StringComparison.OrdinalIgnoreCase));

    private static string Required(JsonObject entry, string key) =>
        entry[key]?.GetValue<string>() ?? throw new InvalidDataException($"Missing balance {key}.");

    private static string HumanizeGroup(string group) => group switch
    {
        "PNJ" => "NPCs",
        "Commun" => "Shared",
        _ => Humanize(group)
    };

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var output = new List<char>(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '_')
            {
                output.Add(' ');
                continue;
            }
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1])) output.Add(' ');
            output.Add(character);
        }
        return new string(output.ToArray());
    }
}
