#include "pch.h"
#include "MediaStream.h"
#include "MediaSource.h"

using Microsoft::WRL::ComPtr;

namespace anycam
{
    namespace
    {
        struct FormatSpec
        {
            uint32_t width;
            uint32_t height;
            uint32_t fps;
            PixelFormat format;
        };

        // The first entry is the default type most apps open. NV12 is what real webcams deliver and
        // what Teams / Chrome / OBS prefer; RGB32 is kept for older DirectShow apps.
        constexpr FormatSpec kFormats[] = {
            { 1920, 1080, 30, PixelFormat_NV12 },
            { 1920, 1080, 60, PixelFormat_NV12 },
            { 1280, 720, 30, PixelFormat_NV12 },
            { 1280, 720, 60, PixelFormat_NV12 },
            { 3840, 2160, 30, PixelFormat_NV12 },
            { 2560, 1440, 30, PixelFormat_NV12 },
            { 1080, 1920, 30, PixelFormat_NV12 }, // vertical (Shorts / Reels / TikTok)
            { 720, 1280, 30, PixelFormat_NV12 },
            { 960, 540, 30, PixelFormat_NV12 },
            { 640, 360, 30, PixelFormat_NV12 },
            { 640, 480, 30, PixelFormat_NV12 },
            { 1920, 1080, 30, PixelFormat_BGRA },
            { 1280, 720, 30, PixelFormat_BGRA },
            { 640, 480, 30, PixelFormat_BGRA },
        };

