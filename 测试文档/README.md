# 欢迎使用 MD阅读器 📖

这是一篇**功能演示文档**，用于验证阅读器的 Markdown 渲染能力。

## 目录大纲演示

### 三级标题示例

#### 四级标题示例

> 💡 左侧点击 **📑 大纲** 按钮，可以看到由上面这些标题自动生成的大纲，点击任意条目即可平滑滚动定位。

## 表格

| 功能 | 状态 | 说明 |
| ---- | ---- | ---- |
| 层级目录树 | ✅ | 自动扫描子目录 |
| 日夜主题 | ✅ | 一键切换 |
| 大纲导航 | ✅ | H1–H4 自动提取 |
| 本地图片 | ✅ | 自动内嵌显示 |

## 任务列表

- [x] 自动读取当前目录的 md 文件
- [x] 支持白天 / 黑夜主题
- [ ] 导出 PDF（规划中）

## 代码块（悬停右上角可复制）

```csharp
// 层级目录构建：仅保留含 md 的分支，空文件夹自动剪枝
var files = Directory.EnumerateFiles(root, "*.md", new EnumerationOptions
{
    RecurseSubdirectories = true,
    IgnoreInaccessible = true,
});
```

```python
print("Hello, MD阅读器!")
```

行内代码如 `FileSystemWatcher`、`Markdig` 也有 GitHub 风格的高亮。

## 本地图片

下图是相对路径引用的本地图片，阅读器会自动内嵌显示（复制文档到别处也不怕丢图）：

![演示图片](images/demo.png)

## 脚注与 Emoji

脚注示例[^1]，Emoji 示例：:+1: :tada: :rocket:

[^1]: 这是一条脚注：离线渲染，无需联网。

## 外部链接

- [Markdig 官方文档](https://github.com/xoofx/markdown)
- [GitHub 官方文档](https://docs.github.com)

> 🔗 点击外部链接会自动用系统默认浏览器打开，不会在阅读器内跳转。

---

**祝阅读愉快！** 把 `MD阅读器.exe` 放到任何目录，双击即可阅读该目录及子目录的全部 Markdown 文档。
