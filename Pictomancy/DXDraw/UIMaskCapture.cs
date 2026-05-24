using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Runtime.InteropServices;
using System.Threading;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

internal unsafe class UIMaskCapture : IDisposable
{
    private const int OMSetRenderTargetsVTableIndex = 33;

    private delegate void OMSetRenderTargetsDelegate(nint deviceContext, uint numViews, nint* renderTargetViews, nint depthStencilView);

    private readonly RenderContext _ctx;
    private readonly Hook<OMSetRenderTargetsDelegate>? _hook;
    private readonly Action? _afterBackbufferDsvBind;
    private readonly Func<bool>? _allowDsvlessBackbufferCallback;

    private readonly VertexShader _vs;
    private readonly PixelShader  _ps;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;

    private Texture2D? _snapshot;
    private ShaderResourceView? _snapshotSRV;

    private Texture2D? _maskRT;
    private RenderTargetView? _maskRTV;
    private ShaderResourceView? _maskSRV;

    private Texture2D? _bbCopy;
    private ShaderResourceView? _bbCopySRV;
    private long _hookCallCount;
    private long _backbufferBindCount;
    private long _backbufferDsvBindCount;
    private long _dsvlessBackbufferCallbackCount;
    private long _lastHookUnixMs;
    private long _lastBackbufferBindUnixMs;
    private long _lastBackbufferDsvBindUnixMs;

    public ShaderResourceView? MaskSRV => _maskSRV;
    public bool HasSnapshot => _snapshot != null;
    public bool IsHookInstalled => _hook != null;
    public long HookCallCount => Interlocked.Read(ref _hookCallCount);
    public long BackbufferBindCount => Interlocked.Read(ref _backbufferBindCount);
    public long BackbufferDsvBindCount => Interlocked.Read(ref _backbufferDsvBindCount);
    public long DsvlessBackbufferCallbackCount => Interlocked.Read(ref _dsvlessBackbufferCallbackCount);
    public long LastHookUnixMs => Interlocked.Read(ref _lastHookUnixMs);
    public long LastBackbufferBindUnixMs => Interlocked.Read(ref _lastBackbufferBindUnixMs);
    public long LastBackbufferDsvBindUnixMs => Interlocked.Read(ref _lastBackbufferDsvBindUnixMs);

    // Used to snapshot once at the first DSV-backed swapchain backbuffer bind for the frame.
    private bool _capturedThisFrame;

