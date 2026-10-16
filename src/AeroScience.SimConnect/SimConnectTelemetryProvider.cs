using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AeroScience.Shared;
using AeroScience.Telemetry;

namespace AeroScience.SimConnect;

/// <summary>
/// Native, read-only SimConnect client. Calls are serialized on one polling loop, with no UI dependency.
/// SDK runtime is supplied by the Windows developer/user, never downloaded from an unofficial source.
/// Real simulator verification is required; see docs/simconnect.md.
/// </summary>
public sealed class SimConnectTelemetryProvider : ITelemetryProvider
{
    public async IAsyncEnumerable<TelemetryUpdate> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            yield return Status(ConnectionState.Unavailable, "SimConnect requires Windows x64. Select Simulated telemetry on this platform.");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            yield return Status(ConnectionState.Initializing, "Connecting to MSFS through the local SimConnect runtime…");
            NativeSession? session = null;
            string? failure = null;
            try { session = new NativeSession(); }
            catch (Exception e) when (IsConnectionError(e)) { failure = Explain(e); }
            if (session is not null)
            {
                using (session)
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        IReadOnlyList<TelemetryUpdate> updates = [];
                        try { updates = session.Poll(); }
                        catch (Exception e) when (IsConnectionError(e)) { failure = Explain(e); }
                        if (failure is not null) break;
                        foreach (var update in updates) yield return update;
                        await Task.Delay(50, cancellationToken);
                    }
                }
            }
            if (failure is not null)
                yield return Status(ConnectionState.Disconnected, failure + " Retrying in 5 seconds.");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private static bool IsConnectionError(Exception e) => e is DllNotFoundException or BadImageFormatException
        or EntryPointNotFoundException or COMException or IOException;
    private static string Explain(Exception e) => e switch
    {
        DllNotFoundException => "The official x64 SimConnect.dll runtime is missing beside AeroScience.Engine.exe.",
        BadImageFormatException => "The SimConnect runtime does not match Windows x64.",
        COMException => $"SimConnect is unavailable (HRESULT 0x{e.HResult:X8}); start MSFS and load a flight.",
        _ => e.Message
    };
    internal static TelemetryUpdate Status(ConnectionState state, string message) => new(
        new(state, TelemetrySource.Simulator, message, DateTimeOffset.UtcNow), null);

    private sealed class NativeSession : IDisposable
    {
        private IntPtr handle;
        private readonly Native.Dispatch callback;
        private readonly List<TelemetryUpdate> updates = [];
        private bool running;
        private bool paused;
        private bool quit;
        private string? sdkError;
        private DateTimeOffset lastFrame = DateTimeOffset.UtcNow;
        private bool reportedStale;

        public NativeSession()
        {
            callback = OnMessage;
            Check(Native.Open(out handle, "AeroScience 0.1.0", IntPtr.Zero, 0, IntPtr.Zero, 0));
            try
            {
                Check(Native.AddToDataDefinition(handle, 1, "TITLE", null, 9, 0, uint.MaxValue));
                foreach (var variable in VariableDefinitions)
                    Check(Native.AddToDataDefinition(handle, 1, variable.Name, variable.Unit, 4, 0, uint.MaxValue));
                Check(Native.SubscribeToSystemEvent(handle, 10, "SimStart"));
                Check(Native.SubscribeToSystemEvent(handle, 11, "SimStop"));
                Check(Native.SubscribeToSystemEvent(handle, 12, "Pause"));
                Check(Native.SubscribeToSystemEvent(handle, 13, "AircraftLoaded"));
                Check(Native.SubscribeToSystemEvent(handle, 14, "FlightLoaded"));
                Check(Native.RequestSystemState(handle, 1, "Sim"));
                Check(Native.RequestDataOnSimObject(handle, 2, 1, 0, 4, 0, 0, 0, 0));
            }
            catch { Dispose(); throw; }
        }

        public IReadOnlyList<TelemetryUpdate> Poll()
        {
            updates.Clear();
            Check(Native.CallDispatch(handle, callback, IntPtr.Zero));
            if (quit) throw new IOException("MSFS closed its SimConnect session.");
            if (sdkError is not null) throw new IOException(sdkError);
            if (DateTimeOffset.UtcNow - lastFrame > TimeSpan.FromSeconds(8) && !reportedStale)
            {
                updates.Add(Status(ConnectionState.Initializing, paused
                    ? "Simulator paused; waiting for fresh aircraft telemetry."
                    : "Waiting for initialized flight telemetry (menus, loading, or session transition)."));
                reportedStale = true;
            }
            return updates.ToArray();
        }

        private void OnMessage(IntPtr data, uint size, IntPtr context)
        {
            // Never throw across the unmanaged callback boundary.
            try
            {
                if (size < 12) return;
                var id = Marshal.ReadInt32(data, 8);
                switch (id)
                {
                    case 1 when size >= 24:
                        sdkError = $"SimConnect SDK exception {Marshal.ReadInt32(data, 12)} (send {Marshal.ReadInt32(data, 16)}).";
                        break;
                    case 3: quit = true; break;
                    case 4 when size >= 24:
                        var eventId = Marshal.ReadInt32(data, 16);
                        var value = Marshal.ReadInt32(data, 20);
                        if (eventId == 10) running = true;
                        if (eventId == 11)
                        {
                            running = false;
                            updates.Add(Status(ConnectionState.Initializing, "Simulator stopped; waiting for a flight."));
                        }
                        if (eventId == 12) paused = value != 0;
                        break;
                    case 6: // EVENT_FILENAME: aircraft or flight loading; query current Sim state again.
                        running = false;
                        Check(Native.RequestSystemState(handle, 1, "Sim"));
                        updates.Add(Status(ConnectionState.Initializing, "Aircraft/flight changed; waiting for initialized data."));
                        break;
                    case 15 when size >= 24: // SYSTEM_STATE
                        if (Marshal.ReadInt32(data, 12) == 1) running = Marshal.ReadInt32(data, 16) == 1;
                        break;
                    case 8 when running && size >= 40 + 352: // SIMOBJECT_DATA, fixed header then TITLE + 12 doubles
                        if (Marshal.ReadInt32(data, 12) != 2 || Marshal.ReadInt32(data, 20) != 1) break;
                        var raw = Marshal.PtrToStructure<AircraftData>(IntPtr.Add(data, 40));
                        var frame = Decode(raw, DateTimeOffset.UtcNow, paused);
                        if (!frame.IsValid) break;
                        lastFrame = frame.TimestampUtc;
                        reportedStale = false;
                        updates.Add(new(new(ConnectionState.Connected, TelemetrySource.Simulator,
                            paused ? "MSFS connected · paused" : "MSFS aircraft telemetry connected", frame.TimestampUtc), frame));
                        break;
                }
            }
            catch (Exception e) { sdkError = $"Unable to process a SimConnect message: {e.Message}"; }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            Native.Close(handle);
            handle = IntPtr.Zero;
        }
    }

    public static readonly (string Name, string Unit)[] VariableDefinitions =
    [
        ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"), ("PLANE ALT ABOVE GROUND", "feet"),
        ("AIRSPEED INDICATED", "knots"), ("AIRSPEED TRUE", "knots"), ("GROUND VELOCITY", "knots"),
        ("PLANE HEADING DEGREES TRUE", "degrees"), ("GPS GROUND TRUE TRACK", "degrees"),
        ("PLANE PITCH DEGREES", "degrees"), ("PLANE BANK DEGREES", "degrees"), ("VERTICAL SPEED", "feet per minute")
    ];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct AircraftData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Title;
        public double Latitude, Longitude, AltitudeMsl, AltitudeAgl, Ias, Tas, Groundspeed,
            Heading, Track, Pitch, Bank, VerticalSpeed;
    }

    public static TelemetryFrame Decode(AircraftData raw, DateTimeOffset now, bool paused) => new(
        now.ToUniversalTime(), TelemetrySource.Simulator, string.IsNullOrWhiteSpace(raw.Title) ? "Unknown MSFS aircraft" : raw.Title.Trim(), raw.Latitude, raw.Longitude,
        raw.AltitudeMsl, double.IsFinite(raw.AltitudeAgl) ? raw.AltitudeAgl : null, raw.Ias, raw.Tas, raw.Groundspeed,
        NormalizeHeading(raw.Heading), NormalizeHeading(raw.Track), -raw.Pitch, -raw.Bank, raw.VerticalSpeed, paused);
    private static double NormalizeHeading(double degrees) => (degrees % 360 + 360) % 360;
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

    internal static class Native
    {
        private const string Library = "SimConnect.dll";
        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, assembly, searchPath) =>
            {
                if (name != Library) return IntPtr.Zero;
                var path = Path.Combine(AppContext.BaseDirectory, Library);
                return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
            });
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void Dispatch(IntPtr data, uint size, IntPtr context);
        [DllImport(Library, EntryPoint = "SimConnect_Open", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int Open(out IntPtr handle, string name, IntPtr window, uint message, IntPtr eventHandle, uint configIndex);
        [DllImport(Library, EntryPoint = "SimConnect_Close", ExactSpelling = true)]
        internal static extern int Close(IntPtr handle);
        [DllImport(Library, EntryPoint = "SimConnect_AddToDataDefinition", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int AddToDataDefinition(IntPtr handle, uint definitionId, string name, string? units, uint type, float epsilon, uint datumId);
        [DllImport(Library, EntryPoint = "SimConnect_RequestDataOnSimObject", ExactSpelling = true)]
        internal static extern int RequestDataOnSimObject(IntPtr handle, uint requestId, uint definitionId, uint objectId, uint period, uint flags, uint origin, uint interval, uint limit);
        [DllImport(Library, EntryPoint = "SimConnect_SubscribeToSystemEvent", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int SubscribeToSystemEvent(IntPtr handle, uint eventId, string eventName);
        [DllImport(Library, EntryPoint = "SimConnect_RequestSystemState", CharSet = CharSet.Ansi, ExactSpelling = true)]
        internal static extern int RequestSystemState(IntPtr handle, uint requestId, string state);
        [DllImport(Library, EntryPoint = "SimConnect_CallDispatch", ExactSpelling = true)]
        internal static extern int CallDispatch(IntPtr handle, Dispatch callback, IntPtr context);
    }
}
