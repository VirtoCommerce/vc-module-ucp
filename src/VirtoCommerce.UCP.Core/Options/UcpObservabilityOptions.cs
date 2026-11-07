namespace VirtoCommerce.UCP.Core.Options;

public class UcpObservabilityOptions
{
    public UcpInputCaptureMode InputCaptureMode { get; set; } = UcpInputCaptureMode.ErrorsOnly;
}

public enum UcpInputCaptureMode
{
    None,
    ErrorsOnly,
    Always,
}
