using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Native = AeroScience.SimConnect.SimConnectTelemetryProvider.Native;

namespace AeroScience.SimConnect;

/// <summary>MSFS 2024 SU5+ CommBus request/reply transport. No HTTP or WebSockets inside Coherent.</summary>
public sealed class CommBusTransport : IDisposable
{
    public const string CommandEvent = "AeroScience.Command.v1";
    public const string ReplyEvent = "AeroScience.Reply.v1";
    public const int ReceiveIdCommBus = 43; // Asserted against the installed official SDK by the Windows SDK contract check.
    private const uint SubscriptionId = 100;
    private IntPtr handle;
    private readonly Native.Dispatch callback;
    private readonly Queue<string> queue = new();
    private bool quit;
    private string? failure;
    private readonly string replyEvent;

    public CommBusTransport(string commandEvent = CommandEvent, string responseEvent = ReplyEvent)
    {
        replyEvent = responseEvent;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("CommBus requires MSFS 2024 on Windows x64.");
        callback = OnMessage;
        Check(Native.Open(out handle, "AeroScience toolbar bridge", IntPtr.Zero, 0, IntPtr.Zero, 0));
        try { Check(Subscribe(handle, SubscriptionId, commandEvent)); }
        catch { Dispose(); throw; }
    }

    // Only read/copy bounded commands in the native callback. Database work happens after dispatch.
    private void OnMessage(IntPtr data, uint size, IntPtr context)
    {
        try
        {
            if (size < 12) return;
            var id = Marshal.ReadInt32(data, 8);
            if (id == 3) { quit = true; return; }
            if (id == 1) { failure = "CommBus subscription returned a SimConnect exception; check the SU5+ SDK/runtime version."; return; }
            if (id != ReceiveIdCommBus || size is < 33 or > 4128 || queue.Count >= 16) return;
            var bytes = new byte[size]; Marshal.Copy(data, bytes, 0, bytes.Length);
            var command = DecodeCommand(bytes);
            if (command is not null) queue.Enqueue(command);
        }
        catch (ArgumentException) { /* Invalid incoming packet cannot crash the native callback. */ }
    }

    public IReadOnlyList<string> Poll()
    {
        Check(Native.CallDispatch(handle, callback, IntPtr.Zero));
        if (quit || failure is not null) throw new IOException(failure ?? "Simulator disconnected from the toolbar bridge.");
        var commands = queue.ToArray(); queue.Clear(); return commands;
    }

    public void Reply(string json)
    {
        var buffer = Encoding.UTF8.GetBytes(json + "\0");
        if (buffer.Length > 8192) throw new InvalidOperationException("Toolbar state exceeds its bounded payload size.");
        // JS only. Send a response only when an active panel asks, avoiding a backlog while JS is halted.
        Check(Call(handle, replyEvent, 1, (uint)buffer.Length, buffer));
    }

    public static string? DecodeCommand(ReadOnlySpan<byte> packet)
    {
        if (packet.Length is < 33 or > 4128) return null;
        // Fixed 32-byte SIMCONNECT_RECV_COMM_BUS header, then UTF-8 data. Commands must fit one message.
        if (At(packet, 0) != packet.Length || At(packet, 8) != ReceiveIdCommBus || At(packet, 28) != SubscriptionId || At(packet, 24) > 1 || At(packet, 20) != 0) return null;
        var body = packet[32..]; var end = body.IndexOf((byte)0);
        if (end >= 0) body = body[..end];
        try { return new UTF8Encoding(false, true).GetString(body); }
        catch (DecoderFallbackException) { return null; }
    }
    private static uint At(ReadOnlySpan<byte> packet, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        Native.Close(handle); handle = IntPtr.Zero;
    }
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    [DllImport("SimConnect.dll", EntryPoint = "SimConnect_SubscribeToCommBusEvent", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int Subscribe(IntPtr handle, uint eventId, string eventName);
    [DllImport("SimConnect.dll", EntryPoint = "SimConnect_CallCommBusEvent", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int Call(IntPtr handle, string eventName, uint broadcastTo, uint size, byte[] buffer);
}