        HRESULT CreateVideoType(const FormatSpec& spec, IMFMediaType** result)
        {
            ComPtr<IMFMediaType> type;
            AC_RETURN_IF_FAILED(MFCreateMediaType(&type));

            const bool nv12 = spec.format == PixelFormat_NV12;
            const uint32_t stride = nv12 ? spec.width : spec.width * 4;
            const uint32_t sampleSize = nv12 ? spec.width * spec.height * 3 / 2 : spec.width * spec.height * 4;
            const uint64_t bitrate = uint64_t(sampleSize) * 8 * spec.fps;

            AC_RETURN_IF_FAILED(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
            AC_RETURN_IF_FAILED(type->SetGUID(MF_MT_SUBTYPE, nv12 ? MFVideoFormat_NV12 : MFVideoFormat_RGB32));
            AC_RETURN_IF_FAILED(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, spec.width, spec.height));
            AC_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, spec.fps, 1));
            AC_RETURN_IF_FAILED(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_SAMPLE_SIZE, sampleSize));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_DEFAULT_STRIDE, stride));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(bitrate > 0xFFFFFFFFull ? 0xFFFFFFFFull : bitrate)));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709));
            AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709));
            if (nv12)
            {
                AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709));
                AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235));
            }
            else
            {
                AC_RETURN_IF_FAILED(type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_0_255));
            }

            *result = type.Detach();
            return S_OK;
        }

        HRESULT SetDeviceStreamAttributes(IMFAttributes* attributes, DWORD streamId)
        {
            AC_RETURN_IF_FAILED(attributes->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
            AC_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_STREAM_ID, streamId));
            AC_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
            AC_RETURN_IF_FAILED(attributes->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color));
            return S_OK;
        }

        int64_t QpcNow()
        {
            LARGE_INTEGER value;
            QueryPerformanceCounter(&value);
            return value.QuadPart;
        }

        int64_t QpcFrequency()
        {
            static const int64_t frequency = [] {
                LARGE_INTEGER value;
                QueryPerformanceFrequency(&value);
                return value.QuadPart;
            }();
            return frequency;
        }
    }

    MediaStream::~MediaStream()
    {
        StopDeliveryThread();
        if (_timer)
            CloseHandle(_timer);
        if (_wakeEvent)
            CloseHandle(_wakeEvent);
    }

    HRESULT MediaStream::RuntimeClassInitialize(MediaSource* source, DWORD streamId)
    {
        AC_RETURN_HR_IF_NULL(E_INVALIDARG, source);
        AC_RETURN_IF_FAILED(source->QueryInterface(IID_PPV_ARGS(&_source)));
        _streamId = streamId;

        AC_RETURN_IF_FAILED(MFCreateAttributes(&_attributes, 8));
        AC_RETURN_IF_FAILED(SetDeviceStreamAttributes(_attributes.Get(), _streamId));
        AC_RETURN_IF_FAILED(MFCreateEventQueue(&_eventQueue));
        AC_RETURN_IF_FAILED(CreateMediaTypes());

        _timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        if (!_timer)
            _timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
        _wakeEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        AC_RETURN_HR_IF(HRESULT_FROM_WIN32(GetLastError()), !_timer || !_wakeEvent);
        return S_OK;
    }

    HRESULT MediaStream::CreateMediaTypes()
    {
        constexpr DWORD count = ARRAYSIZE(kFormats);
        IMFMediaType* types[count]{};
        HRESULT hr = S_OK;
        for (DWORD i = 0; i < count && SUCCEEDED(hr); i++)
            hr = CreateVideoType(kFormats[i], &types[i]);

        if (SUCCEEDED(hr))
            hr = MFCreateStreamDescriptor(_streamId, count, types, &_descriptor);

        if (SUCCEEDED(hr))
        {
            ComPtr<IMFMediaTypeHandler> handler;
            hr = _descriptor->GetMediaTypeHandler(&handler);
            if (SUCCEEDED(hr))
                hr = handler->SetCurrentMediaType(types[0]);
        }
        if (SUCCEEDED(hr))
            hr = SetDeviceStreamAttributes(_descriptor.Get(), _streamId);

        for (auto type : types)
        {
            if (type)
                type->Release();
        }
        return hr;
    }

    // IMFMediaEventGenerator ------------------------------------------------------------------

    STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->BeginGetEvent(callback, state);
    }

    STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->EndGetEvent(result, event);
    }

    STDMETHODIMP MediaStream::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        // GetEvent can block, so don't hold the lock while waiting.
        ComPtr<IMFMediaEventQueue> queue;
        {
            AutoLock lock(_lock);
            AC_RETURN_IF_FAILED(CheckShutdown());
            queue = _eventQueue;
        }
        return queue->GetEvent(flags, event);
    }

    STDMETHODIMP MediaStream::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* val)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->QueueEventParamVar(type, extendedType, status, val);
    }

    // IMFMediaStream ----------------------------------------------------------------------------

    STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** mediaSource)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, mediaSource);
        *mediaSource = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _source.CopyTo(mediaSource);
    }

    STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** streamDescriptor)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, streamDescriptor);
        *streamDescriptor = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        AC_RETURN_HR_IF_NULL(E_UNEXPECTED, _descriptor);
        return _descriptor.CopyTo(streamDescriptor);
    }

    STDMETHODIMP MediaStream::RequestSample(IUnknown* token)
    {
        {
            AutoLock lock(_lock);
            AC_RETURN_IF_FAILED(CheckShutdown());
            if (_state != MF_STREAM_STATE_RUNNING)
                return MF_E_INVALIDREQUEST;
        }
        {
            std::lock_guard<std::mutex> guard(_requestLock);
            _requests.emplace_back(token);
        }
        _requestSignal.notify_one();
        return S_OK;
    }

    // IMFMediaStream2 ---------------------------------------------------------------------------

    STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE state)
    {
        {
            AutoLock lock(_lock);
            AC_RETURN_IF_FAILED(CheckShutdown());
            if (_state == state)
                return S_OK;

            switch (state)
            {
            case MF_STREAM_STATE_PAUSED:
                if (_state != MF_STREAM_STATE_RUNNING)
                    return MF_E_INVALID_STATE_TRANSITION;
                _state = MF_STREAM_STATE_PAUSED;
                return S_OK;

            case MF_STREAM_STATE_RUNNING:
                return StartInternal(nullptr, false);

            case MF_STREAM_STATE_STOPPED:
                break; // handled below, outside the lock (joins the delivery thread)

            default:
                return MF_E_INVALID_STATE_TRANSITION;
            }
        }
        return Stop(false);
    }

    STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* state)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, state);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        *state = _state;
        return S_OK;
    }

    // IKsControl: no custom properties; mimic a driver that doesn't handle the set. -------------

    STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    // Called by the media source ------------------------------------------------------------------

    HRESULT MediaStream::Start(IMFMediaType* mediaType)
    {
        AC_RETURN_HR_IF_NULL(E_INVALIDARG, mediaType);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return StartInternal(mediaType, true);
    }

    HRESULT MediaStream::StartInternal(IMFMediaType* mediaType, bool sendEvent)
    {
        bool sameType = false;
        if (mediaType && _mediaType)
        {
            BOOL match = FALSE;
            sameType = SUCCEEDED(_mediaType->Compare(mediaType, MF_ATTRIBUTES_MATCH_ALL_ITEMS, &match)) && match;
        }
        if (mediaType)
            _mediaType = mediaType;
        if (!_mediaType)
        {
            ComPtr<IMFMediaTypeHandler> handler;
            AC_RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
            AC_RETURN_IF_FAILED(handler->GetCurrentMediaType(&_mediaType));
        }

        if (_state != MF_STREAM_STATE_RUNNING || !sameType)
        {
            GUID subtype{};
            AC_RETURN_IF_FAILED(_mediaType->GetGUID(MF_MT_SUBTYPE, &subtype));
            AC_RETURN_IF_FAILED(MFGetAttributeSize(_mediaType.Get(), MF_MT_FRAME_SIZE, &_width, &_height));
            if (FAILED(MFGetAttributeRatio(_mediaType.Get(), MF_MT_FRAME_RATE, &_fpsNum, &_fpsDen)) || !_fpsNum || !_fpsDen)
            {
                _fpsNum = 30;
                _fpsDen = 1;
            }
            _format = subtype == MFVideoFormat_NV12 ? PixelFormat_NV12 : PixelFormat_BGRA;

            if (!_allocator)
            {
                // The frame server normally hands us an allocator; fall back to our own.
                AC_RETURN_IF_FAILED(MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&_allocator)));
            }
            AC_RETURN_IF_FAILED(_allocator->InitializeSampleAllocator(10, _mediaType.Get()));

            if (!_bridge.IsOpen())
            {
                HRESULT hr = _bridge.Open(0);
                if (FAILED(hr))
                    VLOG(L"Shared memory unavailable (0x%08X); showing the placeholder only", (unsigned)hr);
            }
            _bridge.SetRequestedFormat(_width, _height, _format, _fpsNum, _fpsDen);

            VLOG(L"Stream started: %ux%u %s @ %u/%u fps", _width, _height,
                 _format == PixelFormat_NV12 ? L"NV12" : L"RGB32", _fpsNum, _fpsDen);
        }

        _bridge.SetConsumerState(ConsumerState_Streaming);
        _state = MF_STREAM_STATE_RUNNING;
        StartDeliveryThread();

        if (sendEvent)
            AC_RETURN_IF_FAILED(_eventQueue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
        return S_OK;
    }

    HRESULT MediaStream::Stop(bool sendEvent)
    {
        {
            AutoLock lock(_lock);
            AC_RETURN_IF_FAILED(CheckShutdown());
            _state = MF_STREAM_STATE_STOPPED;
        }

        StopDeliveryThread();
        FlushRequests();
        _bridge.SetConsumerState(ConsumerState_Idle);
        VLOG(L"Stream stopped after %llu samples", _delivered);

        if (sendEvent)
        {
            AutoLock lock(_lock);
            if (_eventQueue)
                AC_RETURN_IF_FAILED(_eventQueue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
        }
        return S_OK;
    }

    void MediaStream::Shutdown()
    {
        {
            AutoLock lock(_lock);
            if (_shutdown)
                return;
            _shutdown = true;
            _state = MF_STREAM_STATE_STOPPED;
        }

        StopDeliveryThread();
        FlushRequests();
        _bridge.Close();

        AutoLock lock(_lock);
        if (_eventQueue)
        {
            _eventQueue->Shutdown();
            _eventQueue.Reset();
        }
        if (_allocator)
        {
            _allocator->UninitializeSampleAllocator();
            _allocator.Reset();
        }
        _mediaType.Reset();
        _descriptor.Reset();
        _source.Reset();
    }

    HRESULT MediaStream::SetAllocator(IUnknown* allocator)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, allocator);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        if (_state == MF_STREAM_STATE_RUNNING)
            return MF_E_INVALIDREQUEST; // can't swap allocators while streaming

        ComPtr<IMFVideoSampleAllocator> sampleAllocator;
        AC_RETURN_IF_FAILED(allocator->QueryInterface(IID_PPV_ARGS(&sampleAllocator)));
        _allocator = sampleAllocator;
        return S_OK;
    }

    HRESULT MediaStream::GetAttributes(IMFAttributes** attributes)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, attributes);
        *attributes = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _attributes.CopyTo(attributes);
    }

    // Delivery thread ---------------------------------------------------------------------------

    void MediaStream::StartDeliveryThread()
    {
        if (_thread.joinable())
            return;
        {
            std::lock_guard<std::mutex> guard(_requestLock);
            _threadStop = false;
        }
        _thread = std::thread(&MediaStream::DeliveryLoop, this);
    }

    void MediaStream::StopDeliveryThread()
    {
        {
            std::lock_guard<std::mutex> guard(_requestLock);
            _threadStop = true;
        }
        _requestSignal.notify_all();
        if (_wakeEvent)
            SetEvent(_wakeEvent);

        if (_thread.joinable())
        {
            if (_thread.get_id() == std::this_thread::get_id())
                _thread.detach();
            else
                _thread.join();
        }
    }

    void MediaStream::FlushRequests()
    {
        std::lock_guard<std::mutex> guard(_requestLock);
        _requests.clear();
    }

    void MediaStream::DeliveryLoop()
    {
        const HRESULT coInit = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const int64_t frequency = QpcFrequency();
        int64_t lastDelivery = 0;
        uint64_t lastProduced = UINT64_MAX;

        auto waitUntil = [this](int64_t deadline, HANDLE extra) -> bool {
            // Returns true if `extra` (frame ready) fired, false on deadline or wake.
            const int64_t remaining = deadline - QpcNow();
            if (remaining <= 0)
                return false;
            LARGE_INTEGER due;
            due.QuadPart = -static_cast<LONGLONG>(remaining * 10'000'000 / QpcFrequency()); // relative, 100 ns
            if (!SetWaitableTimer(_timer, &due, 0, nullptr, nullptr, FALSE))
                return false;
            HANDLE handles[3] = { _wakeEvent, _timer, extra };
            const DWORD count = extra ? 3 : 2;
            const DWORD result = WaitForMultipleObjects(count, handles, FALSE, INFINITE);
            return result == WAIT_OBJECT_0 + 2;
        };

        for (;;)
        {
            {
                std::unique_lock<std::mutex> guard(_requestLock);
                _requestSignal.wait(guard, [this] { return _threadStop || !_requests.empty(); });
                if (_threadStop)
                    break;
            }

            const int64_t interval = frequency * _fpsDen / (_fpsNum ? _fpsNum : 30);
            const bool producerAlive = _bridge.IsProducerAlive();
            if (producerAlive != _producerWasAlive || !_loggedProducerState)
            {
                VLOG(L"AnyCam Desktop is %s", producerAlive ? L"feeding frames" : L"not running (placeholder)");
                _producerWasAlive = producerAlive;
                _loggedProducerState = true;
            }

            if (lastDelivery != 0)
            {
                // Never run faster than the selected frame rate (allow a little catch-up jitter)...
                waitUntil(lastDelivery + interval * 8 / 10, nullptr);

                // ...and when the app is producing, wait for a fresh frame for up to 1.5 frame times
                // before repeating the previous one.
                if (producerAlive && _bridge.FrameReadyEvent())
                {
                    const int64_t latest = lastDelivery + interval * 3 / 2;
                    while (_bridge.FramesProduced() == lastProduced && QpcNow() < latest)
                    {
                        if (!waitUntil(latest, _bridge.FrameReadyEvent()))
                            break;
                    }
                }
                else
                {
                    waitUntil(lastDelivery + interval, nullptr);
                }
            }

            ComPtr<IUnknown> token;
            {
                std::lock_guard<std::mutex> guard(_requestLock);
                if (_threadStop)
                    break;
                if (_requests.empty())
                    continue;
                token = std::move(_requests.front());
                _requests.pop_front();
            }

            lastProduced = _bridge.FramesProduced();
            bool allocatorEmpty = false;
            const HRESULT hr = DeliverSample(token.Get(), &allocatorEmpty);
            if (allocatorEmpty)
            {
                // The app hasn't returned samples yet: retry this request shortly.
                {
                    std::lock_guard<std::mutex> guard(_requestLock);
                    _requests.emplace_front(std::move(token));
                }
                waitUntil(QpcNow() + frequency / 500, nullptr); // 2 ms
                continue;
            }
            if (FAILED(hr) && hr != MF_E_INVALIDREQUEST && hr != MF_E_SHUTDOWN)
                VLOG(L"DeliverSample failed 0x%08X", (unsigned)hr);
            lastDelivery = QpcNow();
        }

        if (SUCCEEDED(coInit))
            CoUninitialize();
    }

    HRESULT MediaStream::DeliverSample(IUnknown* token, bool* allocatorEmpty)
    {
        *allocatorEmpty = false;

        ComPtr<IMFVideoSampleAllocator> allocator;
        ComPtr<IMFMediaEventQueue> queue;
        {
            AutoLock lock(_lock);
            if (_shutdown)
                return MF_E_SHUTDOWN;
            if (_state != MF_STREAM_STATE_RUNNING || !_allocator || !_eventQueue)
                return MF_E_INVALIDREQUEST;
            allocator = _allocator;
            queue = _eventQueue;
        }

        ComPtr<IMFSample> sample;
        HRESULT hr = allocator->AllocateSample(&sample);
        if (hr == MF_E_SAMPLEALLOCATOR_EMPTY)
        {
            *allocatorEmpty = true;
            return hr;
        }
        AC_RETURN_IF_FAILED(hr);

        ComPtr<IMFMediaBuffer> buffer;
        AC_RETURN_IF_FAILED(sample->GetBufferByIndex(0, &buffer));

        ComPtr<IMF2DBuffer2> buffer2d;
        if (SUCCEEDED(buffer.As(&buffer2d)))
        {
            BYTE* scanline0 = nullptr;
            LONG pitch = 0;
            BYTE* start = nullptr;
            DWORD length = 0;
            AC_RETURN_IF_FAILED(buffer2d->Lock2DSize(MF2DBuffer_LockFlags_Write, &scanline0, &pitch, &start, &length));
            FillBuffer(scanline0, pitch);
            buffer2d->Unlock2D();
        }
        else
        {
            BYTE* data = nullptr;
            DWORD maxLength = 0;
            AC_RETURN_IF_FAILED(buffer->Lock(&data, &maxLength, nullptr));
            const LONG pitch = static_cast<LONG>(_format == PixelFormat_BGRA ? _width * 4 : _width);
            FillBuffer(data, pitch);
            buffer->Unlock();
            buffer->SetCurrentLength(_format == PixelFormat_BGRA ? _width * _height * 4 : _width * _height * 3 / 2);
        }

        AC_RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
        AC_RETURN_IF_FAILED(sample->SetSampleDuration(10'000'000LL * _fpsDen / (_fpsNum ? _fpsNum : 30)));
        if (token)
            AC_RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, token));

        AC_RETURN_IF_FAILED(queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get()));
        _bridge.NoteDelivered();
        _delivered++;
        return S_OK;
    }

    void MediaStream::FillBuffer(uint8_t* scanline0, LONG pitch)
    {
        if (_bridge.IsProducerAlive())
        {
            uint64_t frameNumber = 0;
            if (_bridge.CopyLatest(scanline0, pitch, _width, _height, _format, &frameNumber))
            {
                _lastFrameNumber = frameNumber;
                return;
            }
        }

        const std::vector<uint8_t>& pixels = _placeholder.Get(_width, _height, _format);
        CopyFrame(pixels.data(), _placeholder.Stride(), scanline0, pitch, _width, _height, _format);
    }
}
