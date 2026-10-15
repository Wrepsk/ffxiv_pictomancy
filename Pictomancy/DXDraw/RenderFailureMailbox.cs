using System.Runtime.ExceptionServices;

namespace Pictomancy.DXDraw;

// Native callbacks must not unwind into the game. Hand failures to the next managed draw instead.
internal sealed class RenderFailureMailbox
{
    private Exception? failure;

    public void Report(Exception exception) => Interlocked.CompareExchange(ref failure, exception, null);

    public void ThrowIfFailed()
    {
        if (Interlocked.Exchange(ref failure, null) is { } exception)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }
}
