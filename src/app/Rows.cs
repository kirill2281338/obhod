using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Obhod;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    protected void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Palette
{
    // Монохром: «хорошо» = белый, «частично» = серый, «плохо» = тёмно-серый
    public static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    public static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    public static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    public static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly Brush ChipOk = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly Brush ChipOkFg = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0B)));
    public static readonly Brush ChipBad = Freeze(new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush ChipBadFg = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush RowBest = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush RowBestStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush RowNormal = Freeze(new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush RowStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF)));

    public static Brush ForGlyph(string id) => Muted;

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

public sealed class ServiceRow : Observable
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Hint { get; init; } = "";
    public string Glyph { get; init; } = "";
    public Brush GlyphBrush { get; init; } = Palette.Muted;
    public Geometry? Logo { get; init; }
    public Visibility LogoVisibility => Logo != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Logo == null ? Visibility.Visible : Visibility.Collapsed;
    public bool Custom { get; init; }
    public Visibility RemoveVisibility => Custom ? Visibility.Visible : Visibility.Collapsed;

    private bool _isOn;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }

    private Brush _statusBrush = Palette.Muted;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    private string _statusText = "не проверен";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
}

public sealed class StrategyRow : Observable
{
    public required string Name { get; init; }
    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _score = "";
    public string ScoreText { get => _score; set => Set(ref _score, value); }
    private double _bar;
    public double BarWidth { get => _bar; set => Set(ref _bar, value); }
    private Brush _barBrush = Palette.Blue;
    public Brush BarBrush { get => _barBrush; set => Set(ref _barBrush, value); }
    private Brush _row = Palette.RowNormal;
    public Brush RowBrush { get => _row; set => Set(ref _row, value); }
    private Brush _rowStroke = Palette.RowStroke;
    public Brush RowStroke { get => _rowStroke; set => Set(ref _rowStroke, value); }
}

public sealed class CheckRow
{
    public required string Title { get; init; }
    public required string Sub { get; init; }
    public required Brush T12Brush { get; init; }
    public required Brush T13Brush { get; init; }
    public required Brush StreamBrush { get; init; }
    public Brush T12Fg => T12Brush == Palette.ChipOk ? Palette.ChipOkFg : Palette.ChipBadFg;
    public Brush T13Fg => T13Brush == Palette.ChipOk ? Palette.ChipOkFg : Palette.ChipBadFg;
    public Brush StreamFg => StreamBrush == Palette.ChipOk ? Palette.ChipOkFg : Palette.ChipBadFg;
    public required string MsText { get; init; }
}

public sealed class Half : IValueConverter
{
    public static readonly Half Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is double d ? Math.Max(0, d / 2 - 4) : 0d;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
