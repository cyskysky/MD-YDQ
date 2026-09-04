using System.Windows;
using System.Windows.Media.Animation;

namespace MDReader.Services;

/// <summary>
/// 列宽补间动画：GridLength 本身不是可动画类型，用此类补足，实现目录栏平滑收放。
/// 背景：侧栏宽度瞬间在 300↔0 之间跳变时，内容区的 WebView2（独立原生窗口、
/// Chromium 合成器）需要重建渲染表面，会闪出 1~3 帧白屏；220ms 连续小步长过渡可避免。
/// </summary>
public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty EasingFunctionProperty =
        DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction
    {
        get => (IEasingFunction?)GetValue(EasingFunctionProperty);
        set => SetValue(EasingFunctionProperty, value);
    }

    public override Type TargetPropertyType => typeof(GridLength);

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

    public override object GetCurrentValue(
        object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double progress = animationClock.CurrentProgress ?? 0;
        if (EasingFunction is not null) progress = EasingFunction.Ease(progress);
        double from = From.IsAbsolute ? From.Value : ((GridLength)defaultOriginValue).Value;
        double to = To.IsAbsolute ? To.Value : ((GridLength)defaultDestinationValue).Value;
        return new GridLength(Math.Max(0, from + ((to - from) * progress)), GridUnitType.Pixel);
    }
}
