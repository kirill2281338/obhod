using System;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Obhod.Core;

namespace Obhod;

public partial class MainWindow
{
    private readonly DispatcherTimer _networkTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _networkKey, _previousStrategy;
    private int? _zapretCheck;
    private bool _networkChecking, _recheckOnWake, _networkInitialized;

    private async Task StartNetworkMonitoringAsync()
    {
        _networkTimer.Tick += NetworkTimer_Tick;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        SystemEvents.PowerModeChanged += PowerModeChanged;
        try { _networkKey = await ReadNetworkKeyAsync(); }
        catch (Exception ex) { Log.Write("профиль сети: " + ex.GetType().Name); }
        if (!_exiting) RefreshNetworkStrategy();
        _networkInitialized = true;
    }

    private void StopNetworkMonitoring()
    {
        _networkTimer.Stop();
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        SystemEvents.PowerModeChanged -= PowerModeChanged;
    }

    private void NetworkAddressChanged(object? sender, EventArgs e) => QueueNetworkCheck(recheck: false);

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) QueueNetworkCheck(recheck: true);
    }

    private void QueueNetworkCheck(bool recheck) => Dispatcher.BeginInvoke(() =>
    {
        if (_exiting) return;
        _recheckOnWake |= recheck;
        _networkTimer.Stop();
        _networkTimer.Start();
    });

    private async void NetworkTimer_Tick(object? sender, EventArgs e)
    {
        if (_networkChecking || _zBusy || _testBusy || _vpnBusy) return;
        _networkTimer.Stop();
        _networkChecking = true;
        try
        {
            var key = await ReadNetworkKeyAsync();
            if (_exiting) return;
            bool shouldRecheck = _recheckOnWake || (_networkInitialized && key != _networkKey);
            _recheckOnWake = false;
            _networkKey = key;
            _networkInitialized = true;
            RefreshNetworkStrategy();
            if (shouldRecheck) await Task.WhenAll(RecheckZapretAsync(), RecheckVpnAsync());
        }
        catch (Exception ex) { Log.Write("проверка сети: " + ex.GetType().Name); }
        finally { _networkChecking = false; }
    }

    private static async Task<string?> ReadNetworkKeyAsync()
    {
        var start = new ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
            "$n=@(Get-NetConnectionProfile | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }); " +
            "if ($n.Count -eq 1) { Write-Output ($n[0].InterfaceAlias + '|' + $n[0].Name) }" })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process == null) return null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            string identity = (await process.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || identity.Length == 0) return null;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            return null;
        }
    }

    private async Task RecheckZapretAsync()
    {
        var active = ZapretEngine.RunningStrategy;
        if (active == null || _zBusy || _testBusy) return;
        var targets = ServiceCatalog.Targets(_s);
        if (targets.Count == 0) return;
        var report = await Tester.RunAsync(targets, 5, CancellationToken.None);
        if (_exiting || _zBusy || _testBusy || active != ZapretEngine.RunningStrategy) return;
        _zapretCheck = report.Perfect ? 1 : -1;
        UpdateServiceStatuses(report);
        await RefreshStatusAsync();
    }

    private Strategy? NetworkStrategy => _networkKey is string key && _s.NetworkStrategies.TryGetValue(key, out var name)
        ? _strategies.FirstOrDefault(s => s.Name == name) : null;

    private void RefreshNetworkStrategy()
    {
        var strategy = NetworkStrategy;
        bool show = strategy != null && strategy.Name != ZapretEngine.RunningStrategy;
        NetworkStrategyBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        NetworkStrategyBtn.Content = show ? Lc($"для этой сети: {strategy!.Name}") : null;
        NetworkStrategyBtn.IsEnabled = !_zBusy && !_testBusy;
    }

    private void SetPreviousStrategy(string? name)
    {
        _previousStrategy = name;
        RestoreStrategyBtn.Visibility = name == null ? Visibility.Collapsed : Visibility.Visible;
        RestoreStrategyBtn.ToolTip = name;
    }

    private async void RestoreStrategy_Click(object sender, RoutedEventArgs e)
    {
        var strategy = _strategies.FirstOrDefault(x => x.Name == _previousStrategy);
        if (strategy != null && await ApplySavedStrategyAsync(strategy))
        {
            SetPreviousStrategy(null);
            TestSummary.Text = Lc($"восстановлена стратегия: {strategy.Name}.");
        }
    }

    private async void NetworkStrategy_Click(object sender, RoutedEventArgs e)
    {
        if (NetworkStrategy is { } strategy) await ApplySavedStrategyAsync(strategy);
    }

    private async Task RestoreAfterTestAsync(string? previous)
    {
        if (_exiting) return;
        if (previous == null) { await ZapretEngine.StopAsync(); return; }
        if (ZapretEngine.RunningStrategy == previous) return;
        var strategy = _strategies.FirstOrDefault(s => s.Name == previous);
        if (strategy != null)
        {
            var error = await ZapretEngine.StartAsync(strategy, _s);
            if (error != null) Log.Write("восстановление стратегии: " + error);
        }
    }

    private async Task<bool> ApplySavedStrategyAsync(Strategy strategy)
    {
        if (_zBusy || _testBusy) return false;
        var previous = ZapretEngine.RunningStrategy;
        _zBusy = true;
        SetSpin(ZSpin, true);
        try
        {
            var error = await ZapretEngine.StartAsync(strategy, _s);
            if (error != null)
            {
                Toast(error);
                await RestoreAfterTestAsync(previous);
                return false;
            }
            _zapretCheck = null;
            _s.SelectedStrategy = strategy.Name;
            _s.Save();
            return true;
        }
        catch (Exception ex) { Toast(ex.Message); return false; }
        finally
        {
            _zBusy = false;
            SetSpin(ZSpin, false);
            RefreshStrategyBox();
            RefreshNetworkStrategy();
            await RefreshStatusAsync();
        }
    }
}
