using Avalonia;

namespace ALyricEase.Infrastructure;

internal static class WindowsCompositionOptions
{
    // Leave backend selection to Avalonia unless a comparison run explicitly requests a mode.
    internal static AppBuilder WithCompositionSwapChainOverride(this AppBuilder builder) =>
        Environment.GetEnvironmentVariable("ALY_COMPOSITION_SWAP_CHAIN") switch
        {
            "1" => builder.With(new Win32PlatformOptions { UseCompositionSwapChain = true }),
            "0" => builder.With(new Win32PlatformOptions { UseCompositionSwapChain = false }),
            _ => builder,
        };
}
