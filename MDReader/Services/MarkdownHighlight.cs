using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MDReader.Services;

/// <summary>
/// Markdown 实时高亮：纯逻辑模块（不依赖窗口/编辑器控件），可被测试工程直接引用验证。
/// 约定：调用方先把每段归一化为单个 Run（见 <see cref="NormalizeParagraph"/>），
/// 再按段调用 <see cref="PaintParagraph"/>；段内无换行，偏移精确对应字符下标。
/// </summary>
public static class MarkdownHighlight
{
    public sealed class HlPalette
    {
        public Brush Text = Brushes.Black;
        public Brush Heading = Brushes.Blue;
        public Brush Bold = Brushes.Black;
        public Brush Italic = Brushes.Gray;
        public Brush Code = Brushes.DarkRed;
        public Brush CodeBg = Brushes.LightGray;
        public Brush CodeBlockBg = Brushes.LightGray;
        public Brush Quote = Brushes.Gray;
        public Brush List = Brushes.Blue;
        public Brush Link = Brushes.Blue;
        public Brush Url = Brushes.Gray;
        public Brush Hr = Brushes.LightGray;

        private static Brush B(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        public static HlPalette Light() => new()
        {
            Text = B("#1F2328"),
            Heading = B("#0969DA"),
            Bold = B("#1F2328"),
            Italic = B("#59636E"),
            Code = B("#CF222E"),
            CodeBg = B("#F1F3F5"),
            CodeBlockBg = B("#F6F8FA"),
            Quote = B("#59636E"),
            List = B("#0969DA"),
            Link = B("#0969DA"),
            Url = B("#59636E"),
            Hr = B("#D0D7DE"),
        };

        public static HlPalette Dark() => new()
        {
            Text = B("#D6DDE6"),
            Heading = B("#5F9CF5"),
            Bold = B("#D6DDE6"),
            Italic = B("#8D96A0"),
            Code = B("#8FBDF5"),
            CodeBg = B("#191F29"),
            CodeBlockBg = B("#191F29"),
            Quote = B("#8D96A0"),
            List = B("#5F9CF5"),
            Link = B("#5F9CF5"),
            Url = B("#8D96A0"),
            Hr = B("#2C333B"),
        };
    }

