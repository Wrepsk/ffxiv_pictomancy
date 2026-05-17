using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiToolKit.Classes;
using SharpDX.Direct3D11;

namespace Pictomancy;

internal unsafe sealed class PctNamePlateOverlay : IDisposable
{
    private readonly PctOverlayNode node = new();
    private nint attachedAddon;

    public PctNamePlateOverlay()
    {
        PctService.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "NamePlate", OnNamePlatePreFinalize);
    }

    public void UpdateTexture(Texture2D? texture2D, ShaderResourceView? shaderResourceView)
    {
        if (texture2D == null || shaderResourceView == null || !EnsureAttached())
        {
            Hide();
            return;
        }

        node.IsVisible = true;
        node.UpdateTexture(texture2D, shaderResourceView);
        node.Update();
    }

    public void Hide()
    {
        node.IsVisible = false;
        node.Update();
    }

    public void Dispose()
    {
        PctService.AddonLifecycle.UnregisterListener(OnNamePlatePreFinalize);
        Detach();
        node.Dispose();
    }

    private bool EnsureAttached()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName("NamePlate");
        if (addon == null || !addon->IsReady || addon->RootNode == null)
        {
            return false;
        }

        if (attachedAddon == (nint)addon)
        {
            return true;
        }

        Detach();

        node.NodeId = (uint)addon->UldManager.NodeListCount + 1;
        node.AttachNode(addon->RootNode, NodePosition.AsFirstChild);
        attachedAddon = (nint)addon;
        return true;
    }

    private void Detach()
    {
        if (attachedAddon == nint.Zero)
        {
            return;
        }

        try
        {
            node.DetachNode();
        }
        catch (Exception e)
        {
            PctService.Log.Warning(e, "[Pictomancy] Failed to detach NamePlate overlay node.");
        }

        attachedAddon = nint.Zero;
    }

    private void OnNamePlatePreFinalize(AddonEvent type, AddonArgs args)
    {
        if (attachedAddon == args.Addon.Address)
        {
            Detach();
        }
    }
}
