using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace AnyCam.Core.VirtualCamera;

/// <summary>Pixel formats exchanged with the virtual camera (AnyCamVCamShared.h).</summary>
public enum VCamPixelFormat : uint
{
    None = 0,
    Nv12 = 1,
    Bgra = 2,
}

/// <summary>What the app using the virtual camera asked for.</summary>
public readonly record struct VCamRequest(int Width, int Height, VCamPixelFormat Format, int FpsNumerator, int FpsDenominator)
{
    public double Fps => FpsDenominator > 0 ? (double)FpsNumerator / FpsDenominator : 30;
}

/// <summary>
/// Producer side of the shared-memory contract in windows/vcam/include/AnyCamVCamShared.h.
/// The virtual camera (inside the Frame Server service) creates the Global\ objects when an app starts
/// using the camera; this class polls for them, reads what size/format the app selected, and writes
/// frames into the triple buffer with a per-slot sequence lock.
/// </summary>
public sealed unsafe class VirtualCameraOutput : IDisposable
{
    // Layout constants (must match AnyCamVCamShared.h; asserted in the unit tests).
    internal const uint Magic = 0x43564341;
    internal const uint Version = 1;
    internal const int HeaderSize = 4096;
    internal const int SlotCount = 3;
    internal const int MaxWidth = 3840;
    internal const int MaxHeight = 2160;
    internal const int SlotSize = MaxWidth * MaxHeight * 3 / 2;

    internal const int OffMagic = 0;
    internal const int OffVersion = 4;
    internal const int OffSlotSize = 16;
    internal const int OffRequestedWidth = 24;
    internal const int OffRequestedHeight = 28;
    internal const int OffRequestedFormat = 32;
    internal const int OffRequestedFpsNum = 36;
    internal const int OffRequestedFpsDen = 40;
    internal const int OffConsumerState = 44;
    internal const int OffConsumerHeartbeat = 48;
    internal const int OffConsumerPid = 56;
    internal const int OffReadingSlot = 60;
    internal const int OffFramesDelivered = 64;
    internal const int OffProducerHeartbeat = 128;
    internal const int OffProducerPid = 136;
    internal const int OffLatestSlot = 140;
    internal const int OffFramesProduced = 144;
    internal const int OffSlots = 256;
    internal const int SlotInfoSize = 64;

    // SlotInfo field offsets.
    internal const int SlotSeq = 0;
    internal const int SlotWidth = 8;
    internal const int SlotHeight = 12;
    internal const int SlotFormat = 16;
    internal const int SlotStride = 20;
    internal const int SlotDataSize = 24;
    internal const int SlotTimestamp = 32;
    internal const int SlotFrameNumber = 40;

    private const long ConsumerTimeoutMs = 2000;

    private readonly int _cameraIndex;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private EventWaitHandle? _frameReady;
    private byte* _base;
    private long _nextOpenAttempt;
    private int _writingSlot = -1;
    private ulong _frameNumber;

    public VirtualCameraOutput(int cameraIndex = 0) => _cameraIndex = cameraIndex;

    public static string MappingName(int index) => $@"Global\AnyCamVCam{index}_Frames";
    public static string EventName(int index) => $@"Global\AnyCamVCam{index}_FrameReady";

    /// <summary>True while the shared memory is open (an app has used the camera since we started).</summary>
    public bool IsAttached => _base != null;

    /// <summary>
    /// Returns the format an app is currently streaming from the virtual camera, or null if no app
    /// is using it. Cheap to call every frame; reopens the shared memory at most twice a second.
    /// </summary>
    public VCamRequest? Poll()
    {
        if (_base == null && !TryOpen())
            return null;

        var state = Volatile.Read(ref *(uint*)(_base + OffConsumerState));
        var heartbeat = Volatile.Read(ref *(long*)(_base + OffConsumerHeartbeat));
        if (state != 1 || Environment.TickCount64 - heartbeat > ConsumerTimeoutMs)
            return null;

        var width = (int)Volatile.Read(ref *(uint*)(_base + OffRequestedWidth));
        var height = (int)Volatile.Read(ref *(uint*)(_base + OffRequestedHeight));
        var format = (VCamPixelFormat)Volatile.Read(ref *(uint*)(_base + OffRequestedFormat));
        if (width <= 0 || height <= 0 || width > MaxWidth || height > MaxWidth || format == VCamPixelFormat.None)
            return null;
        return new VCamRequest(width, height, format,
            (int)Volatile.Read(ref *(uint*)(_base + OffRequestedFpsNum)),
            (int)Volatile.Read(ref *(uint*)(_base + OffRequestedFpsDen)));
    }

    /// <summary>Number of frames the virtual camera has handed to apps (for stats).</summary>
    public ulong FramesDelivered => _base == null ? 0 : Volatile.Read(ref *(ulong*)(_base + OffFramesDelivered));

