using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.ReadWrite;

namespace Capture.Video;

/// <summary>
/// A camera as a <see cref="IFrameSource"/>: the newest picture it delivered, as top-down BGRX of
/// a fixed size.
/// <para>
/// The camera runs at its own pace, so a thread of its own reads it with the Media Foundation
/// source reader (which decodes MJPG and converts NV12 or YUY2 to RGB32) and keeps the latest
/// picture; <see cref="Capture"/>, on the pipeline's capture thread, copies that out at the
/// pipeline's frame rate. <c>_frameLock</c> guards the latest picture and the problem.
/// </para>
/// <para>
/// The camera is only open while the pipeline wants pictures (<see cref="SetActive"/>), so its
/// light is off and other programs can use it the rest of the time. If it cannot be read (it was
/// unplugged, another program has it), <see cref="Capture"/> says so, the reader thread tries to
/// open it again every two seconds, and the pictures continue when it can.
/// </para>
/// </summary>
internal sealed unsafe class CameraCapturer : IFrameSource
{
    /// <summary>MF_SOURCE_READER_FIRST_VIDEO_STREAM.</summary>
    private const int FirstVideoStream = unchecked((int)0xFFFFFFFC);

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private readonly IntPtr _pixels;
    private readonly IntPtr _latest;

    private bool _fresh;
    private bool _hasPicture;
    private long _framesReceived;
    private string? _problem;
    private volatile bool _active;
    private volatile bool _stopping;
    private volatile bool _disposed;

