using System.Runtime.InteropServices;
using Capture.Audio;
using Capture.Interop;
using Capture.Video;

namespace Capture;

internal static class Program
{
    private static int Main(string[] args)
    {
        // Must happen before anything asks Windows for monitor sizes.
        NativeMethods.EnableDpiAwareness();

        Options options;
        try
        {
            options = Options.Load(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"capture: {ex.Message}");
            Console.Error.WriteLine("Run 'capture --help' for the list of options.");
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        Log.Verbose = options.Verbose;
        if (options.ConfigFile is not null && !options.ListDevices)
        {
            Log.Info($"Configuration: {options.ConfigFile}");
        }

        try
        {
            Mf.Startup();
        }
        catch (Exception ex)
        {
            Log.Error($"Media Foundation is not available: {ex.Message}");
            return 1;
        }

        try
        {
            if (options.ListDevices)
            {
                ListDevices();
                return 0;
            }

            return Run(options);
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            Log.Debug(ex.ToString());
            return 1;
        }
        finally
        {
            Mf.Shutdown();
        }
    }

    private static int Run(Options options)
    {
        using var stopRequested = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);

        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C stops cleanly so the MP4 gets finalized; a second one ends the process.
            if (!stopRequested.IsSet)
            {
                e.Cancel = true;
                stopRequested.Set();
            }
        };

        // Closing the console window or logging off: Windows ends the process as soon as the
        // handler returns, so hold it until the recording has been finalized.
        void OnTerminate(PosixSignalContext context)
        {
            context.Cancel = true;
            stopRequested.Set();
            finished.Wait(TimeSpan.FromSeconds(4));
        }

        using PosixSignalRegistration closeRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminate);
        using PosixSignalRegistration terminateRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminate);

        try
        {
            using var session = new CaptureSession(options);
            session.Faulted += _ => stopRequested.Set();
            session.Start();

            Log.Info(options.DurationSeconds is { } duration
                ? $"Running for {duration:0.#} s. Press Ctrl+C to stop early."
                : "Running. Press Ctrl+C to stop.");

            if (options.DurationSeconds is { } seconds)
            {
                stopRequested.Wait(TimeSpan.FromSeconds(seconds));
            }
            else
            {
                stopRequested.Wait();
            }

            Log.Info("Stopping...");
            session.Dispose();
            Log.Info("Stopped.");
            return session.Failure is null ? 0 : 1;
        }
        finally
        {
            finished.Set();
        }
    }

    private static void ListDevices()
    {
        Console.WriteLine("Screens (--screens):");
        foreach (DisplayInfo display in DisplayInfo.Enumerate())
        {
            string primary = display.IsPrimary ? "  [primary]" : string.Empty;
            Console.WriteLine($"  {display.Number}: {display.DeviceName}  {display.Width}x{display.Height} at ({display.Left},{display.Top}){primary}");
        }

        Console.WriteLine();
        Console.WriteLine("Audio devices (--audio-device):");
        IReadOnlyList<AudioDeviceInfo> audioDevices = AudioDevices.Enumerate();
        foreach ((AudioDeviceKind kind, string heading) in new[] { (AudioDeviceKind.Input, "Recording"), (AudioDeviceKind.Output, "Playback, recorded as what they play") })
        {
            Console.WriteLine($"  {heading}:");
            AudioDeviceInfo[] devices = [.. audioDevices.Where(device => device.Kind == kind)];
            if (devices.Length == 0)
            {
                Console.WriteLine("    (none)");
            }

            foreach (AudioDeviceInfo device in devices)
            {
                Console.WriteLine($"    {device.Number}: {device.Name}{(device.IsDefault ? "  [default]" : string.Empty)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("H.264 encoders (--encoder):");
        IReadOnlyList<string> hardware = H264Encoder.ListHardwareEncoders();
        foreach (string name in hardware)
        {
            Console.WriteLine($"  hardware: {name}");
        }

        if (hardware.Count == 0)
        {
            Console.WriteLine("  hardware: (none found)");
        }

        Console.WriteLine("  software: Microsoft H264 Encoder MFT");
    }
}
