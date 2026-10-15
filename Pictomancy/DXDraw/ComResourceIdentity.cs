using System.Runtime.InteropServices;

namespace Pictomancy.DXDraw;

internal static class ComResourceIdentity
{
    internal static bool AreSame(nint left, nint right)
    {
        if (left == 0 || right == 0) return false;
        if (left == right) return true;
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        nint leftIdentity = 0;
        nint rightIdentity = 0;
        try
        {
            return Marshal.QueryInterface(left, ref unknown, out leftIdentity) == 0
                && Marshal.QueryInterface(right, ref unknown, out rightIdentity) == 0
                && leftIdentity == rightIdentity;
        }
        finally
        {
            if (rightIdentity != 0) Marshal.Release(rightIdentity);
            if (leftIdentity != 0) Marshal.Release(leftIdentity);
        }
    }
}
