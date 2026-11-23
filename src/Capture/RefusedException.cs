namespace Capture;

/// <summary>
/// Something that was asked for cannot be done, for a reason the person who asked can act on
/// ("Streaming is stopped, so there is nothing to pause."). The message is written for them: a
/// viewer that sent the request is shown it as it is. Any other exception while handling a request
/// is a failure of the capture client, reported as such.
/// </summary>
internal sealed class RefusedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
