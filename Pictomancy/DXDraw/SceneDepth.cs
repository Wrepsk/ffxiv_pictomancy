using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

// Wrap the game's typeless depth stencil texture in a typed SRV
internal class SceneDepth : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();

    private IntPtr _cachedTexPtr;
    private ShaderResourceView? _srv;
    private string? _unavailableReason;

    internal ShaderResourceView? SRV => _srv;
    public SharpDX.Vector2 UvScale { get; private set; } = new(1, 1);

    public void Dispose()
    {
        DropSrv();
        RenderContext.Dispose();
    }

    private void DropSrv()
    {
        _srv?.Dispose();
        _srv = null;
        _cachedTexPtr = IntPtr.Zero;
        UvScale = new(1, 1);
    }

    private void SetUnavailable(string reason)
    {
        DropSrv();
        if (_unavailableReason != reason)
            PctService.Log.Warning($"[Pictomancy] SceneDepth.Update: {reason}; skipping world screens.");
        _unavailableReason = reason;
    }

    internal unsafe void Update()
    {
        var rtm = RenderTargetManager.Instance();
        var depthStencil = rtm != null ? rtm->DepthStencil : null;
        if (depthStencil == null || depthStencil->D3D11Texture2D == null)
        {
            SetUnavailable("scene depth source unavailable");
            return;
        }

        var texPtr = (IntPtr)depthStencil->D3D11Texture2D;
        if (texPtr != _cachedTexPtr)
        {
            using var tex = new Texture2D(texPtr);
            try
            {
                var description = tex.Description;
                if (!ScreenDepthPolicy.TryGetShaderFormat(description.Format, description.SampleDescription.Count, out var shaderFormat))
                {
                    SetUnavailable($"unsupported depth format {description.Format} / {description.SampleDescription.Count} samples");
                    return;
                }
                var srvDesc = new ShaderResourceViewDescription
                {
                    Format = shaderFormat,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                };
                srvDesc.Texture2D.MostDetailedMip = 0;
                srvDesc.Texture2D.MipLevels = 1;
                var nextSrv = new ShaderResourceView(RenderContext.Device, tex, srvDesc);
                DropSrv();
                _srv = nextSrv;
                _cachedTexPtr = texPtr;
                _unavailableReason = null;
            }
            catch
            {
                DropSrv();
                throw;
            }
            finally
            {
                // Zero the NativePointer before disposal so we don't Release a ref we don't own.
                tex.NativePointer = IntPtr.Zero;
            }
        }

        UvScale = new SharpDX.Vector2(
            depthStencil->AllocatedWidth > 0 ? (float)depthStencil->ActualWidth / depthStencil->AllocatedWidth : 1f,
            depthStencil->AllocatedHeight > 0 ? (float)depthStencil->ActualHeight / depthStencil->AllocatedHeight : 1f);
    }
}
