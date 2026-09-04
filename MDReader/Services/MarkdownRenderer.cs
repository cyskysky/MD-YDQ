using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MDReader.Models;

namespace MDReader.Services;

/// <summary>
/// Markdown → 离线自包含 HTML（无任何 CDN 依赖，exe 放到哪都能用）。
/// 渲染风格对标 GitHub / Typora：表格、任务列表、脚注、Emoji、自动标题锚点。
/// </summary>
public static class MarkdownRenderer
{
    // 注意：不用 UseAdvancedExtensions（一揽子包自带 AutoIdentifiers，它对中文标题只生成
    // section-N 这类无意义 id）。下面是与一揽子包同等的显式清单，唯独去掉 AutoIdentifiers；
    // 标题 id 统一由 ApplyHeadingIds 按 GitHub 风格（中文友好）生成，大纲与 #锚点跳转都依赖它。
    // 用户手写的 {#custom} 显式 id 由 GenericAttributes 处理，不受影响。
    // 另：刻意不用 UseDiagrams——它会把 ```mermaid 篱笆吞成裸 div，破坏 Mermaid 检测；
    // 去掉后 mermaid 以标准代码块形式输出，由前端统一转图表。
    // FrontMatter（一览子包没有）单独加：文档头 YAML 不应泄漏为正文。
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAbbreviations()
        .UseCitations()
        .UseCustomContainers()
        .UseDefinitionLists()
        .UseEmphasisExtras()
        .UseFigures()
        .UseFootnotes()
        .UseFooters()
        .UseGenericAttributes()
        .UseGridTables()
        .UseMathematics()
        .UseMediaLinks()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmojiAndSmiley()              // :+1: → 👍
        .UseYamlFrontMatter()
        .Build();

