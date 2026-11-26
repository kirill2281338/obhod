using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Obhod.Core;

namespace Obhod;

public sealed class VpnRow : Observable
{
    public required VpnServer Server { get; init; }
    public string Name => Server.Name.ToLowerInvariant();
    public string Sub => Server.Id == VpnServer.AutoId
        ? $"переключается сам, если сервер отвалился · {Server.Host}"
        : $"{Server.Protocol} · {Server.Host}:{Server.Port}" + (Server.Subscription != null ? " · подписка" : "");
    private string _ping = "";
    public string PingText { get => _ping; set => Set(ref _ping, value); }
    private bool _sel;
    public bool IsSelected
    {
        get => _sel;
        set { Set(ref _sel, value); OnChanged(nameof(RowBrush)); OnChanged(nameof(RowStroke)); OnChanged(nameof(DotVisibility)); }
    }
    public Brush RowBrush => _sel ? Palette.RowBest : Palette.RowNormal;
    public Brush RowStroke => _sel ? Palette.RowBestStroke : Palette.RowStroke;
    public Visibility DotVisibility => _sel ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    private readonly ObservableCollection<VpnRow> _vpnRows = new();
    private bool _vpnBusy;
    private int? _vpnCheck;
    private bool _vpnChecking;

    private VpnSettings V => _s.Vpn;

