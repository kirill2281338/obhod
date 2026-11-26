using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Obhod.Core;

namespace Obhod;

public partial class MainWindow : Window
{
    private readonly AppSettings _s;
    private List<Strategy> _strategies = new();
    private readonly ObservableCollection<ServiceRow> _services = new();
    private readonly ObservableCollection<StrategyRow> _stratRows = new();
    private readonly ObservableCollection<CheckRow> _checkRows = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private System.Windows.Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _cts;
    private ReleaseInfo? _zRel, _tRel;
    private bool _zBusy, _tBusy, _testBusy, _suppressUi, _exiting, _shownOnce, _trayHintShown;
    private readonly bool _startHidden;

    public MainWindow(bool startInTray)
    {
        InitializeComponent();
        _s = AppSettings.Load();
        _startHidden = startInTray || _s.StartMinimized;

        ServicesList.ItemsSource = _services;
        StrategyResults.ItemsSource = _stratRows;
        CheckResults.ItemsSource = _checkRows;
        foreach (var p in new FrameworkElement[] { PageServices, PageVpn, PageTests, PageUpdates, PageSettings })
            p.RenderTransform = new TranslateTransform();

        Log.Line += line => Dispatcher.BeginInvoke(() =>
        {
            if (LogBox.LineCount > 400) LogBox.Clear();
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });

        SourceInitialized += (_, _) => ApplyDwm();
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            UpdateBackground();
        };
        IsVisibleChanged += (_, _) => UpdateBackground();
        Closing += OnClosing;
        _timer.Tick += async (_, _) => await RefreshStatusAsync();

