using Capture.Interop;

namespace Capture.Audio;

internal enum AudioInputState
{
    /// <summary>Nothing is being recorded or watched, so the device is not open.</summary>
    Idle,

    /// <summary>The device is being opened; some take a few seconds to wake up.</summary>
    Starting,

    /// <summary>The device is open and its audio is in the mix.</summary>
    Capturing,

    /// <summary>The device should be in the mix but cannot be; the status says why.</summary>
    Unavailable,
}

/// <summary>How one entry of the selection is doing.</summary>
/// <param name="Device">The device the entry stands for, when there is one.</param>
/// <param name="Format">What the device delivers, while it is being captured.</param>
internal sealed record AudioInputStatus(string Id, AudioDeviceInfo? Device, AudioInputState State, string? Problem = null, string? Format = null);

/// <summary>
/// Produces the application's audio: a steady stream of 20 ms frames of 8 kHz mono 16-bit PCM,
/// mixed from the selected audio devices (recording devices, and playback devices by loopback),
/// or silence when none is selected.
/// <para>
/// The mixer runs off the session clock, a little behind real time. Each frame covers a fixed
/// window on that clock and is filled from whatever the devices recorded for that window, so the
/// devices stay aligned with each other and with the video, and the selection can be changed while
/// running without a break in the output.
/// </para>
/// <para>
/// Devices are only open while the mixer is active, so nothing listens to a microphone when
/// nobody is recording or watching. Opening a device can take seconds (a monitor's speakers have
/// to wake up), so devices open in the background, side by side, each on a thread of its own, and
/// each joins the mix when it is ready. Only a device someone has just selected is opened while
/// they wait, so that they can be told if it cannot be. While active, the devices are looked at every two seconds: a device that
/// stopped or was unplugged is opened again once it is back, one that would not open is tried
/// again less and less often, and an entry for Windows' default device moves over when the
/// default changes.
/// </para>
/// <para>
/// Threads: <see cref="SetSelection"/> runs on the thread of the request that asked for it,
/// <see cref="SetActive"/> on the capture session's update thread, <see cref="CheckDevices"/> on a
/// timer thread, and each device opens and closes on a thread of its own, reporting back through
/// <c>Opened</c>. Frames are mixed, and <see cref="FrameCaptured"/> raised, on the mixer thread.
/// Status (<see cref="GetInputs"/>, <see cref="Description"/>) can be read from any thread.
/// </para>
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    // How far behind real time a window is mixed when no device is open; otherwise the slowest
    // open device's latency.
    private const long DefaultLatency = 60 * 10_000;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(1);

    /// <summary>How long a newly selected device may take to open before the selection is refused.</summary>
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    private readonly MediaClock _clock;
    private readonly IAudioEndpoints _endpoints;
    private readonly TimeSpan _checkInterval;

    // Taken for every change (selection, activity, the periodic check, a device finishing opening),
    // so that each one decides from a settled picture. Held only while deciding: never while a
    // device opens or closes, which can take seconds.
    private readonly object _changeLock = new();

    // Guards what the mixing thread and status readers see; only ever held briefly. The fields from
    // _open to _latency are read under it and changed under both locks; the mixing position
    // (_nextFrame) belongs to the mixing thread and is reset under it.
    private readonly object _sync = new();

    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private readonly Timer _checkTimer;

    // Under _changeLock only.
    private readonly Dictionary<string, string> _reported = new(AudioDevices.IdComparer); // device ID or entry -> the problem last logged
    private readonly Dictionary<string, Retry> _retries = new(AudioDevices.IdComparer);   // device ID -> when to try opening it again
    private readonly List<Task> _pending = [];                                            // devices opening and closing

    private readonly Dictionary<string, IAudioSource> _open = new(AudioDevices.IdComparer);
    private readonly HashSet<string> _opening = new(AudioDevices.IdComparer);
    private readonly Dictionary<string, string> _openFailures = new(AudioDevices.IdComparer);
    private string[] _selection;
    private Dictionary<string, Resolution> _resolved;
    private bool _active;
    private long _latency = DefaultLatency;

    private long _nextFrame = -1;
    private long _framesProduced;
    private volatile bool _stopping;
    private bool _started;
    private bool _disposed;

    /// <param name="selection">Device IDs and <see cref="AudioSelection.DefaultInput"/> / <see cref="AudioSelection.DefaultOutput"/>.</param>
    /// <param name="checkInterval">How often the devices are looked at while active; tests turn it off (infinite) and call <see cref="CheckDevices"/> themselves.</param>
    public AudioMixer(MediaClock clock, IAudioEndpoints endpoints, IEnumerable<string> selection, TimeSpan? checkInterval = null)
    {
        _clock = clock;
        _checkInterval = checkInterval ?? CheckInterval;
        _endpoints = endpoints;
        _selection = AudioSelection.Normalize(selection);
        _resolved = ResolveAll(_selection);
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "audio-mixer" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _checkTimer = new Timer(_ => CheckDevices(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Raised for every 20 ms frame, in order, on the mixer thread.</summary>
    public event Action<AudioFrame>? FrameCaptured;

    /// <summary>The entries being recorded: device IDs and the stand-ins for the default devices.</summary>
    public IReadOnlyList<string> Selection
    {
        get
        {
            lock (_sync)
            {
                return _selection;
            }
        }
    }

    public long FramesProduced => Interlocked.Read(ref _framesProduced);

    /// <summary>What is being recorded right now, for display.</summary>
    public string Description
    {
        get
        {
            lock (_sync)
            {
                string[] names = [.. _open.Values.Where(source => source.Failure is null).Select(source => source.Name)];
                return names.Length > 0 ? string.Join(" + ", names)
                    : _selection.Length == 0 ? "nothing (silence)"
                    : !_active ? "idle"
                    : _opening.Count > 0 ? "nothing yet: the devices are starting"
                    : "nothing: the selected devices cannot be recorded";
            }
        }
    }

    public void Start()
    {
        _started = true;
        _thread.Start();
    }

    /// <summary>
    /// Selects what to record. Throws <see cref="RefusedException"/>, leaving the current
    /// selection in place, if a newly selected entry cannot be recorded: it names no device, its
    /// device is not connected, or (while active) the device cannot be opened.
    /// </summary>
    public void SetSelection(IEnumerable<string> entries)
    {
        string[] selection = AudioSelection.Normalize(entries);
        if (selection.Length > AudioSelection.MaxEntries)
        {
            throw new RefusedException($"At most {AudioSelection.MaxEntries} audio devices can be selected.");
        }

        // Decided under the lock...
        Dictionary<string, Resolution> resolved;
        AudioDeviceInfo[] needed;
        lock (_changeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string[] added = [.. selection.Except(_selection, AudioDevices.IdComparer)];
            if (added.Length == 0 && selection.Length == _selection.Length)
            {
                return;
            }

            resolved = ResolveAll(selection);
            foreach (string entry in added)
            {
                if (resolved[entry].Problem is { } problem)
                {
                    throw new RefusedException($"{Label(entry, resolved[entry].Device)} cannot be recorded: {problem}.");
                }
            }

            // While active, what was just selected is opened now: if it cannot be, the person is told and nothing changes.
            needed = _active
                ? [.. added.Select(entry => resolved[entry].Device!).DistinctBy(device => device.Id, AudioDevices.IdComparer).Where(device => !IsOpen(device.Id))]
                : [];
        }

        // ...opened outside it, so that nothing else waits for a device that takes seconds to start...
        Dictionary<string, IAudioSource> opened = OpenNow(needed);

        // ...and applied under it again. If another selection was made meanwhile, this one replaces it.
        lock (_changeLock)
        {
            if (_disposed)
            {
                foreach (IAudioSource source in opened.Values)
                {
                    source.Dispose();
                }

                throw new ObjectDisposedException(nameof(AudioMixer));
            }

            var unused = new List<IAudioSource>();
            lock (_sync)
            {
                _selection = selection;
                _resolved = resolved;
                foreach ((string id, IAudioSource source) in opened)
                {
                    if (_open.TryGetValue(id, out IAudioSource? existing))
                    {
                        if (existing.Failure is null)
                        {
                            unused.Add(source); // opened in the background meanwhile
                            continue;
                        }

                        unused.Add(existing); // stopped by itself; replaced
                    }

                    _open[id] = source;
                    _openFailures.Remove(id);
                }

                UpdateLatency();
            }

            foreach ((string id, IAudioSource source) in opened)
            {
                _retries.Remove(id);
                Log.Debug($"Recording {source.Name} ({source.Format}).");
            }

            unused.ForEach(CloseLater);
            Reconcile(); // closes what the new selection no longer needs, and opens what it needs besides
        }
    }

    /// <summary>
    /// Starts producing frames and opening the selected devices, or stops and closes them. Returns
    /// at once: devices open in the background.
    /// </summary>
    public void SetActive(bool active)
    {
        lock (_changeLock)
        {
            if (active == _active || _disposed)
            {
                return;
            }

            // Back on: every device gets a fresh try, and the defaults are looked up again.
            Dictionary<string, Resolution>? resolved = null;
            if (active)
            {
                _retries.Clear();
                resolved = ResolveAll(_selection);
            }

            lock (_sync)
            {
                _active = active;
                _nextFrame = -1;
                _resolved = resolved ?? _resolved;
            }

            Reconcile();
            TimeSpan interval = active ? _checkInterval : Timeout.InfiniteTimeSpan;
            _checkTimer.Change(interval, interval);
        }

        if (active)
        {
            _wake.Set();
        }
    }

    /// <summary>How each entry of the selection is doing.</summary>
    public IReadOnlyList<AudioInputStatus> GetInputs()
    {
        lock (_sync)
        {
            return [.. _selection.Select(StatusOf)];
        }
    }

    /// <summary>
    /// Looks the devices up again: opens devices that stopped or were missing once they are back,
    /// and moves the default-device entries to the current defaults. Runs every two seconds while
    /// active.
    /// </summary>
    internal void CheckDevices()
    {
        if (!Monitor.TryEnter(_changeLock))
        {
            return; // a change is under way, and it ends the same way
        }

        try
        {
            if (!_active || _disposed)
            {
                return;
            }

            Dictionary<string, Resolution> previous = _resolved;
            Dictionary<string, Resolution> resolved = ResolveAll(_selection);
            lock (_sync)
            {
                _resolved = resolved;
            }

            foreach ((string entry, Resolution now) in resolved)
            {
                if (AudioSelection.KindOfDefault(entry) is { } kind
                    && previous.GetValueOrDefault(entry).Device is { } before
                    && now.Device is { } after
                    && !AudioDevices.IdComparer.Equals(before.Id, after.Id))
                {
                    Log.Info($"Windows' default {AudioDevices.Describe(kind)} is now {after.Name}; recording that instead of {before.Name}.");
                }
            }

            Reconcile();
        }
        catch (Exception ex)
        {
            Log.Debug($"Checking the audio devices failed: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_changeLock);
        }
    }

    /// <summary>For tests: waits until no device is opening or closing.</summary>
    internal bool WaitUntilSettled(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            Task[] pending;
            lock (_changeLock)
            {
                _pending.RemoveAll(task => task.IsCompleted);
                pending = [.. _pending];
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            if (pending.Length == 0)
            {
                return true;
            }

            if (left <= TimeSpan.Zero || !Task.WaitAll(pending, left))
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_changeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _checkTimer.Dispose();
        _stopping = true;
        _wake.Set();
        if (_started)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        // Closed here rather than in the background: the process may be about to end. Devices still
        // opening close themselves when they are done (see Opened).
        IAudioSource[] open;
        lock (_changeLock)
        {
            lock (_sync)
            {
                open = [.. _open.Values];
                _open.Clear();
                _active = false;
            }
        }

        foreach (IAudioSource source in open)
        {
            source.Dispose();
        }

        WaitUntilSettled(TimeSpan.FromSeconds(5));
        _wake.Dispose();
    }

    /// <summary>"The default recording device (Microphone (USB Audio))", or the device's name.</summary>
    private static string Label(string entry, AudioDeviceInfo? device)
    {
        if (AudioSelection.KindOfDefault(entry) is { } kind)
        {
            string label = $"The default {AudioDevices.Describe(kind)}";
            return device is null ? label : $"{label} ({device.Name})";
        }

        return device?.Name ?? $"Audio device {entry}";
    }

    private bool IsOpen(string id)
    {
        lock (_sync)
        {
            return _open.TryGetValue(id, out IAudioSource? source) && source.Failure is null;
        }
    }

    /// <summary>Called holding _sync.</summary>
    private AudioInputStatus StatusOf(string entry)
    {
        Resolution resolved = _resolved.GetValueOrDefault(entry);
        if (!_active)
        {
            return new AudioInputStatus(entry, resolved.Device, AudioInputState.Idle);
        }

        if (resolved.Problem is not null || resolved.Device is not { } device)
        {
            return new AudioInputStatus(entry, resolved.Device, AudioInputState.Unavailable, resolved.Problem ?? "unknown device");
        }

        if (_open.TryGetValue(device.Id, out IAudioSource? source))
        {
            return source.Failure is { } failure
                ? new AudioInputStatus(entry, device, AudioInputState.Unavailable, failure.Message)
                : new AudioInputStatus(entry, device, AudioInputState.Capturing, Format: source.Format);
        }

        return !_opening.Contains(device.Id) && _openFailures.TryGetValue(device.Id, out string? failed)
            ? new AudioInputStatus(entry, device, AudioInputState.Unavailable, failed)
            : new AudioInputStatus(entry, device, AudioInputState.Starting);
    }

    private Dictionary<string, Resolution> ResolveAll(string[] selection)
    {
        var resolved = new Dictionary<string, Resolution>(AudioDevices.IdComparer);
        foreach (string entry in selection)
        {
            resolved[entry] = Resolve(entry);
        }

        return resolved;
    }

    /// <summary>Which device an entry stands for right now, or why it stands for none that can be recorded.</summary>
    private Resolution Resolve(string entry)
    {
        try
        {
            if (AudioSelection.KindOfDefault(entry) is { } kind)
            {
                string? id = _endpoints.GetDefaultId(kind);
                AudioDeviceInfo? device = id is null ? null : _endpoints.Get(id);
                return device is null ? new Resolution(null, $"there is no {AudioDevices.Describe(kind)}") : new Resolution(device, null);
            }

            AudioDeviceInfo? known = _endpoints.Get(entry);
            return known is null ? new Resolution(null, "Windows has no audio device with this ID")
                : !known.IsConnected ? new Resolution(known, "not connected")
                : new Resolution(known, null);
        }
        catch (Exception ex)
        {
            return new Resolution(null, ex.Message);
        }
    }

    /// <summary>The devices that should be open: those the selection stands for, while active. A default entry and its device's own entry share one.</summary>
    private Dictionary<string, AudioDeviceInfo> Wanted()
    {
        var wanted = new Dictionary<string, AudioDeviceInfo>(AudioDevices.IdComparer);
        if (_active && !_disposed)
        {
            foreach (Resolution resolution in _resolved.Values)
            {
                if (resolution is { Problem: null, Device: { } device })
                {
                    wanted.TryAdd(device.Id, device);
                }
            }
        }

        return wanted;
    }

    /// <summary>Runs device work on a thread of its own: opening or closing a device can block for seconds, which must not tie up the shared thread pool.</summary>
    private static Task<T> OnOwnThread<T>(Func<T> work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static Task OnOwnThread(Action work) =>
        Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <summary>
    /// Opens devices side by side and waits for all of them, for at most <see cref="OpenTimeout"/>.
    /// If one fails or does not answer, closes the others (and that one, should it ever answer) and
    /// throws why.
    /// </summary>
    private Dictionary<string, IAudioSource> OpenNow(IReadOnlyCollection<AudioDeviceInfo> devices)
    {
        (AudioDeviceInfo Device, Task<IAudioSource> Task)[] opening = [.. devices.Select(device => (device, OnOwnThread(() => _endpoints.Open(device, _clock))))];
        try
        {
            Task.WaitAll([.. opening.Select(item => item.Task)], OpenTimeout);
        }
        catch (AggregateException)
        {
            // Looked at below.
        }

        if (opening.FirstOrDefault(item => !item.Task.IsCompletedSuccessfully) is { Task: not null } failed)
        {
            foreach ((_, Task<IAudioSource> task) in opening)
            {
                _ = task.ContinueWith(done => done.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
            }

            string reason = failed.Task.IsFaulted
                ? failed.Task.Exception!.InnerException!.Message
                : $"it did not respond within {OpenTimeout.TotalSeconds:0} s.";
            throw new RefusedException($"{failed.Device.Name} cannot be recorded: {reason}", failed.Task.Exception?.InnerException);
        }

        return opening.ToDictionary(item => item.Device.Id, item => item.Task.Result, AudioDevices.IdComparer);
    }

    /// <summary>
    /// Brings the open devices in line with what is wanted: closes devices that are not wanted or
    /// stopped by themselves, and starts opening wanted ones that are not open (unless a recent
    /// failure says to wait). Logs lasting problems once. Called holding _changeLock.
    /// </summary>
    private void Reconcile()
    {
        Dictionary<string, AudioDeviceInfo> wanted = Wanted();
        var closing = new List<(string Id, IAudioSource Source)>();
        var toOpen = new List<AudioDeviceInfo>();
        DateTime now = DateTime.UtcNow;

        lock (_sync)
        {
            foreach ((string id, IAudioSource source) in _open.ToArray())
            {
                if (!wanted.ContainsKey(id) || source.Failure is not null)
                {
                    _open.Remove(id);
                    closing.Add((id, source));
                }
            }

            foreach (AudioDeviceInfo device in wanted.Values)
            {
                bool waiting = _retries.TryGetValue(device.Id, out Retry retry) && now < retry.At;
                if (!_open.ContainsKey(device.Id) && !_opening.Contains(device.Id) && !waiting)
                {
                    _opening.Add(device.Id);
                    toOpen.Add(device);
                }
            }

            foreach (string id in _openFailures.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
            {
                _openFailures.Remove(id);
            }

            UpdateLatency();
        }

        foreach ((string id, IAudioSource source) in closing)
        {
            if (source.Failure is { } failure)
            {
                ReportProblem(id, $"{source.Name} stopped: {failure.Message.TrimEnd('.')}.");
            }

            CloseLater(source);
        }

        foreach (AudioDeviceInfo device in toOpen)
        {
            OpenLater(device);
        }

        if (_active)
        {
            foreach ((string entry, Resolution resolution) in _resolved)
            {
                if (resolution.Problem is { } problem)
                {
                    ReportProblem(entry, $"{Label(entry, resolution.Device)} cannot be recorded: {problem}. It is picked up when it is back.");
                }
            }
        }

        // Forget about what is no longer selected, so that it gets a fresh start if it is selected again.
        var keep = new HashSet<string>(_selection, AudioDevices.IdComparer);
        keep.UnionWith(wanted.Keys);
        foreach (string key in _reported.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            _reported.Remove(key);
        }

        foreach (string id in _retries.Keys.Where(id => !wanted.ContainsKey(id)).ToArray())
        {
            _retries.Remove(id);
        }
    }

    /// <summary>Called holding _sync.</summary>
    private void UpdateLatency() => _latency = _open.Count == 0 ? DefaultLatency : _open.Values.Max(source => source.Latency);

    private void OpenLater(AudioDeviceInfo device)
    {
        _pending.RemoveAll(task => task.IsCompleted);
        _pending.Add(OnOwnThread(() =>
        {
            try
            {
                Opened(device, _endpoints.Open(device, _clock), null);
            }
            catch (Exception ex)
            {
                Opened(device, null, ex);
            }
        }));
    }

    private void CloseLater(IAudioSource source)
    {
        _pending.RemoveAll(task => task.IsCompleted);
        _pending.Add(OnOwnThread(source.Dispose));
    }

    /// <summary>A device opened in the background is ready, or could not be opened.</summary>
    private void Opened(AudioDeviceInfo device, IAudioSource? source, Exception? error)
    {
        lock (_changeLock)
        {
            bool wanted = Wanted().ContainsKey(device.Id);
            bool published = false;
            lock (_sync)
            {
                _opening.Remove(device.Id);
                if (source is not null && wanted && !_open.ContainsKey(device.Id))
                {
                    _open[device.Id] = source;
                    _openFailures.Remove(device.Id);
                    UpdateLatency();
                    published = true;
                }
                else if (error is not null && wanted)
                {
                    _openFailures[device.Id] = error.Message;
                }
            }

            if (published)
            {
                _retries.Remove(device.Id);
                Log.Debug($"Recording {device.Name} ({source!.Format}).");

                // Problems logged for the device, or for an entry that stands for it, are over.
                bool recovered = _reported.Remove(device.Id);
                foreach ((string entry, Resolution resolution) in _resolved)
                {
                    recovered |= AudioDevices.IdComparer.Equals(resolution.Device?.Id, device.Id) && _reported.Remove(entry);
                }

                if (recovered)
                {
                    Log.Info($"{device.Name} is being recorded again.");
                }
            }
            else if (source is not null)
            {
                // No longer wanted (deselected, stopped, or the mixer is gone) while it was opening.
                if (_disposed)
                {
                    source.Dispose();
                }
                else
                {
                    CloseLater(source);
                }
            }
            else if (error is not null && wanted)
            {
                TimeSpan delay = _retries.TryGetValue(device.Id, out Retry previous)
                    ? TimeSpan.FromTicks(Math.Min(previous.Delay.Ticks * 2, MaxRetryDelay.Ticks))
                    : CheckInterval;
                _retries[device.Id] = new Retry(DateTime.UtcNow + delay, delay);
                ReportProblem(device.Id, $"{device.Name} cannot be recorded: {error.Message.TrimEnd('.')}. It is tried again from time to time.");
            }
        }
    }

    /// <summary>Logs a problem once rather than at every check. Called holding _changeLock.</summary>
    private void ReportProblem(string key, string message)
    {
        if (_reported.TryGetValue(key, out string? last) && last == message)
        {
            return;
        }

        _reported[key] = message;
        Log.Warn(message);
    }

    private void MixLoop()
    {
        bool highResolutionTimer = false;
        try
        {
            Span<short> scratch = stackalloc short[PcmFormat.SamplesPerFrame];
            Span<int> sum = stackalloc int[PcmFormat.SamplesPerFrame];

            while (!_stopping)
            {
                bool active;
                lock (_sync)
                {
                    active = _active;
                }

                // Sleeping in 5 ms steps needs the 1 ms system timer; only ask for it while mixing.
                if (active != highResolutionTimer)
                {
                    _ = active ? NativeMethods.timeBeginPeriod(1) : NativeMethods.timeEndPeriod(1);
                    highResolutionTimer = active;
                }

                if (!active)
                {
                    _wake.Wait();
                    _wake.Reset();
                    continue;
                }

                while (TryMixNextFrame(scratch, sum) is { } frame)
                {
                    Interlocked.Increment(ref _framesProduced);
                    FrameCaptured?.Invoke(frame);
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Audio mixing stopped: {ex.Message}");
        }
        finally
        {
            if (highResolutionTimer)
            {
                NativeMethods.timeEndPeriod(1);
            }
        }
    }

    /// <summary>Mixes the next 20 ms window if it is far enough in the past to be complete.</summary>
    private AudioFrame? TryMixNextFrame(Span<short> scratch, Span<int> sum)
    {
        lock (_sync)
        {
            if (!_active)
            {
                return null;
            }

            long due = (_clock.Now - _latency) / PcmFormat.FrameDuration;
            if (_nextFrame < 0 || due - _nextFrame > 100)
            {
                // Just (re)started, or stalled for more than two seconds: begin at the present.
                _nextFrame = due;
            }

            if (_nextFrame >= due)
            {
                return null;
            }

            long firstSample = _nextFrame * PcmFormat.SamplesPerFrame;
            sum.Clear();

            foreach (IAudioSource source in _open.Values)
            {
                if (source.Failure is not null)
                {
                    continue; // replaced at the next check
                }

                source.Timeline.Read(firstSample, scratch);
                for (int i = 0; i < sum.Length; i++)
                {
                    sum[i] += scratch[i];
                }
            }

            short[] samples = new short[PcmFormat.SamplesPerFrame];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
            }

            var frame = new AudioFrame { Samples = samples, Timestamp = _nextFrame * PcmFormat.FrameDuration };
            _nextFrame++;
            return frame;
        }
    }

    /// <summary>The device an entry stands for, and why it cannot be recorded if it cannot.</summary>
    private readonly record struct Resolution(AudioDeviceInfo? Device, string? Problem);

    /// <summary>When to try opening a device that failed again, and how long the last wait was.</summary>
    private readonly record struct Retry(DateTime At, TimeSpan Delay);
}
