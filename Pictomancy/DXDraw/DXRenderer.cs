using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.CompilerServices;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Pictomancy.DXDraw;

internal class DXRenderer : IDisposable
{
    private const long SceneCompositeStallWarningMilliseconds = 2000;

    internal readonly record struct FrameState(
        SharpDX.Matrix ViewProj,
        SharpDX.Vector2 ViewportSize,
        Vector2 ProjScale,
        Vector3 CameraPos);

    public RenderContext RenderContext { get; init; } = new();
    internal RenderTarget? RenderTarget { get; private set; }
    public TriFill TriFill { get; init; }
    public FanFill? FanFill { get; init; }
    public ProjectedFanFill? ProjectedFanFill { get; init; }
    public ProjectedTriFill? ProjectedTriFill { get; init; }
    public Stroke? Stroke { get; init; }
    public Sphere? Sphere { get; init; }
    public Image? Image { get; init; }
    public Sprite? Sprite { get; init; }
    public FullScreenPass FSP { get; init; }
    public ClipZone ClipZone { get; init; }
    public UIMaskCapture? UIMaskCapture { get; private set; }

    private readonly DepthStencilState _clipZoneDSS;
    private readonly DepthStencilState _shapeDSS;
    private SceneDepth? _sceneCompositeDepth;
    private SceneInfo? _sceneCompositeInfo;
    private SceneNormal? _sceneCompositeNormal;
    private FrameState? _sceneCompositeFrameState;
    private PctDrawHints _sceneCompositeHints;
    private bool _sceneCompositePending;
    private bool _sceneCompositeFlushing;
    private long _sceneCompositeScheduledUnixMs;
    private long _lastSceneCompositeFlushUnixMs;
    private long _sceneCompositeScheduleCount;
    private long _sceneCompositeFlushCount;
    private bool _sceneCompositeHookUnavailableLogged;
    private bool _sceneCompositeStallLogged;

    public bool HasPendingSceneComposite
    {
        get
        {
            MaybeLogSceneCompositeStall();
            return _sceneCompositePending;
        }
    }

    internal bool IsSceneCompositeHookUnavailable => UIMaskCapture?.IsHookInstalled != true;

