using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using UniFiles.Core.Tools;

namespace UniFiles.App.ViewModels;

/// <summary>One editable tool setting. The view picks a control from the concrete type.</summary>
public abstract partial class OptionViewModel : ObservableObject
{
    protected OptionViewModel(ToolOption option)
    {
        Option = option;
    }

    public ToolOption Option { get; }

    public string Key => Option.Key;

    public string Label => Option.Label;

    public string? Hint => Option.Hint;

    public bool HasHint => !string.IsNullOrEmpty(Hint);

    [ObservableProperty]
    private bool _isVisible = true;

    public abstract object? CurrentValue { get; }

    /// <summary>Raised when the user changes the value.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Restores a value saved in the preferences; ignores values of the wrong type.</summary>
    public abstract void Restore(JsonElement saved);

    protected void RaiseValueChanged() => ValueChanged?.Invoke(this, EventArgs.Empty);

    public static OptionViewModel Create(ToolOption option) => option switch
    {
        ChoiceOption choice => new ChoiceOptionViewModel(choice),
        NumberOption number => new NumberOptionViewModel(number),
        BoolOption boolean => new BoolOptionViewModel(boolean),
        TextOption text => new TextOptionViewModel(text),
        _ => throw new NotSupportedException(option.GetType().Name),
    };
}

public sealed partial class ChoiceOptionViewModel : OptionViewModel
{
    public ChoiceOptionViewModel(ChoiceOption option) : base(option)
    {
        Choices = option.Choices;
        _selected = Choices.FirstOrDefault(c => c.Value == option.Default) ?? Choices[0];
    }

    public IReadOnlyList<Choice> Choices { get; }

    [ObservableProperty]
    private Choice _selected;

    public override object? CurrentValue => Selected.Value;

    partial void OnSelectedChanged(Choice value) => RaiseValueChanged();

    public override void Restore(JsonElement saved)
    {
        if (saved.ValueKind == JsonValueKind.String && Choices.FirstOrDefault(c => c.Value == saved.GetString()) is { } match)
            Selected = match;
    }
}

public sealed partial class NumberOptionViewModel : OptionViewModel
{
    private readonly NumberOption _number;

    public NumberOptionViewModel(NumberOption option) : base(option)
    {
        _number = option;
        _value = option.Default;
    }

    public int Minimum => _number.Minimum;

    public int Maximum => _number.Maximum;

    public string? Unit => _number.Unit;

    /// <summary>Small ranges (like a 1–100 quality) read best as a slider.</summary>
    public bool UseSlider => Maximum - Minimum <= 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SliderValue), nameof(BoxValue), nameof(DisplayValue))]
    private int _value;

    public string DisplayValue => Unit is null ? Value.ToString() : $"{Value} {Unit}";

    public double SliderValue
    {
        get => Value;
        set => Value = (int)Math.Round(value);
    }

    public decimal? BoxValue
    {
        get => Value;
        set => Value = (int)Math.Clamp(value ?? Minimum, Minimum, Maximum);
    }

    public override object? CurrentValue => Value;

    partial void OnValueChanged(int value) => RaiseValueChanged();

    public override void Restore(JsonElement saved)
    {
        if (saved.ValueKind == JsonValueKind.Number && saved.TryGetInt32(out var number))
            Value = Math.Clamp(number, Minimum, Maximum);
    }
}

public sealed partial class BoolOptionViewModel : OptionViewModel
{
    public BoolOptionViewModel(BoolOption option) : base(option)
    {
        _isChecked = option.Default;
    }

    [ObservableProperty]
    private bool _isChecked;

    public override object? CurrentValue => IsChecked;

    partial void OnIsCheckedChanged(bool value) => RaiseValueChanged();

    public override void Restore(JsonElement saved)
    {
        if (saved.ValueKind is JsonValueKind.True or JsonValueKind.False)
            IsChecked = saved.GetBoolean();
    }
}

public sealed partial class TextOptionViewModel : OptionViewModel
{
    public TextOptionViewModel(TextOption option) : base(option)
    {
        _text = option.Default;
    }

    [ObservableProperty]
    private string _text;

    public override object? CurrentValue => Text;

    partial void OnTextChanged(string value) => RaiseValueChanged();

    public override void Restore(JsonElement saved)
    {
        if (saved.ValueKind == JsonValueKind.String)
            Text = saved.GetString() ?? "";
    }
}
