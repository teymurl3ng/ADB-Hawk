namespace AdbView.NativeUi.Motion;

using System.Windows;
using System.Windows.Media.Animation;
using AdbView.Host;

#pragma warning disable CA1515 // XAML element instantiation requires a public type.
/// <summary>
/// The DESIGN §24 shared production easing — <c>cubic-bezier(0.55, 0.55, 0, 1)</c>, Fluent
/// "Existing Elements / Point to Point" — as a WPF easing function, so the storyboards in
/// <c>Themes/Controls.xaml</c> and the cards animate on the same curve the window animator uses.
/// The math is not duplicated: this delegates to <see cref="Easing.FluentPointToPoint"/>, so the
/// XAML and the code animator cannot drift (decision 0023).
///
/// <para>The frozen curve is one asymmetric curve, not an ease-in/ease-out pair, so the class
/// fixes <see cref="EasingFunctionBase.EasingMode"/> to <c>EaseIn</c> in its constructor — the mode
/// under which WPF's composition returns <c>EaseInCore(t)</c> unchanged. Left at the framework
/// default (<c>EaseOut</c>) the curve would be mirrored and the fidelity lost. A test pins the
/// composed result against <see cref="Easing.FluentPointToPoint"/>.</para>
/// </summary>
public sealed class BezierEase : EasingFunctionBase {
    public BezierEase() {
        // WPF composes as: EaseIn → EaseInCore(t); EaseOut → 1 - EaseInCore(1 - t).
        EasingMode = EasingMode.EaseIn;
    }

    protected override double EaseInCore(double normalizedTime) => Easing.FluentPointToPoint(normalizedTime);

    protected override Freezable CreateInstanceCore() => new BezierEase();
}
