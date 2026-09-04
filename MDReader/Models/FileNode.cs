using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MDReader.Models;

/// <summary>
/// 左侧目录树节点：目录 / Markdown 文件。参考 VS Code Explorer 的层级模型。
/// </summary>
public sealed class FileNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isVisible = true;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public FileNode? Parent { get; set; }

    /// <summary>仅目录有意义：该目录（含子孙）下的 md 文件数。</summary>
    public int FileCount { get; set; }

    public ObservableCollection<FileNode> Children { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    /// <summary>搜索过滤时控制可见性。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// 图标字形（Segoe MDL2 Assets，与人字形指示器同字体，保证任何机器不 fallback 成奇怪表情）。
    /// 目录：E8B7 关闭 / E8B8 展开；文件：E7C3 文档页。
    /// </summary>
    public string Icon => IsDirectory ? (IsExpanded ? "\ue8b8" : "\ue8b7") : "\ue7c3";

    public string ToolTipText => FullPath;

    public void RefreshIcon() => OnPropertyChanged(nameof(Icon));

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>大纲（TOC）条目：从 Markdown 标题解析而来。</summary>
public sealed class OutlineItem
{
    public int Level { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Anchor { get; set; } = string.Empty;
    /// <summary>左侧缩进（Level 1 → 0，Level 2 → 16 ...）。</summary>
    public int Indent => Math.Max(0, (Level - 1) * 16);
}
