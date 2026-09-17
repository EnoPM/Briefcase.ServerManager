using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace Briefcase.ServerManager;

internal sealed class SettingsEditor
{
    private static readonly IReadOnlyDictionary<string, string> MapNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DI_Hardsell"] = "Hard Sell",
        ["DI_SR"] = "Silver Reef",
        ["DI_DS"] = "Diamond Spire",
        ["DI_FS"] = "Fragrant Shore",
        ["DI_SE"] = "Sound Eclipse",
        ["DI_FSN"] = "Fragrant Shore (Night)",
        ["DI_HSD"] = "Hard Sell (Morning)"
    };

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
        var groups = new Dictionary<string, List<KeyValuePair<string, JsonObject>>>(StringComparer.Ordinal);
        var categoryLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        var discoveredCategories = new List<string>();
        foreach (var property in properties)
        {
            if (property.Value is not JsonObject descriptor) continue;
            var category = descriptor["category"]?.GetValue<string>() ?? "general";
            if (!groups.TryGetValue(category, out var fields))
            {
                fields = [];
                groups.Add(category, fields);
                discoveredCategories.Add(category);
                categoryLabels[category] = descriptor["categoryLabel"]?.GetValue<string>() ?? Humanize(category);
            }
            fields.Add(new KeyValuePair<string, JsonObject>(property.Key, descriptor));
        }

        var categoryOrder = new List<string>();
        if (schema["categoryOrder"] is JsonArray declaredOrder)
            foreach (var item in declaredOrder)
            {
                var category = item?.GetValue<string>();
                if (category is not null && groups.ContainsKey(category) && !categoryOrder.Contains(category))
                    categoryOrder.Add(category);
            }
        foreach (var category in new[] { "identity", "network", "gameplay", "bots", "maps", "heat" })
            if (groups.ContainsKey(category) && !categoryOrder.Contains(category))
                categoryOrder.Add(category);
        foreach (var category in discoveredCategories)
            if (!categoryOrder.Contains(category))
                categoryOrder.Add(category);

        for (var categoryIndex = 0; categoryIndex < categoryOrder.Count; categoryIndex++)
        {
            var category = categoryOrder[categoryIndex];
            root.Children.Add(new TextBlock
            {
                Text = categoryLabels[category], FontWeight = FontWeight.Bold, FontSize = 16,
                Margin = new Thickness(0, categoryIndex == 0 ? 0 : 12, 0, 2)
            });
            foreach (var property in groups[category])
            {
                var descriptor = property.Value;
                var type = descriptor["type"]?.GetValue<string>() ?? "string";
                var value = saved[property.Key];
                var control = CreateControl(property.Key, type, value, descriptor);
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
        }
        View = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#19151F")),
            BorderBrush = new SolidColorBrush(Color.Parse("#332A3D")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 20), Child = root
        };
    }

    private static Control CreateControl(string key, string type, JsonNode? value, JsonObject descriptor)
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
            var isMapRotation = string.Equals(key, "MapRotation", StringComparison.Ordinal);
            var arrayOptions = descriptor["enum"] as JsonArray ??
                               (descriptor["items"] as JsonObject)?["enum"] as JsonArray;
            if (isMapRotation || arrayOptions is not null)
            {
                var available = isMapRotation
                    ? MapNames.Keys.ToArray()
                    : arrayOptions!.Select(x => x?.GetValue<string>() ?? "")
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
                var selected = value is JsonArray arrayValues
                    ? arrayValues.Select(x => x?.GetValue<string>() ?? "")
                        .Where(x => available.Contains(x, StringComparer.Ordinal)).ToArray()
                    : [];
                return new ChoiceArrayEditor(available, selected,
                    option => isMapRotation && MapNames.TryGetValue(option, out var name)
                        ? name : Humanize(option),
                    isMapRotation);
            }
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
                "array" when control is ChoiceArrayEditor choices => choices.ReadValues(),
                "array" => new JsonArray((((TextBox)control).Text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                _ when control is ComboBox combo => JsonValue.Create(combo.SelectedItem?.ToString() ?? ""),
                _ => JsonValue.Create(((TextBox)control).Text ?? "")
            };
        }
        return values;
    }

    private sealed class ChoiceArrayEditor : Border
    {
        private readonly List<Choice> choices;
        private readonly StackPanel rows = new() { Spacing = 6 };
        private readonly bool requiresSelection;

        public ChoiceArrayEditor(IEnumerable<string> available, IEnumerable<string> selected,
            Func<string, string> label, bool requiresSelection)
        {
            this.requiresSelection = requiresSelection;
            var selectedValues = selected.Distinct(StringComparer.Ordinal).ToArray();
            var selectedSet = selectedValues.ToHashSet(StringComparer.Ordinal);
            var ordered = selectedValues.Concat(available.Where(x => !selectedSet.Contains(x)));
            choices = ordered.Select(value => new Choice(value, label(value), selectedSet.Contains(value))).ToList();

            var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
            heading.Children.Add(new TextBlock
            {
                Text = "Select maps and arrange their rotation order.",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = .72,
                TextWrapping = TextWrapping.Wrap
            });
            var selectAll = new Button
            {
                Content = IconLabel(Icon.CheckmarkCircle, "Select all"), MinHeight = 32, Padding = new Thickness(12, 5),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            selectAll.Click += (_, _) =>
            {
                foreach (var choice in choices) choice.Selected = true;
                RebuildRows();
            };
            Grid.SetColumn(selectAll, 1);
            heading.Children.Add(selectAll);

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(heading);
            content.Children.Add(rows);
            Background = new SolidColorBrush(Color.Parse("#211A29"));
            BorderBrush = new SolidColorBrush(Color.Parse("#332A3D"));
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(10);
            Child = content;
            RebuildRows();
        }

        public JsonArray ReadValues()
        {
            var selected = choices.Where(x => x.Selected).Select(x => x.Value).ToArray();
            if (requiresSelection && selected.Length == 0)
                throw new InvalidOperationException("Select at least one map for the rotation.");
            return new JsonArray(selected.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }

        private void RebuildRows()
        {
            rows.Children.Clear();
            for (var index = 0; index < choices.Count; index++)
            {
                var choice = choices[index];
                var checkbox = new CheckBox
                {
                    IsChecked = choice.Selected,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinHeight = 32,
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = choice.Label, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = choice.Value, FontSize = 11, Opacity = .58 }
                        }
                    }
                };
                checkbox.IsCheckedChanged += (_, _) => choice.Selected = checkbox.IsChecked == true;

                var up = OrderButton(Icon.ArrowUp, "Move map earlier", index, -1);
                var down = OrderButton(Icon.ArrowDown, "Move map later", index, 1);
                up.IsEnabled = index > 0;
                down.IsEnabled = index + 1 < choices.Count;

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
                row.Children.Add(checkbox);
                Grid.SetColumn(up, 1);
                row.Children.Add(up);
                Grid.SetColumn(down, 2);
                row.Children.Add(down);
                rows.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#27202F")),
                    CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 6), Child = row
                });
            }
        }

        private Button OrderButton(Icon icon, string tooltip, int index, int direction)
        {
            var button = new Button
            {
                Content = new FluentIcon { Icon = icon, IconSize = IconSize.Size16 },
                MinWidth = 34, MinHeight = 32, Padding = new Thickness(8, 4),
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(button, tooltip);
            button.Click += (_, _) =>
            {
                var destination = index + direction;
                if (destination < 0 || destination >= choices.Count) return;
                (choices[index], choices[destination]) = (choices[destination], choices[index]);
                RebuildRows();
            };
            return button;
        }

        private static Control IconLabel(Icon icon, string text)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(new FluentIcon { Icon = icon, IconSize = IconSize.Size16 });
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            return content;
        }

        private sealed class Choice(string value, string label, bool selected)
        {
            public string Value { get; } = value;
            public string Label { get; } = label;
            public bool Selected { get; set; } = selected;
        }
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
