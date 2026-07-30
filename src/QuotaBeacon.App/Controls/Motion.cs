using System.Windows;
using System.Windows.Media.Animation;

namespace QuotaBeacon.App.Controls;

/// <summary>
/// Shared motion vocabulary, so every animated element in the popup moves on the same curve.
/// </summary>
/// <remarks>
/// Motion is suppressed when the system reports that client-area animation is off, which is how
/// Windows surfaces the reduced-motion preference. Callers that skip an animation must still land on
/// the final value, so <see cref="ShouldAnimate"/> gates the animation rather than the assignment.
/// </remarks>
internal static class Motion
{
    /// <summary>Duration for a value settling into place: long enough to read, short enough to feel instant.</summary>
    public static readonly Duration Value = new(TimeSpan.FromMilliseconds(420));

    /// <summary>Duration for a view swapping in.</summary>
    public static readonly Duration Transition = new(TimeSpan.FromMilliseconds(180));

    public static bool ShouldAnimate => SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Decelerating ease. Everything in the popup is a value arriving, so nothing accelerates away.
    /// </summary>
    public static IEasingFunction Ease { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation To(double target, Duration duration) => new(target, duration)
    {
        EasingFunction = Ease,
        // The animation is a presentation detail; the underlying property keeps the real value.
        FillBehavior = FillBehavior.HoldEnd,
    };
}