    private static readonly Regex ReFence = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex ReHeading = new(@"^(#{1,6})\s", RegexOptions.Compiled);
    private static readonly Regex ReHr = new(@"^\s*([-*_]\s*){3,}$", RegexOptions.Compiled);
    private static readonly Regex ReQuote = new(@"^\s*>", RegexOptions.Compiled);
    private static readonly Regex ReList = new(@"^(\s*(?:[-*+]|\d+[.)])\s)", RegexOptions.Compiled);
    private static readonly Regex ReImage = new(@"!\[([^\]\n]*)\]\(([^()\n]*)\)", RegexOptions.Compiled);
    private static readonly Regex ReLink = new(@"\[([^\]\n]*)\]\(([^()\n]*)\)", RegexOptions.Compiled);
    private static readonly Regex ReCode = new(@"`[^`\n]+`", RegexOptions.Compiled);
    private static readonly Regex ReBoldA = new(@"\*\*[^*\n]+\*\*", RegexOptions.Compiled);
    private static readonly Regex ReBoldB = new(@"__[^_\n]+__", RegexOptions.Compiled);
    private static readonly Regex ReItalicA = new(@"(?<!\*)\*[^*\n]+\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex ReItalicB = new(@"(?<!_)_[^_\n]+_(?!_)", RegexOptions.Compiled);

    public static string ParaText(Paragraph p)
    {
        string t = new TextRange(p.ContentStart, p.ContentEnd).Text;
        if (t.EndsWith("\r\n")) return t[..^2];
        if (t.EndsWith("\n") || t.EndsWith("\r")) return t[..^1];
        return t;
    }

    /// <summary>
    /// 段内导航校准。实测结论（探针验证）：
    /// - 从 Paragraph.ContentStart 导航按“符号”（含 Run 等内联元素边界）计数，
    ///   每分裂出一个 Run 就多漂移，且与字符下标不是固定差值——不可直接用；
    /// - 从 Run.ContentStart 导航与字符串下标恒等（UTF-16 单元两侧一致，含 emoji）。
    /// 因此按 Run 步进定位：纯字符串算术找所在 Run，再在其内部导航，精确且稳定。
    /// </summary>
    public static TextPointer? At(Paragraph p, int charIndex)
    {
        try
        {
            if (charIndex < 0) return null;
            int acc = 0;
            foreach (var inl in p.Inlines)
            {
                string rt = inl is Run r ? r.Text
                    : new TextRange(inl.ContentStart, inl.ContentEnd).Text;
                if (charIndex <= acc + rt.Length)
                {
                    if (inl is Run rr)
                        return rr.ContentStart.GetPositionAtOffset(charIndex - acc);
                    return inl.ContentStart; // 非 Run 内联（罕见粘贴情形）：落其起点
                }
                acc += rt.Length;
            }
            return p.ContentEnd;
        }
        catch { return null; }
    }

    /// <summary>把段落合并为单个 Run（文本不变），使后续偏移映射精确。</summary>
    public static string NormalizeParagraph(Paragraph p)
    {
        string t = ParaText(p);
        p.Inlines.Clear();
        p.Inlines.Add(new Run(t));
        return t;
    }

    /// <summary>整篇着色：归一化 → 清格式 → 逐段绘制。调用方负责保存/恢复光标。</summary>
    public static void PaintDocument(FlowDocument doc, HlPalette pal, double baseFontSize = 13.5)
    {
        var paras = doc.Blocks.OfType<Paragraph>().ToList();
        foreach (var p in paras) NormalizeParagraph(p);
        var full = new TextRange(doc.ContentStart, doc.ContentEnd);
        full.ApplyPropertyValue(TextElement.ForegroundProperty, pal.Text);
        full.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
        full.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
        full.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
        full.ApplyPropertyValue(TextElement.FontSizeProperty, baseFontSize);
        full.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
        bool inFence = false;
        foreach (var p in paras) PaintParagraph(p, ref inFence, pal);
    }

    public static void PaintParagraph(Paragraph p, ref bool inFence, HlPalette pal)
    {
        string t = ParaText(p);
        if (t.Length == 0) return;
        var done = new List<(int s, int e)>();
        bool Overlaps(int s, int e)
        {
            foreach (var (a, b) in done)
                if (s < b && a < e) return true;
            return false;
        }
        void Paint(int s, int len, Brush fg, Brush? bg = null,
                   FontWeight? w = null, FontStyle? st = null, double? size = null)
        {
            if (len <= 0 || Overlaps(s, s + len)) return;
            try
            {
                // 关键：每次绘制都重新校准（绘制会分裂 Run，偏移量随之漂移）
                var a = At(p, s);
                var b = At(p, s + len);
                if (a is null || b is null) return;
                var r = new TextRange(a, b);
                r.ApplyPropertyValue(TextElement.ForegroundProperty, fg);
                if (bg is not null) r.ApplyPropertyValue(TextElement.BackgroundProperty, bg);
                if (w is not null) r.ApplyPropertyValue(TextElement.FontWeightProperty, w);
                if (st is not null) r.ApplyPropertyValue(TextElement.FontStyleProperty, st);
                if (size is not null) r.ApplyPropertyValue(TextElement.FontSizeProperty, size.Value);
                done.Add((s, s + len));
            }
            catch { }
        }
        void PaintWhole(Brush fg, Brush? bg = null, FontWeight? w = null, FontStyle? st = null, double? size = null)
        {
            // 整行底色（如标题/引用/代码块）：不占 done，允许行内再叠加
            try
            {
                var r = new TextRange(p.ContentStart, p.ContentEnd);
                r.ApplyPropertyValue(TextElement.ForegroundProperty, fg);
                if (bg is not null) r.ApplyPropertyValue(TextElement.BackgroundProperty, bg);
                if (w is not null) r.ApplyPropertyValue(TextElement.FontWeightProperty, w);
                if (st is not null) r.ApplyPropertyValue(TextElement.FontStyleProperty, st);
                if (size is not null) r.ApplyPropertyValue(TextElement.FontSizeProperty, size.Value);
            }
            catch { }
        }

        // 围栏代码块（含起止 ``` 行）
        if (ReFence.IsMatch(t))
        {
            inFence = !inFence;
            PaintWhole(pal.Code, null, FontWeights.Bold);
            return;
        }
        if (inFence)
        {
            PaintWhole(pal.Text, pal.CodeBlockBg);
            return;
        }
        // 标题（字号随级别）
        var mh = ReHeading.Match(t);
        if (mh.Success)
        {
            double size = mh.Groups[1].Length switch { 1 => 17, 2 => 15.5, 3 => 14, _ => 13.5 };
            PaintWhole(pal.Heading, null, FontWeights.Bold, null, size);
        }
        // 分隔线独占一行
        if (ReHr.IsMatch(t))
        {
            PaintWhole(pal.Hr, null, FontWeights.Bold);
            return;
        }
        // 引用整行弱化
        if (ReQuote.IsMatch(t)) PaintWhole(pal.Quote, null, null, FontStyles.Italic);
        // 列表符号
        var ml = ReList.Match(t);
        if (ml.Success) Paint(0, ml.Length, pal.List, null, FontWeights.Bold);
        // 行内：图片 > 链接 > 行内代码 > 粗体 > 斜体（优先级，后者跳过已着色区）
        foreach (Match m in ReImage.Matches(t))
        {
            if (Overlaps(m.Index, m.Index + m.Length)) continue;
            Paint(m.Index, 1, pal.Url); // !
            Paint(m.Groups[1].Index, m.Groups[1].Length, pal.Link); // alt
            Paint(m.Groups[2].Index, m.Groups[2].Length, pal.Url);  // src
            done.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in ReLink.Matches(t))
        {
            if (Overlaps(m.Index, m.Index + m.Length)) continue;
            Paint(m.Groups[1].Index, m.Groups[1].Length, pal.Link);
            Paint(m.Groups[2].Index, m.Groups[2].Length, pal.Url);
            done.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in ReCode.Matches(t))
            Paint(m.Index, m.Length, pal.Code, pal.CodeBg);
        foreach (Match m in ReBoldA.Matches(t)) Paint(m.Index, m.Length, pal.Bold, null, FontWeights.Bold);
        foreach (Match m in ReBoldB.Matches(t)) Paint(m.Index, m.Length, pal.Bold, null, FontWeights.Bold);
        foreach (Match m in ReItalicA.Matches(t)) Paint(m.Index, m.Length, pal.Italic, null, null, FontStyles.Italic);
        foreach (Match m in ReItalicB.Matches(t)) Paint(m.Index, m.Length, pal.Italic, null, null, FontStyles.Italic);
    }
}