    public void BeginFrame()
    {
        _capturedThisFrame = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Thresholds
    {
        public float Low;
        public float High;
        public float OpaqueAlphaCutoff;
        public float StrongRgbThreshold;
    }

    public UIMaskCapture(
        RenderContext ctx,
        IGameInteropProvider hookProvider,
        Action? afterBackbufferDsvBind = null,
        Func<bool>? allowDsvlessBackbufferCallback = null)
    {
        _ctx = ctx;
        _afterBackbufferDsvBind = afterBackbufferDsvBind;
        _allowDsvlessBackbufferCallback = allowDsvlessBackbufferCallback;

        const string shaderSource = """
            Texture2D    backBuffer     : register(t0);
            Texture2D    backBufferNoUI : register(t1);
            SamplerState samplerState   : register(s0);

            cbuffer Thresholds : register(b0)
            {
                float thresholdLow;
                float thresholdHigh;
                float opaqueAlphaCutoff;
                float strongRgbThreshold;
            };

            struct VSOutput
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD;
            };

            VSOutput vs(uint id : SV_VertexID)
            {
                VSOutput output;
        	    float2 uv = float2((id << 1) & 2, id & 2);
        	    output.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                output.uv = uv;
                return output;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 withUI = backBuffer.Sample(samplerState, input.uv);
                float4 noUI = backBufferNoUI.Sample(samplerState, input.uv);

                if (withUI.a >= opaqueAlphaCutoff && noUI.a < opaqueAlphaCutoff)
                    return float4(0, 0, 0, 1);

                float3 rgbDiff = abs(withUI.rgb - noUI.rgb);
                float maxRgbDiff = max(rgbDiff.r, max(rgbDiff.g, rgbDiff.b));

                if (maxRgbDiff >= strongRgbThreshold)
                    return float4(0, 0, 0, 1);

                float alpha = smoothstep(thresholdLow, thresholdHigh, maxRgbDiff);
                return float4(0, 0, 0, alpha);
            }
        """;

        var vs = ShaderBytecode.Compile(shaderSource, "vs", "vs_5_0");
        PctService.Log.Debug($"UIMaskCapture VS compile: {vs.Message}");
        _vs = new(_ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shaderSource, "ps", "ps_5_0");
        PctService.Log.Debug($"UIMaskCapture PS compile: {ps.Message}");
        _ps = new(_ctx.Device, ps.Bytecode);

        _constantBuffer = new(_ctx.Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        // TY Glyceri for hooking info
        try
        {
            nint contextPtr = _ctx.Device.ImmediateContext.NativePointer;
            nint vtable = Marshal.ReadIntPtr(contextPtr);
            nint omSetPtr = Marshal.ReadIntPtr(vtable, 33 * nint.Size);
            _hook = hookProvider.HookFromAddress<OMSetRenderTargetsDelegate>(omSetPtr, OMSetRenderTargetsDetour);
            _hook.Enable();
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] UIMaskCapture: failed to install OMSetRenderTargets hook.");
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _vs.Dispose();
        _ps.Dispose();
        _constantBuffer.Dispose();
        DisposeSnapshot();
        DisposeMask();
        DisposeBackBufferCopy();
    }

    private void DisposeSnapshot()
    {
        _snapshotSRV?.Dispose();
        _snapshot?.Dispose();
        _snapshotSRV = null;
        _snapshot = null;
    }

    private void DisposeMask()
    {
        _maskSRV?.Dispose();
        _maskRTV?.Dispose();
        _maskRT?.Dispose();
        _maskSRV = null;
        _maskRTV = null;
        _maskRT = null;
    }

    private void DisposeBackBufferCopy()
    {
        _bbCopySRV?.Dispose();
        _bbCopy?.Dispose();
        _bbCopySRV = null;
        _bbCopy = null;
    }

    private void OMSetRenderTargetsDetour(nint deviceContext, uint numViews, nint* rtvs, nint dsv)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Increment(ref _hookCallCount);
        Interlocked.Exchange(ref _lastHookUnixMs, now);

        nint matchedBackbufferResource = nint.Zero;
        bool isBackbufferBind = false;
        bool isBackbufferDsvBind = false;
        bool useDsvlessBackbufferCallback = false;
        try
        {
            isBackbufferBind = TryGetSwapChainBackBufferResource(numViews, rtvs, out matchedBackbufferResource);
            if (isBackbufferBind)
            {
                Interlocked.Increment(ref _backbufferBindCount);
                Interlocked.Exchange(ref _lastBackbufferBindUnixMs, now);

                if (IsResolutionScaled() || dsv != nint.Zero)
                {
                    if (!_capturedThisFrame)
                        CapturePreBindSnapshot(matchedBackbufferResource);

                    isBackbufferDsvBind = true;
                    Interlocked.Increment(ref _backbufferDsvBindCount);
                    Interlocked.Exchange(ref _lastBackbufferDsvBindUnixMs, now);
                }
                else if (_allowDsvlessBackbufferCallback?.Invoke() == true)
                {
                    useDsvlessBackbufferCallback = true;
                }
            }
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] UIMaskCapture: pre-bind capture failed");
        }
        finally
        {
            ReleaseCom(matchedBackbufferResource);
        }

        _hook!.Original(deviceContext, numViews, rtvs, dsv);

        if (isBackbufferDsvBind || useDsvlessBackbufferCallback)
        {
            try
            {
                if (useDsvlessBackbufferCallback)
                    Interlocked.Increment(ref _dsvlessBackbufferCallbackCount);

                _afterBackbufferDsvBind?.Invoke();
            }
            catch (Exception e)
            {
                PctService.Log.Error(e, "[Pictomancy] UIMaskCapture: post-bind callback failed");
            }
        }
    }

