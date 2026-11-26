using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ObhodSetup
{
    internal static class Program
    {
        public const string AppName = "obhod";
        public static readonly string LegacyName = "Zapret" + "Hub";
        public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\obhod";
        public static readonly string LegacyUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + LegacyName;

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obhod");
        public static string LegacyDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyName);
        public static string LegacyInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zapret" + " Hub");
        public static readonly string LogFile = Path.Combine(Path.GetTempPath(), "obhod-setup.log");

        [STAThread]
        private static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("unhandled: " + e.ExceptionObject);
            Application.ThreadException += (s, e) => { Log("thread: " + e.Exception); MessageBox.Show(e.Exception.Message, "обход"); };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
            if (args.Any(a => a.Equals("/selftest", StringComparison.OrdinalIgnoreCase)))
            {
                // Проверка скачивания без установки: пишет результат в журнал установщика
                try { Log("selftest: " + Installer.SelfTest()); }
                catch (Exception ex) { Log("selftest failed: " + ex); Environment.ExitCode = 1; }
                return;
            }
            if (args.Any(a => a.Equals("/silent", StringComparison.OrdinalIgnoreCase)) && !uninstall)
            {
                // Тихая установка/обновление: без окна, с ярлыками, затем запуск программы
                try
                {
                    Installer.Install(true, (s, p) => { });
                    Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "obhod.exe")) { UseShellExecute = true });
                }
                catch (Exception ex) { Log("silent: " + ex); Environment.ExitCode = 1; }
                return;
            }
            bool checkUpdates = !uninstall && !args.Any(a => a.Equals("/skip-update-check", StringComparison.OrdinalIgnoreCase));
            try { Application.Run(new SetupForm(uninstall, checkUpdates)); }
            catch (Exception ex) { Log("main: " + ex); MessageBox.Show(ex.Message, "обход"); }
        }

        public static void Log(string text)
        {
            try { File.AppendAllText(LogFile, DateTime.Now + "  " + text + Environment.NewLine); } catch { }
        }
    }

    /// <summary>Шрифты как в программе: Oswald для заголовка, Manrope для текста (встроены в установщик).</summary>
    internal static class UiFonts
    {
        private static readonly System.Drawing.Text.PrivateFontCollection Collection = new System.Drawing.Text.PrivateFontCollection();
        private static FontFamily _display, _body;

        static UiFonts()
        {
            foreach (var name in new[] { "Oswald-SemiBold.ttf", "Manrope-Regular.ttf", "Manrope-Bold.ttf" })
            {
                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
                    {
                        var data = new byte[s.Length];
                        s.Read(data, 0, data.Length);
                        var ptr = Marshal.AllocCoTaskMem(data.Length); // память не освобождаем: GDI+ читает шрифт из неё
                        Marshal.Copy(data, 0, ptr, data.Length);
                        Collection.AddMemoryFont(ptr, data.Length);
                    }
                }
                catch (Exception ex) { Program.Log("font " + name + ": " + ex.Message); }
            }
            _display = Collection.Families.FirstOrDefault(f => f.Name.StartsWith("Oswald")) ?? new FontFamily("Segoe UI Semibold");
            _body = Collection.Families.FirstOrDefault(f => f.Name.StartsWith("Manrope")) ?? new FontFamily("Segoe UI");
        }

        public static Font Display(float size) => new Font(_display, size, FontStyle.Regular, GraphicsUnit.Point);
        public static Font Body(float size, bool bold = false)
        {
            var style = bold && _body.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            return new Font(_body, size, style, GraphicsUnit.Point);
        }
    }

    /// <summary>
    /// Установщик-мастер в стиле программы: графит, стеклянные фигуры, белая главная кнопка.
    /// Шаги: 1) что внутри → 2) антивирус → 3) параметры → установка → готово (с подсказкой про картинки discord).
    /// </summary>
    internal sealed class SetupForm : Form
    {
        private readonly bool _uninstall;
        private int _step;                 // 0 что внутри, 1 антивирус, 2 параметры, 3 установка, 4 готово
        private string _status = "";
        private string _error;
        private double _progress;
        private bool _busy;
        private bool _desktopShortcut = true, _launch = true, _removeData = true;
        private Rectangle _btn, _back, _close, _link, _chk1, _chk2;
        private bool _hoverBtn, _hoverBack, _hoverClose;
        private Point _drag;

        private static readonly Color White = Color.FromArgb(245, 245, 247);
        private static readonly Color Text2 = Color.FromArgb(170, 255, 255, 255);
        private static readonly Color Text3 = Color.FromArgb(120, 255, 255, 255);
        private static readonly Color Ink = Color.FromArgb(10, 10, 11);
        private static readonly Color Red = Color.FromArgb(255, 105, 97);

        private const int Pad = 52;
        private static readonly Image Logo = LoadLogo();

        private static Image LoadLogo()
        {
            try { using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")) using (var img = Image.FromStream(st)) return new Bitmap(img); }
            catch { return null; }
        }
        private static readonly string[] StepNames = { "что внутри", "антивирус", "установка", "установка", "готово" };

        public SetupForm(bool uninstall, bool checkUpdates)
        {
            if (checkUpdates) Shown += CheckForRelease;
            _uninstall = uninstall;
            Text = uninstall ? "удаление: обход" : "установка: обход";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 600);
            BackColor = Color.FromArgb(28, 28, 30);
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Font = UiFonts.Body(10f);
            Region = new Region(RoundRect(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), 26));
        }

        private async void CheckForRelease(object sender, EventArgs e)
        {
            try
            {
                var release = await Task.Run(InstallerUpdates.FindNewer);
                if (release == null || IsDisposed || _busy || _step > 2) return;
                if (MessageBox.Show(this, $"Доступен обход {release.Version}. Скачать и открыть новый установщик?", "обновление обхода",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

                _busy = true;
                Invalidate();
                using (var installer = await Task.Run(() => InstallerUpdates.Download(release)))
                using (var process = installer.Start())
                {
                    Hide();
                    await Task.Run(() => process.WaitForExit());
                }
                Close();
            }
            catch (Exception ex)
            {
                Program.Log("setup update: " + ex.Message);
                if (_busy && !IsDisposed)
                    MessageBox.Show(this, "Не удалось открыть обновление. " + ex.Message, "обход", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!IsDisposed && _step < 3)
                {
                    _busy = false;
                    Invalidate();
                }
            }
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }
        }

        // ---------------- рисование: базовые элементы ----------------

        private static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private static LinearGradientBrush RimBrush(RectangleF r)
        {
            var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), Color.White, Color.White, 45f);
            b.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(230, 255, 255, 255), Color.FromArgb(64, 255, 255, 255), Color.FromArgb(20, 255, 255, 255),
                    Color.FromArgb(64, 255, 255, 255), Color.FromArgb(180, 255, 255, 255),
                },
                Positions = new[] { 0f, 0.16f, 0.5f, 0.84f, 1f },
            };
            return b;
        }

        private static void Glass(Graphics g, RectangleF r, float radius, int fillAlpha = 18)
        {
            using (var path = RoundRect(r, radius))
            {
                using (var fill = new SolidBrush(Color.FromArgb(fillAlpha, 255, 255, 255))) g.FillPath(fill, path);
                using (var rim = RimBrush(r)) using (var pen = new Pen(rim, 1.1f)) g.DrawPath(pen, path);
            }
        }

        /// <summary>Абзац с переносом строк; возвращает y под текстом.</summary>
        private static float Para(Graphics g, string text, Font f, Color c, float x, float y, float width)
        {
            var size = g.MeasureString(text, f, (int)width);
            using (var b = new SolidBrush(c)) g.DrawString(text, f, b, new RectangleF(x, y, width, size.Height + 2));
            return y + size.Height;
        }

        private static float Bullet(Graphics g, string text, float x, float y, float width)
        {
            using (var dot = new SolidBrush(White)) g.FillEllipse(dot, x + 2, y + 8, 5, 5);
            using (var f = UiFonts.Body(10f)) return Para(g, text, f, Text2, x + 18, y, width - 18) + 4;
        }

        private static float Numbered(Graphics g, int n, string text, float x, float y, float width)
        {
            var circle = new RectangleF(x, y + 1, 20, 20);
            using (var b = new SolidBrush(Color.FromArgb(30, 255, 255, 255))) g.FillEllipse(b, circle);
            using (var f = UiFonts.Body(8.5f, true))
            using (var b = new SolidBrush(White))
            {
                var sz = g.MeasureString(n.ToString(), f);
                g.DrawString(n.ToString(), f, b, circle.X + (20 - sz.Width) / 2 + 0.5f, circle.Y + (20 - sz.Height) / 2);
            }
            using (var f = UiFonts.Body(10f)) return Math.Max(y + 24, Para(g, text, f, Text2, x + 30, y + 1, width - 30) + 5);
        }

        private void Button(Graphics g, Rectangle r, string label, bool primary, bool hover, bool enabled = true)
        {
            using (var p = RoundRect(r, r.Height / 2f))
            {
                if (primary)
                {
                    var fill = !enabled ? Color.FromArgb(60, 255, 255, 255) : hover ? Color.White : White;
                    using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                }
                else
                {
                    using (var b = new SolidBrush(Color.FromArgb(hover ? 36 : 18, 255, 255, 255))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                }
            }
            using (var f = UiFonts.Body(11f, primary))
            using (var b = new SolidBrush(primary && enabled ? Ink : White))
            {
                var sz = g.MeasureString(label, f);
                g.DrawString(label, f, b, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
            }
        }

        private static void Switch(Graphics g, Rectangle r, bool on, string text)
        {
            var box = new RectangleF(r.X, r.Y + 3, 46, 26);
            using (var p = RoundRect(box, 13))
            {
                using (var b = new SolidBrush(on ? White : Color.FromArgb(26, 255, 255, 255))) g.FillPath(b, p);
                if (!on) using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f)) g.DrawPath(pen, p);
            }
            float kx = on ? box.Right - 23 : box.X + 3;
            using (var b = new SolidBrush(on ? Ink : Color.FromArgb(142, 142, 147))) g.FillEllipse(b, kx, box.Y + 3, 20, 20);
            using (var f = UiFonts.Body(10.5f))
            using (var b = new SolidBrush(White))
                g.DrawString(text, f, b, box.Right + 14, r.Y + 6);
        }

        // ---------------- рисование: окно ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;

            // фон и стеклянные фигуры
            using (var bg = new PathGradientBrush(new[] { new PointF(-w, -h), new PointF(2 * w, -h), new PointF(2 * w, 2 * h), new PointF(-w, 2 * h) })
            {
                CenterPoint = new PointF(w * 0.3f, h * 0.2f),
                CenterColor = Color.FromArgb(48, 48, 51),
                SurroundColors = new[] { Color.FromArgb(22, 22, 24) },
            })
                g.FillRectangle(bg, 0, 0, w, h);
            Glass(g, new RectangleF(-90, -110, 230, 230), 115, 20);
            Glass(g, new RectangleF(w - 200, -30, 260, 76), 38, 20);
            Glass(g, new RectangleF(w - 150, h - 170, 170, 170), 85, 20);
            Glass(g, new RectangleF(-60, h - 120, 200, 84), 24, 20);

            // карточка
            var card = new RectangleF(22, 22, w - 44, h - 44);
            using (var p = RoundRect(card, 26))
            {
                using (var b = new LinearGradientBrush(card, Color.FromArgb(165, 36, 36, 39), Color.FromArgb(185, 24, 24, 26), 90f)) g.FillPath(b, p);
                using (var rim = RimBrush(card)) using (var pen = new Pen(rim, 1.2f)) g.DrawPath(pen, p);
            }

            // шапка: логотип, название, шаг
            var logo = new RectangleF(Pad - 2, 46, 48, 48);
            if (Logo != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, logo);
            }
            using (var f = UiFonts.Display(21f))
            using (var b = new SolidBrush(White))
                g.DrawString("обход", f, b, Pad + 56, 44);
            var stepLine = _uninstall ? "удаление программы" : $"шаг {Math.Min(_step, 3) + 1 - (_step >= 3 ? 1 : 0)} из 4 · {StepNames[_step]}";
            if (!_uninstall && _step == 4) stepLine = "шаг 4 из 4 · готово";
            using (var f = UiFonts.Body(9.5f))
            using (var b = new SolidBrush(Text3))
                g.DrawString(stepLine, f, b, Pad + 58, 82);

            // крестик
            _close = new Rectangle(w - Pad - 30, 54, 32, 32);
            using (var b = new SolidBrush(Color.FromArgb(_hoverClose ? 46 : 14, 255, 255, 255))) g.FillEllipse(b, _close);
            using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255), 1f)) g.DrawEllipse(pen, _close);
            using (var pen = new Pen(_hoverClose ? White : Text2, 1.4f))
            {
                g.DrawLine(pen, _close.X + 12, _close.Y + 12, _close.Right - 12, _close.Bottom - 12);
                g.DrawLine(pen, _close.Right - 12, _close.Y + 12, _close.X + 12, _close.Bottom - 12);
            }

            // индикатор шагов
            if (!_uninstall)
            {
                int segs = 4, gap = 8;
                float segW = (w - 2 * Pad - gap * (segs - 1)) / (float)segs;
                int current = _step <= 2 ? _step : _step == 3 ? 2 : 3;
                for (int i = 0; i < segs; i++)
                {
                    var r = new RectangleF(Pad + i * (segW + gap), 112, segW, 4);
                    using (var p = RoundRect(r, 2))
                    using (var b = new SolidBrush(i <= current ? White : Color.FromArgb(34, 255, 255, 255))) g.FillPath(b, p);
                }
            }

            _chk1 = _chk2 = _back = _link = Rectangle.Empty;
            float x = Pad, y = 138, cw = w - 2 * Pad;
            if (_uninstall) DrawUninstall(g, x, y, cw);
            else
                switch (_step)
                {
                    case 0: DrawWhat(g, x, y, cw); break;
                    case 1: DrawAntivirus(g, x, y, cw); break;
                    case 2: DrawOptions(g, x, y, cw); break;
                    case 3: DrawProgress(g, x, y, cw); break;
                    default: DrawDone(g, x, y, cw); break;
                }

            // нижняя панель кнопок
            _btn = new Rectangle(w - Pad - 210, h - Pad - 46, 210, 46);
            string label;
            bool enabled = !_busy;
            if (_uninstall) label = _busy ? "подождите…" : _step == 4 ? "закрыть" : "удалить";
            else switch (_step)
            {
                case 0: case 1: label = "далее"; break;
                case 2: label = "установить"; break;
                case 3: label = _error != null ? "повторить" : "подождите…"; enabled = _error != null; break;
                default: label = "готово"; break;
            }
            Button(g, _btn, label, true, _hoverBtn && enabled, enabled);

            bool canBack = !_uninstall && !_busy && ((_step >= 1 && _step <= 2) || (_step == 3 && _error != null));
            if (canBack)
            {
                _back = new Rectangle(Pad, h - Pad - 46, 130, 46);
                Button(g, _back, "назад", false, _hoverBack);
            }
        }

        private void DrawWhat(Graphics g, float x, float y, float cw)
        {
            using (var f = UiFonts.Body(14f, true)) y = Para(g, "три кнопки в одной программе", f, White, x, y, cw) + 12;
            var items = new[]
            {
                ("запрет", "youtube, discord и другие сайты без vpn. лучшая стратегия подбирается сама."),
                ("telegram", "прокси для telegram, подключается в один клик."),
                ("vpn", "ссылки и подписки из happ и incy: vless, vmess, trojan, shadowsocks."),
            };
            foreach (var (title, desc) in items)
            {
                var row = new RectangleF(x, y, cw, 62);
                Glass(g, row, 18, 12);
                using (var f = UiFonts.Display(14f))
                using (var b = new SolidBrush(White))
                    g.DrawString(title, f, b, x + 18, y + 9);
                using (var f = UiFonts.Body(9.5f)) Para(g, desc, f, Text2, x + 18, y + 35, cw - 36);
                y += 72;
            }
            using (var f = UiFonts.Body(9.5f))
                Para(g, "нужен интернет: если на компьютере нет .net 10, он скачается с сайта microsoft (около 60 мб), прокси для telegram — с github (около 21 мб).", f, Text3, x, y + 4, cw);
        }

        private void DrawAntivirus(Graphics g, float x, float y, float cw)
        {
            using (var f = UiFonts.Body(14f, true)) y = Para(g, "антивирус может ругаться — это нормально", f, White, x, y, cw) + 6;
            using (var f = UiFonts.Body(10f)) y = Para(g, "защитник windows иногда называет программу «трояном». это ложное срабатывание, потому что:", f, Text2, x, y, cw) + 10;
            y = Bullet(g, "запрет перехватывает трафик через драйвер windivert;", x, y, cw);
            y = Bullet(g, "программа правит файл hosts, чтобы открыть картинки discord;", x, y, cw);
            y = Bullet(g, "у файлов нет цифровой подписи, нужны права администратора;", x, y, cw);
            y = Bullet(g, "установщик докачивает файлы из интернета, а сам он пришёл из мессенджера.", x, y, cw);
            y += 10;
            var box = new RectangleF(x, y, cw, 104);
            Glass(g, box, 18, 12);
            using (var f = UiFonts.Body(10.5f, true)) Para(g, "если файл удалили или заблокировали", f, White, x + 18, y + 12, cw - 36);
            float yy = y + 40;
            yy = Numbered(g, 1, "откройте «безопасность windows» → «журнал защиты»", x + 18, yy, cw - 36);
            Numbered(g, 2, "выберите запись → «разрешить на устройстве» и запустите файл снова", x + 18, yy, cw - 36);
        }

        private void DrawOptions(Graphics g, float x, float y, float cw)
        {
            using (var f = UiFonts.Body(14f, true)) y = Para(g, "куда и как установить", f, White, x, y, cw) + 12;
            var box = new RectangleF(x, y, cw, 58);
            Glass(g, box, 18, 12);
            using (var f = UiFonts.Body(9f)) Para(g, "папка программы", f, Text3, x + 18, y + 9, cw - 36);
            using (var f = UiFonts.Body(10.5f)) Para(g, Program.InstallDir, f, White, x + 18, y + 28, cw - 36);
            y += 76;
            _chk1 = new Rectangle((int)x, (int)y, (int)cw, 34);
            Switch(g, _chk1, _desktopShortcut, "ярлык на рабочем столе");
            _chk2 = new Rectangle((int)x, (int)y + 44, (int)cw, 34);
            Switch(g, _chk2, _launch, "запустить после установки");
            using (var f = UiFonts.Body(9.5f))
                Para(g, "настройки и скачанные инструменты хранятся в c:\\programdata\\obhod, ваши файлы запрета не трогаются.", f, Text3, x, y + 100, cw);
        }

        private void DrawProgress(Graphics g, float x, float y, float cw)
        {
            using (var f = UiFonts.Body(14f, true))
                y = Para(g, _error != null ? "не получилось установить" : "устанавливаю…", f, White, x, y, cw) + 20;
            var track = new RectangleF(x, y, cw, 6);
            using (var p = RoundRect(track, 3)) using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(b, p);
            float fw = (float)Math.Max(6, track.Width * Math.Min(1, _progress));
            using (var p = RoundRect(new RectangleF(track.X, track.Y, fw, track.Height), 3))
            using (var b = new SolidBrush(_error != null ? Red : White)) g.FillPath(b, p);
            y += 22;
            using (var f = UiFonts.Body(10f)) y = Para(g, _status, f, Text2, x, y, cw) + 10;
            if (_error != null)
                using (var f = UiFonts.Body(10f))
                    Para(g, _error + "\nпроверьте интернет и нажмите «повторить». подробности: " + Program.LogFile, f, Red, x, y, cw);
        }

        private void DrawDone(Graphics g, float x, float y, float cw)
        {
            using (var f = UiFonts.Body(14f, true)) y = Para(g, "готово! обход установлен", f, White, x, y, cw) + 4;
            using (var f = UiFonts.Body(10f))
                y = Para(g, "ярлык есть в меню «пуск»" + (_desktopShortcut ? " и на рабочем столе." : "."), f, Text2, x, y, cw) + 14;
            var box = new RectangleF(x, y, cw, 214);
            Glass(g, box, 18, 12);
            using (var f = UiFonts.Body(10.5f, true)) Para(g, "если в discord не грузятся картинки", f, White, x + 18, y + 12, cw - 36);
            using (var f = UiFonts.Body(9.5f)) Para(g, "обычно программа исправляет это сама. если нет:", f, Text3, x + 18, y + 34, cw - 36);
            float yy = y + 58;
            yy = Numbered(g, 1, "откройте блокнот от имени администратора", x + 18, yy, cw - 36);
            yy = Numbered(g, 2, "файл → открыть: c:\\windows\\system32\\drivers\\etc\\hosts (тип «все файлы»)", x + 18, yy, cw - 36);
            yy = Numbered(g, 3, "добавьте в конец строки, сохраните и перезапустите discord:", x + 18, yy, cw - 36);
            var mono = new RectangleF(x + 48, yy, cw - 84, 58);
            using (var p = RoundRect(mono, 10)) using (var b = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillPath(b, p);
            using (var f = new Font("Consolas", 9.5f))
            using (var b = new SolidBrush(White))
                g.DrawString("162.159.133.233 media.discordapp.net\n162.159.133.233 images-ext-1.discordapp.net\n162.159.133.233 images-ext-2.discordapp.net", f, b, mono.X + 12, mono.Y + 6);
            // ссылка на полную инструкцию
            using (var f = UiFonts.Body(10f))
            using (var b = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                const string t = "полная инструкция";
                var sz = g.MeasureString(t, f);
                _link = new Rectangle(Pad, ClientSize.Height - Pad - 34, (int)sz.Width + 4, (int)sz.Height + 2);
                g.DrawString(t, f, b, _link.X, _link.Y);
                using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), 1f))
                    g.DrawLine(pen, _link.X + 2, _link.Bottom - 1, _link.X + sz.Width - 2, _link.Bottom - 1);
            }
        }

        private void DrawUninstall(Graphics g, float x, float y, float cw)
        {
            if (_step == 4)
            {
                using (var f = UiFonts.Body(14f, true)) Para(g, "обход удалён", f, White, x, y, cw);
                return;
            }
            using (var f = UiFonts.Body(14f, true)) y = Para(g, "удалить обход?", f, White, x, y, cw) + 6;
            using (var f = UiFonts.Body(10f)) y = Para(g, "программа, запрет, прокси и vpn будут остановлены, ярлыки и автозапуск удалены.", f, Text2, x, y, cw) + 18;
            if (!_busy)
            {
                _chk1 = new Rectangle((int)x, (int)y, (int)cw, 34);
                Switch(g, _chk1, _removeData, "удалить также настройки и скачанные инструменты");
            }
            else
            {
                var track = new RectangleF(x, y + 10, cw, 6);
                using (var p = RoundRect(track, 3)) using (var b = new SolidBrush(Color.FromArgb(26, 255, 255, 255))) g.FillPath(b, p);
                using (var p = RoundRect(new RectangleF(track.X, track.Y, (float)Math.Max(6, track.Width * _progress), 6), 3)) using (var b = new SolidBrush(White)) g.FillPath(b, p);
                using (var f = UiFonts.Body(10f)) Para(g, _status, f, Text2, x, y + 28, cw);
            }
        }

        // ---------------- инструкция ----------------

        public const string InfoText =
            "антивирус может ругаться на программу — это ложное срабатывание. почему так бывает:\n" +
            "• winws.exe (запрет) перехватывает сетевой трафик через драйвер windivert — так же делают и вирусы,\n" +
            "  поэтому защитник часто помечает его как hacktool, riskware или «троян»;\n" +
            "• программа правит системный файл hosts, чтобы открыть картинки discord;\n" +
            "• у файлов нет цифровой подписи, а для работы нужны права администратора;\n" +
            "• установщик и программа докачивают из интернета .net 10, прокси для telegram и ядра vpn;\n" +
            "• файл пришёл через discord, telegram или браузер — windows помечает его как скачанный из интернета.\n\n" +
            "что делать, если файл удалили или заблокировали:\n" +
            "«безопасность windows» → «защита от вирусов и угроз» → «журнал защиты» → выберите запись → «разрешить на устройстве».\n" +
            "надёжнее добавить в исключения папки c:\\program files\\obhod и c:\\programdata\\obhod\n" +
            "(«защита от вирусов и угроз» → «управление настройками» → «исключения»).\n\n" +
            "если в discord не грузятся картинки:\n" +
            "обычно программа чинит это сама. если нет — откройте блокнот от имени администратора,\n" +
            "откройте файл c:\\windows\\system32\\drivers\\etc\\hosts (тип файла «все файлы») и добавьте в конец строки:\n\n" +
            "162.159.133.233 media.discordapp.net\n" +
            "162.159.133.233 images-ext-1.discordapp.net\n" +
            "162.159.133.233 images-ext-2.discordapp.net\n\n" +
            "сохраните файл и перезапустите discord (или нажмите в нём ctrl+r).\n" +
            "у другого провайдера рабочий адрес может отличаться: подойдёт любой адрес cdn.discordapp.com,\n" +
            "который у вас открывается (проверить: ping cdn.discordapp.com).\n";

        private static void OpenInstructions()
        {
            try
            {
                var file = Path.Combine(Path.GetTempPath(), "обход — важно.txt");
                File.WriteAllText(file, InfoText.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(true));
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + file + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Program.Log("instructions: " + ex.Message); }
        }

        // ---------------- мышь ----------------

        private bool IsControl(Point p) =>
            _btn.Contains(p) || _back.Contains(p) || _close.Contains(p) || _link.Contains(p) || _chk1.Contains(p) || _chk2.Contains(p);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (e.Button == MouseButtons.Left && _drag != Point.Empty)
            {
                Location = new Point(Location.X + e.X - _drag.X, Location.Y + e.Y - _drag.Y);
                return;
            }
            Cursor = IsControl(e.Location) ? Cursors.Hand : Cursors.Default;
            bool hb = _btn.Contains(e.Location), hk = _back.Contains(e.Location), hc = _close.Contains(e.Location);
            if (hb != _hoverBtn || hk != _hoverBack || hc != _hoverClose) { _hoverBtn = hb; _hoverBack = hk; _hoverClose = hc; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!IsControl(e.Location)) _drag = e.Location;
        }

        protected override async void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool wasDrag = _drag != Point.Empty;
            _drag = Point.Empty;
            if (wasDrag) return;
            var pt = e.Location;
            if (_close.Contains(pt) && !_busy) { Close(); return; }
            if (_busy) return;
            if (_link.Contains(pt)) { OpenInstructions(); return; }
            if (_back.Contains(pt)) { _step = _step == 3 ? 2 : Math.Max(0, _step - 1); _error = null; Invalidate(); return; }
            if (_chk1.Contains(pt)) { if (_uninstall) _removeData = !_removeData; else _desktopShortcut = !_desktopShortcut; Invalidate(); return; }
            if (_chk2.Contains(pt)) { _launch = !_launch; Invalidate(); return; }
            if (!_btn.Contains(pt)) return;

            if (_uninstall)
            {
                if (_step == 4) { Close(); return; }
                await RunAsync(() => Installer.Uninstall(_removeData, Report));
                if (_error == null) { _step = 4; Invalidate(); Installer.ScheduleSelfDelete(); }
                return;
            }
            switch (_step)
            {
                case 0: case 1: _step++; Invalidate(); return;
                case 2: case 3:
                    _step = 3; _error = null; _progress = 0;
                    await RunAsync(() => Installer.Install(_desktopShortcut, Report));
                    if (_error == null)
                    {
                        _step = 4;
                        if (_launch)
                        {
                            try { Process.Start(new ProcessStartInfo(Path.Combine(Program.InstallDir, "obhod.exe")) { UseShellExecute = true }); }
                            catch (Exception ex) { Program.Log("launch: " + ex.Message); }
                        }
                    }
                    Invalidate();
                    return;
                default: Close(); return;
            }
        }

        private async Task RunAsync(Action work)
        {
            _busy = true; _error = null; _progress = 0; Invalidate();
            try { await Task.Run(work); _progress = 1; }
            catch (Exception ex)
            {
                Program.Log((_uninstall ? "uninstall: " : "install: ") + ex);
                _error = "ошибка: " + ex.Message.ToLowerInvariant();
            }
            finally { _busy = false; Invalidate(); }
        }

        private void Report(string status, double progress)
        {
            BeginInvoke(new Action(() => { _status = status.ToLowerInvariant(); _progress = progress; Invalidate(); }));
        }
    }

    internal static class Installer
    {
        public static void Install(bool desktopShortcut, Action<string, double> report)
        {
            var installed = Path.Combine(Program.InstallDir, "obhod.exe");
            if (File.Exists(installed) && Version.TryParse(FileVersionInfo.GetVersionInfo(installed).FileVersion, out var installedVersion) &&
                installedVersion > new Version(Program.Version + ".0"))
                throw new InvalidOperationException("уже установлена более новая версия. запустите свежий установщик с github.");

            report("останавливаю запущенную программу…", 0.02);
            StopInstalledApp(installed);
            MigrateLegacy(report);

            // 1. .NET 10 Desktop Runtime: качаем с сайта Microsoft, только если его нет
            if (!HasDesktopRuntime())
            {
                var rt = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-10-win-x64.exe");
                Download(RuntimeUrl, rt, (d, t) => report($"скачиваю .net 10 с сайта microsoft: {d / 1048576} из {Math.Max(1, t / 1048576)} мб", 0.04 + 0.46 * d / Math.Max(1, t)));
                report("устанавливаю .net 10 (это займёт около минуты)…", 0.52);
                using (var p = Process.Start(new ProcessStartInfo(rt, "/install /quiet /norestart") { UseShellExecute = false, CreateNoWindow = true }))
                {
                    if (!p.WaitForExit(10 * 60 * 1000)) throw new TimeoutException("установка .net 10 зависла.");
                    if (p.ExitCode != 0 && p.ExitCode != 3010 && p.ExitCode != 1641)
                        throw new InvalidOperationException("установщик .net 10 вернул код " + p.ExitCode);
                }
                TryDelete(rt);
            }

            // 2. Программа и запрет из установщика
            var dir = Program.InstallDir;
            Directory.CreateDirectory(dir);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(res, ZipArchiveMode.Read))
            {
                var entries = zip.Entries.Where(en => !string.IsNullOrEmpty(en.Name)).ToList();
                long total = entries.Sum(en => en.Length), done = 0;
                bool haveZapret = File.Exists(Path.Combine(Program.DataDir, "zapret", "bin", "winws.exe"));
                foreach (var en in entries)
                {
                    string target = null;
                    var name = en.FullName.Replace('/', '\\');
                    if (name.StartsWith("app\\", StringComparison.OrdinalIgnoreCase))
                        target = Path.Combine(dir, name.Substring(4));
                    else if (name.StartsWith("data\\zapret\\", StringComparison.OrdinalIgnoreCase) && !haveZapret)
                        target = Path.Combine(Program.DataDir, name.Substring(5));

                    // уже установленные инструменты и списки пользователя не затираем
                    if (target != null)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        report("копирую " + Path.GetFileName(target), 0.56 + 0.14 * done / Math.Max(1, total));
                        en.ExtractToFile(target, true);
                    }
                    done += en.Length;
                }
            }

            // 3. Прокси для Telegram — последняя версия с GitHub. Если не скачается, программа докачает его сама при запуске.
            var tgExe = Path.Combine(Program.DataDir, "tgws", "TgWsProxy_windows.exe");
            if (!File.Exists(tgExe))
            {
                try
                {
                    report("узнаю последнюю версию прокси для telegram…", 0.71);
                    var json = GetString("https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest");
                    var url = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*TgWsProxy_windows\\.exe)\"").Groups[1].Value;
                    var tag = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"").Groups[1].Value;
                    if (url.Length == 0) throw new InvalidDataException("в релизе нет tgwsproxy_windows.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(tgExe));
                    var tmp = tgExe + ".download";
                    Download(url, tmp, (d, t) => report($"скачиваю прокси для telegram: {d / 1048576} из {Math.Max(1, t / 1048576)} мб", 0.72 + 0.2 * d / Math.Max(1, t)));
                    if (File.Exists(tgExe)) File.Delete(tgExe);
                    File.Move(tmp, tgExe);
                    if (tag.Length > 0) File.WriteAllText(Path.Combine(Path.GetDirectoryName(tgExe), "version.txt"), tag);
                }
                catch (Exception ex)
                {
                    Program.Log("tg download: " + ex.Message);
                    report("прокси для telegram скачается при первом запуске программы.", 0.92);
                    System.Threading.Thread.Sleep(1200);
                }
            }

            try { File.WriteAllText(Path.Combine(dir, "важно.txt"), SetupForm.InfoText.Replace("\n", "\r\n"), new System.Text.UTF8Encoding(true)); } catch { }
            report("создаю ярлыки…", 0.94);
            var exe = Path.Combine(dir, "obhod.exe");
            var uninst = Path.Combine(dir, "Uninstall.exe");
            File.Copy(Application.ExecutablePath, uninst, true);

            // старые ярлыки с прежним названием
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Zapret" + " Hub.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Zapret" + " Hub.lnk"));
            Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "обход.lnk"), exe, dir);
            var desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "обход.lnk");
            if (desktopShortcut) Shortcut(desk, exe, dir);
            else TryDelete(desk);

            try { Registry.LocalMachine.DeleteSubKeyTree(Program.LegacyUninstallKey, false); } catch { }
            using (var k = Registry.LocalMachine.CreateSubKey(Program.UninstallKey))
            {
                k.SetValue("DisplayName", "обход");
                k.SetValue("DisplayVersion", Program.Version);
                k.SetValue("Publisher", "DAVL");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
            }
            report("готово", 1);
        }

        public static void Uninstall(bool removeData, Action<string, double> report)
        {
            report("останавливаю программу, запрет и прокси…", 0.1);
            Kill("obhod"); Kill(Program.LegacyName);
            Kill("winws");
            Kill("TgWsProxy_windows");
            RunHidden("schtasks.exe", "/delete /f /tn \"obhod\"");
            RunHidden("schtasks.exe", "/delete /f /tn \"" + Program.LegacyName + "\"");

            report("удаляю ярлыки…", 0.4);
            foreach (var name in new[] { "Zapret" + " Hub.lnk", "обход.lnk" })
            {
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name));
            }
            try { Registry.LocalMachine.DeleteSubKeyTree(Program.UninstallKey, false); } catch { }

            // убираем наш блок обхода из hosts
            try
            {
                var hosts = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
                if (File.Exists(hosts))
                {
                    var keep = new System.Collections.Generic.List<string>();
                    bool inBlock = false;
                    foreach (var line in File.ReadAllLines(hosts))
                    {
                        if (line.StartsWith("# >>> obhod") || line.StartsWith("# >>> " + Program.LegacyName)) { inBlock = true; continue; }
                        if (line.StartsWith("# <<< obhod") || line.StartsWith("# <<< " + Program.LegacyName)) { inBlock = false; continue; }
                        if (!inBlock) keep.Add(line);
                    }
                    File.WriteAllLines(hosts, keep);
                    RunHidden("ipconfig", "/flushdns");
                }
            }
            catch (Exception ex) { Program.Log("hosts cleanup: " + ex.Message); }

            report("удаляю файлы…", 0.7);
            TryDelete(Path.Combine(Program.InstallDir, "obhod.exe"));
            if (removeData)
            {
                RunHidden("sc.exe", "stop WinDivert");
                RunHidden("sc.exe", "delete WinDivert");
                System.Threading.Thread.Sleep(800);
                try { if (Directory.Exists(Program.DataDir)) Directory.Delete(Program.DataDir, true); }
                catch (Exception ex) { Program.Log("data delete: " + ex.Message); }
            }
            report("готово", 1);
        }

        private const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

        public static string SelfTest()
        {
            var json = GetString("https://api.github.com/repos/Flowseal/tg-ws-proxy/releases/latest");
            var url = System.Text.RegularExpressions.Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]*TgWsProxy_windows\\.exe)\"").Groups[1].Value;
            var tmp = Path.Combine(Path.GetTempPath(), "zh-selftest.bin");
            long got = 0;
            Download(url, tmp, (d, t) => got = d);
            TryDelete(tmp);
            var r = Request(RuntimeUrl); r.Method = "HEAD";
            long rtLen;
            using (var resp = r.GetResponse()) rtLen = resp.ContentLength;
            return $"runtime installed={HasDesktopRuntime()}, tg url={url}, tg bytes={got}, runtime bytes={rtLen}";
        }

        public static bool HasDesktopRuntime()
        {
            var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            try { return Directory.Exists(shared) && Directory.GetDirectories(shared, "10.*").Length > 0; }
            catch { return false; }
        }

        private static void PrepareTls()
        {
            // TLS 1.2 и 1.3 (3072 | 12288) — иначе GitHub и Microsoft не отвечают старому .NET Framework
            try { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)(3072 | 12288); }
            catch { System.Net.ServicePointManager.SecurityProtocol = (System.Net.SecurityProtocolType)3072; }
        }

        internal static System.Net.HttpWebRequest Request(string url)
        {
            PrepareTls();
            var r = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
            r.UserAgent = "obhod-setup/" + Program.Version;
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            r.AllowAutoRedirect = true;
            return r;
        }

        private static string GetString(string url)
        {
            var r = Request(url);
            r.Accept = "application/vnd.github+json";
            using (var resp = r.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        /// <summary>Скачивание с прогрессом и тремя попытками.</summary>
        private static void Download(string url, string dest, Action<long, long> progress)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    using (var resp = Request(url).GetResponse())
                    using (var src = resp.GetResponseStream())
                    using (var dst = File.Create(dest))
                    {
                        long total = resp.ContentLength, done = 0, lastReport = 0;
                        var buf = new byte[81920];
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0)
                        {
                            dst.Write(buf, 0, n);
                            done += n;
                            if (done - lastReport > 512 * 1024) { progress(done, total); lastReport = done; }
                        }
                        progress(done, total);
                        if (done < 1024) throw new IOException("файл скачался пустым");
                    }
                    return;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    Program.Log($"download {url} attempt {attempt}: {ex.Message}");
                    System.Threading.Thread.Sleep(1500 * attempt);
                }
            }
        }

        /// <summary>Переход со старого названия: переносим данные и убираем старую папку программы.</summary>
        private static void MigrateLegacy(Action<string, double> report)
        {
            var oldData = Program.LegacyDataDir;
            if (Directory.Exists(oldData))
            {
                report("переношу настройки из старой версии…", 0.03);
                Kill("winws"); Kill("TgWsProxy_windows"); Kill("xray"); Kill("sing-box");
                RunHidden("sc.exe", "stop WinDivert");
                System.Threading.Thread.Sleep(600);
                if (!Directory.Exists(Program.DataDir))
                {
                    try { Directory.Move(oldData, Program.DataDir); }
                    catch (Exception ex) { Program.Log("move data: " + ex.Message); CopyDir(oldData, Program.DataDir); }
                }
                try { if (Directory.Exists(oldData)) Directory.Delete(oldData, true); } catch (Exception ex) { Program.Log("delete old data: " + ex.Message); }
            }
            // старая задача автозапуска будет пересоздана программой под новым именем
            var oldDir = Program.LegacyInstallDir;
            if (Directory.Exists(oldDir))
            {
                try { Directory.Delete(oldDir, true); } catch (Exception ex) { Program.Log("delete old dir: " + ex.Message); }
            }
        }

        private static void CopyDir(string src, string dst)
        {
            foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dst, d.Substring(src.Length).TrimStart('\\')));
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                var t = Path.Combine(dst, f.Substring(src.Length).TrimStart('\\'));
                try { if (!File.Exists(t)) File.Copy(f, t); } catch { }
            }
        }

        public static void ScheduleSelfDelete()
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Program.InstallDir + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        private static void Kill(string name)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(4000); } catch { }
            }
        }

        private static void StopInstalledApp(string executable)
        {
            foreach (var process in Process.GetProcessesByName(Program.AppName))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited || !executable.Equals(process.MainModule.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (InvalidOperationException) when (process.HasExited) { continue; }

                    if (System.Threading.EventWaitHandle.TryOpenExisting("obhod.ExitForUpdate", out var exitEvent))
                    {
                        using (exitEvent) exitEvent.Set();
                        if (process.WaitForExit(12000)) continue;
                    }
                    throw new InvalidOperationException("выйдите из программы через меню значка в трее и повторите установку.");
                }
            }
        }

        private static void RunHidden(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false }))
                    p.WaitForExit(8000);
            }
            catch { }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        private static long DirSize(string dir)
        {
            try { return new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { return 0; }
        }

        private static void Shortcut(string lnk, string target, string workDir)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                // WScript.Shell не сохраняет ярлык с кириллицей в имени: сохраняем под временным латинским именем и переименовываем
                var tmp = Path.Combine(Path.GetDirectoryName(lnk), "obhod-tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".lnk");
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { tmp });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "запрет, telegram-прокси и vpn в одной программе" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
                if (File.Exists(lnk)) File.Delete(lnk);
                File.Move(tmp, lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }
}
