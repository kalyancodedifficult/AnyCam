#pragma once

namespace anycam
{
    // Consumer side of the shared-memory frame exchange (see AnyCamVCamShared.h).
    // Lives inside the Frame Server; creates the Global\ objects that AnyCam Desktop opens.
    class FrameBridge
    {
    public:
        FrameBridge() = default;
        ~FrameBridge() { Close(); }
        FrameBridge(const FrameBridge&) = delete;
        FrameBridge& operator=(const FrameBridge&) = delete;

        HRESULT Open(uint32_t cameraIndex);
        void Close();
        bool IsOpen() const { return _header != nullptr; }

        // Tell the producer which media type the consuming app selected.
        void SetRequestedFormat(uint32_t width, uint32_t height, PixelFormat format, uint32_t fpsNum, uint32_t fpsDen);
        void SetConsumerState(ConsumerState state);
        void NoteDelivered();

        // True when AnyCam Desktop has written a frame recently.
        bool IsProducerAlive() const;
        uint64_t FramesProduced() const;

        // Auto-reset event set by the producer after each frame (null if not open).
        HANDLE FrameReadyEvent() const { return _frameReady; }

        // Copies the newest frame into `destination` if it matches the requested size/format.
        // Returns false if there is no suitable frame or the copy was torn.
        bool CopyLatest(uint8_t* destination, LONG destinationPitch, uint32_t width, uint32_t height,
                        PixelFormat format, uint64_t* frameNumber);

    private:
        HANDLE _mapping = nullptr;
        HANDLE _frameReady = nullptr;
        SharedHeader* _header = nullptr;
    };

    // Copies one frame of `format` between buffers with different row pitches.
    void CopyFrame(const uint8_t* source, uint32_t sourceStride, uint8_t* destination, LONG destinationPitch,
                   uint32_t width, uint32_t height, PixelFormat format);
}
