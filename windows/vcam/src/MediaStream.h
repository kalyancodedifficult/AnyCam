#pragma once
#include "FrameBridge.h"
#include "Placeholder.h"

namespace anycam
{
    class MediaSource;

    // The single video stream of the virtual camera. Samples are delivered from a dedicated thread
    // that paces output to the selected frame rate and hands out the newest frame AnyCam Desktop
    // wrote to shared memory (or a placeholder when the app isn't running).
    class MediaStream : public Microsoft::WRL::RuntimeClass<
                            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                            Microsoft::WRL::ChainInterfaces<IMFMediaStream2, IMFMediaStream, IMFMediaEventGenerator>,
                            IKsControl,
                            Microsoft::WRL::FtmBase>
    {
    public:
        MediaStream() = default;
        ~MediaStream() override;

        HRESULT RuntimeClassInitialize(MediaSource* source, DWORD streamId);

        // IMFMediaEventGenerator
        STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* val) override;

        // IMFMediaStream
        STDMETHODIMP GetMediaSource(IMFMediaSource** mediaSource) override;
        STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** streamDescriptor) override;
        STDMETHODIMP RequestSample(IUnknown* token) override;

        // IMFMediaStream2
        STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override;
        STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override;

        // IKsControl
        STDMETHODIMP_(NTSTATUS) KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID propertyData, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP_(NTSTATUS) KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID methodData, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP_(NTSTATUS) KsEvent(PKSEVENT event, ULONG eventLength, LPVOID eventData, ULONG dataLength, ULONG* bytesReturned) override;

        // Called by MediaSource.
        HRESULT Start(IMFMediaType* mediaType);
        HRESULT Stop(bool sendEvent);
        void Shutdown();
        HRESULT SetAllocator(IUnknown* allocator);
        HRESULT GetAttributes(IMFAttributes** attributes);
        DWORD Id() const { return _streamId; }
        bool IsRunning() const { return _state == MF_STREAM_STATE_RUNNING; }

    private:
        HRESULT CreateMediaTypes();
        HRESULT CheckShutdown() const { return _shutdown ? MF_E_SHUTDOWN : S_OK; }
        HRESULT StartInternal(IMFMediaType* mediaType, bool sendEvent);
        void StopInternal();
        void StartDeliveryThread();
        void StopDeliveryThread();
        void DeliveryLoop();
        HRESULT DeliverSample(IUnknown* token, bool* allocatorEmpty);
        void FillBuffer(uint8_t* scanline0, LONG pitch);
        void FlushRequests();

        CriticalSection _lock;
        Microsoft::WRL::ComPtr<IMFMediaEventQueue> _eventQueue;
        Microsoft::WRL::ComPtr<IMFStreamDescriptor> _descriptor;
        Microsoft::WRL::ComPtr<IMFAttributes> _attributes;
        Microsoft::WRL::ComPtr<IMFMediaSource> _source; // cycle broken in Shutdown()
        Microsoft::WRL::ComPtr<IMFVideoSampleAllocator> _allocator;
        Microsoft::WRL::ComPtr<IMFMediaType> _mediaType;
        DWORD _streamId = 0;
        MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
        bool _shutdown = false;

        // Current output format.
        uint32_t _width = 0;
        uint32_t _height = 0;
        PixelFormat _format = PixelFormat_None;
        uint32_t _fpsNum = 30;
        uint32_t _fpsDen = 1;

        // Delivery thread state (guarded by _requestLock).
        std::mutex _requestLock;
        std::condition_variable _requestSignal;
        std::deque<Microsoft::WRL::ComPtr<IUnknown>> _requests;
        bool _threadStop = false;
        std::thread _thread;
        HANDLE _timer = nullptr;
        HANDLE _wakeEvent = nullptr;

        FrameBridge _bridge;
        Placeholder _placeholder;
        std::vector<uint8_t> _lastFrame; // last good frame, repeated when the producer is slower than the consumer
        bool _lastFrameValid = false;
        uint64_t _lastFrameNumber = 0;
        uint64_t _delivered = 0;
        bool _loggedProducerState = false;
        bool _producerWasAlive = false;
    };
}
