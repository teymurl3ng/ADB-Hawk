namespace AdbView.Bridge;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReadyAction), "ready")]
[JsonDerivedType(typeof(PinAction), "pin")]
[JsonDerivedType(typeof(UnpinAction), "unpin")]
[JsonDerivedType(typeof(RequestDismissAction), "requestDismiss")]
[JsonDerivedType(typeof(ReportContentSizeAction), "reportContentSize")]
[JsonDerivedType(typeof(PairStartAction), "pairStart")]
[JsonDerivedType(typeof(PairSubmitAction), "pairSubmit")]
[JsonDerivedType(typeof(PairCancelAction), "pairCancel")]
internal abstract record WebAction;

internal sealed record ReadyAction : WebAction {
    public required int Protocol { get; init; }
}

internal sealed record PinAction : WebAction;

internal sealed record UnpinAction : WebAction;

internal sealed record RequestDismissAction : WebAction;

internal sealed record ReportContentSizeAction : WebAction {
    public required double HeightEffectivePx { get; init; }

    /// <summary>
    /// Optional. Omit / null / "live" = immediate; "structural" = native 333 ms;
    /// "reconcile" = post-animation truth. Additive — not a protocol bump.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Optional structural layout identity. Unchanged key suppresses live HWND churn.
    /// </summary>
    public string? LayoutKey { get; init; }
}

internal sealed record PairStartAction : WebAction {
    public required string OfferId { get; init; }
}

internal sealed record PairSubmitAction : WebAction {
    public required string OfferId { get; init; }
    public required string Code { get; init; }
}

internal sealed record PairCancelAction : WebAction {
    public required string OfferId { get; init; }
}