    static MarkdownRenderer()
    {
        // 中文 Windows 常见 GBK/GB18030 编码的 md 文件也能正确打开
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly Lazy<string> MermaidEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.mermaid.min.js"));

    /// <summary>
    /// Mermaid 图表库源码。默认从程序集内嵌资源懒加载（仅当文档含图表时才读取，
    /// 平时零开销）；测试可赋值注入假实现。
    /// </summary>
    public static string? MermaidJsOverride { get; set; }

    private static string MermaidJs => MermaidJsOverride ?? MermaidEmbedded.Value;

    private static string LoadEmbeddedText(string resourceName)
    {
        try
        {
            var asm = typeof(MarkdownRenderer).Assembly;
            using var s = asm.GetManifestResourceStream(resourceName);
            if (s is null) return string.Empty;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    private static readonly Lazy<string> KatexJsEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.katex.min.js"));
    private static readonly Lazy<string> KatexAutoRenderEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.katex-auto-render.min.js"));
    private static readonly Lazy<string> KatexCssEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.katex.css"));

    /// <summary>测试注入点（默认读内嵌资源）。</summary>
    public static string? KatexJsOverride { get; set; }
    public static string? KatexAutoRenderOverride { get; set; }
    public static string? KatexCssOverride { get; set; }

    private static string KatexJs => KatexJsOverride ?? KatexJsEmbedded.Value;
    private static string KatexAutoRenderJs => KatexAutoRenderOverride ?? KatexAutoRenderEmbedded.Value;
    private static string KatexCss => KatexCssOverride ?? KatexCssEmbedded.Value;

    private static readonly Lazy<string> HljsJsEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.hljs.min.js"));
    private static readonly Lazy<string> HljsCssLightEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.hljs-github.min.css"));
    private static readonly Lazy<string> HljsCssDarkEmbedded = new(() => LoadEmbeddedText("MDReader.Assets.hljs-github-dark-dimmed.min.css"));

    /// <summary>测试注入点（默认读内嵌资源）。</summary>
    public static string? HljsJsOverride { get; set; }
    public static string? HljsCssLightOverride { get; set; }
    public static string? HljsCssDarkOverride { get; set; }

    private static string HljsJs => HljsJsOverride ?? HljsJsEmbedded.Value;
    private static string HljsCssLight => HljsCssLightOverride ?? HljsCssLightEmbedded.Value;
    private static string HljsCssDark => HljsCssDarkOverride ?? HljsCssDarkEmbedded.Value;

    /// <summary>正文是否含可高亮的代码块（纯 mermaid 图表块除外，走图表管线）。</summary>
    public static bool ContainsCode(string htmlBody)
    {
        if (string.IsNullOrEmpty(htmlBody) || !htmlBody.Contains("<pre><code", StringComparison.Ordinal))
            return false;
        string rest = htmlBody.Replace("<pre><code class=\"language-mermaid\">", string.Empty, StringComparison.Ordinal);
        return rest.Contains("<pre><code", StringComparison.Ordinal);
    }

    /// <summary>正文是否含数学公式（Markdig 渲染为 class="math" 的 span/div）。</summary>
    public static bool ContainsMath(string htmlBody)
        => !string.IsNullOrEmpty(htmlBody) &&
           htmlBody.Contains("class=\"math\"", StringComparison.Ordinal);

    /// <summary>正文是否含 Mermaid 图表块（Markdig 渲染为 language-mermaid 代码块）。</summary>
    public static bool ContainsMermaid(string htmlBody)
        => !string.IsNullOrEmpty(htmlBody) &&
           htmlBody.Contains("language-mermaid", StringComparison.Ordinal);

    /// <summary>智能读取：UTF-8 优先，失败回退 GB18030 / 系统默认编码。</summary>
    public static string SmartReadAllText(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        // BOM 判定
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch { /* 非合法 UTF-8，继续回退 */ }
        try { return Encoding.GetEncoding("GB18030").GetString(bytes); }
        catch { return Encoding.Default.GetString(bytes); }
    }

    public static string ToHtmlBody(string markdown)
    {
        string html = Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        try { html = ApplyHeadingIds(html); } catch { /* id 重写失败不影响正文 */ }
        try { html = ApplyAlerts(html); } catch { /* 告示失败保留原文引用块 */ }
        try { html = ApplyToc(html); } catch { /* 目录失败保留 [TOC] 标记 */ }
        return html;
    }

    private static readonly Regex HeadingRegex = new(
        @"<h([1-6])((?:\s[^>]*)?)>(.*?)</h\1>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdAttrRegex = new(
        @"\bid\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TocMarkerRegex = new(
        @"<p>\s*(?:\[TOC\]|\[\[_TOC_\]\]|\[\[<em>TOC</em>\]\])\s*</p>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlertRegex = new(
        @"<blockquote>\s*<p>\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]([\s\S]*?)</blockquote>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> AlertTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NOTE"] = "Note",
        ["TIP"] = "Tip",
        ["IMPORTANT"] = "Important",
        ["WARNING"] = "Warning",
        ["CAUTION"] = "Caution",
    };

    /// <summary>
    /// 重写标题 id：Markdig 默认对中文标题只生成 section-N 这类无意义 id，
    /// 这里按 GitHub 风格（中文友好：保留 CJK，小写，空格转连字符，去标点，重复加 -1 后缀）
    /// 生成稳定可读的锚点。大纲与文档内 `#锚点` 跳转都依赖它。
    /// 已有显式 id（如 {#custom}）则予以保留（仅做去重）。
    /// </summary>
    private static string ApplyHeadingIds(string htmlBody)
    {
        if (string.IsNullOrEmpty(htmlBody)) return htmlBody;
        var used = new HashSet<string>(StringComparer.Ordinal);
        return HeadingRegex.Replace(htmlBody, m =>
        {
            string level = m.Groups[1].Value;
            string attrs = m.Groups[2].Value;
            string inner = m.Groups[3].Value;
            var idMatch = IdAttrRegex.Match(attrs);
            if (idMatch.Success)
            {
                // 保留作者显式 id，仅保证唯一
                string keep = idMatch.Groups[1].Value;
                if (string.IsNullOrEmpty(keep)) keep = "section";
                string b = keep; int i = 1;
                while (!used.Add(keep)) keep = $"{b}-{i++}";
                if (keep == idMatch.Groups[1].Value) return m.Value; // 无变化，原样返回
                string newAttrs = IdAttrRegex.Replace(attrs, $"id=\"{keep}\"", 1);
                return $"<h{level}{newAttrs}>{inner}</h{level}>";
            }
            string text = StripHeadingText(inner);
            string slug = Slugify(text);
            if (string.IsNullOrEmpty(slug)) slug = "section";
            string baseSlug = slug; int n = 1;
            while (!used.Add(slug)) slug = $"{baseSlug}-{n++}";
            return $"<h{level} id=\"{WebUtility.HtmlEncode(slug)}\">{inner}</h{level}>";
        });
    }

    private static string StripHeadingText(string inner)
    {
        if (string.IsNullOrEmpty(inner)) return string.Empty;
        string s = Regex.Replace(inner, @"<[^>]+>", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s.Trim(), @"\s+", " ");
    }

    /// <summary>从渲染后的 HTML 提取大纲：锚点取标题真实 id，与点击跳转 100% 一致。</summary>
    public static List<OutlineItem> ParseOutlineFromHtml(string htmlBody)
    {
        var result = new List<OutlineItem>();
        if (string.IsNullOrEmpty(htmlBody)) return result;
        foreach (Match m in HeadingRegex.Matches(htmlBody))
        {
            int level = m.Groups[1].Value[0] - '0';
            if (level is < 1 or > 4) continue;
            var idMatch = IdAttrRegex.Match(m.Groups[2].Value);
            if (!idMatch.Success) continue;
            string anchor = WebUtility.HtmlDecode(idMatch.Groups[1].Value);
            string title = StripHeadingText(m.Groups[3].Value);
            if (title.Length == 0) title = anchor;
            if (title.Length > 80) title = title[..80] + "…";
            result.Add(new OutlineItem { Level = level, Title = title, Anchor = anchor });
            if (result.Count >= 200) break;
        }
        return result;
    }

    /// <summary>
    /// [TOC] 目录：将独立成行的 [TOC] 标记替换为标题导航（Typora 兼容），
    /// 同时接受 GitHub 风格别名 [[_TOC_]]（Markdig 会把它渲染成 [[&lt;em&gt;TOC&lt;/em&gt;]]，正则已兼容）。
    /// 锚点全部取自已重写好的标题 id，与大纲面板完全一致；必须在 ApplyHeadingIds 之后调用。
    /// </summary>
    public static string ApplyToc(string htmlBody)
    {
        if (string.IsNullOrEmpty(htmlBody) ||
            (!htmlBody.Contains("[TOC]", StringComparison.OrdinalIgnoreCase) &&
             !TocMarkerRegex.IsMatch(htmlBody)))
            return htmlBody;
        var outline = ParseOutlineFromHtml(htmlBody);
        var sb = new StringBuilder("<nav class=\"toc\"><p class=\"toc-title\">目录</p>");
        if (outline.Count == 0)
        {
            sb.Append("<p class=\"toc-empty\">（本文无标题）</p></nav>");
        }
        else
        {
            sb.Append("<ul class=\"toc-list\">");
            foreach (var o in outline)
            {
                int level = Math.Clamp(o.Level, 1, 6);
                sb.Append($"<li class=\"toc-l{level}\"><a href=\"#{WebUtility.HtmlEncode(o.Anchor)}\">"
                    + $"{WebUtility.HtmlEncode(o.Title)}</a></li>");
            }
            sb.Append("</ul></nav>");
        }
        return TocMarkerRegex.Replace(htmlBody, sb.ToString());
    }

    /// <summary>
    /// GitHub 告示块：将首段为 [!NOTE] / [!TIP] / [!IMPORTANT] / [!WARNING] / [!CAUTION]
    /// 的引用块转为彩色告示（单段、多段两种写法都支持）。
    /// </summary>
    public static string ApplyAlerts(string htmlBody)
    {
        if (string.IsNullOrEmpty(htmlBody) || !htmlBody.Contains("[!", StringComparison.Ordinal))
            return htmlBody;
        return AlertRegex.Replace(htmlBody, m =>
        {
            string type = m.Groups[1].Value.ToUpperInvariant();
            string inner = m.Groups[2].Value.Trim();
            // 标记独占一段的形状：去掉悬空的 </p>
            if (inner.StartsWith("</p>", StringComparison.OrdinalIgnoreCase))
                inner = inner[4..].TrimStart();
            // 纯文本开头才包 <p>；已是块级结构的直接沿用（避免 <p> 嵌套非法）
            if (inner.Length > 0 && !inner.StartsWith('<') && !inner.Contains("</p>", StringComparison.OrdinalIgnoreCase))
                inner = "<p>" + inner + "</p>";
            string title = AlertTitles.TryGetValue(type, out var t) ? t : type;
            return $"<div class=\"alert alert-{type.ToLowerInvariant()}\">"
                 + $"<p class=\"alert-title\">{title}</p>"
                 + inner + "</div>";
        });
    }
    public static List<OutlineItem> ParseOutline(string markdown)
    {
        var result = new List<OutlineItem>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;
        try
        {
            var doc = Markdig.Markdown.Parse(markdown, Pipeline);
            foreach (var block in doc.Descendants<HeadingBlock>())
            {
                if (block.Level is < 1 or > 4) continue;
                var title = string.Concat(block.Inline?.Descendants<LiteralInline>().Select(l => l.Content.ToString()) ?? Enumerable.Empty<string>());
                if (string.IsNullOrWhiteSpace(title))
                    title = InlineToText(block.Inline);
                if (string.IsNullOrWhiteSpace(title)) continue;
                title = title.Trim();
                if (title.Length > 80) title = title[..80] + "…";
                result.Add(new OutlineItem
                {
                    Level = block.Level,
                    Title = title,
                    Anchor = Slugify(title),
                });
            }
        }
        catch { /* 大纲失败不影响正文 */ }
        // 回退：正则兜底（AST 极端情况下为空时）
        if (result.Count == 0)
        {
            foreach (Match m in Regex.Matches(markdown, @"^(#{1,4})\s+(.+?)\s*#*\s*$", RegexOptions.Multiline))
            {
                var title = m.Groups[2].Value.Trim();
                if (title.Length == 0) continue;
                result.Add(new OutlineItem { Level = m.Groups[1].Length, Title = title, Anchor = Slugify(title) });
                if (result.Count >= 200) break;
            }
        }
        return result;
    }

    private static string InlineToText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var node in inline)
        {
            switch (node)
            {
                case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case LineBreakInline: sb.Append(' '); break;
                case ContainerInline c: sb.Append(InlineToText(c)); break;
            }
        }
        return sb.ToString();
    }

