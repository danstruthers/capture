using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Capture.Interop;
using MediaFoundation;
using MediaFoundation.Misc;
using MediaFoundation.Transform;

namespace Capture.Tests;

/// <summary>
/// Stands in for a hardware H.264 encoder on machines that have none. It wraps the Microsoft
/// software encoder but behaves the way hardware encoder MFTs do: it is asynchronous (has to be
/// unlocked, asks for each input with an event, announces each output with an event), allocates
/// its own output samples, and ends its event stream when shut down.
/// </summary>
internal sealed class AsyncTransformSimulator : IMFTransform, IMFMediaEventGenerator, IMFShutdown, ICodecAPI
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private readonly IMFTransform _inner = (IMFTransform)new MSH264EncoderMFT();
    private readonly IMFAttributes _attributes;
    private readonly BlockingCollection<MediaEventType> _events = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Queue<IMFSample> _outputs = new();
    private readonly object _lock = new();
    private int _inputRequests;
    private int _outputBufferSize;

    public AsyncTransformSimulator()
    {
        Mf.Check(MFExtern.MFCreateAttributes(out _attributes, 2), "MFCreateAttributes");
        _attributes.SetUINT32(MFAttributesClsid.MF_TRANSFORM_ASYNC, 1);
    }

    /// <summary>When false the transform accepts input but never produces anything, like a hung driver.</summary>
    public bool ProducesOutput { get; set; } = true;

    public int InputsAccepted { get; private set; }

    public int KeyFrameRequests { get; private set; }

    public bool WasShutDown => _shutdown.IsCancellationRequested;

    public bool WasUnlocked =>
        !Mf.Failed(_attributes.GetUINT32(MFAttributesClsid.MF_TRANSFORM_ASYNC_UNLOCK, out int unlocked)) && unlocked != 0;

    // ---- IMFTransform: the parts an encoder client uses ---------------------------------------

    public HResult GetAttributes(out IMFAttributes pAttributes)
    {
        // A COM caller would receive its own reference, which it releases; hand out one that can be.
        IntPtr unknown = Marshal.GetIUnknownForObject(_attributes);
        pAttributes = (IMFAttributes)Marshal.GetObjectForIUnknown(unknown);
        Marshal.Release(unknown);
        return HResult.S_OK;
    }

    public HResult SetInputType(int dwInputStreamID, IMFMediaType pType, MFTSetTypeFlags dwFlags) =>
        WasUnlocked ? _inner.SetInputType(dwInputStreamID, pType, dwFlags) : HResult.MF_E_TRANSFORM_ASYNC_LOCKED;

    public HResult SetOutputType(int dwOutputStreamID, IMFMediaType pType, MFTSetTypeFlags dwFlags) =>
        WasUnlocked ? _inner.SetOutputType(dwOutputStreamID, pType, dwFlags) : HResult.MF_E_TRANSFORM_ASYNC_LOCKED;

    public HResult GetOutputAvailableType(int dwOutputStreamID, int dwTypeIndex, out IMFMediaType ppType) =>
        _inner.GetOutputAvailableType(dwOutputStreamID, dwTypeIndex, out ppType);

    public HResult GetOutputCurrentType(int dwOutputStreamID, out IMFMediaType ppType) =>
        _inner.GetOutputCurrentType(dwOutputStreamID, out ppType);

    public HResult GetOutputStreamInfo(int dwOutputStreamID, out MFTOutputStreamInfo pStreamInfo)
    {
        HResult hr = _inner.GetOutputStreamInfo(dwOutputStreamID, out pStreamInfo);
        _outputBufferSize = pStreamInfo.cbSize;
        pStreamInfo.dwFlags |= MFTOutputStreamInfoFlags.ProvidesSamples;
        return hr;
    }

    public HResult ProcessMessage(MFTMessageType eMessage, IntPtr ulParam)
    {
        HResult hr = _inner.ProcessMessage(eMessage, ulParam);

        if (eMessage == MFTMessageType.NotifyStartOfStream)
        {
            RequestInput();
        }
        else if (eMessage == MFTMessageType.CommandDrain)
        {
            PullOutputs();
            _events.Add(MediaEventType.METransformDrainComplete);
        }

        return hr;
    }

    public HResult ProcessInput(int dwInputStreamID, IMFSample pSample, int dwFlags)
    {
        lock (_lock)
        {
            if (_inputRequests == 0)
            {
                return HResult.MF_E_NOTACCEPTING; // input is only allowed in answer to a request
            }

            _inputRequests--;
            InputsAccepted++;
        }

        if (ProducesOutput)
        {
            HResult hr = _inner.ProcessInput(dwInputStreamID, pSample, dwFlags);
            if (Mf.Failed(hr))
            {
                return hr;
            }

            PullOutputs();
        }

        RequestInput();
        return HResult.S_OK;
    }

    public HResult ProcessOutput(MFTProcessOutputFlags dwFlags, int cOutputBufferCount, MFTOutputDataBuffer[] pOutputSamples, out ProcessOutputStatus pdwStatus)
    {
        pdwStatus = ProcessOutputStatus.None;
        IMFSample sample;
        lock (_lock)
        {
            if (_outputs.Count == 0)
            {
                return HResult.E_UNEXPECTED; // output is only allowed in answer to an announcement
            }

            sample = _outputs.Dequeue();
        }

        // The caller takes over this reference, as it would from a real transform that provides samples.
        pOutputSamples[0].pSample = Marshal.GetComInterfaceForObject(sample, typeof(IMFSample));
        Marshal.ReleaseComObject(sample);
        return HResult.S_OK;
    }

    // ---- IMFMediaEventGenerator ---------------------------------------------------------------

    public HResult GetEvent(MFEventFlag dwFlags, out IMFMediaEvent ppEvent)
    {
        ppEvent = null!;
        try
        {
            MediaEventType type = _events.Take(_shutdown.Token);
            return MFExtern.MFCreateMediaEvent(type, Guid.Empty, HResult.S_OK, null, out ppEvent);
        }
        catch (OperationCanceledException)
        {
            return HResult.MF_E_SHUTDOWN;
        }
    }

    public HResult BeginGetEvent(IMFAsyncCallback pCallback, object o) => HResult.E_NOTIMPL;

    public HResult EndGetEvent(IMFAsyncResult pResult, out IMFMediaEvent ppEvent)
    {
        ppEvent = null!;
        return HResult.E_NOTIMPL;
    }

    public HResult QueueEvent(MediaEventType met, Guid guidExtendedType, HResult hrStatus, ConstPropVariant pvValue) => HResult.E_NOTIMPL;

    // ---- IMFShutdown --------------------------------------------------------------------------

    public HResult Shutdown()
    {
        _shutdown.Cancel();
        return HResult.S_OK;
    }

    public HResult GetShutdownStatus(out MFShutdownStatus pStatus)
    {
        pStatus = default;
        return HResult.E_NOTIMPL;
    }

    // ---- ICodecAPI: only SetValue is used -----------------------------------------------------

    public int SetValue(ref Guid api, ref Variant value)
    {
        if (api == CodecApiProperty.AVEncVideoForceKeyFrame)
        {
            KeyFrameRequests++;
        }

        return ((ICodecAPI)_inner).SetValue(ref api, ref value);
    }

    public int IsSupported(ref Guid api) => E_NOTIMPL;

    public int IsModifiable(ref Guid api) => E_NOTIMPL;

    public int GetParameterRange(ref Guid api, out Variant valueMin, out Variant valueMax, out Variant steppingDelta)
    {
        valueMin = valueMax = steppingDelta = default;
        return E_NOTIMPL;
    }

    public int GetParameterValues(ref Guid api, out IntPtr values, out int valuesCount)
    {
        values = IntPtr.Zero;
        valuesCount = 0;
        return E_NOTIMPL;
    }

    public int GetDefaultValue(ref Guid api, out Variant value)
    {
        value = default;
        return E_NOTIMPL;
    }

    public int GetValue(ref Guid api, out Variant value)
    {
        value = default;
        return E_NOTIMPL;
    }

    public int RegisterForEvent(ref Guid api, IntPtr userData) => E_NOTIMPL;

    public int UnregisterForEvent(ref Guid api) => E_NOTIMPL;

    public int SetAllDefaults() => E_NOTIMPL;

    public int SetValueWithNotify(ref Guid api, ref Variant value, out IntPtr changedParam, out int changedParamCount)
    {
        changedParam = IntPtr.Zero;
        changedParamCount = 0;
        return E_NOTIMPL;
    }

    public int SetAllDefaultsWithNotify(out IntPtr changedParam, out int changedParamCount)
    {
        changedParam = IntPtr.Zero;
        changedParamCount = 0;
        return E_NOTIMPL;
    }

    public int GetAllSettings(IntPtr stream) => E_NOTIMPL;

    public int SetAllSettings(IntPtr stream) => E_NOTIMPL;

    public int SetAllSettingsWithNotify(IntPtr stream, out IntPtr changedParam, out int changedParamCount)
    {
        changedParam = IntPtr.Zero;
        changedParamCount = 0;
        return E_NOTIMPL;
    }

    // ---- IMFTransform: everything a client of a fixed one-in, one-out encoder never calls ------

    public HResult GetStreamLimits(MFInt pdwInputMinimum, MFInt pdwInputMaximum, MFInt pdwOutputMinimum, MFInt pdwOutputMaximum) => HResult.E_NOTIMPL;

    public HResult GetStreamCount(MFInt pcInputStreams, MFInt pcOutputStreams) => HResult.E_NOTIMPL;

    public HResult GetStreamIDs(int dwInputIDArraySize, int[] pdwInputIDs, int dwOutputIDArraySize, int[] pdwOutputIDs) => HResult.E_NOTIMPL;

    public HResult GetInputStreamInfo(int dwInputStreamID, out MFTInputStreamInfo pStreamInfo) => _inner.GetInputStreamInfo(dwInputStreamID, out pStreamInfo);

    public HResult GetInputStreamAttributes(int dwInputStreamID, out IMFAttributes pAttributes)
    {
        pAttributes = null!;
        return HResult.E_NOTIMPL;
    }

    public HResult GetOutputStreamAttributes(int dwOutputStreamID, out IMFAttributes pAttributes)
    {
        pAttributes = null!;
        return HResult.E_NOTIMPL;
    }

    public HResult DeleteInputStream(int dwStreamID) => HResult.E_NOTIMPL;

    public HResult AddInputStreams(int cStreams, int[] adwStreamIDs) => HResult.E_NOTIMPL;

    public HResult GetInputAvailableType(int dwInputStreamID, int dwTypeIndex, out IMFMediaType ppType) =>
        _inner.GetInputAvailableType(dwInputStreamID, dwTypeIndex, out ppType);

    public HResult GetInputCurrentType(int dwInputStreamID, out IMFMediaType ppType) => _inner.GetInputCurrentType(dwInputStreamID, out ppType);

    public HResult GetInputStatus(int dwInputStreamID, out MFTInputStatusFlags pdwFlags) => _inner.GetInputStatus(dwInputStreamID, out pdwFlags);

    public HResult GetOutputStatus(out MFTOutputStatusFlags pdwFlags) => _inner.GetOutputStatus(out pdwFlags);

    public HResult SetOutputBounds(long hnsLowerBound, long hnsUpperBound) => HResult.E_NOTIMPL;

    public HResult ProcessEvent(int dwInputStreamID, IMFMediaEvent pEvent) => HResult.E_NOTIMPL;

    // --------------------------------------------------------------------------------------------

    private void RequestInput()
    {
        lock (_lock)
        {
            _inputRequests++;
        }

        _events.Add(MediaEventType.METransformNeedInput);
    }

    /// <summary>Moves whatever the software encoder has finished into the output queue and announces each sample.</summary>
    private void PullOutputs()
    {
        while (true)
        {
            Mf.Check(MFExtern.MFCreateMemoryBuffer(_outputBufferSize, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
            IMFSample sample = Mf.CreateSample(buffer);
            Marshal.ReleaseComObject(buffer);

            IntPtr samplePointer = Marshal.GetComInterfaceForObject(sample, typeof(IMFSample));
            var buffers = new MFTOutputDataBuffer[1];
            buffers[0].pSample = samplePointer;
            HResult hr = _inner.ProcessOutput(MFTProcessOutputFlags.None, 1, buffers, out _);
            Marshal.Release(samplePointer);

            if (Mf.Failed(hr))
            {
                Marshal.ReleaseComObject(sample);
                return; // MF_E_TRANSFORM_NEED_MORE_INPUT: nothing more for now
            }

            lock (_lock)
            {
                _outputs.Enqueue(sample);
            }

            _events.Add(MediaEventType.METransformHaveOutput);
        }
    }
}
