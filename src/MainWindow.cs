using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace AppDataMover
{
    /// <summary>Minimal bilingual strings. 中文/English toggle.</summary>
    public static class Loc
    {
        public static bool Zh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        public static string T(string zh, string en) => Zh ? zh : en;
    }

    public class Row : INotifyPropertyChanged
    {
        public FolderEntry E;
        public Row(FolderEntry e) { E = e; }
        public string Name => E.Name;
        public string Scope => E.Scope;
        public string Size => E.SizeText;
        public string App => E.AppName ?? Loc.T("（未识别）", "(unknown)");
        public string Kind
        {
            get
            {
                switch (E.Kind)
                {
                    case FolderKind.Cache: return Loc.T("缓存", "Cache");
                    case FolderKind.AppData: return Loc.T("应用数据", "App data");
                    case FolderKind.SystemProtected: return Loc.T("系统保护", "Protected");
                    case FolderKind.Special: return Loc.T("特殊处理", "Special");
                    default: return "?";
                }
            }
        }
        public string State
        {
            get
            {
                switch (E.State)
                {
                    case MoveState.AlreadyMoved: return Loc.T("已迁移 → ", "Moved → ") + E.JunctionTarget;
                    case MoveState.Moving: return Loc.T("迁移中…", "Moving…");
                    case MoveState.Moved: return Loc.T("迁移完成", "Done");
                    case MoveState.Failed: return Loc.T("失败", "Failed");
                    default: return "";
                }
            }
        }
        public string Target => E.SuggestedTarget ?? "";
        public Brush KindColor
        {
            get
            {
                switch (E.Kind)
                {
                    case FolderKind.Cache: return Brushes.SeaGreen;
                    case FolderKind.SystemProtected: return Brushes.IndianRed;
                    case FolderKind.Special: return Brushes.DarkOrange;
                    default: return Brushes.SteelBlue;
                }
            }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, null); // refresh all columns
        }
    }

    public partial class MainWindow : Window
    {
        readonly List<Row> _rows = new List<Row>();
        readonly DataGrid _grid = new DataGrid();
        readonly TextBox _log = new TextBox();
        readonly TextBlock _status = new TextBlock();
        readonly ProgressBar _bar = new ProgressBar();
        readonly Button _btnScan = new Button();
        readonly Button _btnMove = new Button();
        readonly Button _btnRestore = new Button();
        readonly Button _btnLocks = new Button();
        readonly Button _btnCloseLocks = new Button();
        readonly Button _btnBrowse = new Button();
        readonly Button _btnLang = new Button();
        readonly TextBox _target = new TextBox();
        CancellationTokenSource _cts;
        string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public MainWindow()
        {
            Title = "AppData Mover 迁移助手";
            Width = 1150; Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            BuildUi();
            ApplyLang();
        }

        void BuildUi()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // action bar
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(140) }); // log
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // status

            // toolbar
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
            foreach (var b in new[] { _btnScan, _btnLocks, _btnCloseLocks, _btnMove, _btnRestore, _btnLang })
            {
                b.Margin = new Thickness(4, 0, 4, 0); b.Padding = new Thickness(12, 5, 12, 5);
                bar.Children.Add(b);
            }
            _btnScan.Click += async (s, e) => await ScanAsync();
            _btnLocks.Click += (s, e) => CheckLocks();
            _btnCloseLocks.Click += (s, e) => CloseLockers();
            _btnMove.Click += async (s, e) => await MoveSelectedAsync();
            _btnRestore.Click += async (s, e) => await RestoreSelectedAsync();
            _btnLang.Click += (s, e) => { Loc.Zh = !Loc.Zh; ApplyLang(); foreach (var r in _rows) r.Refresh(); };
            Grid.SetRow(bar, 0);
            root.Children.Add(bar);

            // grid
            _grid.Margin = new Thickness(8, 0, 8, 4);
            _grid.AutoGenerateColumns = false;
            _grid.IsReadOnly = true;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.SelectionChanged += (s, e) => OnSelect();
            AddCol(Loc.T("文件夹", "Folder"), "Name", 140);
            AddCol("Scope", "Scope", 70);
            AddCol(Loc.T("大小", "Size"), "Size", 90);
            AddCol(Loc.T("对应软件", "App"), "App", 220);
            AddCol(Loc.T("类型", "Kind"), "Kind", 90, "KindColor");
            AddCol(Loc.T("状态", "State"), "State", 200);
            AddCol(Loc.T("默认目标", "Default target"), "Target", 320);
            _grid.ItemsSource = _rows;
            Grid.SetRow(_grid, 1);
            root.Children.Add(_grid);

            // action bar
            var act = new DockPanel { Margin = new Thickness(8, 0, 8, 4) };
            _btnBrowse.Padding = new Thickness(10, 4, 10, 4);
            _btnBrowse.Click += (s, e) => BrowseTarget();
            DockPanel.SetDock(_btnBrowse, Dock.Right);
            act.Children.Add(_btnBrowse);
            _target.VerticalContentAlignment = VerticalAlignment.Center;
            act.Children.Add(_target);
            Grid.SetRow(act, 2);
            root.Children.Add(act);

            // log
            _log.IsReadOnly = true;
            _log.FontFamily = new FontFamily("Consolas");
            _log.FontSize = 12;
            _log.TextWrapping = TextWrapping.Wrap;
            _log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _log.Margin = new Thickness(8, 0, 8, 4);
            Grid.SetRow(_log, 3);
            root.Children.Add(_log);

            // status
            var st = new DockPanel { Margin = new Thickness(8, 0, 8, 6) };
            _bar.Height = 10; _bar.Minimum = 0; _bar.Maximum = 100;
            DockPanel.SetDock(_bar, Dock.Right); _bar.Width = 260;
            st.Children.Add(_bar);
            st.Children.Add(_status);
            Grid.SetRow(st, 4);
            root.Children.Add(st);

            Content = root;
        }

        void AddCol(string header, string binding, double width, string colorBinding = null)
        {
            var col = new DataGridTextColumn { Header = header, Width = width, Binding = new Binding(binding) };
            if (colorBinding != null)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(colorBinding)));
                style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
                col.ElementStyle = style;
            }
            _grid.Columns.Add(col);
        }

        void ApplyLang()
        {
            _btnScan.Content = Loc.T("扫描 AppData", "Scan AppData");
            _btnLocks.Content = Loc.T("检测占用", "Check locks");
            _btnCloseLocks.Content = Loc.T("关闭占用进程", "Close lockers");
            _btnMove.Content = Loc.T("迁移选中项", "Move selected");
            _btnRestore.Content = Loc.T("搬回原位", "Move back");
            _btnLang.Content = Loc.Zh ? "EN" : "中文";
            _btnBrowse.Content = Loc.T("自定义目标…", "Browse target…");
            _grid.Columns[0].Header = Loc.T("文件夹", "Folder");
            _grid.Columns[2].Header = Loc.T("大小", "Size");
            _grid.Columns[3].Header = Loc.T("对应软件", "App");
            _grid.Columns[4].Header = Loc.T("类型", "Kind");
            _grid.Columns[5].Header = Loc.T("状态", "State");
            _grid.Columns[6].Header = Loc.T("默认目标", "Default target");
        }

        Row Selected => _grid.SelectedItem as Row;

        void Log(string s)
        {
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s + Environment.NewLine);
            _log.ScrollToEnd();
        }

        async Task ScanAsync()
        {
            _btnScan.IsEnabled = false;
            _rows.Clear();
            _grid.Items.Refresh();
            Status(Loc.T("正在枚举目录…", "Enumerating folders…"));
            var apps = await Task.Run(() => Attributor.LoadInstalledApps());
            var entries = await Task.Run(() => Scanner.Scan(_profile));
            foreach (var e in entries.OrderByDescending(x => x.Scope).ThenBy(x => x.Name))
            {
                Rules.Classify(e);
                Attributor.Attribute(e, apps);
                e.SuggestedTarget = TargetResolver.Suggest(e);
                _rows.Add(new Row(e));
            }
            _grid.Items.Refresh();
            Status(Loc.T("正在计算大小（后台，多线程）…", "Measuring sizes in background…"));
            var sync = new SynchronizationContextProgress<FolderEntry>(e =>
            {
                var row = _rows.FirstOrDefault(r => r.E == e);
                row?.Refresh();
                Status(Loc.T("已测量 ", "Measured ") + e.Name + "  (" + e.SizeText + ")");
            });
            await Scanner.MeasureAsync(entries, sync.Report);
            // re-sort by size desc within the view
            var sorted = _rows.OrderByDescending(r => r.E.SizeBytes).ToList();
            _rows.Clear(); foreach (var r in sorted) _rows.Add(r);
            _grid.Items.Refresh();
            Status(Loc.T("扫描完成，共 ", "Scan done: ") + _rows.Count + Loc.T(" 个目录。绿色=缓存可放心搬，红色=系统保护勿动。", " folders. Green=cache, safe; red=protected, do not move."));
            _btnScan.IsEnabled = true;
        }

        void OnSelect()
        {
            var r = Selected;
            if (r == null) return;
            _target.Text = r.E.State == MoveState.AlreadyMoved ? (r.E.JunctionTarget ?? "") : r.E.SuggestedTarget;
            Log(Loc.T("选中 ", "Selected ") + r.E.FullPath + "  —  " + (r.E.KindReason ?? ""));
        }

        void CheckLocks()
        {
            var r = Selected; if (r == null) { Log(Loc.T("请先选中一行", "Select a row first")); return; }
            Status(Loc.T("检测占用中…", "Checking locks…"));
            Task.Run(() =>
            {
                var lockers = LockChecker.WhoLocks(r.E.FullPath);
                Dispatcher.Invoke(() =>
                {
                    if (lockers.Count == 0) Log(Loc.T("无进程占用，可以直接迁移。", "No locks. Safe to move."));
                    else
                    {
                        Log(Loc.T("以下进程正在占用：", "Locking processes:"));
                        foreach (var l in lockers) Log("   " + l);
                    }
                    Status("");
                });
            });
        }

        void CloseLockers()
        {
            var r = Selected; if (r == null) return;
            var lockers = LockChecker.WhoLocks(r.E.FullPath);
            if (lockers.Count == 0) { Log(Loc.T("没有占用进程。", "No lockers.")); return; }
            var msg = Loc.T("将尝试【温和关闭】以下进程：\n", "Will gracefully close:\n")
                + string.Join("\n", lockers.Select(l => l.ToString()))
                + Loc.T("\n\n未保存的工作可能提示保存。确定？", "\n\nUnsaved work may prompt to save. Continue?");
            if (MessageBox.Show(msg, "AppData Mover", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var ok = LockChecker.ShutdownLockers(r.E.FullPath, false);
            Log(ok ? Loc.T("已发送关闭请求。", "Close request sent.") : Loc.T("关闭请求失败，可手动结束进程后重试。", "Close request failed; end the processes manually."));
        }

        void BrowseTarget()
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var r = Selected;
                _target.Text = System.IO.Path.Combine(dlg.SelectedPath, r != null ? (r.E.Scope + "-" + r.E.Name) : "moved");
                if (r != null) { r.E.SuggestedTarget = _target.Text; r.Refresh(); }
            }
        }

        async Task MoveSelectedAsync()
        {
            var r = Selected; if (r == null) { Log(Loc.T("请先选中一行", "Select a row first")); return; }
            if (r.E.Kind == FolderKind.SystemProtected) { Log(Loc.T("系统保护目录，禁止迁移。", "Protected folder; move blocked.")); return; }
            if (r.E.Kind == FolderKind.Special) { Log(Loc.T("该目录含虚拟磁盘，请用官方导出/导入流程（见 README）。", "Contains vhdx; use export/import flow (see README).")); return; }
            if (r.E.State == MoveState.AlreadyMoved) { Log(Loc.T("该项已是联接，如需还原请用“搬回原位”。", "Already a junction; use Move back.")); return; }
            var dst = _target.Text.Trim();
            if (string.IsNullOrEmpty(dst)) { Log(Loc.T("目标路径为空。", "Target path empty.")); return; }

            var lockers = await Task.Run(() => LockChecker.WhoLocks(r.E.FullPath));
            if (lockers.Count > 0)
            {
                Log(Loc.T("有进程占用，已取消：", "Locked by:"));
                foreach (var l in lockers) Log("   " + l);
                Log(Loc.T("请先点“关闭占用进程”或手动退出软件。", "Close lockers first."));
                return;
            }

            r.E.State = MoveState.Moving; r.Refresh();
            _cts = new CancellationTokenSource();
            _bar.IsIndeterminate = true;
            var log = new SynchronizationContextProgress<string>(Log);
            var rep = await Mover.MoveAsync(r.E.FullPath, dst, log, _cts.Token);
            _bar.IsIndeterminate = false;
            r.E.State = rep.Success ? MoveState.Moved : MoveState.Failed;
            if (rep.Success) { r.E.JunctionTarget = dst; r.E.State = MoveState.AlreadyMoved; }
            Log(rep.Message + (rep.Success ? "  (" + FolderEntry.Humanize(rep.BytesMoved) + ")" : ""));
            r.Refresh();
            Status(rep.Success ? Loc.T("迁移成功", "Move succeeded") : Loc.T("迁移失败，已回滚", "Move failed, rolled back"));
        }

        async Task RestoreSelectedAsync()
        {
            var r = Selected; if (r == null) return;
            if (r.E.State != MoveState.AlreadyMoved) { Log(Loc.T("该项不是联接，无需搬回。", "Not a junction; nothing to restore.")); return; }
            _bar.IsIndeterminate = true;
            var log = new SynchronizationContextProgress<string>(Log);
            var rep = await Mover.RestoreAsync(r.E.FullPath, log, new CancellationTokenSource().Token);
            _bar.IsIndeterminate = false;
            if (rep.Success) { r.E.State = MoveState.Normal; r.E.JunctionTarget = null; }
            Log(rep.Message);
            r.Refresh();
        }

        void Status(string s) { _status.Text = s; }
    }

    /// <summary>Marshal progress callbacks to the UI thread.</summary>
    public class SynchronizationContextProgress<T> : IProgress<T>
    {
        readonly SynchronizationContext _ctx = SynchronizationContext.Current ?? new SynchronizationContext();
        readonly Action<T> _action;
        public SynchronizationContextProgress(Action<T> action) { _action = action; }
        public void Report(T value) => _ctx.Post(_ => _action(value), null);
    }
}