    /// <summary>Вызывается при запуске: заполняет вкладку и убирает «зависший» системный прокси после аварийного выхода.</summary>
    private void InitVpn()
    {
        VpnList.ItemsSource = _vpnRows;
        _suppressUi = true;
        VpnModeBox.SelectedItem = VpnModeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == V.Mode) ?? VpnModeBox.Items[0];
        VpnRuDirect.IsChecked = V.RuDirect;
        _suppressUi = false;
        CleanupStaleProxy();
        if (!_s.VpnAutoSet)
        {
            if (VpnEngine.RealServers(V).Count >= 2) V.SelectedId = VpnServer.AutoId;
            _s.VpnAutoSet = true;
            _s.Save();
        }
        BuildVpnList();
        UpdateVpnUi();
    }

    private static void CleanupStaleProxy()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", true);
            if (!VpnEngine.IsRunning && k?.GetValue("ProxyServer") is string ps && ps.StartsWith("127.0.0.1:")
                && int.TryParse(ps[10..], out var port) && port >= VpnEngine.PortBase && port <= VpnEngine.PortLast + 1)
            {
                k.SetValue("ProxyEnable", 0, Microsoft.Win32.RegistryValueKind.DWord);
                Log.Write("vpn: убран оставшийся системный прокси после прошлого запуска");
            }
        }
        catch { }
    }

    /// <summary>Строка «авто»: ядро само выбирает быстрейший живой сервер и переключается, если он отвалился.</summary>
    private VpnServer AutoServer => new()
    {
        Id = VpnServer.AutoId, Name = "авто — быстрейший живой сервер", Protocol = "авто",
        Host = $"{VpnEngine.RealServers(V).Count} серверов", Port = 0,
    };

    private void BuildVpnList()
    {
        _vpnRows.Clear();
        var real = VpnEngine.RealServers(V);
        if (real.Count >= 2) _vpnRows.Add(new VpnRow { Server = AutoServer, IsSelected = V.SelectedId == VpnServer.AutoId });
        foreach (var s in real)
            _vpnRows.Add(new VpnRow { Server = s, IsSelected = s.Id == V.SelectedId });
    }

    private VpnServer? SelectedVpn
    {
        get
        {
            var real = VpnEngine.RealServers(V);
            if (V.SelectedId == VpnServer.AutoId && real.Count >= 2) return AutoServer;
            return real.FirstOrDefault(s => s.Id == V.SelectedId)
                   ?? (real.Count >= 2 ? AutoServer : real.FirstOrDefault());
        }
    }

    private void UpdateVpnUi()
    {
        bool needXray = !VpnEngine.XrayInstalled;
        bool needSing = V.Mode == "tun" && !VpnEngine.SingInstalled;
        VpnCoreCard.Visibility = needXray || needSing ? Visibility.Visible : Visibility.Collapsed;
        VpnCoreText.Text = Lc(needXray
            ? "для vpn нужно ядро xray — то же, что внутри happ. оно скачается с github (около 16 мб)" + (needSing ? ", а для режима «весь трафик» ещё sing-box (около 31 мб)." : ".")
            : "для режима «весь трафик» нужно ядро sing-box. оно скачается с github (около 31 мб).");
        bool on = VpnEngine.IsRunning;
        VpnConnectText.Text = Lc(on ? "отключить" : "подключить");
        var sel = SelectedVpn;
        // большая кнопка VPN на главном экране
        VPower.IsChecked = on;
        bool failed = on && _vpnCheck is < 0 && !_vpnChecking;
        bool confirmed = on && _vpnCheck is >= 0 && !_vpnChecking;
        VStatusPill.Text = _vpnChecking ? "проверка" : _vpnBusy ? "подождите" : failed ? "нет ответа" : confirmed ? "работает" : on ? "запущен" : "выключен";
        VPower.Tag = VDot.Fill = _vpnBusy || _vpnChecking ? Palette.Orange : failed ? Palette.Red : confirmed ? Palette.Green : on ? Palette.Orange : Palette.Muted;
        VAction.Text = on ? "отключить vpn" : "подключить vpn";
        VPower.IsEnabled = !_vpnBusy;
        VpnRetryBtn.Visibility = failed && !_vpnBusy ? Visibility.Visible : Visibility.Collapsed;
        VpnMoreLabel.Text = sel == null ? "добавить ссылку" : "серверы и подписки";
        string mode = V.Mode == "tun" ? "весь трафик" : "системный прокси";
        VSub.Text = sel == null ? "добавьте ссылку или подписку" : $"{sel.Name} · {mode}";
        ToolTipService.SetToolTip(VSub, VSub.Text);
        if (!_vpnBusy)
            VpnStatus.Text = Lc(on ? (_vpnCheck == -1 ? "сервер не отвечает · " : _vpnCheck >= 0 ? $"проверка {_vpnCheck} мс · " : "") + $"подключено: {sel?.Name}" + (V.Mode == "tun" ? " · весь трафик" : " · системный прокси 127.0.0.1:" + VpnEngine.HttpPort)
                : V.Servers.Count == 0 ? "добавьте ссылку или подписку" : "не подключено");
    }

    private void VpnPaste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) VpnInput.Text = Clipboard.GetText().Trim(); } catch { }
    }

    private async void VpnAdd_Click(object sender, RoutedEventArgs e)
    {
        var text = VpnInput.Text.Trim();
        if (text.Length == 0) { Toast("вставьте ссылку из happ: подписку https://… или vless://…"); return; }
        VpnStatus.Text = Lc("разбираю ссылку…");
        try
        {
            var (servers, sub, error) = await VpnLinks.ParseInputAsync(text, CancellationToken.None);
            if (error != null) { Toast(error); UpdateVpnUi(); return; }
            if (sub != null)
            {
                if (!V.Subscriptions.Contains(sub)) V.Subscriptions.Add(sub);
                V.Servers.RemoveAll(s => s.Subscription == sub);
            }
            int added = 0;
            foreach (var s in servers)
                if (!V.Servers.Any(x => x.Id == s.Id)) { V.Servers.Add(s); added++; }
            V.SelectedId ??= servers.Count(x => !x.IsPlaceholder) >= 2 ? VpnServer.AutoId : servers.FirstOrDefault(x => !x.IsPlaceholder)?.Id;
            _s.Save();
            VpnInput.Clear();
            BuildVpnList();
            Toast(sub != null ? $"подписка добавлена: серверов {servers.Count}." : $"добавлено серверов: {added}.");
            _ = PingAllAsync();
        }
        catch (Exception ex)
        {
            Log.Write("VPN: " + ex.Message);
            Toast("не удалось добавить: " + ex.Message);
        }
        UpdateVpnUi();
    }

    private async void VpnRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VpnRow row) return;
        if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) != null) return; // нажали «удалить»
        V.SelectedId = row.Server.Id;  // для строки «авто» это "auto"
        _s.Save();
        foreach (var r in _vpnRows) r.IsSelected = r == row;
        if (VpnEngine.IsRunning) await VpnConnectAsync(true);
        UpdateVpnUi();
    }

    private void VpnRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VpnRow row) return;
        V.Servers.Remove(row.Server);
        if (V.SelectedId == row.Server.Id) V.SelectedId = V.Servers.FirstOrDefault()?.Id;
        // подписка без серверов больше не нужна
        if (row.Server.Subscription is string sub && !V.Servers.Any(s => s.Subscription == sub)) V.Subscriptions.Remove(sub);
        _s.Save();
        BuildVpnList();
        UpdateVpnUi();
    }

    private async void VpnConnect_Click(object sender, RoutedEventArgs e) => await VpnConnectAsync(!VpnEngine.IsRunning);

    private async void VpnRetry_Click(object sender, RoutedEventArgs e) => await RecheckVpnAsync();

    private async Task RecheckVpnAsync()
    {
        if (_vpnBusy || !VpnEngine.IsRunning) return;
        _vpnBusy = true;
        try { await VerifyVpnAsync(retry: false); }
        finally { _vpnBusy = false; UpdateVpnUi(); }
    }

    private async Task VerifyVpnAsync(bool retry)
    {
        _vpnChecking = true;
        try
        {
            UpdateVpnUi();
            VpnStatus.Text = "проверяю соединение…";
            var ms = await VpnEngine.VerifyAsync(6);
            if (ms < 0 && retry)
            {
                await Task.Delay(2500);
                ms = await VpnEngine.VerifyAsync(8);
            }
            _vpnCheck = ms;
            Log.Write(ms >= 0 ? $"vpn: проверка прошла, {ms} мс" : "vpn: сервер не отвечает");
        }
        finally { _vpnChecking = false; }
    }

    private async Task VpnConnectAsync(bool on)
    {
        if (_vpnBusy) return;
        _vpnBusy = true;
        _vpnCheck = null;
        UpdateVpnUi();
        VpnConnectBtn.IsEnabled = false;
        SetSpin(VSpin, true);
        try
        {
            if (!on)
            {
                VpnStatus.Text = Lc("отключаю…");
                await VpnEngine.DisconnectAsync();
                Log.Write("vpn отключён");
                return;
            }
            var sel = SelectedVpn;
            if (sel == null) { Toast("сначала добавьте сервер."); return; }
            if (!VpnEngine.XrayInstalled || (V.Mode == "tun" && !VpnEngine.SingInstalled))
            { Toast("сначала скачайте ядро: кнопка «скачать» выше."); return; }
            VpnStatus.Text = Lc($"подключаюсь к {sel.Name}…");
            var err = await VpnEngine.ConnectAsync(sel, V);
            if (err != null) { Toast(err); Log.Write("vpn: " + err); }
            else
            {
                await VerifyVpnAsync(retry: true);
                if (_vpnCheck is < 0) Toast(sel.Id == VpnServer.AutoId
                    ? "vpn включён, но ни один сервер пока не ответил. проверьте подписку или интернет."
                    : "vpn включён, но этот сервер не отвечает. выберите «авто» — ядро само найдёт живой сервер.");
            }
        }
        finally
        {
            _vpnBusy = false;
            VpnConnectBtn.IsEnabled = true;
            SetSpin(VSpin, false);
            UpdateVpnUi();
        }
    }

    private async void VpnMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUi || VpnModeBox.SelectedItem is not ComboBoxItem it) return;
        V.Mode = (string)it.Tag;
        _s.Save();
        UpdateVpnUi();
        if (VpnEngine.IsRunning) await VpnConnectAsync(true);
    }

    private async void VpnRuDirect_Click(object sender, RoutedEventArgs e)
    {
        V.RuDirect = VpnRuDirect.IsChecked == true;
        _s.Save();
        if (VpnEngine.IsRunning) await VpnConnectAsync(true);
    }

    private async void VpnPing_Click(object sender, RoutedEventArgs e) => await PingAllAsync();

    private async Task PingAllAsync()
    {
        foreach (var r in _vpnRows) r.PingText = "…";
        await Task.WhenAll(_vpnRows.ToList().Select(async r =>
        {
            if (r.Server.Id == VpnServer.AutoId) { r.PingText = "сам выбирает"; return; }
            var ms = await VpnEngine.PingAsync(r.Server, CancellationToken.None);
            r.PingText = ms < 0 ? "нет ответа" : $"{ms} мс";
        }));
    }

    private async void VpnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (V.Subscriptions.Count == 0) { Toast("подписок нет: добавьте ссылку https://… из happ."); return; }
        VpnStatus.Text = Lc("обновляю подписки…");
        int total = 0;
        foreach (var sub in V.Subscriptions.ToList())
        {
            try
            {
                var list = await VpnLinks.FetchSubscriptionAsync(sub, CancellationToken.None);
                if (list.Count == 0) continue;
                V.Servers.RemoveAll(s => s.Subscription == sub);
                V.Servers.AddRange(list);
                total += list.Count;
            }
            catch (Exception ex) { Log.Write($"vpn: подписка не обновилась: {ex.Message}"); }
        }
        if (V.Servers.All(s => s.Id != V.SelectedId)) V.SelectedId = V.Servers.FirstOrDefault()?.Id;
        _s.Save();
        BuildVpnList();
        UpdateVpnUi();
        Toast($"подписки обновлены: серверов {total}.");
        _ = PingAllAsync();
    }

    private async void VpnCore_Click(object sender, RoutedEventArgs e)
    {
        VpnCoreBtn.IsEnabled = false;
        VpnCoreBarHost.Visibility = Visibility.Visible;
        var prog = new Progress<double>(v => VpnCoreBar.Width = ((FrameworkElement)VpnCoreBar.Parent).ActualWidth * v);
        try
        {
            if (!VpnEngine.XrayInstalled) { VpnCoreText.Text = Lc("скачиваю xray с github…"); await VpnEngine.InstallXrayAsync(prog, CancellationToken.None); }
            if (V.Mode == "tun" && !VpnEngine.SingInstalled) { VpnCoreText.Text = Lc("скачиваю sing-box с github…"); await VpnEngine.InstallSingBoxAsync(prog, CancellationToken.None); }
            Toast("ядро vpn установлено.");
        }
        catch (Exception ex)
        {
            Log.Write("vpn: ядро не скачалось: " + ex);
            Toast("не удалось скачать ядро: " + ex.Message + ". включите запрет и попробуйте ещё раз.");
        }
        finally
        {
            VpnCoreBtn.IsEnabled = true;
            VpnCoreBarHost.Visibility = Visibility.Collapsed;
            UpdateVpnUi();
            UpdateVersionTexts();
        }
    }

    // ---------- Обновления ядер VPN (страница «обновления») ----------

    private ReleaseInfo? _xRel, _sRel;

    /// <summary>Обновляет карточки xray и sing-box; возвращает true, если есть что обновить.</summary>
    private bool UpdateCoreTexts()
    {
        var xl = VpnEngine.XrayInstalled ? VpnEngine.LocalVersion(VpnEngine.XrayDir) : null;
        var sl = VpnEngine.SingInstalled ? VpnEngine.LocalVersion(VpnEngine.SingDir) : null;
        XVer.Text = Lc($"установлено: {xl ?? "нет"}   ·   на github: {_xRel?.Version ?? "—"}");
        SVer.Text = Lc($"установлено: {sl ?? "нет"}   ·   на github: {_sRel?.Version ?? "—"}");
        bool xNew = xl != null && _xRel != null && (xl == "?" || Updater.IsNewer(_xRel.Version, xl));
        bool sNew = sl != null && _sRel != null && (sl == "?" || Updater.IsNewer(_sRel.Version, sl));
        XUpdBtn.Content = xl == null ? "скачать" : xNew ? "обновить" : "актуально";
        SUpdBtn.Content = sl == null ? "скачать" : sNew ? "обновить" : "актуально";
        XUpdBtn.IsEnabled = xl == null || xNew;
        SUpdBtn.IsEnabled = sl == null || sNew;
        return xNew || sNew;
    }

    private async void CoreUpdate_Click(object sender, RoutedEventArgs e)
    {
        bool xray = (string)((FrameworkElement)sender).Tag == "X";
        var btn = xray ? XUpdBtn : SUpdBtn;
        var host = xray ? XUpdBarHost : SUpdBarHost;
        var bar = xray ? XUpdBar : SUpdBar;
        bool wasOn = VpnEngine.IsRunning;
        btn.IsEnabled = false;
        host.Visibility = Visibility.Visible;
        var prog = new Progress<double>(v => bar.Width = ((FrameworkElement)bar.Parent).ActualWidth * v);
        try
        {
            if (wasOn) await VpnEngine.DisconnectAsync();
            if (xray) await VpnEngine.InstallXrayAsync(prog, CancellationToken.None, _xRel);
            else await VpnEngine.InstallSingBoxAsync(prog, CancellationToken.None, _sRel);
            Toast((xray ? "xray" : "sing-box") + " обновлён.");
            if (wasOn) await VpnConnectAsync(true);
        }
        catch (Exception ex)
        {
            Log.Write("ядро vpn: " + ex);
            Toast("не удалось скачать ядро: " + ex.Message);
        }
        finally
        {
            host.Visibility = Visibility.Collapsed;
            UpdateVersionTexts();
            UpdateVpnUi();
        }
    }

    private void OpenVpnPage_Click(object sender, RoutedEventArgs e)
    {
        bool xray = (string)((FrameworkElement)sender).Tag == "X";
        var url = (xray ? _xRel?.PageUrl : _sRel?.PageUrl) ?? $"https://github.com/{(xray ? VpnEngine.XrayRepo : VpnEngine.SingRepo)}/releases";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }
}