    /// <summary>
    /// Opens the camera once to learn its modes, unless they are given; it is opened again
    /// whenever pictures are wanted.
    /// </summary>
    /// <param name="fps">The frame rate the pipeline will read it at, which with <paramref name="maxVideoSize"/> decides the mode (<see cref="CameraDevices.ChooseMode"/>).</param>
    /// <param name="maxVideoSize">The largest video it will be encoded as; no limit when null.</param>
    /// <param name="modes">Its modes, as an earlier capturer of the same camera found them: then it is not opened until pictures are wanted.</param>
    public CameraCapturer(CameraDevice device, int fps, (int Width, int Height)? maxVideoSize = null, IReadOnlyList<CameraMode>? modes = null)
    {
        Device = device;
        MaxVideoSize = maxVideoSize ?? (int.MaxValue, int.MaxValue);
        if (modes is null)
        {
            using CameraReader reader = CameraReader.Open(device);
            modes = reader.ListModes();
        }

        Modes = modes;
        Mode = ModeFor(fps) ?? throw new InvalidOperationException($"{device.Name} offers no video format.");
        Width = Mode.Width & ~1; // 4:2:0 video needs even sizes
        Height = Mode.Height & ~1;

        _pixels = (IntPtr)NativeMemory.AllocZeroed((nuint)SizeInBytes);
        _latest = (IntPtr)NativeMemory.AllocZeroed((nuint)SizeInBytes);
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "camera-reader" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public CameraDevice Device { get; }

    /// <summary>Every mode the camera offers.</summary>
    public IReadOnlyList<CameraMode> Modes { get; }

    /// <summary>The mode it runs in.</summary>
    public CameraMode Mode { get; }

    /// <summary>The largest video it will be encoded as.</summary>
    public (int Width, int Height) MaxVideoSize { get; }

    /// <summary>
    /// Whether <see cref="Pixels"/> holds a picture from the camera as it is now opened: false from
    /// the moment it is opened until its first picture arrives, so that neither black nor a picture
    /// left from the last time it was open is sent as live.
    /// </summary>
    public bool HasPicture
    {
        get
        {
            lock (_frameLock)
            {
                return _hasPicture;
            }
        }
    }

    public string Id => CameraDevices.StreamId;

    public string Name => Device.Name;

    public int Width { get; }

    public int Height { get; }

    public int SizeInBytes => Width * Height * 4;

    public IntPtr Pixels => _pixels;

    public string Method => $"camera, {Mode}";

    /// <summary>How many pictures the camera has delivered.</summary>
    public long FramesReceived => Interlocked.Read(ref _framesReceived);

    /// <summary>Why the camera is not delivering pictures right now, or null.</summary>
    public string? Problem
    {
        get
        {
            lock (_frameLock)
            {
                return _problem;
            }
        }
    }

    public bool Capture(out string? problem)
    {
        lock (_frameLock)
        {
            problem = _problem;
            if (problem is not null)
            {
                return false;
            }

            if (_fresh)
            {
                Buffer.MemoryCopy((void*)_latest, (void*)_pixels, SizeInBytes, SizeInBytes);
                _fresh = false;
                _hasPicture = true;
            }

            return true;
        }
    }

    /// <summary>The mode this camera would run in for <paramref name="fps"/>, within <see cref="MaxVideoSize"/>.</summary>
    public CameraMode? ModeFor(int fps) => CameraDevices.ChooseMode(Modes, fps, MaxVideoSize.Width, MaxVideoSize.Height);

    public void SetActive(bool active)
    {
        if (_active == active || _disposed)
        {
            return;
        }

        _active = active;
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check above and here: the camera is being replaced or switched off.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _wake.Set();

        // A read returns within a frame; a camera that hangs keeps its buffers rather than have them freed under it.
        if (_thread.Join(TimeSpan.FromSeconds(3)))
        {
            NativeMemory.Free((void*)_pixels);
            NativeMemory.Free((void*)_latest);
            _wake.Dispose();
        }
        else
        {
            Log.Warn($"{Name} did not stop in time; leaving it to Windows.");
        }
    }

    private void ReadLoop()
    {
        CameraReader? reader = null;
        long retryAt = 0;
        try
        {
            while (!_stopping)
            {
                if (!_active)
                {
                    if (reader is not null)
                    {
                        reader.Dispose();
                        reader = null;
                        Log.Debug($"{Name} is closed: nothing needs its pictures.");
                    }

                    // Whatever went wrong while it was wanted is no longer true of it, and its last
                    // picture is no longer live: it starts afresh the next time it is wanted.
                    retryAt = 0;
                    Forget();

                    _wake.Wait();
                    _wake.Reset();
                    continue;
                }

                if (reader is null)
                {
                    long now = Environment.TickCount64;
                    if (now < retryAt)
                    {
                        _wake.Wait(TimeSpan.FromMilliseconds(Math.Min(retryAt - now, 200)));
                        _wake.Reset();
                        continue;
                    }

                    try
                    {
                        reader = CameraReader.Open(Device);
                        reader.Start(Mode, Width, Height);
                        Log.Debug($"{Name} is open: {Mode}.");
                    }
                    catch (Exception ex)
                    {
                        reader?.Dispose();
                        reader = null;
                        SetProblem($"the camera cannot be opened: {ex.Message}");
                        retryAt = Environment.TickCount64 + (long)RetryInterval.TotalMilliseconds;
                        continue;
                    }
                }

                try
                {
                    if (reader.Read(this))
                    {
                        SetProblem(null);
                    }
                }
                catch (Exception ex)
                {
                    reader.Dispose();
                    reader = null;
                    SetProblem($"the camera stopped delivering pictures: {ex.Message}");
                    retryAt = Environment.TickCount64 + (long)RetryInterval.TotalMilliseconds;
                }
            }
        }
        catch (Exception ex)
        {
            SetProblem(ex.Message);
            Log.Debug($"{Name}: the reader thread ended: {ex}");
        }
        finally
        {
            reader?.Dispose();
        }
    }

    private void SetProblem(string? problem)
    {
        lock (_frameLock)
        {
            _problem = problem;
        }
    }

    /// <summary>Drops the problem and the pictures: there is none until the camera delivers one again.</summary>
    private void Forget()
    {
        lock (_frameLock)
        {
            _problem = null;
            _fresh = false;
            _hasPicture = false;
        }
    }

    /// <summary>
    /// Copies a picture the source reader delivered into the latest picture. The rows are read
    /// through IMF2DBuffer where the buffer has it, as it knows which way up they are.
    /// </summary>
    private void Store(IMFMediaBuffer buffer, int defaultStride)
    {
        int rowBytes = Width * 4;
        IntPtr scan0;
        int pitch;
        bool twoD = buffer is IMF2DBuffer;
        if (twoD)
        {
            Mf.Check(((IMF2DBuffer)buffer).Lock2D(out scan0, out pitch), "IMF2DBuffer.Lock2D");
        }
        else
        {
            Mf.Check(buffer.Lock(out IntPtr start, out _, out int length), "IMFMediaBuffer.Lock");
            pitch = defaultStride;
            int rows = Math.Abs(pitch) * Height;
            if (length < rows)
            {
                buffer.Unlock();
                throw new InvalidOperationException($"The camera delivered {length} bytes where {rows} were expected.");
            }

            scan0 = pitch >= 0 ? start : start + (Height - 1) * -pitch; // a negative stride: bottom-up rows
        }

        try
        {
            lock (_frameLock)
            {
                for (int y = 0; y < Height; y++)
                {
                    Buffer.MemoryCopy((void*)(scan0 + y * pitch), (void*)(_latest + y * rowBytes), rowBytes, rowBytes);
                }

                _fresh = true;
            }

            Interlocked.Increment(ref _framesReceived);
        }
        finally
        {
            if (twoD)
            {
                ((IMF2DBuffer)buffer).Unlock2D();
            }
            else
            {
                buffer.Unlock();
            }
        }
    }

    /// <summary>One opening of the camera: its media source and a source reader on it.</summary>
    private sealed class CameraReader : IDisposable
    {
        private readonly IMFMediaSource _source;
        private readonly IMFSourceReader _reader;
        private int _defaultStride;

        private CameraReader(IMFMediaSource source, IMFSourceReader reader)
        {
            _source = source;
            _reader = reader;
        }

        public static CameraReader Open(CameraDevice device)
        {
            IMFAttributes attributes = Mf.CreateAttributes(2);
            IMFMediaSource? source = null;
            try
            {
                Mf.Check(attributes.SetGUID(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, CLSID.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID), "Set MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE");
                Mf.Check(attributes.SetString(MFAttributesClsid.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, device.Id), "Set MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK");
                Mf.Check(MFExtern.MFCreateDeviceSource(attributes, out source), "MFCreateDeviceSource");

                IMFAttributes readerAttributes = Mf.CreateAttributes(1);
                try
                {
                    // Lets the reader decode MJPG and convert the camera's YUV to RGB32, at any size.
                    Mf.Check(readerAttributes.SetUINT32(MFAttributesClsid.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1), "Set MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING");
                    Mf.Check(MFExtern.MFCreateSourceReaderFromMediaSource(source, readerAttributes, out IMFSourceReader reader), "MFCreateSourceReaderFromMediaSource");
                    return new CameraReader(source, reader);
                }
                finally
                {
                    Mf.Release(readerAttributes);
                }
            }
            catch
            {
                if (source is not null)
                {
                    source.Shutdown();
                    Mf.Release(source);
                }

                throw;
            }
            finally
            {
                Mf.Release(attributes);
            }
        }

        public IReadOnlyList<CameraMode> ListModes()
        {
            var modes = new List<CameraMode>();
            for (int index = 0; ; index++)
            {
                if (Mf.Failed(_reader.GetNativeMediaType(FirstVideoStream, index, out IMFMediaType type)) || type is null)
                {
                    break; // MF_E_NO_MORE_TYPES
                }

                try
                {
                    if (Describe(type) is { } mode && !modes.Contains(mode))
                    {
                        modes.Add(mode);
                    }
                }
                finally
                {
                    Mf.Release(type);
                }
            }

            return modes;
        }

        /// <summary>Sets the camera to <paramref name="mode"/> and the reader to deliver RGB32 of the given size.</summary>
        public void Start(CameraMode mode, int width, int height)
        {
            // The camera's own mode first; if it no longer offers it, the reader picks one and scales.
            for (int index = 0; ; index++)
            {
                if (Mf.Failed(_reader.GetNativeMediaType(FirstVideoStream, index, out IMFMediaType native)) || native is null)
                {
                    break;
                }

                try
                {
                    if (Describe(native) == mode)
                    {
                        Mf.Check(_reader.SetCurrentMediaType(FirstVideoStream, null, native), "Setting the camera's mode");
                        break;
                    }
                }
                finally
                {
                    Mf.Release(native);
                }
            }

            IMFMediaType rgb = Mf.CreateVideoType(MFMediaType.RGB32, width, height, Math.Max(1, (int)Math.Round(mode.Fps)));
            try
            {
                Mf.Check(_reader.SetCurrentMediaType(FirstVideoStream, null, rgb), "Asking the camera for RGB32 pictures");
            }
            finally
            {
                Mf.Release(rgb);
            }

            Mf.Check(_reader.GetCurrentMediaType(FirstVideoStream, out IMFMediaType current), "Reading the camera's picture format");
            try
            {
                _defaultStride = Mf.Failed(current.GetUINT32(MFAttributesClsid.MF_MT_DEFAULT_STRIDE, out int stride)) ? width * 4 : stride;
            }
            finally
            {
                Mf.Release(current);
            }
        }

        /// <summary>Waits for the camera's next picture and stores it. False when the reader had none to give (a gap in the stream).</summary>
        public bool Read(CameraCapturer capturer)
        {
            Mf.Check(_reader.ReadSample(FirstVideoStream, 0, out _, out MF_SOURCE_READER_FLAG flags, out _, out IMFSample sample), "Reading from the camera");
            try
            {
                if ((flags & (MF_SOURCE_READER_FLAG.Error | MF_SOURCE_READER_FLAG.EndOfStream)) != 0)
                {
                    throw new InvalidOperationException("the camera ended its stream");
                }

                if (sample is null)
                {
                    return false;
                }

                Mf.Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "IMFSample.ConvertToContiguousBuffer");
                try
                {
                    capturer.Store(buffer, _defaultStride);
                }
                finally
                {
                    Mf.Release(buffer);
                }

                return true;
            }
            finally
            {
                Mf.Release(sample);
            }
        }

        public void Dispose()
        {
            Mf.Release(_reader);
            _source.Shutdown(); // lets go of the camera
            Mf.Release(_source);
        }

        private static CameraMode? Describe(IMFMediaType type)
        {
            if (Mf.Failed(type.GetGUID(MFAttributesClsid.MF_MT_SUBTYPE, out Guid subtype))
                || Mf.Failed(MFExtern.MFGetAttributeSize(type, MFAttributesClsid.MF_MT_FRAME_SIZE, out int width, out int height))
                || Mf.Failed(MFExtern.MFGetAttributeRatio(type, MFAttributesClsid.MF_MT_FRAME_RATE, out int numerator, out int denominator))
                || width < 64 || height < 64 || numerator <= 0 || denominator <= 0)
            {
                return null;
            }

            return new CameraMode(subtype, width, height, Math.Round(numerator / (double)denominator, 2));
        }
    }
}