        SetupTray();
        if (!_startHidden) ShowAnimated();
        Dispatcher.BeginInvoke(new Action(async () => await InitAsync()), DispatcherPriority.Background);
    }

    // ================= Запуск =================

    private async Task InitAsync()
    {
        ApplySettingsToUi();
        _zBusy = true;
        SetSpin(ZSpin, true);
        ZSub.Text = Lc("подготавливаю инструменты…");
        try
        {
            await Task.Run(() =>
            {
                Paths.Ensure();
                Updater.ImportLocalZapret();
                Updater.ImportLocalTg(_s);
            });

            if (!ZapretEngine.IsInstalled)
            {
                ZSub.Text = Lc("скачиваю запрет с github…");
                var rel = await Updater.LatestZapretAsync();
                if (rel != null) await Updater.InstallZapretAsync(rel, null, CancellationToken.None);
            }
            if (!TgProxy.IsInstalled)
            {
                TSub.Text = Lc("скачиваю tgwsproxy с github…");
                var rel = await Updater.LatestTgAsync();
                if (rel != null)
                {
                    await Updater.InstallTgAsync(rel, null, CancellationToken.None);
                    _s.TgVersion = rel.Version;
                }
            }
            _s.TgVersion ??= TgProxy.VersionFromLog();
            _s.Save();
        }
        catch (Exception ex)
        {
            Log.Write("подготовка: " + ex.Message);
            Toast("не удалось скачать инструменты: " + ex.Message);
        }
        finally
        {
            _zBusy = false;
            SetSpin(ZSpin, false);
        }

        LoadStrategies();
        BuildServices();
        InitVpn();
        await RefreshStatusAsync();
        UpdateVersionTexts();
        _timer.Start();
        await StartNetworkMonitoringAsync();
        if (_exiting) return;

        if (_s.AutoStartTg && TgProxy.IsInstalled && !TgProxy.IsRunning)
            _ = SetTgAsync(true);

        // Сначала обходим блокировку по IP (картинки Discord), чтобы тест стратегий видел рабочие адреса
        if (_s.EnabledServices.Contains("discord")) await FixIpBlocksAsync();
        if (_exiting) return;

        if (_s.AutoStartZapret && ZapretEngine.IsInstalled)
        {
            if (_s.AutoPickOnStart) await AutoPickAsync(full: false);
            else await SetZapretAsync(true);
        }

        if (_s.CheckUpdatesOnStart) _ = CheckUpdatesAsync(silent: true);
    }

    /// <summary>Картинки Discord: если провайдер закрыл адреса media.discordapp.net по IP, подставляем рабочий адрес через hosts.</summary>
    private async Task FixIpBlocksAsync()
    {
        try
        {
            var r = await Task.Run(() => HostsFix.ApplyAsync());
            Log.Write("картинки discord: " + r);
        }
        catch (Exception ex) { Log.Write("обход блокировки по ip: " + ex.Message); }
    }

    private void LoadStrategies()
    {
        _strategies = StrategyLoader.Load(Paths.ZapretDir);
        _suppressUi = true;
        StrategyBox.ItemsSource = _strategies;
        var name = ZapretEngine.RunningStrategy ?? _s.SelectedStrategy ?? _s.LastGoodStrategy;
        StrategyBox.SelectedItem = _strategies.FirstOrDefault(x => x.Name == name) ?? _strategies.FirstOrDefault();
        _suppressUi = false;
        Log.Write($"стратегий загружено: {_strategies.Count}");
    }

    private Strategy? Selected => StrategyBox.SelectedItem as Strategy;

    // ================= Запрет =================

    private async void ZPower_Click(object sender, RoutedEventArgs e)
    {
        if (_zBusy) { ZPower.IsChecked = ZapretEngine.IsRunning; return; }
        bool want = ZPower.IsChecked == true;
        ZPower.IsChecked = !want; // визуально переключим после результата
        if (want && Selected == null) { await AutoPickAsync(false); return; }
        await SetZapretAsync(want);
    }

    private async Task SetZapretAsync(bool on)
    {
        if (_zBusy) return;
        _zBusy = true;
        SetSpin(ZSpin, true);
        try
        {
            if (on)
            {
                var st = Selected ?? _strategies.FirstOrDefault();
                if (st == null) { Toast("нет стратегий: обновите запрет во вкладке «обновления»."); return; }
                ZSub.Text = Lc($"запускаю «{st.Name}»…");
                var err = await ZapretEngine.StartAsync(st, _s);
                if (err != null) { Toast(err); Log.Write(err); }
                else
                {
                    _zapretCheck = null;
                    _s.SelectedStrategy = st.Name;
                    _s.Save();
                    Log.Write($"запрет включён: {st.Name}");
                }
            }
            else
            {
                ZSub.Text = Lc("выключаю…");
                await ZapretEngine.StopAsync();
                Log.Write("запрет выключен");
            }
        }
        finally
        {
            _zBusy = false;
            SetSpin(ZSpin, false);
            await RefreshStatusAsync();
        }
    }

    private async void StrategyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || Selected == null) return;
        _s.SelectedStrategy = Selected.Name;
        _s.Save();
        if (ZapretEngine.IsRunning && !_zBusy && ZapretEngine.RunningStrategy != Selected.Name)
        {
            await SetZapretAsync(true);
        }
    }

    private async void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (_testBusy) { _cts?.Cancel(); return; }
        await AutoPickAsync(full: false, forcePerfect: true);
    }

    /// <summary>Подбор стратегии. Быстрый режим начинает с последней удачной и останавливается, как только результат не хуже прежнего.</summary>
    private async Task AutoPickAsync(bool full, bool forcePerfect = false)
    {
        if (_testBusy || _zBusy) return;
        if (!ServiceCatalog.Targets(_s).Any()) { Toast("включите хотя бы один сервис во вкладке «сервисы»."); return; }

        _testBusy = true; _zBusy = true;
        var previous = ZapretEngine.RunningStrategy;
        var network = _networkKey;
        _zapretCheck = null;
        SetPreviousStrategy(null);
        _cts = new CancellationTokenSource();
        SetTestingUi(true);
        SetSpin(ZSpin, true);

        var ordered = StrategyPicker.Order(_strategies, _s).ToList();
        int? stopAt = null;
        if (!full)
        {
            stopAt = 1000;
            if (!forcePerfect && _s.LastGoodStrategy != null && _s.Scores.TryGetValue(_s.LastGoodStrategy, out var prev) && prev > 0)
                stopAt = prev;
        }

        _stratRows.Clear();
        ShowResults(strategies: true);
        var rows = new Dictionary<string, StrategyRow>();
        int best = -1;

        var progress = new Progress<StrategyPicker.Progress>(p =>
        {
            var row = new StrategyRow { Name = p.Strategy.Display };
            FillRow(row, p.Report);
            rows[p.Strategy.Name] = row;
            _stratRows.Add(row);
            if (p.Report.Score > best)
            {
                best = p.Report.Score;
                foreach (var r in _stratRows) { r.RowBrush = Palette.RowNormal; r.RowStroke = Palette.RowStroke; }
                row.RowBrush = Palette.RowBest;
                row.RowStroke = Palette.RowBestStroke;
            }
            SetProgress((double)p.Index / p.Count);
            TestSummary.Text = Lc($"проверено {p.Index} из {p.Count}. сейчас: {p.Strategy.Name}");
            ZSub.Text = Lc(full ? $"тест стратегий {p.Index}/{p.Count}…" : $"подбираю стратегию {p.Index}/{p.Count}…");
            UpdateServiceStatuses(p.Report);
        });

        try
        {
            ZSub.Text = Lc("подбираю стратегию…");
            SetProgress(0);
            TestSummary.Text = Lc(full ? "проверяю все стратегии по очереди…" : "начинаю с последней удачной стратегии…");
            var (winner, rep) = await StrategyPicker.RunAsync(ordered, _s, stopAt, progress, _cts.Token);
            SetProgress(1);
            if (winner != null && rep != null)
            {
                _suppressUi = true;
                StrategyBox.SelectedItem = winner;
                _suppressUi = false;
                UpdateServiceStatuses(rep);
                if (previous != null && previous != winner.Name) SetPreviousStrategy(previous);
                if (rep.Perfect && network != null && network == _networkKey)
                {
                    _s.NetworkStrategies[network] = winner.Name;
                    _s.Save();
                }
                var msg = rep.Perfect
                    ? $"лучшая стратегия: {winner.Name}. всё открывается."
                    : $"лучшая стратегия: {winner.Name} ({rep.Passed}/{rep.Total} проверок).";
                TestSummary.Text = Lc(msg);
                Toast(msg);
            }
            else
            {
                await RestoreAfterTestAsync(previous);
                TestSummary.Text = Lc("ни одна стратегия не открыла выбранные сервисы. проверьте интернет или отключите vpn/прокси.");
                Toast("ни одна стратегия не подошла.");
            }
        }
        catch (OperationCanceledException)
        {
            TestSummary.Text = Lc("проверка остановлена.");
            await RestoreAfterTestAsync(previous);
            _s.Save();
        }
        catch (Exception ex)
        {
            await RestoreAfterTestAsync(previous);
            Log.Write("подбор: " + ex.Message);
            Toast(ex.Message);
        }
        finally
        {
            _testBusy = false; _zBusy = false;
            SetTestingUi(false);
            SetSpin(ZSpin, false);
            RefreshStrategyBox();
            RefreshNetworkStrategy();
            await RefreshStatusAsync();
        }
    }

    private void RefreshStrategyBox()
    {
        // Обновляем список, чтобы порядок/выбор были актуальны
        var sel = Selected?.Name;
        _suppressUi = true;
        StrategyBox.Items.Refresh();
        StrategyBox.SelectedItem = _strategies.FirstOrDefault(x => x.Name == (ZapretEngine.RunningStrategy ?? sel));
        _suppressUi = false;
    }

    private static void FillRow(StrategyRow row, TestReport r)
    {
        if (r.StartError != null)
        {
            row.Detail = "не запустилась: " + r.StartError;
            row.ScoreText = "ошибка";
            row.BarWidth = 0;
            row.BarBrush = Palette.Red;
            return;
        }
        var failed = r.Checks.Where(c => !c.Ok && !c.IpBlocked).Select(c => c.Service).Distinct().ToList();
        row.Detail = failed.Count == 0 ? $"всё открывается · {r.AvgMs} мс" : "проблемы: " + string.Join(", ", failed);
        row.ScoreText = $"{r.Passed}/{r.Total}";
        row.BarWidth = 150 * r.Ratio;
        row.BarBrush = r.Perfect ? Palette.Green : r.Ratio >= 0.6 ? Palette.Orange : Palette.Red;
    }

    // ================= Тесты =================

    private async void TestCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_testBusy) return;
        var targets = ServiceCatalog.Targets(_s);
        if (targets.Count == 0) { Toast("включите хотя бы один сервис во вкладке «сервисы»."); return; }
        _testBusy = true;
        _cts = new CancellationTokenSource();
        SetTestingUi(true);
        ShowResults(strategies: false);
        _checkRows.Clear();
        SetProgress(0.15);
        var mode = ZapretEngine.IsRunning ? $"со стратегией «{ZapretEngine.RunningStrategy ?? "внешний winws"}»" : "без запрета (как есть)";
        TestSummary.Text = Lc($"проверяю {targets.Count} адресов {mode}…");
        try
        {
            var rep = await Tester.RunAsync(targets, 5, _cts.Token);
            SetProgress(1);
            foreach (var c in rep.Checks)
            {
                _checkRows.Add(new CheckRow
                {
                    Title = Lc(c.Service),
                    Sub = c.IpBlocked ? $"{c.Url} — блокировка по ip: запрет не поможет, нужен vpn или прокси"
                        : c.Error == null ? c.Url : $"{c.Url} — {c.Error}",
                    T12Brush = c.Tls12 ? Palette.ChipOk : Palette.ChipBad,
                    T13Brush = c.Tls13 ? Palette.ChipOk : Palette.ChipBad,
                    StreamBrush = c.Stream ? Palette.ChipOk : Palette.ChipBad,
                    MsText = c.Ms > 0 ? $"{c.Ms} мс" : "—",
                });
            }
            UpdateServiceStatuses(rep);
            TestSummary.Text = Lc($"результат {mode}: {rep.Passed} из {rep.Total} проверок прошли" + (rep.AvgMs > 0 ? $", в среднем {rep.AvgMs} мс." : "."));
            if (ZapretEngine.RunningStrategy is string rs) { _s.Scores[rs] = rep.Score; _s.Save(); }
        }
        catch (OperationCanceledException) { TestSummary.Text = Lc("проверка остановлена."); }
        finally
        {
            _testBusy = false;
            SetTestingUi(false);
        }
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e) => await AutoPickAsync(full: true);

    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void SetTestingUi(bool on)
    {
        TestCurrentBtn.IsEnabled = !on;
        TestAllBtn.IsEnabled = !on;
        StopBtn.IsEnabled = on;
        StrategyBox.IsEnabled = !on;
        PickBtnText.Text = Lc(on ? "остановить" : "подобрать лучшую");
    }

    private void ShowResults(bool strategies)
    {
        StrategyResults.Visibility = strategies ? Visibility.Visible : Visibility.Collapsed;
        CheckResults.Visibility = strategies ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetProgress(double v)
    {
        var a = new DoubleAnimation(Math.Clamp(v, 0, 1), TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        TestBarS.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        TestPercent.Text = Lc(v > 0 && v < 1 ? $"{(int)(v * 100)}%" : "");
    }

    // ================= Сервисы =================

    private void BuildServices()
    {
        _services.Clear();
        foreach (var d in ServiceCatalog.All(_s))
        {
            _services.Add(new ServiceRow
            {
                Id = d.Id, Name = Lc(d.Name), Hint = Lc(d.Hint), Glyph = d.Glyph, Custom = d.Custom,
                GlyphBrush = Palette.ForGlyph(d.Id),
                Logo = LogoFor(d.Id),
                IsOn = _s.EnabledServices.Contains(d.Id),
            });
        }
    }

    private static readonly Dictionary<string, Geometry?> LogoCache = new();

    private static Geometry? LogoFor(string id)
    {
        if (LogoCache.TryGetValue(id, out var g)) return g;
        g = null;
        if (BrandIcons.Paths.TryGetValue(id, out var data))
        {
            try { g = Geometry.Parse(data); g.Freeze(); }
            catch (Exception ex) { Log.Write($"логотип {id}: {ex.Message}"); }
        }
        return LogoCache[id] = g;
    }

    private async void ServiceToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ServiceRow row) return;
        if (row.IsOn) _s.EnabledServices.Add(row.Id); else _s.EnabledServices.Remove(row.Id);
        _s.Save();
        await ApplyListsAsync();
    }

    private async void AddService_Click(object sender, RoutedEventArgs e)
    {
        var domains = NewSvcDomains.Text.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ServiceCatalog.NormalizeDomain).Where(d => d.Contains('.')).Distinct().ToList();
        var name = NewSvcName.Text.Trim();
        if (domains.Count == 0) { Toast("укажите хотя бы один домен, например example.com"); return; }
        if (name.Length == 0) name = domains[0];
        if (_s.CustomServices.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { Toast("сервис с таким названием уже есть."); return; }

        var cs = new CustomService { Name = name, Domains = domains };
        _s.CustomServices.Add(cs);
        _s.EnabledServices.Add("custom:" + name);
        _s.Save();
        NewSvcName.Clear(); NewSvcDomains.Clear();
        BuildServices();
        await ApplyListsAsync();
        Toast($"добавлен «{name}». нажмите «проверить текущую» во вкладке «тесты», чтобы убедиться, что он открывается.");
    }

    private async void RemoveService_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServiceRow row || !row.Custom) return;
        _s.CustomServices.RemoveAll(c => "custom:" + c.Name == row.Id);
        _s.EnabledServices.Remove(row.Id);
        _s.Save();
        _services.Remove(row);
        await ApplyListsAsync();
    }

    /// <summary>Пишет домены в список и перезапускает winws, чтобы он перечитал списки.</summary>
    private async Task ApplyListsAsync()
    {
        ServiceCatalog.WriteUserList(_s);
        if (ZapretEngine.IsRunning && !_zBusy && ZapretEngine.RunningStrategy != null) await SetZapretAsync(true);
    }

    private void UpdateServiceStatuses(TestReport rep)
    {
        foreach (var g in rep.Checks.GroupBy(c => c.Service))
        {
            var row = _services.FirstOrDefault(s => s.Name.Equals(g.Key, StringComparison.OrdinalIgnoreCase));
            if (row == null) continue;
            int ok = g.Count(c => c.Ok), all = g.Count();
            if (rep.StartError != null) continue;
            if (g.All(c => c.IpBlocked)) { row.StatusBrush = Palette.Red; row.StatusText = "блок по ip, нужен vpn"; }
            else if (ok == all) { row.StatusBrush = Palette.Green; row.StatusText = "работает"; }
            else if (g.Any(c => c.Tls12 || c.Tls13)) { row.StatusBrush = Palette.Orange; row.StatusText = "частично"; }
            else { row.StatusBrush = Palette.Red; row.StatusText = "не открывается"; }
        }
    }

    // ================= Telegram =================

    private async void TPower_Click(object sender, RoutedEventArgs e)
    {
        bool want = TPower.IsChecked == true;
        TPower.IsChecked = !want;
        await SetTgAsync(want);
    }

    private async Task SetTgAsync(bool on)
    {
        if (_tBusy) return;
        _tBusy = true;
        SetSpin(TSpin, true);
        try
        {
            if (on)
            {
                TSub.Text = Lc("запускаю прокси…");
                var err = await TgProxy.StartAsync();
                if (err != null) { Toast(err); Log.Write(err); }
                else Log.Write("telegram-прокси включён");
            }
            else
            {
                await TgProxy.StopAsync();
                Log.Write("telegram-прокси выключен");
            }
        }
        finally
        {
            _tBusy = false;
            SetSpin(TSpin, false);
            await RefreshStatusAsync();
        }
    }

    private async void TgLink_Click(object sender, RoutedEventArgs e)
    {
        if (!TgProxy.IsRunning) await SetTgAsync(true);
        var link = TgProxy.TelegramLink();
        if (link == null) { Toast("прокси ещё не создал настройки. запустите его один раз и попробуйте снова."); return; }
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })?.Dispose();
            Toast("telegram откроет окно добавления прокси: нажмите «подключить».");
        }
        catch
        {
            Clipboard.SetText(link);
            Toast("telegram не найден. ссылка на прокси скопирована в буфер обмена.");
        }
    }

    // ================= Статус =================

    private async Task RefreshStatusAsync()
    {
        bool z = ZapretEngine.IsRunning;
        bool t = TgProxy.IsRunning;
        bool tl = t && await TgProxy.IsListeningAsync();

        if (!_zBusy)
        {
            ZPower.IsChecked = z;
            ZSub.Text = Lc(!ZapretEngine.IsInstalled ? "запрет не установлен: откройте «обновления»"
                : z ? (ZapretEngine.RunningStrategy != null ? $"работает: {ZapretEngine.RunningStrategy}" : "winws запущен вне программы")
                : "нажмите, чтобы включить");
        }
        ZStatus.Text = _zBusy ? "проверка" : z ? (_zapretCheck is < 0 ? "частично" : "включён") : "выключен";
        ZPower.Tag = ZDot.Fill = _zBusy || (z && _zapretCheck is < 0) ? Palette.Orange : z ? Palette.Green : Palette.Muted;
        ZAction.Text = z ? "выключить запрет" : "включить запрет";

        if (!_tBusy)
        {
            TPower.IsChecked = t;
            var (host, port, _) = TgProxy.ReadConfig();
            TSub.Text = Lc(!TgProxy.IsInstalled ? "tgwsproxy не установлен"
                : $"порт {port}" + (_s.TgVersion != null ? $" · v{_s.TgVersion}" : ""));
        }
        TStatus.Text = Lc(tl ? "работает" : t ? "запуск" : "выключен");
        TPower.Tag = TDot.Fill = tl ? Palette.Green : t || _tBusy ? Palette.Orange : Palette.Muted;
        TAction.Text = t ? "выключить прокси" : "включить прокси";
        if (!_vpnBusy) UpdateVpnUi();

        if (_tray != null)
            _tray.Text = $"обход\nзапрет: {(z ? "вкл" : "выкл")} · telegram: {(t ? "вкл" : "выкл")} · vpn: {(VpnEngine.IsRunning ? "вкл" : "выкл")}";
    }

    // ================= Обновления =================

    private void UpdateVersionTexts()
    {
        var zl = ZapretEngine.LocalVersion() ?? "—";
        ZVer.Text = Lc($"установлено: {zl}   ·   на github: {_zRel?.Version ?? "—"}");
        TVer.Text = Lc($"установлено: {_s.TgVersion ?? "—"}   ·   на github: {_tRel?.Version ?? "—"}");
        bool zNew = _zRel != null && Updater.IsNewer(_zRel.Version, ZapretEngine.LocalVersion());
        bool tNew = _tRel != null && Updater.IsNewer(_tRel.Version, _s.TgVersion);
        ZUpdBtn.IsEnabled = _zRel?.AssetUrl != null && (zNew || !ZapretEngine.IsInstalled);
        TUpdBtn.IsEnabled = _tRel?.AssetUrl != null && (tNew || !TgProxy.IsInstalled);
        ZUpdBtn.Content = ZapretEngine.IsInstalled ? (zNew ? "обновить" : "актуально") : "скачать";
        TUpdBtn.Content = TgProxy.IsInstalled ? (tNew ? "обновить" : "актуально") : "скачать";
        bool coresNew = UpdateCoreTexts();
        UpdDot.Visibility = zNew || tNew || coresNew ? Visibility.Visible : Visibility.Collapsed;
        VersionText.Text = Lc($"запрет {zl} · прокси {_s.TgVersion ?? "—"}");
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        var zt = Updater.LatestZapretAsync();
        var tt = Updater.LatestTgAsync();
        var xt = VpnEngine.LatestXrayAsync();
        var st = VpnEngine.LatestSingBoxAsync();
        await Task.WhenAll(zt, tt, xt, st);
        _zRel = zt.Result ?? _zRel;
        _tRel = tt.Result ?? _tRel;
        _xRel = xt.Result ?? _xRel;
        _sRel = st.Result ?? _sRel;
        UpdateVersionTexts();
        if (!silent)
        {
            if (_zRel == null && _tRel == null) Toast("github недоступен. включите запрет и попробуйте снова.");
            else if (UpdDot.Visibility == Visibility.Visible) Toast("есть обновления.");
            else Toast("всё обновлено.");
        }
        else if (UpdDot.Visibility == Visibility.Visible)
        {
            _tray?.ShowBalloonTip(4000, "обход", "доступны обновления запрета, прокси или ядра vpn.", System.Windows.Forms.ToolTipIcon.Info);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(false);

    private async void ZUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_zRel == null || _testBusy || _zBusy) return;
        bool wasOn = ZapretEngine.IsRunning;
        _zBusy = true;
        ZUpdBtn.IsEnabled = false;
        ZUpdBarHost.Visibility = Visibility.Visible;
        SetSpin(ZSpin, true);
        ZSub.Text = Lc($"обновляю запрет до {_zRel.Version}…");
        try
        {
            var prog = new Progress<double>(v => ZUpdBar.Width = ((FrameworkElement)ZUpdBar.Parent).ActualWidth * v);
            await Updater.InstallZapretAsync(_zRel, prog, CancellationToken.None);
            LoadStrategies();
            Toast($"запрет обновлён до {_zRel.Version}.");
        }
        catch (Exception ex) { Toast("обновление не удалось: " + ex.Message); Log.Write("обновление запрета: " + ex); }
        finally
        {
            _zBusy = false;
            SetSpin(ZSpin, false);
            ZUpdBarHost.Visibility = Visibility.Collapsed;
            UpdateVersionTexts();
        }
        if (wasOn) await SetZapretAsync(true);
        else await RefreshStatusAsync();
    }

    private async void TUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_tRel == null || _tBusy) return;
        _tBusy = true;
        TUpdBtn.IsEnabled = false;
        TUpdBarHost.Visibility = Visibility.Visible;
        SetSpin(TSpin, true);
        try
        {
            var prog = new Progress<double>(v => TUpdBar.Width = ((FrameworkElement)TUpdBar.Parent).ActualWidth * v);
            await Updater.InstallTgAsync(_tRel, prog, CancellationToken.None);
            _s.TgVersion = _tRel.Version;
            _s.Save();
            Toast($"tgwsproxy обновлён до {_tRel.Version}.");
        }
        catch (Exception ex) { Toast("обновление не удалось: " + ex.Message); Log.Write("обновление прокси: " + ex); }
        finally
        {
            _tBusy = false;
            SetSpin(TSpin, false);
            TUpdBarHost.Visibility = Visibility.Collapsed;
            UpdateVersionTexts();
            await RefreshStatusAsync();
        }
    }

    private void OpenZapretPage_Click(object sender, RoutedEventArgs e) => OpenUrl(_zRel?.PageUrl ?? $"https://github.com/{Updater.ZapretRepo}/releases");
    private void OpenTgPage_Click(object sender, RoutedEventArgs e) => OpenUrl(_tRel?.PageUrl ?? $"https://github.com/{Updater.TgRepo}/releases");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    // ================= Настройки =================

    private void ApplySettingsToUi()
    {
        _suppressUi = true;
        ApplyTheme(_s.Theme);
        SAutoZ.IsChecked = _s.AutoStartZapret;
        SAutoPick.IsChecked = _s.AutoPickOnStart;
        SAutoTg.IsChecked = _s.AutoStartTg;
        SUpd.IsChecked = _s.CheckUpdatesOnStart;
        STray.IsChecked = _s.CloseToTray;
        GameBox.SelectedItem = GameBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _s.GameFilter) ?? GameBox.Items[0];
        _suppressUi = false;
        // Если автозапуск включён, переписываем задачу на текущий путь (после переустановки или переноса программы)
        _ = Task.Run(() => { var on = Updater.IsAutostartOn(); if (on) Updater.SetAutostart(true); return on; })
            .ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _suppressUi = true; SWin.IsChecked = t.Result; _suppressUi = false;
        }));
    }

    private void Setting_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUi) return;
        _s.AutoStartZapret = SAutoZ.IsChecked == true;
        _s.AutoPickOnStart = SAutoPick.IsChecked == true;
        _s.AutoStartTg = SAutoTg.IsChecked == true;
        _s.CheckUpdatesOnStart = SUpd.IsChecked == true;
        _s.CloseToTray = STray.IsChecked == true;
        _s.Save();
    }

    private async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool want = SWin.IsChecked == true;
        bool ok = await Task.Run(() => Updater.SetAutostart(want));
        if (!ok) { Toast("не удалось изменить автозапуск."); SWin.IsChecked = !want; }
        else Toast(want ? "обход будет запускаться вместе с windows." : "автозапуск выключен.");
    }

    private async void GameBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || GameBox.SelectedItem is not ComboBoxItem it) return;
        _s.GameFilter = (string)it.Tag;
        _s.Save();
        if (ZapretEngine.IsRunning && ZapretEngine.RunningStrategy != null) await SetZapretAsync(true);
    }

    private void OpenUserList_Click(object sender, RoutedEventArgs e)
    {
        ServiceCatalog.WriteUserList(_s);
        OpenUrl(System.IO.Path.Combine(Paths.ZapretLists, "list-general-user.txt"));
    }
    private void OpenExcludeList_Click(object sender, RoutedEventArgs e) => OpenUrl(System.IO.Path.Combine(Paths.ZapretLists, "list-exclude-user.txt"));
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenUrl(Paths.Root);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => OpenUrl(Paths.LogFile);

    // ================= Вкладки и анимации =================

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || Pages == null) return;
        int idx = int.Parse((string)rb.Tag);
        var ease = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut };
        SegHiT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(idx * 112, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        var pages = new FrameworkElement[] { PageServices, PageVpn, PageTests, PageUpdates, PageSettings };
        for (int i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            if (i != idx) { p.Visibility = Visibility.Collapsed; continue; }
            p.Visibility = Visibility.Visible;
            p.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
            ((TranslateTransform)p.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (idx == 3 && _zRel == null && _tRel == null) _ = CheckUpdatesAsync(true);
    }

    private bool _blobsStarted;

    /// <summary>Фон анимируется только когда окно видно: в трее программа почти не тратит процессор.</summary>
    private void UpdateBackground()
    {
        var sb = (Storyboard)FindResource("BlobsSb");
        bool run = IsVisible && WindowState != WindowState.Minimized;
        if (run)
        {
            if (!_blobsStarted) { sb.Begin(this, true); _blobsStarted = true; }
            else sb.Resume(this);
        }
        else if (_blobsStarted) sb.Pause(this);
    }

    private readonly Dictionary<Ellipse, Storyboard> _spins = new();

    private void SetSpin(Ellipse e, bool on)
    {
        if (on)
        {
            if (!_spins.ContainsKey(e))
            {
                var sb = ((Storyboard)FindResource("SpinSb")).Clone();
                Storyboard.SetTarget(sb, e);
                sb.Begin(e, true);
                _spins[e] = sb;
            }
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) =>
            {
                if (e.Opacity < 0.05 && _spins.Remove(e, out var sb)) sb.Stop(e);
            };
            e.BeginAnimation(OpacityProperty, fade);
        }
    }

    private DispatcherTimer? _toastTimer;

    private void Toast(string text)
    {
        text = Lc(text);
        if (!IsVisible)
        {
            _tray?.ShowBalloonTip(3500, "обход", text, System.Windows.Forms.ToolTipIcon.None);
            return;
        }
        ToastText.Text = Lc(text);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } });
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 18.0, 3, 7)) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer!.Stop();
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            ToastT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        };
        _toastTimer.Start();
    }


    private void ShowAnimated()
    {
        Show();
        if (_shownOnce) return;
        _shownOnce = true;
        int i = 0;
        foreach (var el in new FrameworkElement[] { TCard, ZCard, VCard })
        {
            var tt = new TranslateTransform(0, 22);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(80 + i++ * 90);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(620))
            { BeginTime = begin, EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        }
    }

    public void ShowFromTray()
    {
        ShowAnimated();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
    }

    // ================= Окно и трей =================

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;
        if (_s.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray?.ShowBalloonTip(3000, "обход работает в трее", "запрет и прокси продолжают работать. выход — через меню значка.", System.Windows.Forms.ToolTipIcon.None);
            }
            return;
        }
        e.Cancel = true;
        _ = ExitAsync();
    }

    internal async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _cts?.Cancel();
        _timer.Stop();
        StopNetworkMonitoring();
        try
        {
            await VpnEngine.DisconnectAsync();
            await ZapretEngine.StopAsync();
            await TgProxy.StopAsync();
        }
        catch { }
        _s.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Application.Current.Shutdown();
    }

    private void SetupTray()
    {
        System.Drawing.Icon icon;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Shield; }
        catch { icon = System.Drawing.SystemIcons.Shield; }

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            ShowImageMargin = true,
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
        };
        menu.Items.Add("открыть обход", null, (_, _) => ShowFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        // пункты со статусом: зелёная точка — работает, красная — выключено; нажатие переключает
        var zItem = menu.Items.Add("", null, async (_, _) => await SetZapretAsync(!ZapretEngine.IsRunning));
        var tItem = menu.Items.Add("", null, async (_, _) => await SetTgAsync(!TgProxy.IsRunning));
        var vItem = menu.Items.Add("", null, async (_, _) => await VpnConnectAsync(!VpnEngine.IsRunning));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("подобрать стратегию", null, async (_, _) => await AutoPickAsync(false, true));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("выход (выключить всё)", null, async (_, _) => await ExitAsync());

        void Status(System.Windows.Forms.ToolStripItem item, string name, bool on, string? detail)
        {
            item.Text = $"{name}: {(on ? "включён" : "выключен")}" + (on && !string.IsNullOrEmpty(detail) ? $"  ·  {Lc(detail)}" : "");
            item.Image = on ? DotOn : DotOff;
            item.ToolTipText = on ? "нажмите, чтобы выключить" : "нажмите, чтобы включить";
        }
        menu.Opening += (_, _) =>
        {
            Status(zItem, "запрет", ZapretEngine.IsRunning, ZapretEngine.RunningStrategy);
            Status(tItem, "telegram-прокси", TgProxy.IsRunning, null);
            Status(vItem, "vpn", VpnEngine.IsRunning, VpnEngine.IsRunning ? SelectedVpn?.Name : null);
        };

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "обход",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, a) => { if (a.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray(); };
    }

    private static readonly System.Drawing.Bitmap DotOn = MakeDot(System.Drawing.Color.FromArgb(48, 209, 88));
    private static readonly System.Drawing.Bitmap DotOff = MakeDot(System.Drawing.Color.FromArgb(255, 69, 58));

    private static System.Drawing.Bitmap MakeDot(System.Drawing.Color c)
    {
        var b = new System.Drawing.Bitmap(16, 16);
        using var g = System.Drawing.Graphics.FromImage(b);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var br = new System.Drawing.SolidBrush(c);
        g.FillEllipse(br, 4, 4, 8, 8);
        return b;
    }

    /// <summary>Тёмное меню трея в стиле программы.</summary>
    private sealed class TrayRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public TrayRenderer() : base(new TrayColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = System.Drawing.Color.FromArgb(240, 240, 242);
            base.OnRenderItemText(e);
        }

        private sealed class TrayColors : System.Windows.Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(28, 28, 30);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(58, 58, 62);
            private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(60, 60, 64);
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Hover;
            public override System.Drawing.Color MenuItemSelected => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => Hover;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Bg;
        }
    }

    // Тёмная рамка и скругление Windows 11
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDwm()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4);
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            int border = 0x00302A2A; DwmSetWindowAttribute(h, 34, ref border, 4);
        }
        catch { }
    }
}
