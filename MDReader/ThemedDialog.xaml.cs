using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MDReader;

/// <summary>
/// 跟随主题的自绘确认框：替代原生 MessageBox（后者永远经典样式，与日夜主题割裂）。
/// 签名与 MessageBox.Show 对齐（含原生 Esc / 右上角关闭的默认返回值语义），调用处只换名字。
/// 配色不写死：构造时从主窗口资源同步当前主题色；主窗口不存在时用浅色默认值。
/// </summary>
public partial class ThemedDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private ThemedDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? "MD阅读器" : title;
        DlgMessage.Text = message ?? string.Empty;
        SyncTheme();
        BuildIcon(icon);
        BuildButtons(buttons);
    }

    /// <param name="owner">主窗口（传 this）；传 null 则屏幕居中（如全局异常兜底）</param>
    public static MessageBoxResult Show(Window? owner, string message,
        string title = "MD阅读器",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None)
    {
        var d = new ThemedDialog(message, title, buttons, icon);
        try
        {
            if (owner is not null && owner.IsLoaded)
            {
                d.Owner = owner;
                d.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        catch { d.WindowStartupLocation = WindowStartupLocation.CenterScreen; }
        d.ShowDialog();
        return d.Result;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyCaptionTheme(); // 标题栏与主窗口同色（DWM），失败静默保持系统默认
    }

    #region 原生标题栏跟随主题（与主窗口同一套 DWM 参数）

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyCaptionTheme()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            Color cap = (Resources["DlgTop"] as SolidColorBrush)?.Color ?? Colors.White;
            Color txt = (Resources["DlgText"] as SolidColorBrush)?.Color ?? Colors.Black;
            Color brd = (Resources["DlgBorder"] as SolidColorBrush)?.Color ?? cap;
            int c = ToColorRef(cap), t = ToColorRef(txt), b = ToColorRef(brd);
            int size = sizeof(int);
            bool ok = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, size) == 0
                && DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref t, size) == 0
                && DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref b, size) == 0;
            if (!ok)
            {
                // Win10 回退：按顶栏亮度猜深浅，只换标题栏按钮颜色
                int dark = (0.299 * cap.R + 0.587 * cap.G + 0.114 * cap.B) < 128 ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
        }
        catch { /* 标题栏配色失败不打扰 */ }
    }

    private static int ToColorRef(Color c) => (c.B << 16) | (c.G << 8) | c.R;

    #endregion

    private void SyncTheme()
    {
        try
        {
            var mw = Application.Current?.MainWindow;
            if (mw is null) return;
            // 对话框本地键 ← 主窗口主题键
            (string Dst, string Src)[] map =
            [
                ("DlgCard", "CardBrush"),
                ("DlgTop", "TopBarBrush"),
                ("DlgText", "TextPrimary"),
                ("DlgSubtle", "TextSecondary"),
                ("DlgAccent", "AccentBrush"),
                ("DlgAccentFg", "AccentFgBrush"),
                ("DlgBorder", "BorderBrush"),
            ];
            foreach (var (dst, src) in map)
            {
                if (mw.Resources[src] is SolidColorBrush s && Resources[dst] is SolidColorBrush t)
                    t.Color = s.Color;
            }
        }
        catch { /* 同步失败则用浅色默认值，绝不打扰 */ }
    }

    private void BuildIcon(MessageBoxImage icon)
    {
        // 实心语义色徽章 + 白色矢量符号：未保存确认是“可能丢稿”的警告语义，用琥珀色
        // （与编辑器脏点 #D29922 同色系，应用内语言统一）；主按钮保持主题蓝。
        // 这样焦点唯一：颜色归图标，行动归按钮，互不抢戏。
        (string Kind, Color Main) = icon switch
        {
            MessageBoxImage.Question => ("excl", Color.FromRgb(0xD2, 0x99, 0x22)),
            MessageBoxImage.Warning or MessageBoxImage.Exclamation => ("excl", Color.FromRgb(0xD2, 0x99, 0x22)),
            MessageBoxImage.Error or MessageBoxImage.Stop or MessageBoxImage.Hand => ("cross", Color.FromRgb(0xE5, 0x48, 0x4D)),
            MessageBoxImage.Information or MessageBoxImage.Asterisk => ("info", Color.FromRgb(0x6E, 0x76, 0x81)),
            _ => ("", Colors.Transparent),
        };
        if (string.IsNullOrEmpty(Kind)) return;
        var badgeFill = new SolidColorBrush(Main);
        DlgTri.Fill = badgeFill;
        DlgCirc.Background = badgeFill;
        Resources["DlgBadgeFg"] = new SolidColorBrush(Colors.White);
        bool isTri = Kind == "excl";
        DlgTri.Visibility = isTri ? Visibility.Visible : Visibility.Collapsed;
        DlgCirc.Visibility = isTri ? Visibility.Collapsed : Visibility.Visible;
        ExclGrid.Visibility = Kind == "excl" ? Visibility.Visible : Visibility.Collapsed;
        CrossGrid.Visibility = Kind == "cross" ? Visibility.Visible : Visibility.Collapsed;
        InfoGrid.Visibility = Kind == "info" ? Visibility.Visible : Visibility.Collapsed;
        DlgBadge.Visibility = Visibility.Visible;
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                Result = MessageBoxResult.No; // Esc / 右上角关闭 = 否（与原生一致）
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("否", MessageBoxResult.No, primary: false, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                Result = MessageBoxResult.Cancel; // 右上角关闭 = 取消（与原生一致）
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("否", MessageBoxResult.No);
                AddButton("取消", MessageBoxResult.Cancel, isCancel: true);
                break;
            case MessageBoxButton.OKCancel:
                Result = MessageBoxResult.Cancel;
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true);
                AddButton("取消", MessageBoxResult.Cancel, isCancel: true);
                break;
            default: // OK
                Result = MessageBoxResult.OK;
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true, isCancel: true);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result,
        bool primary = false, bool isDefault = false, bool isCancel = false)
    {
        var b = new System.Windows.Controls.Button
        {
            Content = text,
            Style = (Style)Resources[primary ? "DlgPrimaryBtn" : "DlgBtn"],
            Margin = new Thickness(DlgButtons.Children.Count == 0 ? 0 : 8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        b.Click += (_, _) => { Result = result; Close(); };
        DlgButtons.Children.Add(b);
    }
}
