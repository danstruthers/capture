using Capture.Interop;

namespace Capture.Tests;

/// <summary>Starts Media Foundation once for the tests that use it and keeps them from running in parallel.</summary>
public sealed class MediaFoundationFixture : IDisposable
{
    public MediaFoundationFixture() => Mf.Startup();

    public void Dispose() => Mf.Shutdown();
}

[CollectionDefinition(Name)]
public sealed class MediaFoundationCollection : ICollectionFixture<MediaFoundationFixture>
{
    public const string Name = "Media Foundation";
}
