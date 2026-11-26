using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Obhod.Core;

namespace Obhod;

public sealed class DnsRow : Observable
{
    public required DnsProvider P { get; init; }
    public string Name => P.Name;
    public string Sub => $"{P.Primary}, {P.Secondary} · {P.Hint}" + (P.Doh != null ? " · doh" : "");
    private int _ping = -2;
    public int Ping { get => _ping; set { Set(ref _ping, value); OnChanged(nameof(PingText)); OnChanged(nameof(PingBrush)); } }
    public string PingText => _ping == -2 ? "…" : _ping < 0 ? "нет ответа" : $"{_ping} мс";
    public Brush PingBrush => _ping < 0 ? Palette.Red : _ping < 40 ? Palette.Green : _ping < 90 ? Palette.Orange : Palette.Red;
    private bool _active;
    public bool Active { get => _active; set { Set(ref _active, value); OnChanged(nameof(ButtonText)); OnChanged(nameof(RowBrush)); OnChanged(nameof(RowStroke)); } }
    private bool _best;
    public bool Best { get => _best; set { Set(ref _best, value); OnChanged(nameof(Badge)); } }
    public string Badge => _best ? "   быстрый" : "";
    public string ButtonText => _active ? "используется" : "выбрать";
    public Brush RowBrush => _active ? Palette.RowBest : Palette.RowNormal;
    public Brush RowStroke => _active ? Palette.RowBestStroke : Palette.RowStroke;
}

public partial class DnsWindow : Window
{
    private readonly ObservableCollection<DnsRow> _rows = new();

    public DnsWindow()
    {
        InitializeComponent();
        foreach (var p in DnsManager.Providers) _rows.Add(new DnsRow { P = p });
        List.ItemsSource = _rows;
        Loaded += async (_, _) => { RefreshCurrent(); await PingAllAsync(); };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) try { DragMove(); } catch { } };
    }

    private void RefreshCurrent()
    {
        var cur = DnsManager.Current();
        var match = _rows.FirstOrDefault(r => cur.Contains(r.P.Primary));
        foreach (var r in _rows) r.Active = r == match;
        CurrentText.Text = cur.Count == 0 ? "сейчас: не удалось определить"
            : match != null ? $"сейчас: {match.Name} ({string.Join(", ", cur)})"
            : $"сейчас: dns провайдера или свой ({string.Join(", ", cur)})";
    }

    private async Task PingAllAsync()
    {
        PingBtn.IsEnabled = false;
        foreach (var r in _rows) { r.Ping = -2; r.Best = false; }
        await Task.WhenAll(_rows.Select(async r => r.Ping = await DnsManager.PingAsync(r.P.Primary)));
        // сортировка по пингу, лучший помечаем
        var sorted = _rows.OrderBy(r => r.Ping < 0 ? int.MaxValue : r.Ping).ToList();
        for (int i = 0; i < sorted.Count; i++) { var idx = _rows.IndexOf(sorted[i]); if (idx != i) _rows.Move(idx, i); }
        if (sorted.FirstOrDefault(r => r.Ping >= 0) is DnsRow best) best.Best = true;
        PingBtn.IsEnabled = true;
    }

    private async void Ping_Click(object sender, RoutedEventArgs e) => await PingAllAsync();

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DnsRow row || row.Active) return;
        CurrentText.Text = $"применяю {row.Name}…";
        var err = await DnsManager.ApplyAsync(row.P);
        if (err != null) CurrentText.Text = "не удалось: " + err.ToLowerInvariant();
        else RefreshCurrent();
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        CurrentText.Text = "возвращаю dns провайдера…";
        var err = await DnsManager.ResetAsync();
        if (err != null) CurrentText.Text = "не удалось: " + err.ToLowerInvariant();
        else RefreshCurrent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
