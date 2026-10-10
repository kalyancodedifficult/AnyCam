#include "pch.h"
#include "FrameBridge.h"

namespace anycam
{
    namespace
    {
        // SYSTEM, Administrators and LocalService get full access; any authenticated user (the desktop
        // app runs as the logged-on user, at medium integrity) can read/write frames and set the event.
        constexpr wchar_t kSecurityDescriptor[] =
            L"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GA;;;AU)";

        struct LocalFreeDeleter
        {
            void operator()(void* p) const { LocalFree(p); }
        };
    }

    HRESULT FrameBridge::Open(uint32_t cameraIndex)
    {
        if (_header)
            return S_OK;

        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(kSecurityDescriptor, SDDL_REVISION_1, &descriptor, nullptr))
            return HRESULT_FROM_WIN32(GetLastError());
        std::unique_ptr<void, LocalFreeDeleter> descriptorOwner(descriptor);

        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };

        wchar_t name[128];
        FramesMappingName(name, ARRAYSIZE(name), cameraIndex);
        _mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE,
                                      static_cast<DWORD>(kMappingSize >> 32), static_cast<DWORD>(kMappingSize), name);
        if (!_mapping)
        {
            HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
            VLOG(L"CreateFileMapping(%s) failed 0x%08X", name, (unsigned)hr);
            return hr;
        }
        const bool existed = GetLastError() == ERROR_ALREADY_EXISTS;

        _header = static_cast<SharedHeader*>(MapViewOfFile(_mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        if (!_header)
        {
            HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
            Close();
            return hr;
        }

        FrameReadyEventName(name, ARRAYSIZE(name), cameraIndex);
        _frameReady = CreateEventW(&attributes, FALSE, FALSE, name);
        if (!_frameReady)
        {
            HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
            VLOG(L"CreateEvent(%s) failed 0x%08X", name, (unsigned)hr);
            Close();
            return hr;
        }

        if (!existed || _header->magic != kSharedMagic || _header->version != kSharedVersion)
        {
            ZeroMemory(_header, kHeaderSize);
            _header->headerSize = kHeaderSize;
            _header->slotCount = kSlotCount;
            _header->slotSize = kSlotSize;
            _header->latestSlot = -1;
            _header->readingSlot = -1;
            _header->version = kSharedVersion;
            MemoryBarrier();
            _header->magic = kSharedMagic;
        }
        _header->consumerPid = GetCurrentProcessId();
        _header->readingSlot = -1;

        VLOG(L"FrameBridge opened camera %u (mapping %s)", cameraIndex, existed ? L"reused" : L"created");
        return S_OK;
    }

    void FrameBridge::Close()
    {
        if (_header)
        {
            _header->consumerState = ConsumerState_Idle;
            _header->readingSlot = -1;
            UnmapViewOfFile(_header);
            _header = nullptr;
        }
        if (_frameReady)
        {
            CloseHandle(_frameReady);
            _frameReady = nullptr;
        }
        if (_mapping)
        {
            CloseHandle(_mapping);
            _mapping = nullptr;
        }
    }

    void FrameBridge::SetRequestedFormat(uint32_t width, uint32_t height, PixelFormat format, uint32_t fpsNum, uint32_t fpsDen)
    {
        if (!_header)
            return;
        _header->requestedWidth = width;
        _header->requestedHeight = height;
        _header->requestedFormat = format;
        _header->requestedFpsNum = fpsNum;
        _header->requestedFpsDen = fpsDen ? fpsDen : 1;
        MemoryBarrier();
    }

    void FrameBridge::SetConsumerState(ConsumerState state)
    {
        if (!_header)
            return;
        _header->consumerState = state;
        _header->consumerHeartbeat = static_cast<int64_t>(GetTickCount64());
        MemoryBarrier();
    }

    void FrameBridge::NoteDelivered()
    {
        if (!_header)
            return;
        _header->consumerHeartbeat = static_cast<int64_t>(GetTickCount64());
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&_header->framesDelivered));
    }

    bool FrameBridge::IsProducerAlive() const
    {
        if (!_header || _header->producerPid == 0 || _header->latestSlot < 0)
            return false;
        const int64_t age = static_cast<int64_t>(GetTickCount64()) - _header->producerHeartbeat;
        return age >= 0 && static_cast<uint64_t>(age) < kProducerTimeoutMs;
    }

    uint64_t FrameBridge::FramesProduced() const
    {
        return _header ? _header->framesProduced : 0;
    }

    bool FrameBridge::CopyLatest(uint8_t* destination, LONG destinationPitch, uint32_t width, uint32_t height,
                                 PixelFormat format, uint64_t* frameNumber)
    {
        if (!_header)
            return false;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            const int32_t slot = _header->latestSlot;
            if (slot < 0 || slot >= static_cast<int32_t>(kSlotCount))
                return false;

            InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->readingSlot), slot);

            const SlotInfo& info = _header->slots[slot];
            const int64_t seqBefore = InterlockedCompareExchange64(const_cast<volatile LONG64*>(&info.seq), 0, 0);
            if (seqBefore & 1)
                continue; // being written right now; the producer will pick another slot next time

            if (info.width != width || info.height != height || info.format != static_cast<uint32_t>(format) ||
                info.stride < (format == PixelFormat_BGRA ? width * 4 : width) || info.dataSize > kSlotSize)
            {
                InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->readingSlot), -1);
                return false;
            }

            const uint64_t number = info.frameNumber;
            CopyFrame(SlotData(_header, slot), info.stride, destination, destinationPitch, width, height, format);

            MemoryBarrier();
            const int64_t seqAfter = InterlockedCompareExchange64(const_cast<volatile LONG64*>(&info.seq), 0, 0);
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->readingSlot), -1);
            if (seqAfter == seqBefore)
            {
                if (frameNumber)
                    *frameNumber = number;
                return true;
            }
        }

        InterlockedExchange(reinterpret_cast<volatile LONG*>(&_header->readingSlot), -1);
        return false;
    }

    void CopyFrame(const uint8_t* source, uint32_t sourceStride, uint8_t* destination, LONG destinationPitch,
                   uint32_t width, uint32_t height, PixelFormat format)
    {
        if (format == PixelFormat_BGRA)
        {
            const size_t rowBytes = size_t(width) * 4;
            for (uint32_t y = 0; y < height; y++)
                memcpy(destination + LONG_PTR(y) * destinationPitch, source + size_t(y) * sourceStride, rowBytes);
            return;
        }

        // NV12: luma rows, then interleaved chroma rows (height / 2) directly after the luma plane.
        const uint8_t* sourceChroma = source + size_t(sourceStride) * height;
        uint8_t* destinationChroma = destination + LONG_PTR(destinationPitch) * height;
        for (uint32_t y = 0; y < height; y++)
            memcpy(destination + LONG_PTR(y) * destinationPitch, source + size_t(y) * sourceStride, width);
        for (uint32_t y = 0; y < height / 2; y++)
            memcpy(destinationChroma + LONG_PTR(y) * destinationPitch, sourceChroma + size_t(y) * sourceStride, width);
    }
}
