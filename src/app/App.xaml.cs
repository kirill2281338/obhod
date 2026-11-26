using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Obhod.Core;

namespace Obhod;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _updateExitEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--vpn-auto-config")
        {
            // служебная проверка: конфиг режима «авто» по сохранённой подписке
            var st = AppSettings.Load();
            var auto = new VpnServer { Id = VpnServer.AutoId, Name = "auto" };
            System.IO.File.WriteAllText(e.Args[1], VpnEngine.BuildXrayConfig(auto, st.Vpn).ToJsonString());
            Shutdown();
            return;
        }
        if (e.Args.Length >= 1 && e.Args[0] == "--dns")
        {
            // окно dns отдельно (для проверки)
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var dw = new DnsWindow { WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = true };
            MainWindow = dw;
            dw.Show();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--vpn-config")
        {
            // Служебная проверка: разобрать ссылку и записать конфиг Xray рядом с журналом
            try
            {
                var (list, _, err) = System.Threading.Tasks.Task.Run(() => VpnLinks.ParseInputAsync(e.Args[1], System.Threading.CancellationToken.None)).GetAwaiter().GetResult();
                if (err != null) throw new InvalidOperationException(err);
                var s = list.First();
                var cfg = VpnEngine.BuildXrayConfig(s, new VpnSettings()).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Root, "vpn-test.json"), s.Name + "\n" + cfg);
            }
            catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Root, "vpn-test.json"), ex.ToString()); }
            Shutdown();
            return;
        }
        // Ждём до 3 с: при переустановке старый экземпляр может ещё завершаться
        _single = new Mutex(false, "obhod.Single");
        bool first;
        try { first = _single.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { first = true; }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "obhod.Show");
        if (!first)
        {
            _showEvent.Set(); // второй запуск — просто показываем окно первого
            Shutdown();
            return;
        }

        // перенос данных из старой папки (программа раньше называлась иначе)
        try { Migration.Run(); } catch { }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Write("ошибка: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("критическая ошибка: " + ex.ExceptionObject);

        var tray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        var w = new MainWindow(tray);
        MainWindow = w;
        _updateExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "obhod.ExitForUpdate");

        var t = new Thread(() =>
        {
            while (WaitHandle.WaitAny(new[] { _showEvent, _updateExitEvent }) == 0)
                Dispatcher.BeginInvoke(() => w.ShowFromTray());
            Dispatcher.BeginInvoke(() => _ = w.ExitAsync());
        }) { IsBackground = true };
        t.Start();
    }
}
