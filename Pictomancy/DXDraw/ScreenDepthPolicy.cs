using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace Pictomancy.DXDraw;

internal static class ScreenDepthPolicy
{
    internal static bool TryGetShaderFormat(Format resourceFormat, int sampleCount, out Format shaderFormat)
    {
        shaderFormat = resourceFormat switch
        {
            Format.R24G8_Typeless => Format.R24_UNorm_X8_Typeless,
            Format.R32_Typeless => Format.R32_Float,
            Format.R16_Typeless => Format.R16_UNorm,
            Format.R32G8X24_Typeless => Format.R32_Float_X8X24_Typeless,
            _ => Format.Unknown,
        };
        return sampleCount == 1 && shaderFormat != Format.Unknown;
    }

    internal static DepthStencilStateDescription CreateImageState(bool writeDepth)
    {
        var description = DepthStencilStateDescription.Default();
        description.IsDepthEnabled = true;
        description.DepthComparison = Comparison.GreaterEqual; // FFXIV uses reverse Z.
        description.DepthWriteMask = writeDepth ? DepthWriteMask.All : DepthWriteMask.Zero;
        description.IsStencilEnabled = true;
        description.StencilWriteMask = 0;
        description.StencilReadMask = 0xFF;
        description.FrontFace = new DepthStencilOperationDescription
        {
            Comparison = Comparison.Equal,
            FailOperation = StencilOperation.Keep,
            DepthFailOperation = StencilOperation.Keep,
            PassOperation = StencilOperation.Keep,
        };
        description.BackFace = description.FrontFace;
        return description;
    }
}
