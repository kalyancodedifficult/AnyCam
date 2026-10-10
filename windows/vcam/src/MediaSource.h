#pragma once

namespace anycam
{
    class MediaStream;

    // Media Foundation media source for the AnyCam virtual camera (one color stream).
    // Structure follows Microsoft's Windows-Camera VirtualCamera sample (SimpleMediaSource).
    class MediaSource : public Microsoft::WRL::RuntimeClass<
                            Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                            Microsoft::WRL::ChainInterfaces<IMFMediaSource2, IMFMediaSourceEx, IMFMediaSource, IMFMediaEventGenerator>,
                            IMFGetService,
                            IKsControl,
                            IMFSampleAllocatorControl,
                            Microsoft::WRL::FtmBase>
    {
    public:
        MediaSource();
        ~MediaSource() override; // defined in the .cpp where MediaStream is complete

        HRESULT RuntimeClassInitialize(IMFAttributes* activateAttributes);

        // IMFMediaEventGenerator
        STDMETHODIMP BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state) override;
        STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event) override;
        STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** event) override;
        STDMETHODIMP QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* val) override;

        // IMFMediaSource
        STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** presentationDescriptor) override;
        STDMETHODIMP GetCharacteristics(DWORD* characteristics) override;
        STDMETHODIMP Pause() override;
        STDMETHODIMP Shutdown() override;
        STDMETHODIMP Start(IMFPresentationDescriptor* presentationDescriptor, const GUID* timeFormat, const PROPVARIANT* startPosition) override;
        STDMETHODIMP Stop() override;

        // IMFMediaSourceEx
        STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override;
        STDMETHODIMP GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes) override;
        STDMETHODIMP SetD3DManager(IUnknown* manager) override;

        // IMFMediaSource2
        STDMETHODIMP SetMediaType(DWORD streamId, IMFMediaType* mediaType) override;

        // IMFGetService
        STDMETHODIMP GetService(REFGUID service, REFIID riid, LPVOID* object) override;

        // IKsControl
        STDMETHODIMP_(NTSTATUS) KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID propertyData, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP_(NTSTATUS) KsMethod(PKSMETHOD method, ULONG methodLength, LPVOID methodData, ULONG dataLength, ULONG* bytesReturned) override;
        STDMETHODIMP_(NTSTATUS) KsEvent(PKSEVENT event, ULONG eventLength, LPVOID eventData, ULONG dataLength, ULONG* bytesReturned) override;

        // IMFSampleAllocatorControl
        STDMETHODIMP SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator) override;
        STDMETHODIMP GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage) override;

    private:
        enum class State
        {
            Stopped,
            Started,
            Shutdown,
        };

        HRESULT CheckShutdown() const { return _state == State::Shutdown ? MF_E_SHUTDOWN : S_OK; }

        CriticalSection _lock;
        State _state = State::Stopped;
        Microsoft::WRL::ComPtr<IMFAttributes> _attributes;
        Microsoft::WRL::ComPtr<IMFMediaEventQueue> _eventQueue;
        Microsoft::WRL::ComPtr<IMFPresentationDescriptor> _presentationDescriptor;
        Microsoft::WRL::ComPtr<MediaStream> _stream;
    };
}