    internal static string Slugify(string title)
    {
        var s = title.Trim().ToLowerInvariant();
        s = Regex.Replace(s, @"\s+", "-");
        s = Regex.Replace(s, @"[^\p{L}\p{N}\-_]", "");
        return string.IsNullOrEmpty(s) ? "section" : s;
    }

    public static (int words, int chars) CountWords(string markdown, string? htmlBody = null)
    {
        if (string.IsNullOrEmpty(markdown)) return (0, 0);
        int cjk = Regex.Matches(markdown, @"[\u4e00-\u9fff\u3400-\u4dbf]").Count;
        int words = Regex.Matches(markdown, @"[A-Za-z0-9_]+").Count + cjk;
        return (words, markdown.Length);
    }

    /// <summary>组装完整 HTML：内联全部 CSS/JS，file:// 图片可加载，代码块带复制按钮。</summary>
    public static string BuildHtml(string markdown, string fileDir, bool isDark, double fontSize)
        => WrapHtml(ToHtmlBody(markdown), fileDir, isDark, fontSize);

    /// <summary>用已渲染好的 body 组装完整 HTML（避免重复解析 Markdown）。</summary>
    public static string WrapHtml(string htmlBody, string fileDir, bool isDark, double fontSize)
    {
        string body = htmlBody ?? string.Empty;
        try { body = EmbedLocalImages(body, fileDir); } catch { /* 图片内嵌失败不影响正文 */ }
        string baseHref = string.IsNullOrWhiteSpace(fileDir)
            ? string.Empty
            : $"<base href=\"{new Uri(EnsureTrailingSlash(fileDir)).AbsoluteUri}\">";
        string themeClass = isDark ? "dark" : "light";
        string css = ThemeCss(fontSize);
        // 有图表才注入 mermaid（2.4MB 库，平时零开销；无图表文档 HTML 体积不变）
        string mermaidScripts = string.Empty;
        if (ContainsMermaid(body))
        {
            string lib = MermaidJs;
            if (!string.IsNullOrEmpty(lib))
            {
                string mTheme = isDark ? "dark" : "default";
                mermaidScripts = "<script>" + lib + "</script>\n<script>\n"
                    + "(function(){"
                    + "if(!window.mermaid) return;"
                    + "try{"
                    + $"mermaid.initialize({{startOnLoad:false,theme:'{mTheme}',securityLevel:'strict'}});"
                    + "document.querySelectorAll('pre code.language-mermaid').forEach(function(code){"
                    + "var pre=code.parentElement;"
                    + "var holder=document.createElement('pre');"
                    + "holder.className='mermaid';"
                    + "holder.textContent=code.textContent;"
                    + "pre.replaceWith(holder);"
                    + "});"
                    + "var p=mermaid.run({querySelector:'.mermaid'});"
                    + "if(p&&p.then) p.then(function(){window.__mzAttach&&window.__mzAttach();},function(){});"
                    + "}catch(e){/* 渲染失败则保留代码原文 */}"
                    + "})();\n</script>";
            }
        }

        // 有公式才注入 KaTeX（库+字体平时零开销；颜色继承正文，日夜自动适配）
        string katexHead = string.Empty;
        string katexScripts = string.Empty;
        if (ContainsMath(body))
        {
            string kjs = KatexJs, kar = KatexAutoRenderJs, kcss = KatexCss;
            if (!string.IsNullOrEmpty(kjs) && !string.IsNullOrEmpty(kar))
            {
                if (!string.IsNullOrEmpty(kcss))
                    katexHead = "<style>" + kcss + "</style>";
                katexScripts = "<script>" + kjs + "</script>\n<script>" + kar + "</script>\n<script>\n"
                    + "(function(){"
                    + "if(!window.renderMathInElement) return;"
                    + "try{"
                    + "renderMathInElement(document.querySelector('.markdown-body')||document.body,{"
                    + "delimiters:["
                    + "{left:'$$',right:'$$',display:true},"
                    + "{left:'\\\\(',right:'\\\\)',display:false},"
                    + "{left:'\\\\[',right:'\\\\]',display:true}"
                    + "],throwOnError:false});"
                    + "}catch(e){/* 公式失败保留源码 */}"
                    + "})();\n</script>";
            }
        }

        // 有代码块才注入 highlight.js（约 120KB 库 + 两套 github 主题，平时零开销；无代码文档 HTML 体积不变）
        string hljsHead = string.Empty;
        string hljsScript = string.Empty;
        if (ContainsCode(body))
        {
            string hjs = HljsJs, hcl = HljsCssLight, hcd = HljsCssDark;
            if (!string.IsNullOrEmpty(hjs) && !string.IsNullOrEmpty(hcl))
            {
                string darkDisabled = isDark ? string.Empty : " disabled";
                hljsHead = "<style>" + hcl + "</style>\n"
                    + (!string.IsNullOrEmpty(hcd)
                        ? "<style id=\"hljs-dark\"" + darkDisabled + ">" + hcd + "</style>"
                        : string.Empty);
                hljsScript = "<script>" + hjs + "</script>";
            }
        }

        return $$"""
<!DOCTYPE html>
<html lang="zh-CN" class="{{themeClass}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
{{baseHref}}
<style>{{css}}</style>
{{katexHead}}
{{hljsHead}}
</head>
<body>
<div class="page">
<article class="markdown-body">{{body}}</article>
</div>
<button id="toTop" title="回到顶部">↑</button>
{{mermaidScripts}}
{{katexScripts}}
{{hljsScript}}
<script>
(function(){
  // 代码语法高亮（highlight.js 离线包；未知语言自动跳过，mermaid 块走图表管线不受影响）
  if(window.hljs){ try{ hljs.highlightAll(); }catch(e){} }
  // 高亮主题与日夜模式对齐：注意 <style> 元素的 disabled 属性是摆设（只对 <link> 有效），
  // 必须走 sheet.disabled（CSSOM 标准，各浏览器通用）；首屏也同步一次，不依赖 markup 写法
  (function(){ var hd=document.getElementById('hljs-dark');
    if(hd&&hd.sheet) hd.sheet.disabled=(document.documentElement.className!=='dark'); })();
  // 代码块复制按钮（对标 Typora / GitHub 的 hover 复制；图表 pre.mermaid 排除，改走放大预览）
  // 注意：按钮必须挂在“不滚动的外层 .code-wrap”上。若直接 append 到 pre 里（pre 自身 overflow:auto），
  // 按钮会相对整个可滚动内容宽度定位，横滚后看起来“跑到中间去了”。
  document.querySelectorAll('pre:not(.mermaid)').forEach(function(pre){
    if(pre.parentNode && pre.parentNode.classList && pre.parentNode.classList.contains('code-wrap')) return;
    var wrap=document.createElement('div'); wrap.className='code-wrap';
    pre.parentNode.insertBefore(wrap, pre); wrap.appendChild(pre);
    var btn=document.createElement('button');
    btn.className='copy-btn'; btn.type='button'; btn.textContent='复制';
    btn.onclick=function(){
      var code=pre.querySelector('code');
      var t=code?code.innerText:pre.innerText;
      (navigator.clipboard?navigator.clipboard.writeText(t):Promise.reject()).then(function(){
        btn.textContent='已复制'; setTimeout(function(){btn.textContent='复制';},1500);
      },function(){
        var ta=document.createElement('textarea'); ta.value=t; document.body.appendChild(ta);
        ta.select(); try{document.execCommand('copy'); btn.textContent='已复制';}catch(e){btn.textContent='失败';}
        document.body.removeChild(ta); setTimeout(function(){btn.textContent='复制';},1500);
      });
    };
    wrap.appendChild(btn);
  });
  // 图片加载失败占位（本地相对路径缺失时更友好）
  document.querySelectorAll('img').forEach(function(img){
    img.setAttribute('loading','lazy');
    img.addEventListener('error',function(){
      if(img.dataset.broken) return; img.dataset.broken='1';
      var ph=document.createElement('div'); ph.className='img-broken';
      ph.textContent='🖼️ 图片无法加载：'+(img.getAttribute('src')||'');
      img.replaceWith(ph);
    });
  });
  // 外部链接新窗口（由宿主 WebView2 拦截改为系统浏览器打开）
  document.querySelectorAll('a[href^="http"]').forEach(function(a){
    a.setAttribute('target','_blank'); a.setAttribute('rel','noopener');
  });
  // 回到顶部
  var top=document.getElementById('toTop');
  window.addEventListener('scroll',function(){ top.style.display=window.scrollY>400?'block':'none'; });
  top.onclick=function(){ window.scrollTo({top:0,behavior:'smooth'}); };
  // 主题切换 API（供 C# 调用）：只换 class，CSS transition 做 450ms 缓动，不重载页面
  window.setTheme=function(t){ if(t==='dark'||t==='light') document.documentElement.className=t;
    var hd=document.getElementById('hljs-dark'); if(hd&&hd.sheet) hd.sheet.disabled=(t!=='dark'); };
  // 大纲跳转 API（供 C# 调用）
  window.scrollToAnchor=function(id){
    var el=document.getElementById(decodeURIComponent(id));
    if(el){ el.scrollIntoView({behavior:'smooth',block:'start'}); return true; }
    return false;
  };
  /* ============ Mermaid 图表放大预览（灯箱）：点击图表进入，滚轮缩放/拖拽平移/Esc 关闭 ============ */
  window.__mzAttach=function(){
    document.querySelectorAll('pre.mermaid,div.mermaid').forEach(function(host){
      if(host.dataset.mzReady || !host.querySelector('svg')) return;
      host.dataset.mzReady='1';
      host.style.cursor='zoom-in';
      var badge=document.createElement('button');
      badge.className='mz-badge'; badge.type='button'; badge.textContent='⤢ 放大';
      badge.onclick=function(ev){ ev.stopPropagation(); window.openMermaidZoom(host); };
      host.appendChild(badge);
      host.addEventListener('click',function(){ window.openMermaidZoom(host); });
    });
  };
  window.openMermaidZoom=function(host){
    var svg=host.querySelector('svg');
    if(!svg) return;
    var ov=document.getElementById('mzOverlay');
    if(!ov){
      ov=document.createElement('div'); ov.id='mzOverlay'; ov.className='mermaid-zoom-overlay';
      ov.innerHTML='<div class="mz-panel" role="dialog" aria-label="图表预览">'
        +'<div class="mz-toolbar"><span class="mz-title">图表预览</span>'
        +'<span class="mz-pct">100%</span>'
        +'<button data-z="out" title="缩小">－</button>'
        +'<button data-z="reset" title="实际大小">1:1</button>'
        +'<button data-z="fit" title="适应宽度">适应</button>'
        +'<button data-z="in" title="放大">＋</button>'
        +'<button data-z="close" title="关闭 (Esc)">✕</button></div>'
        +'<div class="mz-stage"><div class="mz-canvas"></div></div>'
        +'<div class="mz-hint">滚轮缩放 · 按住拖拽平移 · Esc 关闭</div></div>';
      document.body.appendChild(ov);
      var st={scale:1,baseW:800,baseH:600};
      ov._st=st;
      function stage(){ return ov.querySelector('.mz-stage'); }
      function mzCv(){ return ov.querySelector('.mz-canvas'); }
      function label(){ ov.querySelector('.mz-pct').textContent=Math.round(st.scale*100)+'%'; }
      function apply(ns,cx,cy){
        ns=Math.min(8,Math.max(0.15,ns));
        var sg=stage(), sgRect=sg.getBoundingClientRect();
        if(cx===undefined){ cx=sgRect.left+sgRect.width/2; cy=sgRect.top+sgRect.height/2; }
        var px=sg.scrollLeft+(cx-sgRect.left), py=sg.scrollTop+(cy-sgRect.top);
        var k=ns/st.scale; st.scale=ns;
        var cv=mzCv(); cv.style.width=(st.baseW*ns)+'px'; cv.style.height=(st.baseH*ns)+'px';
        sg.scrollLeft=px*k-(cx-sgRect.left); sg.scrollTop=py*k-(cy-sgRect.top);
        var cvSvg=cv.querySelector('svg'); if(cvSvg){ cvSvg.style.width='100%'; cvSvg.style.height='auto'; }
        label();
      }
      ov._zoom=function(d,cx,cy){ apply(st.scale*d,cx,cy); };
      ov._set=function(ns){ apply(ns); };
      ov._fit=function(){
        var sg=stage(); apply(Math.min(1,(sg.clientWidth-48)/st.baseW));
        sg.scrollLeft=0; sg.scrollTop=0;
      };
      ov.querySelectorAll('.mz-toolbar button').forEach(function(b){
        b.onclick=function(){
          var z=b.getAttribute('data-z');
          if(z==='in') ov._zoom(1.25);
          else if(z==='out') ov._zoom(1/1.25);
          else if(z==='reset') ov._set(1);
          else if(z==='fit') ov._fit();
          else if(z==='close') window.closeMermaidZoom();
        };
      });
      ov.addEventListener('click',function(e){ if(e.target===ov) window.closeMermaidZoom(); });
      document.addEventListener('keydown',function(e){
        if(e.key==='Escape'&&ov.style.display==='flex') window.closeMermaidZoom();
      });
      var sgg=stage();
      sgg.addEventListener('wheel',function(e){
        e.preventDefault();
        ov._zoom(e.deltaY<0?1.15:1/1.15,e.clientX,e.clientY);
      },{passive:false});
      var drag=null;
      sgg.addEventListener('mousedown',function(e){
        if(e.target.closest('.mz-toolbar')) return;
        drag={x:e.clientX,y:e.clientY,l:sgg.scrollLeft,t:sgg.scrollTop};
        sgg.classList.add('grabbing');
      });
      window.addEventListener('mouseup',function(){ drag=null; sgg.classList.remove('grabbing'); });
      window.addEventListener('mousemove',function(e){
        if(!drag) return;
        sgg.scrollLeft=drag.l-(e.clientX-drag.x);
        sgg.scrollTop=drag.t-(e.clientY-drag.y);
      });
    }
    var st=ov._st, canvas=ov.querySelector('.mz-canvas');
    /* 整体搬运宿主内容（含 svg 及 mermaid 生成的同级 <style>），样式作用域保持完整；
       仅去掉放大钮，避免嵌套 */
    canvas.innerHTML=host.innerHTML;
    var badge=canvas.querySelector('.mz-badge'); if(badge) badge.remove();
    var bw=800,bh=600;
    try{
      if(svg.viewBox&&svg.viewBox.baseVal&&svg.viewBox.baseVal.width>0){ bw=svg.viewBox.baseVal.width; bh=svg.viewBox.baseVal.height; }
      else { var r=svg.getBoundingClientRect(); if(r.width>0){ bw=r.width; bh=r.height; } }
    }catch(e){}
    st.baseW=bw; st.baseH=bh;
    ov.style.display='flex';
    document.body.style.overflow='hidden';
    ov._fit();
  };
  window.closeMermaidZoom=function(){
    var ov=document.getElementById('mzOverlay');
    if(!ov) return;
    ov.style.display='none';
    document.body.style.overflow='';
  };
  setTimeout(function(){ window.__mzAttach&&window.__mzAttach(); },2500);
})();
</script>
</body>
</html>
""";
    }

