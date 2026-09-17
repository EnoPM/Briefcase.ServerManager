using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Briefcase.ServerManager;

internal sealed class SettingsEditor
{
    private readonly Dictionary<string, (string Type, Control Control)> controls = new(StringComparer.Ordinal);
    public Border View { get; }
    public string Revision { get; }

    public SettingsEditor(JsonObject document)
    {
        Revision = document["revision"]?.GetValue<string>() ?? "";
        var schema = document["schema"]?.AsObject() ?? throw new InvalidDataException("Missing settings schema.");
        var properties = schema["properties"]?.AsObject() ?? throw new InvalidDataException("Missing settings properties.");
        var saved = document["saved"]?.AsObject() ?? throw new InvalidDataException("Missing saved settings.");
        var root = new StackPanel { Spacing = 10 };
        string? previousCategory = null;
        foreach (var property in properties)
        {
            if (property.Value is not JsonObject descriptor) continue;
            var category = descriptor["categoryLabel"]?.GetValue<string>() ??
                           descriptor["category"]?.GetValue<string>() ?? "General";
            if (!string.Equals(category, previousCategory, StringComparison.Ordinal))
            {
                root.Children.Add(new TextBlock
                {
                    Text = Humanize(category), FontWeight = FontWeight.Bold, FontSize = 16,
                    Margin = new Thickness(0, previousCategory is null ? 0 : 12, 0, 2)
                });
                previousCategory = category;
            }
            var type = descriptor["type"]?.GetValue<string>() ?? "string";
            var value = saved[property.Key];
            var control = CreateControl(type, value, descriptor);
            controls[property.Key] = (type, control);
            var label = descriptor["displayName"]?.GetValue<string>() ??
                        descriptor["description"]?.GetValue<string>() ?? Humanize(property.Key);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 14 };
            row.Children.Add(new TextBlock
            {
                Text = label, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0)
            });
            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            root.Children.Add(row);
        }
        View = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#18141F")),
            BorderBrush = new SolidColorBrush(Color.Parse("#5C4F6E")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(22, 20), Child = root
        };
    }

    private static Control CreateControl(string type, JsonNode? value, JsonObject descriptor)
    {
        if (type == "boolean")
            return new CheckBox { IsChecked = value?.GetValue<bool>() ?? false, MinHeight = 32 };
        if (type is "integer" or "number")
        {
            var number = value?.GetValue<double>() ?? 0;
            return new NumericUpDown
            {
                Value = (decimal)number,
                Minimum = descriptor["minimum"] is null ? decimal.MinValue : (decimal)descriptor["minimum"]!.GetValue<double>(),
                Maximum = descriptor["maximum"] is null ? decimal.MaxValue : (decimal)descriptor["maximum"]!.GetValue<double>(),
                Increment = type == "integer" ? 1 : 0.1m,
                FormatString = type == "integer" ? "0" : "0.###",
                MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }
        if (type == "array")
        {
            var text = value is JsonArray values
                ? string.Join(", ", values.Select(x => x?.GetValue<string>() ?? "")) : "";
            return new TextBox { Text = text, MinHeight = 36, PlaceholderText = "Comma-separated values" };
        }
        if (descriptor["enum"] is JsonArray options)
        {
            var items = options.Select(x => x?.GetValue<string>() ?? "").ToArray();
            return new ComboBox { ItemsSource = items, SelectedItem = value?.GetValue<string>() ?? "", MinHeight = 36 };
        }
        if (descriptor["secret"]?.GetValue<bool>() == true)
            return new TextBox { Text = value?.GetValue<string>() ?? "", PasswordChar = '●', MinHeight = 36 };
        return new TextBox { Text = value?.GetValue<string>() ?? "", MinHeight = 36 };
    }

    public JsonObject ReadValues()
    {
        var values = new JsonObject();
        foreach (var field in controls)
        {
            var (type, control) = field.Value;
            values[field.Key] = type switch
            {
                "boolean" => JsonValue.Create(((CheckBox)control).IsChecked == true),
                "integer" => JsonValue.Create((long)(((NumericUpDown)control).Value ?? 0)),
                "number" => JsonValue.Create((double)(((NumericUpDown)control).Value ?? 0)),
                "array" => new JsonArray((((TextBox)control).Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                _ when control is ComboBox combo => JsonValue.Create(combo.SelectedItem?.ToString() ?? ""),
                _ => JsonValue.Create(((TextBox)control).Text ?? "")
            };
        }
        return values;
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "General";
        var chars = new List<char>(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            if (i > 0 && char.IsUpper(value[i]) && char.IsLower(value[i - 1])) chars.Add(' ');
            chars.Add(value[i] is '_' or '-' ? ' ' : value[i]);
        }
        var result = new string(chars.ToArray()).Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.ToLowerInvariant());
    }
}
