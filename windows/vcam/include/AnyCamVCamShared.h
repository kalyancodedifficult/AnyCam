// AnyCamVCamShared.h
//
// Shared-memory contract between AnyCam Desktop (the *producer*, runs in the user's session) and
// AnyCamVCam.dll (the *consumer*, a Media Foundation media source hosted by the Windows Camera
// Frame Server service in session 0).
//
// The virtual camera creates the objects (only a service can create Global\ objects) with an ACL
// that lets interactive users open them; the desktop app opens them while they exist.
//
//   Global\AnyCamVCam<N>_Frames      file mapping: SharedHeader + kSlotCount frame slots
//   Global\AnyCamVCam<N>_FrameReady  auto-reset event, set by the producer after each frame
//
// Frames are exchanged through a triple buffer protected by a per-slot sequence lock:
//   producer: pick a slot that is neither `latestSlot` nor `readingSlot`; seq++ (odd = writing);
//             write pixels + slot info; seq++ (even = stable); latestSlot = slot; SetEvent(FrameReady).
//   consumer: s = latestSlot; readingSlot = s; read seq (must be even); copy; re-read seq — if it
//             changed the copy is torn and is discarded; readingSlot = -1.
//
// The C# mirror of this file lives in windows/src/AnyCam.Core/VirtualCamera/VCamShared.cs.
// Keep both in sync (offsets are asserted on both sides).

#pragma once
#include <stdint.h>
#include <stdio.h>

namespace anycam
{
    constexpr uint32_t kSharedMagic = 0x43564341; // 'ACVC'
    constexpr uint32_t kSharedVersion = 1;
    constexpr uint32_t kHeaderSize = 4096;
    constexpr uint32_t kSlotCount = 3;

    // Largest frame a slot can hold: 3840x2160 NV12 (12,441,600 bytes).
    // BGRA frames are only advertised up to 1920x1080 (8,294,400 bytes) so they fit as well.
    constexpr uint32_t kMaxWidth = 3840;
    constexpr uint32_t kMaxHeight = 2160;
    constexpr uint32_t kSlotSize = kMaxWidth * kMaxHeight * 3 / 2;
    constexpr uint64_t kMappingSize = uint64_t(kHeaderSize) + uint64_t(kSlotCount) * kSlotSize;

    // A producer heartbeat older than this means "the app is not feeding frames": show the placeholder.
    constexpr uint64_t kProducerTimeoutMs = 1500;

    enum PixelFormat : uint32_t
    {
        PixelFormat_None = 0,
        PixelFormat_NV12 = 1, // Y plane (stride * height) followed by interleaved UV plane (stride * height / 2)
        PixelFormat_BGRA = 2, // 32-bit B,G,R,A top-down, stride = width * 4
    };

    enum ConsumerState : uint32_t
    {
        ConsumerState_Idle = 0,
        ConsumerState_Streaming = 1,
    };

#pragma pack(push, 8)
    struct SlotInfo // 64 bytes
    {
        volatile int64_t seq;   // sequence lock: odd while the producer writes this slot
        uint32_t width;
        uint32_t height;
        uint32_t format;        // PixelFormat
        uint32_t stride;        // bytes per row (luma plane for NV12)
        uint32_t dataSize;      // bytes of pixel data in the slot
        uint32_t reserved0;
        int64_t timestamp;      // producer time, 100 ns units (QPC based, like MFGetSystemTime)
        uint64_t frameNumber;
        uint8_t reserved1[16];
    };

    struct SharedHeader
    {
        // Written once by the virtual camera when it creates the mapping.
        uint32_t magic;                       // 0
        uint32_t version;                     // 4
        uint32_t headerSize;                  // 8
        uint32_t slotCount;                   // 12
        uint32_t slotSize;                    // 16
        uint32_t reserved0;                   // 20

        // Consumer (virtual camera) -> producer.
        volatile uint32_t requestedWidth;     // 24  media type chosen by the app using the camera
        volatile uint32_t requestedHeight;    // 28
        volatile uint32_t requestedFormat;    // 32  PixelFormat
        volatile uint32_t requestedFpsNum;    // 36
        volatile uint32_t requestedFpsDen;    // 40
        volatile uint32_t consumerState;      // 44  ConsumerState
        volatile int64_t consumerHeartbeat;   // 48  GetTickCount64() at the last delivered sample
        volatile uint32_t consumerPid;        // 56
        volatile int32_t readingSlot;         // 60  slot being copied, -1 if none
        volatile uint64_t framesDelivered;    // 64
        uint8_t reserved1[56];                // 72

        // Producer (AnyCam Desktop) -> consumer.
        volatile int64_t producerHeartbeat;   // 128 GetTickCount64() at the last produced frame
        volatile uint32_t producerPid;        // 136
        volatile int32_t latestSlot;          // 140 newest complete slot, -1 if none
        volatile uint64_t framesProduced;     // 144
        uint8_t reserved2[104];               // 152

        SlotInfo slots[kSlotCount];           // 256
    };
#pragma pack(pop)

    static_assert(sizeof(SlotInfo) == 64, "SlotInfo layout");
    static_assert(sizeof(SharedHeader) == 256 + 64 * kSlotCount, "SharedHeader layout");
    static_assert(sizeof(SharedHeader) <= kHeaderSize, "SharedHeader too large");

    inline uint8_t* SlotData(void* mappingBase, uint32_t slot)
    {
        return static_cast<uint8_t*>(mappingBase) + kHeaderSize + uint64_t(slot) * kSlotSize;
    }

    // Object names. `index` is the virtual camera instance (0 for "AnyCam").
    inline void FramesMappingName(wchar_t* buffer, size_t count, uint32_t index)
    {
        _snwprintf_s(buffer, count, _TRUNCATE, L"Global\\AnyCamVCam%u_Frames", index);
    }

    inline void FrameReadyEventName(wchar_t* buffer, size_t count, uint32_t index)
    {
        _snwprintf_s(buffer, count, _TRUNCATE, L"Global\\AnyCamVCam%u_FrameReady", index);
    }
}