    private bool TryOpen()
    {
        var now = Environment.TickCount64;
        if (now < _nextOpenAttempt)
            return false;
        _nextOpenAttempt = now + 500;

        try
        {
            _mapping = MemoryMappedFile.OpenExisting(MappingName(_cameraIndex), MemoryMappedFileRights.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _base = pointer + _view.PointerOffset;
            if (*(uint*)(_base + OffMagic) != Magic || *(uint*)(_base + OffVersion) != Version ||
                *(uint*)(_base + OffSlotSize) != SlotSize)
            {
                Close();
                return false;
            }
            _frameReady = EventWaitHandle.OpenExisting(EventName(_cameraIndex));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            Close();
            return false;
        }
    }

    /// <summary>
    /// Reserves a slot for a new frame. Write <paramref name="height"/> rows of luma (NV12) or BGRA pixels
    /// at <paramref name="destination"/> with row pitch <paramref name="stride"/>; for NV12 the chroma rows
    /// follow directly after the luma plane. Must be followed by <see cref="CommitFrame"/>.
    /// </summary>
    public bool TryBeginFrame(int width, int height, VCamPixelFormat format, out byte* destination, out int stride)
    {
        destination = null;
        stride = format == VCamPixelFormat.Bgra ? width * 4 : width;
        var size = format == VCamPixelFormat.Bgra ? (long)stride * height : (long)stride * height * 3 / 2;
        if (_base == null || size > SlotSize || _writingSlot >= 0)
            return false;

        var latest = Volatile.Read(ref *(int*)(_base + OffLatestSlot));
        var reading = Volatile.Read(ref *(int*)(_base + OffReadingSlot));
        var slot = 0;
        while (slot == latest || slot == reading)
            slot++;

        var info = _base + OffSlots + slot * SlotInfoSize;
        Interlocked.Increment(ref *(long*)(info + SlotSeq)); // odd: being written
        _writingSlot = slot;
        destination = _base + HeaderSize + (long)slot * SlotSize;
        return true;
    }

    public void CommitFrame(int width, int height, VCamPixelFormat format, long timestamp100ns)
    {
        if (_base == null || _writingSlot < 0)
            return;

        var slot = _writingSlot;
        _writingSlot = -1;
        var info = _base + OffSlots + slot * SlotInfoSize;
        var stride = format == VCamPixelFormat.Bgra ? width * 4 : width;
        *(uint*)(info + SlotWidth) = (uint)width;
        *(uint*)(info + SlotHeight) = (uint)height;
        *(uint*)(info + SlotFormat) = (uint)format;
        *(uint*)(info + SlotStride) = (uint)stride;
        *(uint*)(info + SlotDataSize) = (uint)(format == VCamPixelFormat.Bgra ? stride * height : stride * height * 3 / 2);
        *(long*)(info + SlotTimestamp) = timestamp100ns;
        *(ulong*)(info + SlotFrameNumber) = ++_frameNumber;
        Interlocked.Increment(ref *(long*)(info + SlotSeq)); // even: stable (full fence)

        Interlocked.Exchange(ref *(int*)(_base + OffLatestSlot), slot);
        Volatile.Write(ref *(uint*)(_base + OffProducerPid), (uint)Environment.ProcessId);
        Volatile.Write(ref *(long*)(_base + OffProducerHeartbeat), Environment.TickCount64);
        Interlocked.Increment(ref *(long*)(_base + OffFramesProduced));
        _frameReady?.Set();
    }

    /// <summary>Abandons a frame started with <see cref="TryBeginFrame"/> (e.g. a GPU readback failed).</summary>
    public void AbortFrame()
    {
        if (_base == null || _writingSlot < 0)
            return;
        var info = _base + OffSlots + _writingSlot * SlotInfoSize;
        Interlocked.Increment(ref *(long*)(info + SlotSeq));
        _writingSlot = -1;
    }

    /// <summary>Tells the camera we're gone so it shows its placeholder right away.</summary>
    private void MarkStopped()
    {
        if (_base == null)
            return;
        Volatile.Write(ref *(long*)(_base + OffProducerHeartbeat), 0);
        Volatile.Write(ref *(uint*)(_base + OffProducerPid), 0);
    }

    private void Close()
    {
        if (_view is not null && _base != null)
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _base = null;
        _writingSlot = -1;
        _view?.Dispose();
        _view = null;
        _mapping?.Dispose();
        _mapping = null;
        _frameReady?.Dispose();
        _frameReady = null;
    }

    public void Dispose()
    {
        AbortFrame();
        MarkStopped();
        Close();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long SlotOffset(int slot) => HeaderSize + (long)slot * SlotSize;
}
