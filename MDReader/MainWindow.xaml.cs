using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MDReader.Models;
using MDReader.Services;
using static MDReader.Services.MarkdownHighlight;
using Microsoft.Web.WebView2.Core;

namespace MDReader;

public partial class MainWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();
    private string? _rootFolder;
    private string? _currentFilePath;
    private string _currentMarkdown = string.Empty;
    private string _currentBody = string.Empty;
    private List<OutlineItem> _outline = new();
    private bool _isDark;
    private double _fontSize = 15;
    private bool _webReady;
    private bool _suppressOutlineEvent;
    private bool _suppressTreeEvent;
    private bool _treeMouseDown;    // 本次选中变化是否由鼠标点击引起（键盘导航不切换展开态）
    private bool _treeExpanderDown; // 本次点击是否落在人字形指示器上（它自己会切换，此处不再重复）
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watcherCts;
    private CancellationTokenSource? _renderCts;
    private bool _sidebarCollapsed;
    private GridLength _sidebarExpandedWidth = new GridLength(300);
    // 编辑模式状态：左侧 ✏️ 进入，右侧文本区直接改 markdown，Ctrl+S 保存
    private bool _editing;
    private bool _editorDirty;
    private bool _loadingEditor;
    private bool _outlineWasVisible;
    private bool _highlighting;
    private bool _findOpen;
    private readonly DispatcherTimer _hlTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private HlPalette _hl = HlPalette.Light();
    private ScrollViewer? _editorScroller;

    public MainWindow()
    {
        InitializeComponent();

        // 标题栏显示版本号，避免新旧包混淆（如“切换报错”类的版本误报）
        try
        {
            var v = GetType().Assembly.GetName().Version;
            if (v is not null) Title = $"MD阅读器 v{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { }

        // 主题：只认 light/dark（记住用户上次选择）；其他一律按白天模式处理——新装默认白天
        _isDark = _config.Theme == "dark";
        _fontSize = _config.FontSize;

        Width = _config.WindowWidth;
        Height = _config.WindowHeight;
        if (_config.SidebarWidth is >= 200 and <= 480)
            SidebarCol.Width = new GridLength(_config.SidebarWidth);
        _sidebarExpandedWidth = SidebarCol.Width;
        _sidebarCollapsed = _config.SidebarCollapsed;
        ApplySidebarState(animate: false); // 启动直接定型（窗口未显示，无需动画）
        // 大纲默认展开（开箱即见），用户上次手动收起过则尊重其选择
        OutlinePanel.Visibility = _config.OutlineExpanded ? Visibility.Visible : Visibility.Collapsed;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        DragOver += MainWindow_DragOver;
        Drop += MainWindow_Drop;
        // 记录鼠标按下位置，用于区分“点人字形”（自切换）与“点行”（代切换）
        FileTree.PreviewMouseLeftButtonDown += (s, e) =>
        {
            _treeMouseDown = true;
            _treeExpanderDown = IsInsideExpander(e.OriginalSource as DependencyObject);
        };
        // 高亮防抖定时器（打字停 300ms 后着色，避免逐键重排卡顿）
        _hlTimer.Tick += (_, _) => { _hlTimer.Stop(); HighlightMarkdown(); };
        // 窗口句柄就绪后才能调 DWM 设置标题栏配色
        SourceInitialized += (_, _) =>
        {
            ApplyTitleBarTheme();
            // 默认最大化：在显示之前强制，避免先普通显示再弹跳，也不受 Width/Height 还原值干扰
            WindowState = WindowState.Maximized;
        };
    }

    // ================= 初始化 =================

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.LogStage("Loaded开始");
        ApplyTheme(animate: false); // 启动直接定型，避免开屏闪变
        App.LogStage("主题应用完成");
        FontSizeLabel.Text = _fontSize.ToString("0");
        await InitWebViewAsync();
        App.LogStage("WebView初始化完成");
        string initial = ResolveInitialFolder();
        App.LogStage($"初始目录={initial}");
        await LoadRootAsync(initial, firstRun: true);
        App.LogStage("LoadRoot完成");
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _config.Theme = _isDark ? "dark" : "light";
            _config.FontSize = _fontSize;
            _config.WindowWidth = ActualWidth;
            _config.WindowHeight = ActualHeight;
            if (!_sidebarCollapsed)
                _config.SidebarWidth = SidebarCol.ActualWidth; // 收起时不覆盖记住的宽度
            _config.SidebarCollapsed = _sidebarCollapsed;
            if (!string.IsNullOrEmpty(_rootFolder)) _config.LastFolder = _rootFolder;
            _config.Save();
        }
        catch { /* 退出时不打扰 */ }
        _watcher?.Dispose();
        // 预览临时文件由一日 janitor 清理，此处不删（避免多开实例互相删文件）
    }

    /// <summary>
    /// 初始目录决策（兼顾“exe 所在目录自动读取”与开发调试体验）：
    /// 命令行参数 &gt; 上次目录 &gt; exe 目录（含 md 时） &gt; 当前工作目录 &gt; exe 目录。
    /// </summary>
    private string ResolveInitialFolder()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Length > 0)
            {
                string p = args[0].Trim('"');
                if (File.Exists(p) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(p) ?? AppContext.BaseDirectory;
                if (Directory.Exists(p)) return p;
            }
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(_config.LastFolder) && Directory.Exists(_config.LastFolder))
            return _config.LastFolder;

        string exeDir = AppContext.BaseDirectory;
        try
        {
            if (Directory.EnumerateFiles(exeDir, "*.md", SearchOption.TopDirectoryOnly).Any())
                return exeDir;
            string cwd = Environment.CurrentDirectory;
            if (Directory.Exists(cwd) && Directory.EnumerateFiles(cwd, "*.md", SearchOption.AllDirectories).Any())
                return cwd;
        }
        catch { }
        return exeDir;
    }

    // ================= WebView2 =================

    private async Task InitWebViewAsync()
    {
        try
        {
            // WebView2 缓存/数据重定向到 LocalAppData，避免在 exe 所在目录生成 .WebView2 文件夹
            //（“阅读器放到哪、哪里就保持干净”，绿色软件惯例）
            string dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MD阅读器", "WebView2");
            Directory.CreateDirectory(dataDir);
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Preview.EnsureCoreWebView2Async(env);
            var settings = Preview.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreDevToolsEnabled = false;
            settings.IsZoomControlEnabled = true;
            settings.IsBuiltInErrorPageEnabled = true;
            Preview.NavigationStarting += Preview_NavigationStarting;
            Preview.NavigationCompleted += Preview_NavigationCompleted;
            _webReady = true;
        }
        catch (Exception ex)
        {
            _webReady = false;
            SetStatus($"预览组件初始化失败：{ex.Message}", ok: false);
            var dl = ThemedDialog.Show(this, 
                "Markdown 预览组件（WebView2 Runtime）初始化失败。\n\n" +
                "目录浏览功能仍可正常使用，右侧预览区暂不可用。\n\n" +
                "是否现在打开官方下载页面安装运行时？",
                "MD阅读器", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (dl == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://go.microsoft.com/fwlink/p/?LinkId=2124703")
                    { UseShellExecute = true });
                }
                catch { SetStatus("无法打开下载页面，请手动搜索安装 WebView2 Runtime", ok: false); }
            }
        }
    }

    private void Preview_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        string uri = e.Uri ?? string.Empty;
        try
        {
            // http(s) → 系统默认浏览器打开，不在阅读器内跳转（行业通用做法）
            if (uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return;
            }
            // 本地 .md 互链 → 在阅读器内打开
            if (uri.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
                uri.Split('?')[0].EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                string local = new Uri(uri).LocalPath;
                if (File.Exists(local))
                {
                    e.Cancel = true;
                    _ = OpenFileAsync(local);
                }
            }
        }
        catch { /* 拦截失败则放行默认行为 */ }
    }

    private void Preview_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess)
            SetStatus("页面加载未完成，已显示尽力而为的内容", ok: false);
    }

    // ================= 主题 =================

    /// <summary>
    /// 应用主题。WPF 边框用 400ms 颜色渐变，与内容区 CSS 过渡（450ms）同步，
    /// 避免“一闪切换”的割裂感。启动时传 <paramref name="animate"/> = false 直接定型。
    /// </summary>
    private void ApplyTheme(bool animate = true)
    {
        var r = Resources;
        if (_isDark)
        {
            Set("BgBrush", "#12161D"); Set("SidebarBrush", "#1A2029"); Set("TopBarBrush", "#1A2029");
            Set("BorderBrush", "#2C333B"); Set("TextPrimary", "#D6DDE6"); Set("TextSecondary", "#8D96A0");
            Set("AccentBrush", "#5F9CF5"); Set("AccentFgBrush", "#FFFFFF");
            Set("HoverBrush", "#232A35"); Set("SelectedBrush", "#2F7DE9", 0.25); // 深色选中用半透明
            Set("InputBrush", "#12161D"); Set("BadgeBrush", "#232A35"); Set("CardBrush", "#12161D");
            ThemeBtn.Content = "☀️ 白天";
            ThemeStatusText.Text = "🌙 黑夜模式";
        }
        else
        {
            Set("BgBrush", "#FFFFFF"); Set("SidebarBrush", "#F6F8FA"); Set("TopBarBrush", "#FFFFFF");
            Set("BorderBrush", "#E1E4E8"); Set("TextPrimary", "#1F2328"); Set("TextSecondary", "#59636E");
            Set("AccentBrush", "#0969DA"); Set("AccentFgBrush", "#FFFFFF");
            Set("HoverBrush", "#F3F4F6"); Set("SelectedBrush", "#DDF0FF");
            Set("InputBrush", "#FFFFFF"); Set("BadgeBrush", "#EFF1F3"); Set("CardBrush", "#FFFFFF");
            ThemeBtn.Content = "🌙 夜晚";
            ThemeStatusText.Text = "☀️ 白天模式";
        }

        void Set(string key, string hex, double opacity = 1.0)
        {
            // 注意：XAML 中定义的 Brush 会被 WPF 自动冻结（Freeze），直接改 Color 会抛
            // InvalidOperationException。正确做法是整体替换，DynamicResource 会自动跟进。
            // 切换时从旧色向新色做 400ms 渐变；若遇冻结等极端情况则降级为直接换色——
            // 主题正确性优先，绝不因动画抛错打扰用户。
            var to = (Color)ColorConverter.ConvertFromString(hex);
            try
            {
                var old = r[key] as SolidColorBrush;
                var from = old?.Color ?? to;
                var fromOp = old?.Opacity ?? opacity;
                var brush = new SolidColorBrush(animate ? from : to)
                {
                    Opacity = animate ? fromOp : opacity,
                };
                r[key] = brush;
                if (!animate) return;
                if (brush.IsFrozen)
                {
                    // 关键修复：被已封存模板引用的画刷，新值一存入就会被冻结。
                    // 冻结画刷显示完全正常（只是不能动画），所以再存一个目标色的新实例定型即可。
                    App.LogStage($"主题动画跳过(画刷冻结):{key}");
                    r[key] = new SolidColorBrush(to) { Opacity = opacity };
                    return;
                }
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                var dur = new Duration(TimeSpan.FromMilliseconds(400));
                if (from != to)
                    brush.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(from, to, dur) { EasingFunction = ease });
                if (Math.Abs(fromOp - opacity) > 0.001)
                    brush.BeginAnimation(SolidColorBrush.OpacityProperty,
                        new DoubleAnimation(fromOp, opacity, dur) { EasingFunction = ease });
            }
            catch (Exception ex)
            {
                App.LogStage($"主题动画降级:{key} {ex.GetType().Name}");
                try { r[key] = new SolidColorBrush(to) { Opacity = opacity }; }
                catch { /* 连保底都失败则保持原样，绝不抛给用户 */ }
            }
        }

        ApplyTitleBarTheme();
        ApplyEditorTheme();
    }

    /// <summary>编辑器跟随主题：高亮配色切换 + 选区/光标配色 + 正在编辑则立即重着色。</summary>
    private void ApplyEditorTheme()
    {
        _hl = _isDark ? HlPalette.Dark() : HlPalette.Light();
        try
        {
            if (_isDark)
            {
                // 选区 = VS Code 深色选区色 #264F78 做 55% 面纱：色块本身实色保证区域分明，
                // 透明度留 45% 让字形透出来（WPF 选区层盖在字形之上，不透明度 1.0 会活埋文字）。
                // 浅色沿用系统默认，不动已验收的观感
                MdEditor.SelectionBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x4F, 0x78));
                MdEditor.SelectionOpacity = 0.55;
                MdEditor.CaretBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0xDD, 0xE6));
            }
            else
            {
                MdEditor.ClearValue(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty);
                MdEditor.ClearValue(System.Windows.Controls.Primitives.TextBoxBase.SelectionOpacityProperty);
                MdEditor.ClearValue(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty);
            }
        }
        catch { /* 编辑器 chrome 失败不影响正文高亮 */ }
        if (_editing && !_loadingEditor && !_highlighting)
            HighlightMarkdown();
    }

    #region 原生标题栏跟随主题（DWM）

    // Win11 支持标题栏/文字/边框精确配色；Win10 仅支持深浅模式开关。
    // 这样原生标题栏的最大化/最小化/关闭按钮与贴靠布局都保留，又与界面主题统一。
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>标题栏配色与当前日夜主题对齐（与 TopBarBrush 同色，无缝衔接）。</summary>
    private void ApplyTitleBarTheme()
    {
        IntPtr hwnd;
        try { hwnd = new WindowInteropHelper(this).Handle; }
        catch { return; }
        if (hwnd == IntPtr.Zero) return; // 句柄未就绪（SourceInitialized 会再调一次）

        try
        {
            if (_isDark)
            {
                // 黑夜（柔和）：标题栏 #1A2029、文字 #D6DDE6、边框 #2C333B
                if (TrySetCaption(hwnd, 0x0029201A, 0x00E6DDD6, 0x003B332C))
                    App.LogStage("标题栏=黑夜DWM精确配色");
                else { SetDarkMode(hwnd, 1); App.LogStage("标题栏=黑夜回退深色模式"); } // Win10 回退
            }
            else
            {
                // 白天：标题栏纯白、文字 #1F2328、边框 #E1E4E8
                if (TrySetCaption(hwnd, 0x00FFFFFF, 0x0028231F, 0x00E8E4E1))
                    App.LogStage("标题栏=白天DWM精确配色");
                else { SetDarkMode(hwnd, 0); App.LogStage("标题栏=白天回退浅色模式"); } // Win10 回退
            }
        }
        catch { /* DWM 不可用时静默保持系统默认标题栏 */ }
    }

    /// <summary>Win11 精确配色（COLORREF = 0x00BBGGRR）。任一失败返回 false 走回退。</summary>
    private static bool TrySetCaption(IntPtr hwnd, int caption, int text, int border)
    {
        int size = sizeof(int);
        return DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, size) == 0
            && DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, size) == 0
            && DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, size) == 0;
    }

    private static void SetDarkMode(IntPtr hwnd, int on)
        => DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

    #endregion

    private async void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDark = !_isDark;
        _config.Theme = _isDark ? "dark" : "light";
        _config.Save();
        ApplyTheme(); // WPF 边框 400ms 渐变
        await SwitchContentThemeAsync(); // 内容区 CSS 450ms 缓动，两侧同步
    }

    /// <summary>
    /// 内容区主题切换：无图表时只换页面 class 走 CSS 过渡，不重载页面（无闪烁、无滚动丢失）；
    /// 含 Mermaid 图表时整体重绘（图表库主题需随之重建，滚动位置影响极小可接受）。
    /// 脚本不可用时兜底整体重绘。
    /// </summary>
    private async Task SwitchContentThemeAsync()
    {
        if (!_webReady || Preview.CoreWebView2 is null) return;
        if (_currentBody.Contains("language-mermaid", StringComparison.Ordinal))
        {
            await RenderCurrentAsync();
            return;
        }
        try
        {
            string t = _isDark ? "dark" : "light";
            await Preview.CoreWebView2.ExecuteScriptAsync(
                $"window.setTheme && window.setTheme('{t}')");
        }
        catch { await RenderCurrentAsync(); }
    }

    // ================= 目录加载 =================

    private async Task LoadRootAsync(string folder, bool firstRun = false)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            if (!firstRun)
                ThemedDialog.Show(this, "所选目录不存在或无法访问。", "MD阅读器",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rootFolder = Path.GetFullPath(folder);
        _config.LastFolder = _rootFolder;
        _config.Save();

        RootPathText.Text = _rootFolder;
        RootPathText.ToolTip = _rootFolder;
        SetStatus("正在扫描 Markdown 文件…");
        SetupWatcher(_rootFolder);
        App.LogStage("开始扫描树");

        var (nodes, total) = await Task.Run(() => BuildTree(_rootFolder));
        App.LogStage($"扫描完成 total={total}");

        // 记录打开历史（含文档数），立即落盘以便下次快速重开
        _config.PushHistory(_rootFolder, total);
        _config.Save();

        FileTree.ItemsSource = nodes;
        FileCountBadge.Text = $"{total} 个文件";
        TreeCountText.Text = total > 0 ? $"共 {total} 篇" : string.Empty;
        TrackExpansion(nodes);
        ApplySearchFilter();
        UpdateExpandToggle();

        if (total == 0)
        {
            CurrentFileName.Text = "没有找到 Markdown 文件";
            CurrentFilePath.Text = _rootFolder;
            WordCountText.Text = string.Empty;
            CenterSep.Visibility = Visibility.Collapsed;
            OutlineList.ItemsSource = null;
            await ShowEmptyAsync("📂 该目录下没有 Markdown 文件",
                "换个目录试试，或把 .md 文档放入此目录及子目录后点击刷新。");
            SetStatus("扫描完成：未找到 .md 文件", ok: true);
            return;
        }

        // 默认选中第一篇（深度优先），对标 Typora / Obsidian 的开箱体验
        var first = FirstFile(nodes);
        if (first is not null)
        {
            ExpandTo(first);
            _suppressTreeEvent = true;
            try { SelectNode(first); }
            finally { _suppressTreeEvent = false; }
            await OpenFileAsync(first.FullPath);
        }
        SetStatus($"扫描完成：共 {total} 个 Markdown 文件", ok: true);
    }

    /// <summary>构建“仅含 md 分支”的目录树：空文件夹自动剪枝，目录优先+字母序。</summary>
    private static (ObservableCollection<FileNode> roots, int total) BuildTree(string root)
    {
        var roots = new ObservableCollection<FileNode>();
        var dirNodes = new Dictionary<string, FileNode>(StringComparer.OrdinalIgnoreCase);
        int total = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.md", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            });
        }
        catch { files = Enumerable.Empty<string>(); }

        FileNode GetDir(string dir)
        {
            if (dirNodes.TryGetValue(dir, out var exist)) return exist;
            var node = new FileNode
            {
                Name = Path.GetFileName(dir),
                FullPath = dir,
                IsDirectory = true,
            };
            if (string.IsNullOrEmpty(node.Name)) node.Name = dir; // 根盘符兜底
            dirNodes[dir] = node;
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            // parent 为空 / 越界 / 回到扫描根 → 作为顶层目录直接挂到 roots（不为扫描根本身建节点，
            // 否则根下文件与根节点平级会造成层级错乱）
            if (!string.IsNullOrEmpty(parent)
                && !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)
                && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                node.Parent = GetDir(parent);
                node.Parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
            return node;
        }

        foreach (string f in files)
        {
            string name = Path.GetFileName(f);
            if (name.StartsWith('.')) continue; // 跳过 .obsidian 等隐藏文件
            string? dir = Path.GetDirectoryName(f);
            if (string.IsNullOrEmpty(dir)) continue;
            if (!dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            total++;

            var fileNode = new FileNode { Name = name, FullPath = f, IsDirectory = false };
            if (string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                roots.Add(fileNode);
            }
            else
            {
                var d = GetDir(dir);
                fileNode.Parent = d;
                d.Children.Add(fileNode);
            }
        }

        SortNodes(roots);
        foreach (var d in dirNodes.Values)
            d.FileCount = CountFiles(d);

        return (roots, total);

        static void SortNodes(ObservableCollection<FileNode> list)
        {
            var ordered = list
                .OrderByDescending(n => n.IsDirectory)
                .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            list.Clear();
            foreach (var n in ordered)
            {
                list.Add(n);
                if (n.Children.Count > 0) SortNodes(n.Children);
            }
        }

        static int CountFiles(FileNode node)
        {
            int c = 0;
            foreach (var ch in node.Children)
                c += ch.IsDirectory ? CountFiles(ch) : 1;
            return c;
        }
    }

    private static FileNode? FirstFile(IEnumerable<FileNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (!n.IsDirectory) return n;
            var f = FirstFile(n.Children);
            if (f is not null) return f;
        }
        return null;
    }

    private static void ExpandTo(FileNode node)
    {
        var p = node.Parent;
        while (p is not null) { p.IsExpanded = true; p = p.Parent; }
    }

    private void SelectNode(FileNode node)
    {
        // TreeView 是数据绑定，选中态通过 IsSelected 双向绑定同步；
        // 这里额外做视觉展开即可，实际选中由用户点击或以下递归触发。
        node.IsSelected = true;
        ExpandTo(node);
    }

    // ================= 文件打开与渲染 =================

    private async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path)) return;
        _currentFilePath = path;
        LoadingMask.Visibility = Visibility.Visible;
        SetStatus("正在读取…", loading: true);

        var cts = new CancellationTokenSource();
        _renderCts?.Cancel();
        _renderCts = cts;

        try
        {
            string md = await Task.Run(() => MarkdownRenderer.SmartReadAllText(path), cts.Token);
            if (cts.IsCancellationRequested) return;
            _currentMarkdown = md;
            // 正文只解析一次：body（含重写后的标题 id）既用于渲染也用于提纲，保证锚点 100% 一致
            var rendered = await Task.Run(() =>
            {
                string b = MarkdownRenderer.ToHtmlBody(md);
                var ol = MarkdownRenderer.ParseOutlineFromHtml(b);
                if (ol.Count == 0) ol = MarkdownRenderer.ParseOutline(md); // 极端兜底
                return (body: b, outline: ol);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _currentBody = rendered.body;
            _outline = rendered.outline;

            _suppressOutlineEvent = true;
            try
            {
                OutlineList.ItemsSource = _outline;
                OutlineBtn.Content = _outline.Count > 0 ? $"📑 大纲({_outline.Count})" : "📑 大纲";
            }
            finally { _suppressOutlineEvent = false; }

            var (words, _) = MarkdownRenderer.CountWords(md);
            string rel = GetRelative(path);
            CurrentFileName.Text = Path.GetFileName(path);
            CurrentFilePath.Text = rel;
            CurrentFilePath.ToolTip = path;
            WordCountText.Text = $"{words:N0} 词 · {md.Length:N0} 字符 · {new FileInfo(path).Length / 1024.0:0.#} KB";
            CenterSep.Visibility = Visibility.Visible;

            await RenderCurrentAsync();
            SetStatus("就绪", ok: true);
        }
        catch (OperationCanceledException) { /* 被新请求取代 */ }
        catch (Exception ex)
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            await ShowEmptyAsync("⚠️ 文件读取失败", ex.Message);
            SetStatus("文件读取失败", ok: false);
        }
    }

    private async Task RenderCurrentAsync()
    {
        if (!_webReady) { LoadingMask.Visibility = Visibility.Collapsed; return; }
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        try
        {
            string dir = Path.GetDirectoryName(_currentFilePath) ?? _rootFolder ?? string.Empty;
            string html = MarkdownRenderer.WrapHtml(_currentBody, dir, _isDark, _fontSize);
            await NavigateHtmlAsync(html);
            // NavigationCompleted 会关闭 LoadingMask；加 3s 兜底防止事件丢失
            await Task.Delay(300);
            if (LoadingMask.Visibility == Visibility.Visible)
                LoadingMask.Visibility = Visibility.Collapsed;
        }
        catch
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            SetStatus("预览加载失败，请重新点击左侧文件", ok: false);
        }
    }

    /// <summary>
    /// 经 WebView2 加载完整 HTML。注意不用 NavigateToString：它对内容有约 2MB 上限，
    /// 内联 mermaid/katex 库的页面（3MB+）会被静默丢弃、停留在旧页面且无任何回调。
    /// 也不能复用固定临时文件名：WebView2 会按 URL 缓存 file:// 页面，导致新导航命中旧缓存、
    /// 显示过期内容——必须每次用唯一文件名（附带一日 janitor 清理）。
    /// </summary>
    private async Task NavigateHtmlAsync(string html)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MD阅读器");
        Directory.CreateDirectory(dir);
        CleanupOldPreviews(dir);
        string tmp = Path.Combine(dir, $"preview-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.html");
        await Task.Run(() => File.WriteAllText(tmp, html, new UTF8Encoding(false)));
        if (Preview.CoreWebView2 is null) return;
        Preview.CoreWebView2.Navigate(new Uri(tmp).AbsoluteUri);
    }

    private static void CleanupOldPreviews(string dir)
    {
        try
        {
            var now = DateTime.Now;
            foreach (var f in Directory.EnumerateFiles(dir, "preview-*.html"))
            {
                try
                {
                    if (now - File.GetCreationTime(f) > TimeSpan.FromDays(1)) File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task ShowEmptyAsync(string title, string hint)
    {
        _currentMarkdown = string.Empty;
        _currentBody = string.Empty;
        _currentFilePath = null;
        if (!_webReady) return;
        await NavigateHtmlAsync(MarkdownRenderer.BuildEmptyHtml(_isDark, title, hint));
        await Task.Delay(300);
        LoadingMask.Visibility = Visibility.Collapsed;
    }

    private string GetRelative(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(_rootFolder) &&
                path.StartsWith(_rootFolder, StringComparison.OrdinalIgnoreCase))
                return "▪ " + Path.GetRelativePath(_rootFolder, path);
        }
        catch { }
        return path;
    }

    // ================= 左侧交互 =================

    private async void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeEvent) return;
        if (e.NewValue is not FileNode node) return;
        node.RefreshIcon();
        if (e.OldValue is FileNode old) old.RefreshIcon();
        if (node.IsDirectory)
        {
            // 三种情况保证只切换一次（互斥）：
            // 1) 点人字形指示器 → ToggleButton 双向绑定已切换，此处跳过（否则点一次等于没点）；
            // 2) 键盘方向键导航 → 不切换（原生左右键本就支持展开/折叠，避免误触）；
            // 3) 鼠标点行 → 在此切换一次（比双击更顺手）。
            bool mouse = _treeMouseDown, onExpander = _treeExpanderDown;
            _treeMouseDown = _treeExpanderDown = false;
            if (mouse && !onExpander)
            {
                node.IsExpanded = !node.IsExpanded;
                node.RefreshIcon();
            }
            return;
        }
        // 编辑中有未保存更改时切换文件：先确认，避免丢稿
        if (_editing && !string.Equals(node.FullPath, _currentFilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (_editorDirty)
            {
                var dr = ThemedDialog.Show(this, 
                    $"当前文档有未保存的更改，是否保存？\n\n{Path.GetFileName(_currentFilePath)}",
                    "MD阅读器", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (dr == MessageBoxResult.Yes) await SaveEditAsync();
                else if (dr == MessageBoxResult.Cancel)
                {
                    // 取消：选回当前编辑的文件
                    var cur = string.IsNullOrEmpty(_currentFilePath) ? null : FindNodeByPath(_currentFilePath);
                    if (cur is not null)
                    {
                        _suppressTreeEvent = true;
                        try { cur.IsSelected = true; ExpandTo(cur); }
                        finally { _suppressTreeEvent = false; }
                    }
                    return;
                }
                // “否”→ 放弃更改，继续打开新文件
            }
            _editing = false;
            ExitEditUI();
        }
        else if (_editing) return; // 点回正在编辑的同一文件：无事发生，避免重载冲掉编辑区
        _ = OpenFileAsync(node.FullPath);
    }

    private static bool IsInsideExpander(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is ToggleButton tb && tb.Name == "Expander") return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        ApplySearchFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        FileTree.Focus();
    }

    private void ApplySearchFilter()
    {
        if (FileTree.ItemsSource is not ObservableCollection<FileNode> roots) return;
        string kw = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(kw))
        {
            SetAllVisible(roots, true);
            TreeCountText.Text = CountAllFiles(roots) is var t && t > 0 ? $"共 {t} 篇" : string.Empty;
            return;
        }
        int hits = FilterNodes(roots, kw);
        TreeCountText.Text = hits > 0 ? $"找到 {hits} 篇" : "无匹配";
        UpdateExpandToggle(); // 搜索会自动展开命中分支，按钮文案同步
    }

    private static void SetAllVisible(IEnumerable<FileNode> nodes, bool visible)
    {
        foreach (var n in nodes)
        {
            n.IsVisible = visible;
            if (n.Children.Count > 0) SetAllVisible(n.Children, visible);
        }
    }

    /// <returns>命中的文件数</returns>
    private static int FilterNodes(IEnumerable<FileNode> nodes, string keyword)
    {
        int hits = 0;
        foreach (var n in nodes)
        {
            if (n.IsDirectory)
            {
                int sub = FilterNodes(n.Children, keyword);
                bool selfHit = n.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                n.IsVisible = selfHit || sub > 0;
                if (selfHit)
                {
                    // 目录名自身命中 → 其下全部可见，避免“有目录无内容”的困惑
                    SetAllVisible(n.Children, true);
                    n.IsExpanded = true;
                    hits += CountAllFiles(n.Children);
                }
                else
                {
                    if (n.IsVisible) n.IsExpanded = true; // 有命中时自动展开
                    hits += sub;
                }
            }
            else
            {
                bool hit = n.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                n.IsVisible = hit;
                if (hit) hits++;
            }
        }
        return hits;
    }

    private static int CountAllFiles(IEnumerable<FileNode> nodes)
    {
        int c = 0;
        foreach (var n in nodes) c += n.IsDirectory ? CountAllFiles(n.Children) : 1;
        return c;
    }

    private static void SetExpanded(IEnumerable<FileNode> nodes, bool expanded)
    {
        foreach (var n in nodes)
        {
            if (n.IsDirectory)
            {
                n.IsExpanded = expanded;
                n.RefreshIcon();
                SetExpanded(n.Children, expanded);
            }
        }
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.ItemsSource is ObservableCollection<FileNode> roots) SetExpanded(roots, true);
        UpdateExpandToggle();
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.ItemsSource is ObservableCollection<FileNode> roots) SetExpanded(roots, false);
        UpdateExpandToggle();
    }

    /// <summary>
    /// 单按钮互斥切换（行业通用 toggle-all 语义）：
    /// 全部已展开 → 显示“全部收起”并收起；否则（含半展开/全收起）→ 显示“全部展开”并展开。
    /// </summary>
    private void ExpandToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FileTree.ItemsSource is not ObservableCollection<FileNode> roots) return;
        if (AreAllExpanded(roots)) SetExpanded(roots, false);
        else SetExpanded(roots, true);
        UpdateExpandToggle();
    }

    private static bool HasDirectory(IEnumerable<FileNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.IsDirectory) return true;
            if (HasDirectory(n.Children)) return true;
        }
        return false;
    }

    /// <summary>
    /// 是否所有目录都已展开。注意：无子目录的分支按空真值返回 true（否则叶子目录永远判 false，
    /// 按钮就永远翻不到“全部收起”——这正是之前翻转失灵的根因）。
    /// 无目录的整体判空由调用方 HasDirectory 负责。
    /// </summary>
    private static bool AreAllExpanded(IEnumerable<FileNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (!n.IsDirectory) continue;
            if (!n.IsExpanded || !AreAllExpanded(n.Children)) return false;
        }
        return true;
    }

    private void UpdateExpandToggle()
    {
        if (ExpandToggleBtn is null) return;
        if (FileTree.ItemsSource is not ObservableCollection<FileNode> roots || !HasDirectory(roots))
        {
            ExpandToggleBtn.IsEnabled = false;
            ExpandToggleBtn.Content = "全部展开";
            return;
        }
        ExpandToggleBtn.IsEnabled = true;
        ExpandToggleBtn.Content = AreAllExpanded(roots) ? "全部收起" : "全部展开";
    }

    /// <summary>订阅全部目录节点的展开态变化，手动点人字形后按钮文案自动跟随。</summary>
    private void TrackExpansion(IEnumerable<FileNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (!n.IsDirectory) continue;
            n.PropertyChanged -= DirNode_PropertyChanged;
            n.PropertyChanged += DirNode_PropertyChanged;
            TrackExpansion(n.Children);
        }
    }

    private void DirNode_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileNode.IsExpanded))
            Dispatcher.BeginInvoke(UpdateExpandToggle);
    }

    // ================= 目录栏收起 / 展开（对标 VS Code Ctrl+B） =================

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_sidebarCollapsed)
            _sidebarExpandedWidth = SidebarCol.Width; // 记住当前宽度，展开时恢复
        _sidebarCollapsed = !_sidebarCollapsed;
        _config.SidebarCollapsed = _sidebarCollapsed;
        _config.Save();
        ApplySidebarState();
    }

    private Storyboard? _sidebarStoryboard;

    private void ApplySidebarState(bool animate = true)
    {
        // 中间 rail 常驻可见：展开/收起人字形互斥显示（逻辑见 UpdateRailGlyph）
        UpdateRailGlyph();
        double from = SidebarCol.ActualWidth;
        double to = _sidebarCollapsed
            ? 0
            : (_sidebarExpandedWidth.Value is >= 200 and <= 480 ? _sidebarExpandedWidth.Value : 300);
        if (_sidebarCollapsed)
            SetStatus("目录栏已隐藏，点击中间把手 » 或按 Ctrl+B 恢复");
        else
            SetStatus("就绪"); // 恢复时清掉收起提示，避免状态栏信息过期

        _sidebarStoryboard?.Stop();
        _sidebarStoryboard = null;
        // 收起前先放开最小宽约束；展开动画结束后再恢复 200 下限
        SidebarCol.MinWidth = 0;
        // 先把终值写进本地值：动画结束即移除时钟（FillBehavior.Stop），靠本地值无缝衔接；
        // 若完成时钟一直驻留（默认 HoldEnd），它会压住本地值，导致之后拖拽 Splitter 改宽度“看起来没反应”。
        SidebarCol.Width = new GridLength(to);
        if (!animate || Math.Abs(from - to) < 0.5)
        {
            if (!_sidebarCollapsed) SidebarCol.MinWidth = 200;
            return;
        }
        // 220ms 缓动收放：列宽连续小步长变化，WebView2 无需重建渲染表面，不再左右闪烁
        var anim = new GridLengthAnimation
        {
            From = new GridLength(from),
            To = new GridLength(to),
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        var sb = new Storyboard { FillBehavior = FillBehavior.Stop };
        sb.Children.Add(anim);
        Storyboard.SetTarget(anim, SidebarCol);
        Storyboard.SetTargetProperty(anim, new PropertyPath(ColumnDefinition.WidthProperty));
        sb.Completed += (_, _) =>
        {
            if (!_sidebarCollapsed) SidebarCol.MinWidth = 200;
        };
        _sidebarStoryboard = sb;
        sb.Begin();
    }

    // ================= 顶栏 / 内容区操作 =================

    private async void OpenFolder_Click(object sender, RoutedEventArgs e) => await PickFolderAsync();

    private async Task PickFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要阅读的 Markdown 目录",
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) == true)
            await LoadRootAsync(dlg.FolderName);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_rootFolder)) return;
        // 记住展开态 + 当前文件，刷新后无缝恢复
        var expanded = CollectExpanded();
        string? cur = _currentFilePath;
        await LoadRootAsync(_rootFolder);
        RestoreExpanded(expanded);
        if (cur is not null && File.Exists(cur))
            await OpenFileAsync(cur);
    }

    private HashSet<string> CollectExpanded()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FileTree.ItemsSource is ObservableCollection<FileNode> roots)
            WalkExpanded(roots, set, true);
        return set;
    }

    private void RestoreExpanded(HashSet<string> set)
    {
        if (FileTree.ItemsSource is not ObservableCollection<FileNode> roots) return;
        WalkExpanded(roots, set, false);
    }

    private static void WalkExpanded(IEnumerable<FileNode> nodes, HashSet<string> set, bool collect)
    {
        foreach (var n in nodes)
        {
            if (!n.IsDirectory) continue;
            if (collect)
            {
                if (n.IsExpanded) set.Add(n.FullPath);
            }
            else if (set.Contains(n.FullPath)) n.IsExpanded = true;
            WalkExpanded(n.Children, set, collect);
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _ = ZoomAsync(+1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _ = ZoomAsync(-1);

    private async Task ZoomAsync(int delta)
    {
        _fontSize = Math.Clamp(_fontSize + delta, 11, 24);
        FontSizeLabel.Text = _fontSize.ToString("0");
        await RenderCurrentAsync();
    }

    private void OutlineToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = OutlinePanel.Visibility != Visibility.Visible;
        OutlinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _config.OutlineExpanded = show;
        _config.Save();
        if (show && _outline.Count == 0)
            SetStatus("当前文档没有可显示的大纲标题（H1–H4）");
    }

    private async void OutlineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOutlineEvent || !_webReady) return;
        if (OutlineList.SelectedItem is not OutlineItem item) return;
        if (Preview.CoreWebView2 is null) return;
        try
        {
            // 锚点里可能含引号/反斜杠（如英文标题 it's），必须做 JS 转义，否则脚本直接语法错误
            string esc = item.Anchor.Replace("\\", "\\\\").Replace("'", "\\'");
            string ret = await Preview.CoreWebView2.ExecuteScriptAsync(
                $"window.scrollToAnchor('{esc}')");
            if (ret != null && ret.Trim() == "false")
                SetStatus($"未找到标题“{item.Title}”的位置", ok: false);
        }
        catch { /* 脚本失败不打扰阅读 */ }
    }

    private void RevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;
        Process.Start("explorer.exe", $"/select,\"{_currentFilePath}\"");
    }

    // ================= 编辑模式（左侧 ✏️ 进入，Typora 式源码编辑） =================

    private void EnterEdit_Click(object sender, RoutedEventArgs e) => EnterEditMode();

    private void EnterEditMode()
    {
        if (_editing) return;
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath))
        {
            SetStatus("请先在左侧选择要编辑的 Markdown 文档", ok: false);
            return;
        }
        _editing = true;
        _loadingEditor = true;
        try
        {
            SetEditorText(_currentMarkdown);
            _editorDirty = false;
        }
        finally { _loadingEditor = false; }
        EditFileName.Text = Path.GetFileName(_currentFilePath);
        EditFileName.ToolTip = _currentFilePath;
        _outlineWasVisible = OutlinePanel.Visibility == Visibility.Visible;
        OutlinePanel.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Collapsed;
        EditorWrap.Visibility = Visibility.Visible;
        // 面包屑栏切换为编辑态（文件信息与工具只出现一次）
        CrumbPreviewBox.Visibility = Visibility.Collapsed;
        CrumbEditBox.Visibility = Visibility.Visible;
        CrumbPreviewActions.Visibility = Visibility.Collapsed;
        CrumbEditActions.Visibility = Visibility.Visible;
        HookEditorScroll();
        UpdateEditBar();
        HighlightMarkdown();
        UpdateGutter();
        MdEditor.Focus();
        MdEditor.CaretPosition = MdEditor.Document.ContentStart;
        MdEditor.ScrollToHome();
        SetStatus("编辑模式：Ctrl+S 保存，Ctrl+F 查找，Esc 退出");
    }

    private async void SaveEdit_Click(object sender, RoutedEventArgs e) => await SaveEditAsync();

    private async Task SaveEditAsync()
    {
        if (!_editing || string.IsNullOrEmpty(_currentFilePath)) return;
        if (!_editorDirty)
        {
            SetStatus("没有未保存的更改");
            return;
        }
        SetStatus("正在保存…", loading: true);
        try
        {
            App.LogStage("SAVE-ENTER dirty=" + _editorDirty);
            string text = GetEditorText();
            string path = _currentFilePath;
            // 统一存为无 BOM 的 UTF-8（VS Code / Typora 默认，与状态栏标注一致）
            await Task.Run(() => File.WriteAllText(path, text, new UTF8Encoding(false)));
            App.LogStage("SAVE-WROTE len=" + text.Length);
            var rendered = await Task.Run(() =>
            {
                string b = MarkdownRenderer.ToHtmlBody(text);
                var ol = MarkdownRenderer.ParseOutlineFromHtml(b);
                if (ol.Count == 0) ol = MarkdownRenderer.ParseOutline(text);
                return (body: b, outline: ol);
            });
            App.LogStage("SAVE-RENDERED");
            // 保存期间若已退出编辑或切换了文件，只落盘、不刷新界面
            if (!_editing || !string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase)) return;
            _currentMarkdown = text;
            _currentBody = rendered.body;
            _outline = rendered.outline;
            _suppressOutlineEvent = true;
            try
            {
                OutlineList.ItemsSource = _outline;
                OutlineBtn.Content = _outline.Count > 0 ? $"📑 大纲({_outline.Count})" : "📑 大纲";
            }
            finally { _suppressOutlineEvent = false; }
            var (words, _) = MarkdownRenderer.CountWords(text);
            WordCountText.Text = $"{words:N0} 词 · {text.Length:N0} 字符 · {new FileInfo(path).Length / 1024.0:0.#} KB";
            CenterSep.Visibility = Visibility.Visible;
            _editorDirty = false;
            UpdateEditBar();
            // 保存成功即回到阅读模式（需求）；先退 UI 再提示，避免状态错乱
            string stamp = DateTime.Now.ToString("HH:mm:ss");
            App.LogStage("SAVE-EXITING");
            ExitEditUI();
            App.LogStage("SAVE-EXITED");
            await RenderCurrentAsync();
            SetStatus($"已保存 {stamp}，已回到阅读模式");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败：{ex.Message}", ok: false);
            ThemedDialog.Show(this, $"保存失败：\n{ex.Message}", "MD阅读器",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExitEdit_Click(object sender, RoutedEventArgs e) => _ = ExitEditModeAsync();

    private async Task ExitEditModeAsync()
    {
        if (!_editing) return;
        if (_editorDirty)
        {
            var r = ThemedDialog.Show(this, 
                $"“{Path.GetFileName(_currentFilePath)}”有未保存的更改，是否保存？",
                "MD阅读器", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes)
            {
                await SaveEditAsync();
                if (_editorDirty) return; // 保存失败则留在编辑模式
            }
            else if (r == MessageBoxResult.Cancel) return;
            // “否”→ 放弃更改（_currentMarkdown 未动过，直接退出即可）
        }
        ExitEditUI();
        await RenderCurrentAsync();
        SetStatus("就绪", ok: true);
    }

    private void ExitEditUI()
    {
        _editing = false;
        _editorDirty = false;
        EditorWrap.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Visible;
        if (_outlineWasVisible) OutlinePanel.Visibility = Visibility.Visible;
        // 面包屑栏切回预览态
        CrumbPreviewBox.Visibility = Visibility.Visible;
        CrumbEditBox.Visibility = Visibility.Collapsed;
        CrumbPreviewActions.Visibility = Visibility.Visible;
        CrumbEditActions.Visibility = Visibility.Collapsed;
        UpdateEditBar();
    }

    private void MdEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_editing || _loadingEditor || _highlighting) return;
        if (!_editorDirty)
        {
            _editorDirty = true;
            UpdateEditBar();
        }
        ScheduleHighlight();
    }

    private void MdEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_editing) return;
        try
        {
            var paras = EditorParagraphs();
            var (pi, off) = ParaOffsetOf(paras, MdEditor.CaretPosition);
            EditCaretText.Text = $"行 {pi + 1}/{paras.Count}，列 {off + 1}";
        }
        catch { }
    }

    private void UpdateEditBar()
    {
        EditDirtyDot.Visibility = _editorDirty ? Visibility.Visible : Visibility.Collapsed;
        if (_editing) MdEditor_SelectionChanged(MdEditor, new RoutedEventArgs());
        else EditCaretText.Text = string.Empty;
    }

    // ================= 编辑器基础：文本存取与位置映射 =================

    private List<Paragraph> EditorParagraphs()
        => MdEditor.Document.Blocks.OfType<Paragraph>().ToList();

    /// <summary>取编辑器全文（去掉 FlowDocument 末尾自动补的换行）。</summary>
    private string GetEditorText()
    {
        string t = new TextRange(MdEditor.Document.ContentStart, MdEditor.Document.ContentEnd).Text;
        if (t.EndsWith("\r\n")) t = t[..^2];
        return t;
    }

    private void SetEditorText(string s)
    {
        new TextRange(MdEditor.Document.ContentStart, MdEditor.Document.ContentEnd).Text = s ?? string.Empty;
    }

    /// <summary>TextPointer → （段落序号，段内偏移）。段内无换行，偏移精确。</summary>
    private static (int para, int off) ParaOffsetOf(IReadOnlyList<Paragraph> paras, TextPointer pointer)
    {
        for (int i = 0; i < paras.Count; i++)
        {
            var p = paras[i];
            try
            {
                if (pointer.CompareTo(p.ContentStart) >= 0 &&
                    (pointer.CompareTo(p.ElementEnd) <= 0 || i == paras.Count - 1))
                {
                    int off = new TextRange(p.ContentStart, pointer).Text.Length;
                    return (i, Math.Max(0, off));
                }
            }
            catch { /* 越界指针钳制到段首 */ return (i, 0); }
        }
        return (Math.Max(0, paras.Count - 1), 0);
    }

    private static TextPointer ParaOffsetToPointer(IReadOnlyList<Paragraph> paras, int para, int off)
    {
        if (paras.Count == 0) throw new InvalidOperationException("空文档");
        para = Math.Clamp(para, 0, paras.Count - 1);
        var p = paras[para];
        off = Math.Clamp(off, 0, ParaText(p).Length);
        // 同高亮引擎：GetPositionAtOffset 按符号（含 Run 边界）计数，必须经校准
        return At(p, off) ?? p.ContentStart;
    }

    /// <summary>光标在全文中的字符偏移（段落间按 \r\n 占 2 字符，与字符串一致）。</summary>
    private int CaretDocOffset()
    {
        var paras = EditorParagraphs();
        var (pi, off) = ParaOffsetOf(paras, MdEditor.CaretPosition);
        int sum = off;
        for (int i = 0; i < pi; i++) sum += ParaText(paras[i]).Length + 2;
        return sum;
    }

    private TextPointer DocOffsetToPointer(int docOff)
    {
        var paras = EditorParagraphs();
        foreach (var p in paras.Select((pp, i) => (pp, i)))
        {
            int len = ParaText(p.pp).Length;
            if (docOff <= len || p.i == paras.Count - 1)
                return ParaOffsetToPointer(paras, p.i, Math.Min(docOff, len));
            docOff -= len + 2;
        }
        return MdEditor.Document.ContentEnd;
    }

    // ================= Markdown 实时高亮（标题/粗斜体/代码/引用/列表/链接） =================

    private void ScheduleHighlight()
    {
        if (!_editing || _highlighting) return;
        if (GetEditorText().Length > 300_000) return; // 超大文档跳过高亮保流畅
        _hlTimer.Stop();
        _hlTimer.Start();
    }

    private void HighlightMarkdown()
    {
        if (!_editing || _highlighting) return;
        var paras = EditorParagraphs();
        if (paras.Count == 0) return;
        _highlighting = true;
        try
        {
            var (cpi, coff) = ParaOffsetOf(paras, MdEditor.CaretPosition);
            var (spi, soff) = ParaOffsetOf(paras, MdEditor.Selection.Start);
            var (epi, eoff) = ParaOffsetOf(paras, MdEditor.Selection.End);
            PaintDocument(MdEditor.Document, _hl);
            MdEditor.Selection.Select(
                ParaOffsetToPointer(paras, spi, soff),
                ParaOffsetToPointer(paras, epi, eoff));
            MdEditor.CaretPosition = ParaOffsetToPointer(paras, cpi, coff);
            UpdateGutter();
        }
        catch { }
        finally { _highlighting = false; }
    }

    // ================= 行号栏 =================

    private ScrollViewer? FindEditorScroller()
    {
        if (_editorScroller is not null) return _editorScroller;
        _editorScroller = FindVisualChild<ScrollViewer>(MdEditor);
        return _editorScroller;
    }

    private static T? FindVisualChild<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            var f = FindVisualChild<T>(c);
            if (f is not null) return f;
        }
        return null;
    }

    private void HookEditorScroll()
    {
        var sv = FindEditorScroller();
        if (sv is null) return;
        sv.ScrollChanged -= EditorScrollChanged;
        sv.ScrollChanged += EditorScrollChanged;
    }

    private void EditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_editing) return;
        var sv = FindEditorScroller();
        if (sv is not null) LineNumberScroller.ScrollToVerticalOffset(sv.VerticalOffset);
    }

    private void UpdateGutter()
    {
        int n = Math.Max(1, EditorParagraphs().Count);
        int show = Math.Min(n, 50000);
        var sb = new StringBuilder(show * 4);
        for (int i = 1; i <= show; i++) sb.AppendLine(i.ToString());
        LineNumberText.Text = sb.ToString().TrimEnd('\r', '\n');
        var sv = FindEditorScroller();
        if (sv is not null) LineNumberScroller.ScrollToVerticalOffset(sv.VerticalOffset);
    }

    // ================= 排版工具栏 =================

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        if (!_editing || sender is not Button b || b.Tag is not string tag) return;
        switch (tag)
        {
            case "bold": WrapSelection("**", "**", "粗体"); break;
            case "italic": WrapSelection("*", "*", "斜体"); break;
            case "strike": WrapSelection("~~", "~~", "删除线"); break;
            case "code": WrapSelection("`", "`", "代码"); break;
            case "link": WrapSelection("[", "](https://)", "链接文本"); break;
            case "quote": PrefixLines("> "); break;
            case "list": PrefixLines("- "); break;
            case "codeblock": WrapSelection("```\n", "\n```", null); break;
            case "hr": InsertHr(); break;
            case "h": CycleHeading(); break;
        }
        ScheduleHighlight();
        MdEditor.Focus();
    }

    private void WrapSelection(string left, string right, string? placeholder)
    {
        using (MdEditor.DeclareChangeBlock())
        {
            var sel = MdEditor.Selection;
            if (sel.IsEmpty)
            {
                // 无选中：插入标记对，光标停在中间（不再做占位选中，避免指针换算漂移）
                string ph = placeholder ?? string.Empty;
                sel.Text = left + ph + right;
            }
            else
            {
                sel.Text = left + sel.Text + right;
            }
        }
    }

    private void PrefixLines(string prefix)
    {
        var paras = EditorParagraphs();
        if (paras.Count == 0) return;
        var (spi, _) = ParaOffsetOf(paras, MdEditor.Selection.Start);
        var (epi, _) = ParaOffsetOf(paras, MdEditor.Selection.End);
        if (spi > epi) (spi, epi) = (epi, spi);
        using (MdEditor.DeclareChangeBlock())
        {
            for (int i = spi; i <= epi; i++)
                paras[i].ContentStart.InsertTextInRun(prefix);
        }
        var fresh = EditorParagraphs();
        MdEditor.CaretPosition = ParaOffsetToPointer(fresh, epi, ParaText(fresh[epi]).Length);
        MdEditor.Selection.Select(MdEditor.CaretPosition, MdEditor.CaretPosition);
    }

    private void CycleHeading()
    {
        var paras = EditorParagraphs();
        if (paras.Count == 0) return;
        var (pi, _) = ParaOffsetOf(paras, MdEditor.CaretPosition);
        var p = paras[pi];
        string t = ParaText(p);
        string nt;
        var m = Regex.Match(t, @"^(#{1,6})\s");
        if (!m.Success) nt = "# " + t.TrimStart();
        else if (m.Groups[1].Length >= 3) nt = t[m.Length..].TrimStart();
        else nt = new string('#', m.Groups[1].Length + 1) + " " + t[m.Length..].TrimStart();
        using (MdEditor.DeclareChangeBlock())
        {
            p.Inlines.Clear();
            p.Inlines.Add(new Run(nt));
        }
        var fresh = EditorParagraphs();
        MdEditor.CaretPosition = ParaOffsetToPointer(fresh, pi, nt.Length);
        MdEditor.Selection.Select(MdEditor.CaretPosition, MdEditor.CaretPosition);
    }

    private void InsertHr()
    {
        using (MdEditor.DeclareChangeBlock())
        {
            var sel = MdEditor.Selection;
            if (!sel.IsEmpty) sel.Text = string.Empty;
            var p = MdEditor.CaretPosition.Paragraph;
            if (p is not null && ParaText(p).Trim().Length > 0)
            {
                // 当前行有内容：先换行再插分隔线
                MdEditor.CaretPosition.InsertTextInRun("\r\n");
                var paras = EditorParagraphs();
                var (pi, _) = ParaOffsetOf(paras, MdEditor.CaretPosition);
                paras[pi].ContentStart.InsertTextInRun("---");
                MdEditor.CaretPosition = ParaOffsetToPointer(EditorParagraphs(), pi, 3);
            }
            else
            {
                MdEditor.CaretPosition.InsertTextInRun("---");
            }
        }
    }

    // ================= 查找条 =================

    private void OpenFind()
    {
        if (!_editing) return;
        _findOpen = true;
        if (!MdEditor.Selection.IsEmpty)
            FindBox.Text = MdEditor.Selection.Text.Replace("\r", "").Replace("\n", "");
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findOpen = false;
        FindCountText.Text = string.Empty;
        MdEditor.Focus();
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { FindNext(); e.Handled = true; }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private void FindNext()
    {
        string needle = FindBox.Text ?? string.Empty;
        if (needle.Length == 0) { FindCountText.Text = string.Empty; return; }
        string full = GetEditorText();
        int total = CountOccurrences(full, needle);
        if (total == 0)
        {
            FindCountText.Text = "0 个匹配";
            SetStatus($"未找到“{needle}”");
            return;
        }
        int start = CaretDocOffset();
        // 若光标正好落在当前匹配上，从其后继续找（否则永远停在原地）
        int from = start;
        int at = full.IndexOf(needle, Math.Min(from, full.Length), StringComparison.OrdinalIgnoreCase);
        if (at == from && !MdEditor.Selection.IsEmpty &&
            string.Equals(MdEditor.Selection.Text, needle, StringComparison.OrdinalIgnoreCase))
            at = full.IndexOf(needle, Math.Min(from + needle.Length, full.Length), StringComparison.OrdinalIgnoreCase);
        else if (at < 0 || at < from)
            at = full.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase); // 回绕
        if (at < 0) at = full.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
        var a = DocOffsetToPointer(at);
        var b = DocOffsetToPointer(at + needle.Length);
        MdEditor.Selection.Select(a, b);
        MdEditor.CaretPosition = b;
        a.Paragraph?.BringIntoView();
        int ordinal = CountOccurrences(full[..Math.Min(at + needle.Length, full.Length)], needle);
        FindCountText.Text = $"{ordinal}/{total}";
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        int c = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            c++;
            i += needle.Length;
        }
        return c;
    }
    private FileNode? FindNodeByPath(string path)
    {
        if (FileTree.ItemsSource is not ObservableCollection<FileNode> roots) return null;
        return Walk(roots);
        FileNode? Walk(IEnumerable<FileNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase)) return n;
                var f = Walk(n.Children);
                if (f is not null) return f;
            }
            return null;
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        try
        {
            Clipboard.SetText(_currentFilePath);
            SetStatus("已复制文件路径到剪贴板");
        }
        catch { SetStatus("复制失败", ok: false); }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath) || !File.Exists(_currentFilePath)) return;
        try { Process.Start(new ProcessStartInfo(_currentFilePath) { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus($"无法打开：{ex.Message}", ok: false); }
    }

    // ================= 打开历史 =================

    private bool _historyDeleting;

    private void RecentBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshHistoryList();
        try
        {
            var p = RecentBtn.TranslatePoint(new Point(0, 0), this);
            // 面板左缘对齐窗口左边界并留 12px 间隔（与侧栏内容边距一致），
            // 宽度钳制保证右缘不超出窗口。
            HistoryPopup.HorizontalOffset = 12 - p.X;
            double maxW = ActualWidth - 12 - 16;
            HistoryPanel.Width = Math.Clamp(maxW, 240, 320);
        }
        catch { HistoryPopup.HorizontalOffset = 0; HistoryPanel.Width = 320; }
        HistoryPopup.IsOpen = true;
    }

    /// <summary>刷新历史列表：刷新人性化时间，缺失目录标注而非静默丢弃（对标 VS Code 置灰）。</summary>
    private void RefreshHistoryList()
    {
        foreach (var h in _config.History)
        {
            h.RefreshDisplayTime();
            if (!Directory.Exists(h.Folder))
                h.DisplayTime = "目录已不存在";
        }
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _config.History;
        HistoryEmptyHint.Visibility = _config.History.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = _config.History.Count > 0 ? $"{_config.History.Count} 条" : string.Empty;
    }

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_historyDeleting) { _historyDeleting = false; return; }
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;
        HistoryPopup.IsOpen = false;
        HistoryList.SelectedItem = null;
        if (!Directory.Exists(entry.Folder))
        {
            SetStatus("该目录已不存在，可在历史中删除此条目", ok: false);
            return;
        }
        if (string.Equals(entry.Folder, _rootFolder, StringComparison.OrdinalIgnoreCase))
            return; // 已在当前目录，无需重载
        await LoadRootAsync(entry.Folder);
    }

    private void HistoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string folder) return;
        _historyDeleting = true; // 避免删除按钮的点击冒泡触发“打开”
        _config.RemoveHistory(folder);
        _config.Save();
        RefreshHistoryList();
        e.Handled = true;
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _config.History.Clear();
        _config.Save();
        RefreshHistoryList();
        SetStatus("已清空打开历史");
    }

    private async void HistoryOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        HistoryPopup.IsOpen = false;
        await PickFolderAsync();
    }

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        App.LogStage($"DRAG completed change={e.HorizontalChange:0} cancelled={e.Canceled} actual={SidebarCol.ActualWidth:0}");
        // 拉到很窄则吸附为收起态（带动画滑入）；拉开则记住宽度（已在目标尺寸，无需动画）。
        if (SidebarCol.ActualWidth < 100)
        {
            _sidebarCollapsed = true;
            _config.SidebarCollapsed = true;
            _config.Save();
            ApplySidebarState();
        }
        else
        {
            _sidebarCollapsed = false;
            _config.SidebarCollapsed = false;
            SidebarCol.MinWidth = 200;
            _sidebarExpandedWidth = SidebarCol.Width;
            _config.SidebarWidth = SidebarCol.ActualWidth;
            _config.Save();
            UpdateRailGlyph();
        }
    }

    private void UpdateRailGlyph()
    {
        SidebarRailBtn.Content = _sidebarCollapsed ? "\uE76C" : "\uE76B";
        SidebarRailBtn.ToolTip = _sidebarCollapsed ? "展开目录栏 (Ctrl+B)" : "收起目录栏 (Ctrl+B)";
    }

    // ================= 文件监听（防抖自动刷新） =================

    private void SetupWatcher(string folder)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(folder, "*.md")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler h = (_, _) => DebouncedRefresh();
            RenamedEventHandler rh = (_, _) => DebouncedRefresh();
            _watcher.Created += h; _watcher.Deleted += h; _watcher.Changed += h; _watcher.Renamed += rh;
        }
        catch { _watcher = null; /* 无权限等情况静默降级为手动刷新 */ }
    }

    private async void DebouncedRefresh()
    {
        _watcherCts?.Cancel();
        var cts = new CancellationTokenSource();
        _watcherCts = cts;
        try
        {
            await Task.Delay(800, cts.Token);
            if (cts.IsCancellationRequested || string.IsNullOrEmpty(_rootFolder)) return;
            await Dispatcher.InvokeAsync(async () =>
            {
                string? cur = _currentFilePath;
                var expanded = CollectExpanded(); // 自动刷新同样保持展开态，否则树会突然收起
                var (nodes, total) = await Task.Run(() => BuildTree(_rootFolder));
                FileTree.ItemsSource = nodes;
                FileCountBadge.Text = $"{total} 个文件";
                TrackExpansion(nodes);
                RestoreExpanded(expanded);
                ApplySearchFilter();
                UpdateExpandToggle();
                if (cur is not null && File.Exists(cur))
                {
                    // 编辑中不重载正文（保护未保存内容），目录树照常刷新；
                    // 保存动作自己会刷新预览，所以这里跳过即可。
                    if (!(_editing && string.Equals(cur, _currentFilePath, StringComparison.OrdinalIgnoreCase)))
                        await OpenFileAsync(cur);
                }
                SetStatus($"已自动刷新：共 {total} 个文件");
            });
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ================= 快捷键 / 拖拽 =================

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc：查找条开着先关查找条，否则编辑模式下退出编辑（有未保存更改会先确认）
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (_findOpen)
            {
                FindBar.Visibility = Visibility.Collapsed;
                _findOpen = false;
                FindCountText.Text = string.Empty;
                MdEditor.Focus();
                e.Handled = true;
                return;
            }
            if (_editing)
            {
                await ExitEditModeAsync();
                e.Handled = true;
                return;
            }
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.O: await PickFolderAsync(); e.Handled = true; break;
                case Key.F:
                    // 编辑模式打开编辑器内查找，否则定位到文件名搜索
                    if (_editing) { OpenFind(); e.Handled = true; }
                    else { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
                    break;
                case Key.S:
                    // 编辑模式保存（平时 Ctrl+S 无动作，避免误触）
                    if (_editing) { await SaveEditAsync(); e.Handled = true; }
                    break;
                case Key.R: Refresh_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.D: ThemeToggle_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.B: SidebarToggle_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case Key.OemPlus:
                case Key.Add: await ZoomAsync(+1); e.Handled = true; break;
                case Key.OemMinus:
                case Key.Subtract: await ZoomAsync(-1); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.F5)
        {
            Refresh_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (paths is null || paths.Length == 0) return;
        string p = paths[0];
        try
        {
            if (File.Exists(p) && p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                string? dir = Path.GetDirectoryName(p);
                if (dir is not null) await LoadRootAsync(dir);
                await OpenFileAsync(p);
            }
            else if (Directory.Exists(p))
            {
                await LoadRootAsync(p);
            }
        }
        catch (Exception ex) { SetStatus($"打开失败：{ex.Message}", ok: false); }
    }

    // ================= 状态栏 =================

    private void SetStatus(string text, bool ok = true, bool loading = false)
    {
        StatusText.Text = text;
        StatusDot.Fill = loading
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D29922"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(ok ? "#2DA44E" : "#CF222E"));
    }
}