    public static string BuildEmptyHtml(bool isDark, string message, string hint)
    {
        string css = ThemeCss(15);
        string cls = isDark ? "dark" : "light";
        return $$"""
<!DOCTYPE html><html lang="zh-CN" class="{{cls}}"><head><meta charset="utf-8"><style>{{css}}</style></head>
<body><div class="empty">
<div class="empty-icon">📖</div>
<div class="empty-title">{{WebUtility.HtmlEncode(message)}}</div>
<div class="empty-hint">{{WebUtility.HtmlEncode(hint)}}</div>
</div></body></html>
""";
    }

    private static string EnsureTrailingSlash(string dir)
    {
        if (!dir.EndsWith(Path.DirectorySeparatorChar) && !dir.EndsWith(Path.AltDirectorySeparatorChar))
            dir += Path.DirectorySeparatorChar;
        return dir;
    }

    /// <summary>
    /// 本地相对图片 → base64 data URI 内嵌。
    /// 原因：WebView2 的 NavigateToString 起源为 about:blank，直接引用 file:// 子资源
    /// 可能被拦截；内嵌后无论 exe 放到哪里、文档带到哪里，图片都能稳定显示。
    /// 仅处理 &lt; 3MB 的常见位图；超大图片保留原路径（由 &lt;base&gt; 兜底）。
    /// </summary>
    private static string EmbedLocalImages(string htmlBody, string fileDir)
    {
        if (string.IsNullOrEmpty(htmlBody) || string.IsNullOrWhiteSpace(fileDir)) return htmlBody;
        return Regex.Replace(htmlBody, @"<img([^>]*?)\ssrc=""([^""]+)""",
            m =>
            {
                string attrs = m.Groups[1].Value;
                string src = m.Groups[2].Value;
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith('#'))
                    return m.Value;
                try
                {
                    string clean = Uri.UnescapeDataString(src.Split('?')[0].Split('#')[0].Replace('/', Path.DirectorySeparatorChar));
                    if (Path.IsPathRooted(clean)) return m.Value; // 绝对路径保持原样（base 可解析）
                    string abs = Path.GetFullPath(Path.Combine(fileDir, clean));
                    if (!File.Exists(abs)) return m.Value;
                    string ext = Path.GetExtension(abs).ToLowerInvariant();
                    string? mime = ext switch
                    {
                        ".png" => "image/png",
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".bmp" => "image/bmp",
                        ".webp" => "image/webp",
                        ".svg" => "image/svg+xml",
                        ".ico" => "image/x-icon",
                        _ => null,
                    };
                    if (mime is null) return m.Value;
                    var fi = new FileInfo(abs);
                    if (fi.Length > 3 * 1024 * 1024) return m.Value;
                    string b64 = Convert.ToBase64String(File.ReadAllBytes(abs));
                    return $"<img{attrs} src=\"data:{mime};base64,{b64}\" data-orig-src=\"{WebUtility.HtmlEncode(src)}\"";
                }
                catch { return m.Value; }
            }, RegexOptions.IgnoreCase);
    }

    // —— 配色：CSS 变量承载双主题（浅色对标 GitHub Light / 深色对标 GitHub Dark）。
    // 切换主题只换 <html> 的 class，靠 transition 做 450ms 缓动（与 WPF 边框动画同步），不重载页面。
    private static string ThemeCss(double fontSize)
        => ThemeVars() + SharedCss((int)Math.Round(fontSize));

    private static string ThemeVars() => """
html.light{
  --bg:#ffffff; --page:#ffffff; --text:#1f2328; --muted:#59636e;
  --border:#d0d7de; --codebg:#f6f8fa; --prebg:#f6f8fa; --accent:#0969da;
  --qborder:#0969da; --stripe:#f6f8fa; --hborder:#d8dee4;
  --icode:#cf222e; --icodebg:rgba(175,184,193,.2);
  --btnbg:#f6f8fa; --btnborder:#d0d7de; color-scheme:light;
  --al-note:#0969da; --al-note-bg:rgba(9,105,218,.08);
  --al-tip:#1a7f37; --al-tip-bg:rgba(26,127,55,.09);
  --al-imp:#8250df; --al-imp-bg:rgba(130,80,223,.09);
  --al-warn:#9a6700; --al-warn-bg:rgba(154,103,0,.10);
  --al-cau:#cf222e; --al-cau-bg:rgba(207,34,46,.08);
}
html.dark{
  --bg:#12161d; --page:#12161d; --text:#d6dde6; --muted:#8d96a0;
  --border:#2c333b; --codebg:#191f29; --prebg:#191f29; --accent:#5f9cf5;
  --qborder:#5f9cf5; --stripe:rgba(110,118,129,.12); --hborder:#232a35;
  --icode:#8fbdf5; --icodebg:rgba(110,118,129,.35);
  --btnbg:#232a35; --btnborder:#2c333b; color-scheme:dark;
  --al-note:#5f9cf5; --al-note-bg:rgba(95,156,245,.13);
  --al-tip:#3fb950; --al-tip-bg:rgba(63,185,80,.13);
  --al-imp:#b083f0; --al-imp-bg:rgba(176,131,240,.13);
  --al-warn:#d4a72c; --al-warn-bg:rgba(212,167,44,.13);
  --al-cau:#f47067; --al-cau-bg:rgba(244,112,103,.12);
}
""";

    private static string SharedCss(int px) => $$"""
*{box-sizing:border-box;}
html{background:var(--bg);}
body{
  margin:0; background:var(--bg); color:var(--text);
  font-family:'Segoe UI','Microsoft YaHei','PingFang SC',-apple-system,BlinkMacSystemFont,Helvetica,Arial,sans-serif;
  font-size:{{px}}px; line-height:1.75;
}
.page{max-width:1080px; margin:0 auto; padding:28px 40px 80px; background:var(--page);}
.markdown-body{word-wrap:break-word;}
/* 主题缓动：内容区所有配色过渡 450ms，与 WPF 边框渐变同步 */
body,.page,.markdown-body,.markdown-body *,.img-broken,.empty,.empty *,#toTop,.toc,.toc *, .alert,.alert *{
  transition:background-color .45s ease,color .45s ease,border-color .45s ease,box-shadow .45s ease;
}
/* 目录与告示 */
.toc{border:1px solid var(--border); border-radius:10px; padding:12px 18px; margin:1.2em 0; background:var(--codebg);}
.toc-title{font-weight:650; margin:0 0 .4em;}
.toc-empty{color:var(--muted); margin:0;}
.toc-list{list-style:none; padding:0; margin:0;}
.toc-list li{margin:.28em 0;}
.toc-list a{color:var(--text); text-decoration:none;}
.toc-list a:hover{color:var(--accent); text-decoration:underline;}
.toc-l2{margin-left:1.2em;} .toc-l3{margin-left:2.4em;} .toc-l4,.toc-l5,.toc-l6{margin-left:3.6em;}
.alert{border-left:4px solid var(--border); border-radius:0 8px 8px 0; padding:.6em 1em; margin:1.1em 0;}
.alert-title{font-weight:650; margin:0 0 .3em !important;}
.alert p:last-child{margin-bottom:.3em;}
.alert-note{border-color:var(--al-note); background:var(--al-note-bg);}
.alert-note .alert-title{color:var(--al-note);}
.alert-tip{border-color:var(--al-tip); background:var(--al-tip-bg);}
.alert-tip .alert-title{color:var(--al-tip);}
.alert-important{border-color:var(--al-imp); background:var(--al-imp-bg);}
.alert-important .alert-title{color:var(--al-imp);}
.alert-warning{border-color:var(--al-warn); background:var(--al-warn-bg);}
.alert-warning .alert-title{color:var(--al-warn);}
.alert-caution{border-color:var(--al-cau); background:var(--al-cau-bg);}
.alert-caution .alert-title{color:var(--al-cau);}
.markdown-body h1,.markdown-body h2,.markdown-body h3,.markdown-body h4{
  margin:1.4em 0 .7em; font-weight:650; line-height:1.35; scroll-margin-top:16px;
}
.markdown-body h1{font-size:1.9em; padding-bottom:.35em; border-bottom:1px solid var(--hborder);}
.markdown-body h2{font-size:1.5em; padding-bottom:.3em; border-bottom:1px solid var(--hborder);}
.markdown-body h3{font-size:1.22em;}
.markdown-body h4{font-size:1.05em; color:var(--muted);}
.markdown-body p{margin:.8em 0;}
.markdown-body a{color:var(--accent); text-decoration:none;}
.markdown-body a:hover{text-decoration:underline;}
.markdown-body img{max-width:100%; border-radius:8px; box-shadow:0 1px 6px rgba(0,0,0,.18);}
.markdown-body hr{border:none; border-top:1px solid var(--border); margin:2em 0;}
.markdown-body blockquote{
  margin:1em 0; padding:.4em 1em; color:var(--muted);
  border-left:4px solid var(--qborder); background:var(--codebg); border-radius:0 8px 8px 0;
}
.markdown-body ul,.markdown-body ol{padding-left:1.8em; margin:.6em 0;}
.markdown-body li+li{margin-top:.25em;}
.markdown-body table{display:block; width:100%; overflow:auto; border-collapse:collapse; margin:1.2em 0; font-size:.94em;}
.markdown-body table th{font-weight:650; background:var(--codebg);}
.markdown-body table th,.markdown-body table td{padding:8px 14px; border:1px solid var(--border); white-space:normal; overflow-wrap:break-word; max-width:420px;}
.markdown-body table tr:nth-child(even) td{background:var(--stripe);}
.markdown-body code{
  font-family:Consolas,'Cascadia Code',SFMono-Regular,Menlo,'Microsoft YaHei Mono',monospace;
  font-size:.86em; color:var(--icode); background:var(--icodebg);
  padding:.15em .4em; border-radius:6px;
}
.markdown-body pre{
  position:relative; background:var(--prebg); border:1px solid var(--border); border-radius:10px;
  padding:16px; overflow:auto; line-height:1.6;
}
.markdown-body pre code{
  background:transparent; color:var(--text); padding:0; font-size:.88em; white-space:pre;
}
/* highlight.js 接管后的归一：底色/内边距沿用本站 pre（主题跟随），滚动只留 pre 一层 */
.markdown-body pre code.hljs{background:transparent; padding:0; overflow-x:visible;}
/* Mermaid 图表：居中自适应；渲染失败时退化为普通代码块样式 */
.markdown-body pre.mermaid{background:transparent; border:none; text-align:center; padding:8px 0;}
.markdown-body pre.mermaid svg{max-width:100%; height:auto;}
/* 图表放大预览（灯箱）：点击图表进入，悬停显示放大钮 */
.markdown-body pre.mermaid,.markdown-body div.mermaid{position:relative; cursor:zoom-in;}
.mz-badge{position:absolute; top:8px; right:8px; font-size:12px; cursor:zoom-in;
  background:var(--btnbg); color:var(--text); border:1px solid var(--btnborder);
  border-radius:6px; padding:3px 10px; opacity:0; transition:opacity .15s;}
.markdown-body pre.mermaid:hover .mz-badge,.markdown-body div.mermaid:hover .mz-badge{opacity:1;}
.mz-badge:hover{border-color:var(--accent); color:var(--accent);}
.mermaid-zoom-overlay{position:fixed; inset:0; z-index:9999; display:none;
  align-items:center; justify-content:center; background:rgba(0,0,0,.55);}
.mz-panel{background:var(--page); border:1px solid var(--border); border-radius:12px;
  width:min(1100px,94vw); height:min(760px,90vh); display:flex; flex-direction:column;
  overflow:hidden; box-shadow:0 12px 48px rgba(0,0,0,.35);}
.mz-toolbar{display:flex; align-items:center; gap:6px; padding:8px 12px;
  border-bottom:1px solid var(--border); background:var(--codebg);}
.mz-title{font-weight:650; margin-right:auto;}
.mz-pct{font-size:12px; color:var(--muted); min-width:44px; text-align:right;}
.mz-toolbar button{font-size:12.5px; cursor:pointer; background:var(--btnbg); color:var(--text);
  border:1px solid var(--btnborder); border-radius:6px; padding:3px 11px;}
.mz-toolbar button:hover{border-color:var(--accent); color:var(--accent);}
.mz-stage{flex:1; overflow:auto; cursor:grab; background:var(--bg);}
.mz-stage.grabbing{cursor:grabbing;}
.mz-canvas{transform-origin:0 0; margin:24px;}
.mz-canvas svg{max-width:none; height:auto; display:block;}
.mz-hint{text-align:center; font-size:12px; color:var(--muted); padding:6px; border-top:1px solid var(--border);}
@media print{
  .mermaid-zoom-overlay{display:none !important;}
}
/* 代码块外层：自身不滚动，复制按钮钉在这里，横滚/竖滚都保持右上角 */
.code-wrap{position:relative;}
.code-wrap > pre{margin:0;}
.copy-btn{
  position:absolute; top:8px; right:8px; z-index:2; font-size:12px; cursor:pointer;
  background:var(--btnbg); color:var(--text); border:1px solid var(--btnborder); border-radius:6px; padding:3px 10px; opacity:0;
  transition:opacity .15s,background-color .45s ease,color .45s ease,border-color .45s ease;
}
.code-wrap:hover .copy-btn{opacity:1;}
.copy-btn:hover{border-color:var(--accent); color:var(--accent);}
.img-broken{
  border:1px dashed var(--border); border-radius:8px; padding:18px; color:var(--muted);
  background:var(--codebg); font-size:.9em; margin:1em 0;
}
.markdown-body input[type=checkbox]{accent-color:var(--accent); width:15px; height:15px; vertical-align:-2px;}
.footnotes{font-size:.88em; color:var(--muted); border-top:1px solid var(--border); margin-top:2em; padding-top:1em;}
#toTop{
  position:fixed; right:22px; bottom:22px; display:none; width:38px; height:38px; border-radius:50%;
  border:1px solid var(--btnborder); background:var(--btnbg); color:var(--text); font-size:16px; cursor:pointer;
  box-shadow:0 2px 10px rgba(0,0,0,.2);
}
#toTop:hover{border-color:var(--accent); color:var(--accent);}
.empty{text-align:center; padding:12vh 20px; color:var(--muted);}
.empty-icon{font-size:64px; margin-bottom:12px;}
.empty-title{font-size:19px; font-weight:600; color:var(--text); margin-bottom:8px;}
.empty-hint{font-size:13.5px;}
::-webkit-scrollbar{width:10px; height:10px;}
::-webkit-scrollbar-thumb{background:var(--border); border-radius:6px; border:2px solid var(--bg);}
::-webkit-scrollbar-track{background:transparent;}
@media (max-width:640px){
  .page{padding:18px 16px 60px;}
}
@media print{
  #toTop,.copy-btn{display:none!important;}
  .page{max-width:none;}
}
""";
}
