using System.Threading;

namespace Capture3DS
{
    /// <summary>
    /// Optional cooperative cancellation for a capture worker. Cancellation is observed
    /// between native calls; it never aborts or disposes a handle from another thread.
    /// The caller must wait for the worker to exit before opening another session.
    /// </summary>
    public interface ICancellableCapture3DSDevice : ICapture3DSDevice
    {
        void Connect(CancellationToken cancellationToken);
        Capture3DSFrame ReadFrame(CancellationToken cancellationToken);
    }
}
