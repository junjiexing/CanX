// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CanX.Core.Downloads;

namespace CanX.App.ViewModels;

public sealed class DownloadCommonOptionViewModel : INotifyPropertyChanged
{
    private readonly decimal _minimum;
    private readonly decimal _maximum;
    private string _value = string.Empty;

    public DownloadCommonOptionViewModel(
        string label,
        string placeholder,
        DownloadOptionDefinition definition)
    {
        Label = label;
        Placeholder = placeholder;
        Definition = definition;
        EditorKind = ResolveEditorKind(definition, out _minimum, out _maximum);
        ChoiceItems = EditorKind == DownloadOptionEditorKind.Choice
            ? [
                new(string.Empty, "不修改"),
                .. ParsePossibleValues(definition.PossibleValues)
                    .Select(static value => new DownloadOptionChoice(value, value)),
            ]
            : [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; }

    public string Placeholder { get; }

    public DownloadOptionDefinition Definition { get; }

    public string Name => Definition.Name;

    public DownloadOptionEditorKind EditorKind { get; }

    public bool IsSingleLineTextEditor => EditorKind == DownloadOptionEditorKind.Text && !IsTrackerList;

    public bool IsMultilineTextEditor => EditorKind == DownloadOptionEditorKind.Text && IsTrackerList;

    public bool IsBooleanEditor => EditorKind == DownloadOptionEditorKind.Boolean;

    public bool IsNumberEditor => EditorKind == DownloadOptionEditorKind.Number;

    public bool IsUnitNumberEditor => EditorKind == DownloadOptionEditorKind.UnitNumber;

    public bool IsChoiceEditor => EditorKind == DownloadOptionEditorKind.Choice;

    public double EditorWidth => Name switch
    {
        "bt-tracker" or "bt-exclude-tracker" or "checksum" => 460,
        "all-proxy" => 360,
        "dht-listen-port" => 220,
        _ when IsBooleanEditor => 190,
        _ when IsNumberEditor => 150,
        _ when IsUnitNumberEditor => 210,
        _ when IsChoiceEditor => 180,
        _ => 300,
    };

    public string UnitLabel => Name == "min-split-size" ? "KiB" : "KiB/s";

    public IReadOnlyList<DownloadOptionChoice> ChoiceItems { get; }

    public string ScopeSummary
    {
        get
        {
            var scopes = new List<string>();
            if (Definition.Initial) scopes.Add("新建");
            if (Definition.ChangeActive) scopes.Add("活动");
            if (Definition.ChangeReserved) scopes.Add("等待");
            if (Definition.ChangeGlobal) scopes.Add("全局");
            return string.Join(" / ", scopes);
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            value ??= string.Empty;
            if (_value == value)
            {
                return;
            }

            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BooleanValue));
            OnPropertyChanged(nameof(BooleanStateText));
            OnPropertyChanged(nameof(SelectedChoice));
        }
    }

    public bool? BooleanValue
    {
        get => bool.TryParse(Value, out var value) ? value : null;
        set => Value = value?.ToString().ToLowerInvariant() ?? string.Empty;
    }

    public string BooleanStateText => BooleanValue switch
    {
        true => "已启用",
        false => "已禁用",
        null => "未设置（不修改）",
    };

    public DownloadOptionChoice? SelectedChoice
    {
        get => ChoiceItems.FirstOrDefault(item => item.Value == Value);
        set => Value = value?.Value ?? string.Empty;
    }

    public string ToAria2Value()
    {
        var value = Value.Trim();
        return EditorKind switch
        {
            DownloadOptionEditorKind.Number => ValidateNumber(value, convertFromKibibytes: false),
            DownloadOptionEditorKind.UnitNumber => ValidateNumber(value, convertFromKibibytes: true),
            DownloadOptionEditorKind.Text when IsTrackerList => NormalizeTrackerList(value),
            _ => value,
        };
    }

    public void SetFromAria2Value(string? value)
    {
        value ??= string.Empty;
        if (EditorKind == DownloadOptionEditorKind.UnitNumber &&
            TryParseAria2ByteSize(value, out var bytes))
        {
            Value = (bytes / 1024m).ToString(CultureInfo.InvariantCulture);
            return;
        }

        Value = value;
    }

    private static bool TryParseAria2ByteSize(string value, out decimal bytes)
    {
        value = value.Trim();
        var multiplier = 1m;
        if (value.Length > 1)
        {
            multiplier = char.ToUpperInvariant(value[^1]) switch
            {
                'K' => 1024m,
                'M' => 1024m * 1024m,
                'G' => 1024m * 1024m * 1024m,
                _ => 1m,
            };
            if (multiplier != 1m)
            {
                value = value[..^1];
            }
        }

        if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            bytes = 0;
            return false;
        }

        bytes = number * multiplier;
        return true;
    }

    private bool IsTrackerList => Name is "bt-tracker" or "bt-exclude-tracker";

    private string ValidateNumber(string value, bool convertFromKibibytes)
    {
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(value, style, CultureInfo.InvariantCulture, out var number))
        {
            throw new InvalidOperationException($"{Label}必须是数字。");
        }

        var valueForRange = convertFromKibibytes ? number * 1024m : number;
        var allowsFraction = HasFractionalRange(Definition) && !convertFromKibibytes;
        if (convertFromKibibytes
                ? decimal.Truncate(valueForRange) != valueForRange
                : !allowsFraction && decimal.Truncate(number) != number)
        {
            throw new InvalidOperationException($"{Label}必须是整数。");
        }

        if (valueForRange < _minimum || valueForRange > _maximum)
        {
            var unit = convertFromKibibytes ? " KiB" : string.Empty;
            throw new InvalidOperationException(
                $"{Label}必须在 {FormatBound(_minimum, convertFromKibibytes, true)}–" +
                $"{FormatBound(_maximum, convertFromKibibytes, false)}{unit} 范围内。");
        }

        return (convertFromKibibytes ? decimal.Truncate(valueForRange) : number)
            .ToString(CultureInfo.InvariantCulture);
    }

    private static string NormalizeTrackerList(string value)
    {
        var trackers = value
            .Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (trackers.Length == 0)
        {
            throw new InvalidOperationException("Tracker 列表不能为空。");
        }

        return string.Join(',', trackers);
    }

    private static string FormatBound(decimal value, bool convertFromKibibytes, bool roundUp)
    {
        if (value == decimal.MinValue)
        {
            return "最小值";
        }

        if (value == decimal.MaxValue)
        {
            return "最大值";
        }

        var normalized = convertFromKibibytes ? value / 1024m : value;
        normalized = roundUp ? decimal.Ceiling(normalized) : decimal.Floor(normalized);
        return normalized.ToString(CultureInfo.InvariantCulture);
    }

    private static DownloadOptionEditorKind ResolveEditorKind(
        DownloadOptionDefinition definition,
        out decimal minimum,
        out decimal maximum)
    {
        minimum = decimal.MinValue;
        maximum = decimal.MaxValue;
        var possibleValues = ParsePossibleValues(definition.PossibleValues);
        if (possibleValues.Length == 2 &&
            possibleValues.Contains("true", StringComparer.Ordinal) &&
            possibleValues.Contains("false", StringComparer.Ordinal))
        {
            return DownloadOptionEditorKind.Boolean;
        }

        if (possibleValues.Length >= 2 && possibleValues.All(IsSimpleChoice))
        {
            return DownloadOptionEditorKind.Choice;
        }

        if (!TryParseNumericRange(definition.PossibleValues, out minimum, out maximum))
        {
            return DownloadOptionEditorKind.Text;
        }

        if (UnitNumberOptionNames.Contains(definition.Name))
        {
            return DownloadOptionEditorKind.UnitNumber;
        }

        return definition.DefaultValue.Length == 0 ||
               decimal.TryParse(
                   definition.DefaultValue,
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out _)
            ? DownloadOptionEditorKind.Number
            : DownloadOptionEditorKind.Text;
    }

    private static string[] ParsePossibleValues(string possibleValues) =>
        possibleValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsSimpleChoice(string value) =>
        value.Length > 0 && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool TryParseNumericRange(string possibleValues, out decimal minimum, out decimal maximum)
    {
        minimum = decimal.MinValue;
        maximum = decimal.MaxValue;
        var separator = possibleValues.IndexOf("-", StringComparison.Ordinal);
        if (separator <= 0 || separator != possibleValues.LastIndexOf("-", StringComparison.Ordinal))
        {
            return false;
        }

        var minimumText = possibleValues[..separator].Trim();
        var maximumText = possibleValues[(separator + 1)..].Trim();
        if (minimumText != "*" &&
            !decimal.TryParse(minimumText, NumberStyles.Number, CultureInfo.InvariantCulture, out minimum))
        {
            return false;
        }

        if (maximumText != "*" &&
            !decimal.TryParse(maximumText, NumberStyles.Number, CultureInfo.InvariantCulture, out maximum))
        {
            return false;
        }

        return minimum <= maximum;
    }

    private static bool HasFractionalRange(DownloadOptionDefinition definition) =>
        definition.DefaultValue.Contains(".", StringComparison.Ordinal) ||
        definition.PossibleValues.Contains(".", StringComparison.Ordinal);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static readonly HashSet<string> UnitNumberOptionNames = new(StringComparer.Ordinal)
    {
        "max-download-limit",
        "max-upload-limit",
        "max-overall-download-limit",
        "max-overall-upload-limit",
        "min-split-size",
    };
}

public enum DownloadOptionEditorKind
{
    Text,
    Boolean,
    Number,
    UnitNumber,
    Choice,
}

public sealed record DownloadOptionChoice(string Value, string DisplayName);