    internal string SceneCompositeStatus
    {
        get
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pending = _sceneCompositePending
                ? $"pending for {Math.Max(0, now - _sceneCompositeScheduledUnixMs)} ms"
                : "not pending";
            var lastFlush = _lastSceneCompositeFlushUnixMs > 0
                ? $"{Math.Max(0, now - _lastSceneCompositeFlushUnixMs)} ms ago"
                : "never";
            return $"scene composite {pending}; scheduled {_sceneCompositeScheduleCount}; flushed {_sceneCompositeFlushCount}; last flush {lastFlush}; {BuildHookStatus(now)}";
        }
    }

    internal bool IsSceneCompositeStalled(TimeSpan maxPendingAge)
    {
        if (!_sceneCompositePending || _sceneCompositeScheduledUnixMs <= 0)
            return false;

        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sceneCompositeScheduledUnixMs;
        return age >= maxPendingAge.TotalMilliseconds;
    }

    // Runs of contiguous same-type projected Adds, in user submission order.
    // Used so projected objects of different types draw in the same order they are added.
    private enum ProjectionType
    {
        Default,
        Fan,
        Tri,
        Image,
    }
    private readonly List<(int Count, ProjectionType Type)> _projectedRuns = new();

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Vector2 ViewportSize { get; private set; }
    // Diagonal projection scale (M11, M22) from the active camera; used to convert pixel sizes to world units.
    public Vector2 ProjScale { get; private set; } = Vector2.One;
    public Vector3 CameraPos { get; private set; } = Vector3.Zero;

    public bool FanDegraded => FanFill == null;

    public bool StrokeDegraded => Stroke == null;

    public DXRenderer(PctOptions options)
    {
        try
        {
            // uncomment to test linux fanfill fallback renderer
            //throw new Exception("test exception please ignore");
            FanFill = new(RenderContext, options.MaxFans);
        }
        catch (Exception e)
        {
            PctService.Log.Error("[Pictomancy] Failed to compile fan shader; starting in degraded mode.");
        }
        try
        {
            ProjectedFanFill = new(RenderContext, options.MaxFans);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile projected fan shader; projection-mode fans will fall back to flat draw.");
        }
        try
        {
            ProjectedTriFill = new(RenderContext, Math.Max(1, options.MaxTriangleVertices / 3));
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile projected triangle shader; projection-mode triangles will fall back to flat draw.");
        }
        try
        {
            // uncomment to test linux imgui fallback renderer
            //throw new Exception("test exception please ignore");
            Stroke = new(RenderContext, options.MaxStrokeSegments);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile stroke shader; starting in degraded mode.");
        }
        try
        {
            Sphere = new(RenderContext, options.MaxSpheres);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile sphere shader; sphere draws will be skipped.");
        }
        try
        {
            Image = new(RenderContext, options.MaxImages);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile image shader; image draws will be skipped.");
        }
        try
        {
            Sprite = new(RenderContext, options.MaxSprites);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile sprite shader; sprite draws will be skipped.");
        }

        // TriFill's buffer doubles as the fan-fallback path's storage in degraded mode.
        TriFill = new(RenderContext, options.MaxTriangleVertices + (FanDegraded ? options.MaxFans * 360 : 0));

        FSP = new(RenderContext);
        ClipZone = new(RenderContext, options.MaxClipZones);

        try
        {
            UIMaskCapture = new UIMaskCapture(RenderContext, PctService.HookProvider, FlushSceneCompositeFromHook);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to create UIMaskCapture; UIMask.BackbufferSubtraction will fall back to backbuffer alpha.");
        }

        var clipZoneDesc = DepthStencilStateDescription.Default();
        clipZoneDesc.IsDepthEnabled = false;
        clipZoneDesc.DepthWriteMask = DepthWriteMask.Zero;
        clipZoneDesc.IsStencilEnabled = true;
        clipZoneDesc.StencilReadMask = 0xFF;
        clipZoneDesc.StencilWriteMask = 0xFF;
        clipZoneDesc.FrontFace = new DepthStencilOperationDescription
        {
            FailOperation = StencilOperation.Keep,
            DepthFailOperation = StencilOperation.Keep,
            PassOperation = StencilOperation.Replace,
            Comparison = Comparison.Always,
        };
        clipZoneDesc.BackFace = clipZoneDesc.FrontFace;
        _clipZoneDSS = new(RenderContext.Device, clipZoneDesc);

        // Shape pass: no depth test (PS handles occlusion), stencil-equal-zero to skip clip zones.
        var shapeDesc = DepthStencilStateDescription.Default();
        shapeDesc.IsDepthEnabled = false;
        shapeDesc.DepthWriteMask = DepthWriteMask.Zero;
        shapeDesc.IsStencilEnabled = true;
        shapeDesc.StencilReadMask = 0xFF;
        shapeDesc.StencilWriteMask = 0;
        shapeDesc.FrontFace = new DepthStencilOperationDescription
        {
            FailOperation = StencilOperation.Keep,
            DepthFailOperation = StencilOperation.Keep,
            PassOperation = StencilOperation.Keep,
            Comparison = Comparison.Equal,
        };
        shapeDesc.BackFace = shapeDesc.FrontFace;
        _shapeDSS = new(RenderContext.Device, shapeDesc);
    }

    public void Dispose()
    {
        RenderTarget?.Dispose();
        TriFill.Dispose();
        FanFill?.Dispose();
        ProjectedFanFill?.Dispose();
        ProjectedTriFill?.Dispose();
        Stroke?.Dispose();
        Sphere?.Dispose();
        Image?.Dispose();
        Sprite?.Dispose();
        ClipZone.Dispose();
        FSP.Dispose();
        UIMaskCapture?.Dispose();
        _clipZoneDSS.Dispose();
        _shapeDSS.Dispose();
        RenderContext.Dispose();
    }

    internal unsafe void BeginFrame(FrameState? frameState = null)
    {
        RenderContext.BeginFrame();

        var state = frameState ?? CaptureFrameState();
        ViewportSize = state.ViewportSize;
        ViewProj = state.ViewProj;
        ProjScale = state.ProjScale;
        CameraPos = state.CameraPos;

        var device = Device.Instance();

        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        if (rtm != null && rtm->DepthStencil != null)
        {
            var resolutionScaled = rtm->DepthStencil->ActualWidth != device->Width || rtm->DepthStencil->ActualHeight != device->Height;
            if (resolutionScaled && PctService.Hints.UIMask is UIMask.BackbufferAlpha)
            {
                PctService.Hints = PctService.Hints with { UIMask = UIMask.BackbufferSubtraction };
            }
        }

        bool canMask = PctService.Hints.AutoDraw is not AutoDraw.NativeOverlay
            and not AutoDraw.SceneComposite;
        bool useBackbufferAlphaMask = canMask && PctService.Hints.UIMask is UIMask.BackbufferAlpha;
        bool useSubtractionMask = canMask && PctService.Hints.UIMask is UIMask.BackbufferSubtraction
            && UIMaskCapture?.HasSnapshot == true;

        if (canMask && PctService.Hints.UIMask is UIMask.BackbufferSubtraction)
        {
            UIMaskCapture?.BeginFrame();
        }

        FSP.UpdateConstants(RenderContext, new()
        {
            MaxAlpha = PctService.Hints.MaxAlphaFraction,
            UseMask = (useBackbufferAlphaMask || useSubtractionMask) ? 1f : 0f,
        });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y, PctService.Hints.AlphaBlendMode);
        }
        RenderTarget.Bind(RenderContext);
    }

    internal FrameState CaptureFrameState()
    {
        unsafe
        {
            var device = Device.Instance();
            var viewportSize = device != null
                ? new SharpDX.Vector2(device->Width, device->Height)
                : ViewportSize;
            var viewProj = *(SharpDX.Matrix*)&Control.Instance()->ViewProjectionMatrix;
            var projScale = ProjScale;
            var cameraPos = CameraPos;

            var sceneCamera = Control.Instance()->CameraManager.GetActiveCamera();
            var renderCam = sceneCamera != null ? sceneCamera->SceneCamera.RenderCamera : null;
            if (renderCam != null)
            {
                var proj = renderCam->ProjectionMatrix;
                projScale = new Vector2(proj.M11, proj.M22);
                cameraPos = renderCam->Origin;
            }

            return new FrameState(viewProj, viewportSize, projScale, cameraPos);
        }
    }

    internal unsafe RenderTarget EndFrame(ShaderResourceView? sceneDepthSRV, SharpDX.Vector2 sceneDepthUvScale, ShaderResourceView? sceneInfoSRV, ShaderResourceView? sceneNormalSRV, bool compositeToBackBuffer = false)
    {
        var rtSize = new Vector2(ViewportSize.X, ViewportSize.Y);
        var pixelToUv = new Vector2(sceneDepthUvScale.X, sceneDepthUvScale.Y) / rtSize;
        TriFill.UpdateConstants(new() { ViewProj = ViewProj, PixelToUv = pixelToUv });
        FanFill?.UpdateConstants(new() { ViewProj = ViewProj, PixelToUv = pixelToUv });
        Stroke?.UpdateConstants(new() { ViewProj = ViewProj, RenderTargetSize = rtSize, PixelToUv = pixelToUv });

        if (Sprite?.HasPending == true)
        {
            Sprite.UpdateConstants(new()
            {
                ViewProj = ViewProj,
                RenderTargetSize = rtSize,
                PixelToUv = pixelToUv,
            });
        }

        if (ProjectedFanFill?.HasPending == true || ProjectedTriFill?.HasPending == true || Sphere?.HasPending == true || Image?.HasPending == true)
        {
            var invViewProj = ViewProj;
            invViewProj.Invert();
            var sceneCamera = Control.Instance()->CameraManager.GetActiveCamera();
            var renderCam = sceneCamera != null ? sceneCamera->SceneCamera.RenderCamera : null;
            var cameraPos = renderCam != null ? renderCam->Origin : default;
            if (ProjectedFanFill?.HasPending == true)
            {
                ProjectedFanFill.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                    CameraPos = CameraPos,
                });
            }
            if (ProjectedTriFill?.HasPending == true)
            {
                ProjectedTriFill.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                    CameraPos = CameraPos,
                });
            }
            if (Sphere?.HasPending == true)
            {
                Sphere.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                    CameraPos = CameraPos,
                });
            }
            if (Image?.HasPending == true)
            {
                Image.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                });
            }
        }

        bool hasShapes = TriFill.HasPending || FanFill?.HasPending == true || ProjectedFanFill?.HasPending == true || ProjectedTriFill?.HasPending == true || Stroke?.HasPending == true || Sphere?.HasPending == true || Image?.HasPending == true || Sprite?.HasPending == true;
        if (hasShapes && sceneDepthSRV != null)
        {
            // DSV-only target with cleared stencil for the clip-zone pass.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget!.ClipStencilDSV);
            RenderContext.Context.ClearDepthStencilView(RenderTarget.ClipStencilDSV, DepthStencilClearFlags.Stencil, 0f, 0);

            if (ClipZone.HasPending)
            {
                ClipZone.UpdateConstants(new() { ViewportSize = rtSize });
                RenderContext.Context.OutputMerger.SetDepthStencilState(_clipZoneDSS, 1);
                ClipZone.Flush();
            }

            // Bind RTV+DSV; shape pass uses stencil-equal-zero, no depth test.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget.ClipStencilDSV, RenderTarget.BaseRTV);
            RenderContext.Context.OutputMerger.SetDepthStencilState(_shapeDSS, 0);

            RenderContext.Context.PixelShader.SetShaderResource(0, sceneDepthSRV);
            RenderContext.Context.PixelShader.SetShaderResource(1, sceneInfoSRV);
            RenderContext.Context.PixelShader.SetShaderResource(2, sceneNormalSRV);
        }

        FlushProjectedInOrder();
        TriFill.Flush();
        FanFill?.Flush();
        Sphere?.Flush();
        Image?.FlushFlat(CameraPos);
        Sprite?.Flush(CameraPos);
        Stroke?.Flush();

        var device = Device.Instance();
        if (device != null &&
            device->SwapChain != null &&
            device->SwapChain->BackBuffer != null &&
            device->SwapChain->BackBuffer->D3D11Texture2D != null)
        {
            var backBuffer = new Texture2D((IntPtr)device->SwapChain->BackBuffer->D3D11Texture2D);

            ShaderResourceView? overrideMaskSRV = null;
            if (!compositeToBackBuffer
                && PctService.Hints.UIMask == UIMask.BackbufferSubtraction
                && PctService.Hints.AutoDraw != AutoDraw.NativeOverlay
                && PctService.Hints.AutoDraw != AutoDraw.SceneComposite
                && UIMaskCapture?.HasSnapshot == true)
            {
                UIMaskCapture.BuildMask(backBuffer);
                overrideMaskSRV = UIMaskCapture.MaskSRV;
            }

            var maskEnabled = !compositeToBackBuffer
                && PctService.Hints.AutoDraw is not AutoDraw.NativeOverlay
                and not AutoDraw.SceneComposite
                && (PctService.Hints.UIMask is UIMask.BackbufferAlpha || overrideMaskSRV != null);
            FSP.UpdateConstants(RenderContext, new()
            {
                MaxAlpha = PctService.Hints.MaxAlphaFraction,
                UseMask = maskEnabled ? 1f : 0f,
            });

            if (compositeToBackBuffer)
                RenderTarget!.ExecuteFSPToBackBuffer(RenderContext, backBuffer, FSP);
            else
                RenderTarget!.ExecuteFSP(RenderContext, backBuffer, FSP, overrideMaskSRV);
        }
        else
        {
            PctService.Log.Warning("[Pictomancy] DXRenderer.EndFrame: Device or BackBuffer is null; skipping combined pass.");
        }

        RenderContext.Execute();
        return RenderTarget;
    }

    internal void ScheduleSceneComposite(SceneDepth sceneDepth, SceneInfo sceneInfo, SceneNormal sceneNormal, FrameState frameState, PctDrawHints hints)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _sceneCompositeScheduleCount++;
        if (!_sceneCompositePending)
        {
            _sceneCompositeScheduledUnixMs = now;
            _sceneCompositeStallLogged = false;
        }

        _sceneCompositeDepth = sceneDepth;
        _sceneCompositeInfo = sceneInfo;
        _sceneCompositeNormal = sceneNormal;
        _sceneCompositeFrameState = frameState;
        _sceneCompositeHints = hints with { AutoDraw = AutoDraw.SceneComposite, UIMask = UIMask.None };
        _sceneCompositePending = true;

        if (IsSceneCompositeHookUnavailable && !_sceneCompositeHookUnavailableLogged)
        {
            _sceneCompositeHookUnavailableLogged = true;
            PctService.Log.Warning($"[Pictomancy] SceneComposite cannot flush because the OMSetRenderTargets hook is unavailable. {SceneCompositeStatus}");
        }
    }

    internal void CancelSceneComposite(string reason)
    {
        if (!_sceneCompositePending)
            return;

        PctService.Log.Warning($"[Pictomancy] SceneComposite pending render cancelled: {reason}. {SceneCompositeStatus}");
        _sceneCompositePending = false;
        _sceneCompositeDepth = null;
        _sceneCompositeInfo = null;
        _sceneCompositeNormal = null;
        _sceneCompositeFrameState = null;
        _sceneCompositeStallLogged = false;
    }

    private void FlushSceneCompositeFromHook()
    {
        if (!_sceneCompositePending || _sceneCompositeFlushing)
            return;

        if (_sceneCompositeDepth == null || _sceneCompositeInfo == null || _sceneCompositeNormal == null)
            return;

        _sceneCompositePending = false;
        _sceneCompositeFlushing = true;

        var previousHints = PctService.Hints;
        PctService.Hints = _sceneCompositeHints;
        try
        {
            BeginFrame(_sceneCompositeFrameState);
            _sceneCompositeDepth.Update();
            _sceneCompositeInfo.Update();
            _sceneCompositeNormal.Update();
            EndFrame(
                _sceneCompositeDepth.SRV,
                _sceneCompositeDepth.UvScale,
                _sceneCompositeInfo.SRV,
                _sceneCompositeNormal.SRV,
                compositeToBackBuffer: true);
            _lastSceneCompositeFlushUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sceneCompositeFlushCount++;
            _sceneCompositeStallLogged = false;
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] SceneComposite flush failed.");
        }
        finally
        {
            PctService.Hints = previousHints;
            _sceneCompositeDepth = null;
            _sceneCompositeInfo = null;
            _sceneCompositeNormal = null;
            _sceneCompositeFrameState = null;
            _sceneCompositeFlushing = false;
        }
    }

    private void MaybeLogSceneCompositeStall()
    {
        if (!_sceneCompositePending || _sceneCompositeStallLogged || _sceneCompositeScheduledUnixMs <= 0)
            return;

        var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sceneCompositeScheduledUnixMs;
        if (age < SceneCompositeStallWarningMilliseconds)
            return;

        _sceneCompositeStallLogged = true;
        PctService.Log.Warning($"[Pictomancy] SceneComposite has been pending for {age} ms without a hook flush. {SceneCompositeStatus}");
    }

    private string BuildHookStatus(long now)
    {
        if (UIMaskCapture == null)
            return "hook capture unavailable";

        if (!UIMaskCapture.IsHookInstalled)
            return "hook not installed";

        var lastHook = UIMaskCapture.LastHookUnixMs > 0
            ? $"{Math.Max(0, now - UIMaskCapture.LastHookUnixMs)} ms ago"
            : "never";
        var lastBind = UIMaskCapture.LastBackbufferDsvBindUnixMs > 0
            ? $"{Math.Max(0, now - UIMaskCapture.LastBackbufferDsvBindUnixMs)} ms ago"
            : "never";
        return $"hook installed; OMSetRenderTargets calls {UIMaskCapture.HookCallCount}; backbuffer DSV binds {UIMaskCapture.BackbufferDsvBindCount}; last hook {lastHook}; last backbuffer bind {lastBind}";
    }
    public void DrawText(Vector2 position, string text)
    {
        throw new NotImplementedException("try again later");
        //RenderContext.Context2.BeginDraw();
        //RenderContext.Context2.DrawText(text, position);
        //RenderContext.Context2.EndDraw();
    }

    private void FlushProjectedInOrder()
    {
        ProjectedFanFill?.EndBuilder();
        ProjectedTriFill?.EndBuilder();
        Image?.EndProjectedBuilder();

        int fanStart = 0;
        int triStart = 0;
        int imageStart = 0;
        foreach (var (count, type) in _projectedRuns)
        {
            switch (type)
            {
                case ProjectionType.Fan:
                    ProjectedFanFill!.FlushRange(fanStart, count);
                    fanStart += count;
                    break;
                case ProjectionType.Tri:
                    ProjectedTriFill!.FlushRange(triStart, count);
                    triStart += count;
                    break;
                case ProjectionType.Image:
                    Image!.FlushProjectedRange(imageStart, count);
                    imageStart += count;
                    break;
            }
        }
        _projectedRuns.Clear();
    }

    private void AppendProjectedRun(ProjectionType type)
    {
        var lastProjection = _projectedRuns.ElementAtOrDefault(_projectedRuns.Count - 1);
        if (lastProjection.Type == type)
        {
            _projectedRuns[^1] = (lastProjection.Count + 1, type);
        }
        else
        {
            _projectedRuns.Add((1, type));
        }
    }

    public void DrawTriangle(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC, Vector3 aaMask, PctDxParams p)
    {
        if (p.ProjectionHeight > 0 && ProjectedTriFill != null)
        {
            ProjectedTriFill.Add(a, b, c, colorA, colorB, colorC, aaMask, p);
            AppendProjectedRun(ProjectionType.Tri);
        }
        else
        {
            // Non-projected TriFill has no edge AA; aaMask is ignored on this path.
            TriFill.Add(a, b, c, colorA, colorB, colorC, p);
        }
    }

    public void AddClipZone(Vector2 min, Vector2 max) => ClipZone.Add(min, max);

    private void DrawTriangleFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments, PctDxParams p)
    {
        float totalAngle = maxAngle - minAngle;
        if (numSegments == 0) numSegments = (uint)(MathF.Abs(totalAngle) * 8);

        float angleStep = totalAngle / numSegments;

        Vector3 prev = new();
        for (int step = 0; step <= numSegments; step++)
        {
            float angle = MathF.PI / 2 + minAngle + step * angleStep;
            Vector3 offset = new(MathF.Cos(angle), 0, MathF.Sin(angle));

            if (step > 0)
            {
                if (innerRadius > 0)
                {
                    // Fan fallback: shared edges between adjacent segment triangles get hard cuts
                    // (Vector3.Zero) so the ring doesn't show seams. Real edge AA would require
                    // per-triangle masks; this fallback path is degraded-mode only, so we keep it
                    // simple and accept slightly aliased perimeter edges.
                    DrawTriangle(center + innerRadius * prev, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor, Vector3.Zero, p);
                    DrawTriangle(center + outerRadius * offset, center + innerRadius * offset, center + innerRadius * prev, outerColor, innerColor, innerColor, Vector3.Zero, p);
                }
                else
                {
                    DrawTriangle(center, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor, Vector3.Zero, p);
                }
            }
            prev = offset;
        }
    }
    public void DrawFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments, PctDxParams p)
    {
        bool project = p.ProjectionHeight > 0;
        if (project && ProjectedFanFill != null && numSegments == 0)
        {
            ProjectedFanFill.Add(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, p);
            AppendProjectedRun(ProjectionType.Fan);
        }
        else if (!project && !FanDegraded && numSegments == 0)
        {
            FanFill!.Add(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, p);
        }
        else
        {
            DrawTriangleFan(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, numSegments, p);
        }
    }

    public void DrawStroke(IEnumerable<Vector3> world, float thickness, uint color, bool closed, PctDxParams p)
    {
        Stroke?.Add(world.ToArray(), thickness, color, closed, p);
    }

    public void DrawSphere(Vector3 center, float radius, uint color, PctDxParams p)
    {
        Sphere?.Add(center, radius, color, p);
    }

    public void DrawImage(IntPtr nativePtr, Vector3 center, Vector3 right, Vector3 down, PctDxParams p)
    {
        DrawImage(nativePtr, center, right, down, Vector2.Zero, Vector2.One, p);
    }

    public void DrawImage(IntPtr nativePtr, Vector3 center, Vector3 right, Vector3 down, Vector2 uvMin, Vector2 uvMax, PctDxParams p)
    {
        if (Image == null) return;
        Image.Add(nativePtr, center, right, down, uvMin, uvMax, p);
        if (p.ProjectionHeight > 0f)
            AppendProjectedRun(ProjectionType.Image);
    }

    public void DrawSprite(IntPtr nativePtr, Vector3 worldPosition, Vector2 screenSize, Vector2 offset, PctDxParams p)
    {
        Sprite?.Add(nativePtr, worldPosition, screenSize, offset, p);
    }

    public void DrawBillboard(IntPtr nativePtr, Vector3 worldPosition, Vector2 worldSize, PctDxParams p)
    {
        if (Sprite == null) return;
        float w = worldPosition.X * ViewProj.M14 + worldPosition.Y * ViewProj.M24 + worldPosition.Z * ViewProj.M34 + ViewProj.M44;
        if (w <= 0f) return;
        var scale = new Vector2(ProjScale.X * ViewportSize.X, ProjScale.Y * ViewportSize.Y) * 0.5f / w;
        Sprite.Add(nativePtr, worldPosition, worldSize * scale, Vector2.Zero, p);
    }

    private static unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }

    internal static int AlignTo16<T>() where T : unmanaged => ((Unsafe.SizeOf<T>() + 15) & ~15);
}
