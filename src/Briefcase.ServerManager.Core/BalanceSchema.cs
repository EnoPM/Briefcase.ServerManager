using System.Globalization;
using System.Text.Json.Nodes;

namespace Briefcase.ServerManager.Core;

public enum BalanceValueType
{
    Boolean,
    Number,
    Unsupported
}

public sealed record BalanceValueConstraint(
    decimal? Minimum,
    decimal? Maximum,
    decimal Increment,
    IReadOnlyList<decimal> AllowedValues)
{
    public bool TryValidate(JsonNode? node, BalanceValueType valueType, out string error)
    {
        error = "";
        if (valueType == BalanceValueType.Boolean)
        {
            if (node is JsonValue boolean && boolean.TryGetValue<bool>(out _)) return true;
            error = "The value must be true or false.";
            return false;
        }
        if (valueType != BalanceValueType.Number || !TryDecimal(node, out var value))
        {
            error = "The value must be a number.";
            return false;
        }
        if (Minimum is not null && value < Minimum.Value)
        {
            error = $"The minimum allowed value is {Format(Minimum.Value)}.";
            return false;
        }
        if (Maximum is not null && value > Maximum.Value)
        {
            error = $"The maximum allowed value is {Format(Maximum.Value)}.";
            return false;
        }
        if (AllowedValues.Count > 0 && !AllowedValues.Contains(value))
        {
            error = $"Allowed values: {string.Join(", ", AllowedValues.Select(Format))}.";
            return false;
        }
        return true;
    }

    public static bool TryDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue json) return false;
        if (json.TryGetValue<decimal>(out value)) return true;
        if (json.TryGetValue<int>(out var signed32)) { value = signed32; return true; }
        if (json.TryGetValue<long>(out var signed64)) { value = signed64; return true; }
        if (json.TryGetValue<uint>(out var unsigned32)) { value = unsigned32; return true; }
        if (json.TryGetValue<ulong>(out var unsigned64))
        {
            value = unsigned64;
            return true;
        }
        if (json.TryGetValue<float>(out var single) && float.IsFinite(single))
        {
            value = (decimal)single;
            return true;
        }
        if (!json.TryGetValue<double>(out var number) || double.IsNaN(number) || double.IsInfinity(number))
            return false;
        if (number > (double)decimal.MaxValue || number < (double)decimal.MinValue) return false;
        value = (decimal)number;
        return true;
    }

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record BalanceOptionSchema(BalanceEntry Entry);

public sealed record BalanceRowSchema(
    string Key,
    string Label,
    IReadOnlyList<BalanceOptionSchema> Options);

public sealed record BalanceComponentSchema(
    string Key,
    string Label,
    IReadOnlyList<BalanceRowSchema> Rows)
{
    public int OptionCount => Rows.Sum(x => x.Options.Count);
}

public sealed record BalanceVariantSchema(
    int Number,
    string Label,
    IReadOnlyList<BalanceComponentSchema> Components)
{
    public int OptionCount => Components.Sum(x => x.OptionCount);
}

public sealed record BalanceCategorySchema(
    BalanceCategory Category,
    string Label,
    IReadOnlyList<BalanceVariantSchema> Variants)
{
    public int OptionCount => Variants.Sum(x => x.OptionCount);
}

public sealed record BalanceGroupSchema(
    string Key,
    string Label,
    BalanceRoot Root,
    IReadOnlyList<BalanceCategorySchema> Categories)
{
    public int OptionCount => Categories.Sum(x => x.OptionCount);
}

public static class BalanceSchemaGenerator
{
    public static BalanceGroupSchema Generate(string group, string label, BalanceRoot root,
        IReadOnlyList<BalanceEntry> entries)
    {
        var categories = entries
            .GroupBy(x => x.Path.Category)
            .OrderBy(x => CategoryOrder(x.Key))
            .Select(category => new BalanceCategorySchema(
                category.Key,
                BalanceSchemaCustomization.Default.CategoryLabel(category.Key),
                category.GroupBy(x => x.Path.Variant)
                    .OrderBy(x => VariantOrder(x.Key))
                    .Select(variant => new BalanceVariantSchema(
                        variant.Key,
                        BalanceSchemaCustomization.Default.VariantLabel(group, category.Key, variant.Key),
                        variant.GroupBy(x => x.Path.Component)
                            .OrderBy(x => x.First().ComponentLabel, StringComparer.OrdinalIgnoreCase)
                            .Select(component => new BalanceComponentSchema(
                                component.Key,
                                component.First().ComponentLabel,
                                component.GroupBy(x => x.Row)
                                    .OrderBy(x => x.First().RowLabel, StringComparer.OrdinalIgnoreCase)
                                    .Select(row => new BalanceRowSchema(
                                        BalanceSchemaKeys.Row(row.First().Table, row.Key),
                                        row.First().RowLabel,
                                        row.Select(x => new BalanceOptionSchema(x)).ToArray()))
                                    .ToArray()))
                            .ToArray()))
                    .ToArray()))
            .ToArray();
        return new BalanceGroupSchema(group, label, root, categories);
    }

    private static int VariantOrder(int value) => value == 0 ? 4 : value;

    private static int CategoryOrder(BalanceCategory category) => category switch
    {
        BalanceCategory.Weapons => 0,
        BalanceCategory.Passives => 1,
        BalanceCategory.Expertises => 2,
        BalanceCategory.Gadgets => 3,
        BalanceCategory.Npcs => 4,
        _ => 5
    };
}

public static class BalanceConstraintParser
{
    public static BalanceValueConstraint Parse(string text, JsonNode? currentValue)
    {
        decimal? minimum = null;
        decimal? maximum = null;
        var parts = text.Split(" to ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 &&
            decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinimum) &&
            decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaximum))
        {
            minimum = parsedMinimum;
            maximum = parsedMaximum;
        }
        return new BalanceValueConstraint(minimum, maximum,
            InferIncrement(currentValue, minimum, maximum), []);
    }

    private static decimal InferIncrement(JsonNode? currentValue, decimal? minimum, decimal? maximum)
    {
        var scale = Math.Max(Scale(minimum), Scale(maximum));
        if (BalanceValueConstraint.TryDecimal(currentValue, out var current))
        {
            var magnitude = Math.Abs(current);
            if (magnitude is > 0 and < .01m) return .0001m;
            if (magnitude is > 0 and < 1m) scale = Math.Max(scale, 2);
        }
        scale = Math.Clamp(scale, 1, 6);
        var increment = 1m;
        for (var index = 0; index < scale; index++) increment /= 10m;
        return increment;
    }

    private static int Scale(decimal? value) => value is null
        ? 0
        : (decimal.GetBits(value.Value)[3] >> 16) & 0x7F;
}