    private unsafe bool IsMatch(nint resource, nint targetD3D11)
    {
        if (resource == targetD3D11) return true;

        Guid iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        nint resVtable = *(nint*)resource;
        var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, out nint, int>)*(nint*)(resVtable + 0);
        
        if (queryInterface(resource, &iid, out nint resTex) == 0)
        {
            nint texVtable = *(nint*)resTex;
            var getDesc = (delegate* unmanaged[Stdcall]<nint, out Texture2DDescription, void>)*(nint*)(texVtable + 10 * sizeof(nint));
            
            Texture2DDescription resDesc;
            getDesc(resTex, out resDesc);
            
            var releaseTex = (delegate* unmanaged[Stdcall]<nint, uint>)*(nint*)(texVtable + 2 * sizeof(nint));
            releaseTex(resTex);

            nint targetVtable = *(nint*)targetD3D11;
            var queryTarget = (delegate* unmanaged[Stdcall]<nint, Guid*, out nint, int>)*(nint*)(targetVtable + 0);
            
            if (queryTarget(targetD3D11, &iid, out nint targetTex) == 0)
            {
                nint tgtTexVtable = *(nint*)targetTex;
                var getTargetDesc = (delegate* unmanaged[Stdcall]<nint, out Texture2DDescription, void>)*(nint*)(tgtTexVtable + 10 * sizeof(nint));
                
                Texture2DDescription tgtDesc;
                getTargetDesc(targetTex, out tgtDesc);
                
                var releaseTgtTex = (delegate* unmanaged[Stdcall]<nint, uint>)*(nint*)(tgtTexVtable + 2 * sizeof(nint));
                releaseTgtTex(targetTex);

                if (resDesc.Width == tgtDesc.Width &&
                    resDesc.Height == tgtDesc.Height &&
                    resDesc.Format == tgtDesc.Format)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private unsafe bool TryGetSwapChainBackBufferResource(uint numViews, nint* rtvs, out nint matchedResource)
    {
        matchedResource = nint.Zero;
        if (numViews == 0) return false;

        var device = Device.Instance();
        if (device == null
            || device->SwapChain == null
            || device->SwapChain->BackBuffer == null
            || device->SwapChain->BackBuffer->D3D11Texture2D == null)
        {
            return false;
        }

        nint targetD3D11 = (nint)device->SwapChain->BackBuffer->D3D11Texture2D;
        if (targetD3D11 == nint.Zero) return false;

        for (uint i = 0; i < numViews; i++)
        {
            nint rtv = rtvs[i];
            if (rtv == nint.Zero) continue;

            nint vtable = *(nint*)rtv;
            var getResource = (delegate* unmanaged[Stdcall]<nint, out nint, void>)*(nint*)(vtable + 7 * sizeof(nint));
            getResource(rtv, out nint resource);

            if (resource != nint.Zero)
            {
                bool isTarget;
                try
                {
                    isTarget = IsMatch(resource, targetD3D11);
                }
                catch
                {
                    ReleaseCom(resource);
                    throw;
                }

                if (isTarget)
                {
                    matchedResource = resource;
                    return true;
                }

                ReleaseCom(resource);
            }
        }

        return false;
    }

    private unsafe static bool IsResolutionScaled()
    {
        var device = Device.Instance();
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        return device != null
            && rtm != null
            && rtm->DepthStencil != null
            && (rtm->DepthStencil->ActualWidth != device->Width || rtm->DepthStencil->ActualHeight != device->Height);
    }

    private void CapturePreBindSnapshot(nint backbufferResource)
    {
        EnsureSnapshot(backbufferResource);

        var src = new Texture2D(backbufferResource);
        _ctx.Device.ImmediateContext.CopyResource(src, _snapshot);
        GC.SuppressFinalize(src);

        _capturedThisFrame = true;
    }

    private unsafe static void ReleaseCom(nint resource)
    {
        if (resource == nint.Zero)
            return;

        nint vtable = *(nint*)resource;
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)*(nint*)(vtable + 2 * sizeof(nint));
        release(resource);
    }

    private void EnsureSnapshot(nint deviceBackBufferD3D11)
    {
        var src  = new Texture2D(deviceBackBufferD3D11);
        var desc = src.Description;
        GC.SuppressFinalize(src);

        if (_snapshot != null
            && _snapshot.Description.Width  == desc.Width
            && _snapshot.Description.Height == desc.Height
            && _snapshot.Description.Format == desc.Format)
        {
            return;
        }

        DisposeSnapshot();

        var snapDesc = desc;
        snapDesc.BindFlags      = BindFlags.ShaderResource;
        snapDesc.CpuAccessFlags = CpuAccessFlags.None;
        snapDesc.OptionFlags    = ResourceOptionFlags.None;
        snapDesc.Usage          = ResourceUsage.Default;
        snapDesc.Format         = snapDesc.Format.ToUNorm();

        _snapshot = new(_ctx.Device, snapDesc);
        _snapshotSRV = new(_ctx.Device, _snapshot);
    }

    private void EnsureMask(int width, int height)
    {
        if (_maskRT != null
            && _maskRT.Description.Width  == width
            && _maskRT.Description.Height == height)
        {
            return;
        }

        DisposeMask();

        var desc = new Texture2DDescription
        {
            Width             = width,
            Height            = height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags    = CpuAccessFlags.None,
            OptionFlags       = ResourceOptionFlags.None,
        };

        _maskRT  = new(_ctx.Device, desc);
        _maskRTV = new(_ctx.Device, _maskRT);
        _maskSRV = new(_ctx.Device, _maskRT);
    }

    private void EnsureBackBufferCopy(Texture2DDescription srcDesc)
    {
        if (_bbCopy != null
            && _bbCopy.Description.Width == srcDesc.Width
            && _bbCopy.Description.Height == srcDesc.Height
            && _bbCopy.Description.Format == srcDesc.Format)
        {
            return;
        }

        DisposeBackBufferCopy();

        var copyDesc = srcDesc;
        copyDesc.BindFlags = BindFlags.ShaderResource;
        copyDesc.CpuAccessFlags = CpuAccessFlags.None;
        copyDesc.OptionFlags = ResourceOptionFlags.None;
        copyDesc.Usage = ResourceUsage.Default;
        copyDesc.Format = copyDesc.Format.ToUNorm();

        _bbCopy = new(_ctx.Device, copyDesc);
        _bbCopySRV = new(_ctx.Device, _bbCopy);
    }

    public void BuildMask(Texture2D currentBackBuffer, float thresholdLow = 0.002f, float thresholdHigh = 0.20f, float opaqueAlphaCutoff = 0.999f, float strongRgbThreshold = 0.30f)
    {
        if (_snapshot == null || _snapshotSRV == null)
        {
            return;
        }

        var bbDesc = currentBackBuffer.Description;
        if (_snapshot.Description.Width != bbDesc.Width || _snapshot.Description.Height != bbDesc.Height)
        {
            return;
        }

        EnsureBackBufferCopy(bbDesc);
        EnsureMask(bbDesc.Width, bbDesc.Height);
        _ctx.Context.CopyResource(currentBackBuffer, _bbCopy);

        var thresholds = new Thresholds
        {
            Low = thresholdLow,
            High = thresholdHigh,
            OpaqueAlphaCutoff = opaqueAlphaCutoff,
            StrongRgbThreshold = strongRgbThreshold,
        };
        _ctx.Context.UpdateSubresource(ref thresholds, _constantBuffer);

        _ctx.Context.ClearRenderTargetView(_maskRTV, new());
        _ctx.Context.OutputMerger.SetTargets(_maskRTV);
        _ctx.Context.Rasterizer.SetViewport(0, 0, bbDesc.Width, bbDesc.Height);
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.SetShaderResource(0, _bbCopySRV);
        _ctx.Context.PixelShader.SetShaderResource(1, _snapshotSRV);

        _ctx.Context.Draw(3, 0);

        _ctx.Context.PixelShader.SetShaderResource(0, null);
        _ctx.Context.PixelShader.SetShaderResource(1, null);
    }

}
