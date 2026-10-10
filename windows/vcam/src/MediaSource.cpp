#include "pch.h"
#include "MediaSource.h"
#include "MediaStream.h"

using Microsoft::WRL::ComPtr;

namespace anycam
{
    namespace
    {
        constexpr DWORD kStreamId = 0;
    }

    MediaSource::MediaSource() = default;
    MediaSource::~MediaSource() = default;

    HRESULT MediaSource::RuntimeClassInitialize(IMFAttributes* activateAttributes)
    {
        AC_RETURN_IF_FAILED(MFCreateAttributes(&_attributes, 4));
        if (activateAttributes)
            AC_RETURN_IF_FAILED(activateAttributes->CopyAllItems(_attributes.Get()));

        // Sensor profiles: the legacy profile is mandatory so profile-unaware apps keep working.
        ComPtr<IMFSensorProfileCollection> profiles;
        ComPtr<IMFSensorProfile> profile;
        AC_RETURN_IF_FAILED(MFCreateSensorProfileCollection(&profiles));
        AC_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_Legacy, 0, nullptr, &profile));
        AC_RETURN_IF_FAILED(profile->AddProfileFilter(kStreamId, L"((RES==;FRT<=30,1;SUT==))"));
        AC_RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
        profile.Reset();
        AC_RETURN_IF_FAILED(MFCreateSensorProfile(KSCAMERAPROFILE_HighFrameRate, 0, nullptr, &profile));
        AC_RETURN_IF_FAILED(profile->AddProfileFilter(kStreamId, L"((RES==;FRT>=60,1;SUT==))"));
        AC_RETURN_IF_FAILED(profiles->AddProfile(profile.Get()));
        AC_RETURN_IF_FAILED(_attributes->SetUnknown(MF_DEVICEMFT_SENSORPROFILE_COLLECTION, profiles.Get()));

        AC_RETURN_IF_FAILED(MFCreateEventQueue(&_eventQueue));
        AC_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaStream>(&_stream, this, kStreamId));

        ComPtr<IMFStreamDescriptor> streamDescriptor;
        AC_RETURN_IF_FAILED(_stream->GetStreamDescriptor(&streamDescriptor));
        IMFStreamDescriptor* descriptors[] = { streamDescriptor.Get() };
        AC_RETURN_IF_FAILED(MFCreatePresentationDescriptor(1, descriptors, &_presentationDescriptor));

        _state = State::Stopped;
        return S_OK;
    }

    // IMFMediaEventGenerator ------------------------------------------------------------------

    STDMETHODIMP MediaSource::BeginGetEvent(IMFAsyncCallback* callback, IUnknown* state)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->BeginGetEvent(callback, state);
    }

    STDMETHODIMP MediaSource::EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** event)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->EndGetEvent(result, event);
    }

    STDMETHODIMP MediaSource::GetEvent(DWORD flags, IMFMediaEvent** event)
    {
        ComPtr<IMFMediaEventQueue> queue;
        {
            AutoLock lock(_lock);
            AC_RETURN_IF_FAILED(CheckShutdown());
            queue = _eventQueue;
        }
        return queue->GetEvent(flags, event);
    }

    STDMETHODIMP MediaSource::QueueEvent(MediaEventType type, REFGUID extendedType, HRESULT status, const PROPVARIANT* val)
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _eventQueue->QueueEventParamVar(type, extendedType, status, val);
    }

    // IMFMediaSource ----------------------------------------------------------------------------

    STDMETHODIMP MediaSource::CreatePresentationDescriptor(IMFPresentationDescriptor** presentationDescriptor)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, presentationDescriptor);
        *presentationDescriptor = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        return _presentationDescriptor->Clone(presentationDescriptor);
    }

    STDMETHODIMP MediaSource::GetCharacteristics(DWORD* characteristics)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, characteristics);
        *characteristics = 0;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        *characteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }

    STDMETHODIMP MediaSource::Pause()
    {
        return MF_E_INVALID_STATE_TRANSITION;
    }

    STDMETHODIMP MediaSource::Shutdown()
    {
        AutoLock lock(_lock);
        if (_state == State::Shutdown)
            return S_OK;
        _state = State::Shutdown;
        VLOG(L"MediaSource::Shutdown");

        if (_stream)
        {
            _stream->Shutdown();
            _stream.Reset();
        }
        if (_eventQueue)
        {
            _eventQueue->Shutdown();
            _eventQueue.Reset();
        }
        _presentationDescriptor.Reset();
        _attributes.Reset();
        return S_OK;
    }

    STDMETHODIMP MediaSource::Start(IMFPresentationDescriptor* presentationDescriptor, const GUID* timeFormat, const PROPVARIANT* startPosition)
    {
        AC_RETURN_HR_IF_NULL(E_INVALIDARG, presentationDescriptor);
        AC_RETURN_HR_IF_NULL(E_INVALIDARG, startPosition);
        if (timeFormat && *timeFormat != GUID_NULL)
            return MF_E_UNSUPPORTED_TIME_FORMAT;

        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());

        DWORD count = 0;
        AC_RETURN_IF_FAILED(presentationDescriptor->GetStreamDescriptorCount(&count));
        AC_RETURN_HR_IF(E_INVALIDARG, count != 1);

        PROPVARIANT startTime;
        AC_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &startTime));

        HRESULT hr = S_OK;
        for (DWORD i = 0; i < count && SUCCEEDED(hr); i++)
        {
            BOOL selected = FALSE;
            ComPtr<IMFStreamDescriptor> descriptor;
            hr = presentationDescriptor->GetStreamDescriptorByIndex(i, &selected, &descriptor);
            if (FAILED(hr))
                break;

            DWORD streamId = 0;
            hr = descriptor->GetStreamIdentifier(&streamId);
            if (FAILED(hr))
                break;
            if (streamId != _stream->Id())
            {
                hr = E_INVALIDARG;
                break;
            }

            BOOL wasSelected = FALSE;
            ComPtr<IMFStreamDescriptor> ownDescriptor;
            hr = _presentationDescriptor->GetStreamDescriptorByIndex(0, &wasSelected, &ownDescriptor);
            if (FAILED(hr))
                break;

            if (selected)
            {
                hr = _presentationDescriptor->SelectStream(0);
                ComPtr<IMFMediaTypeHandler> handler;
                ComPtr<IMFMediaType> mediaType;
                if (SUCCEEDED(hr))
                    hr = descriptor->GetMediaTypeHandler(&handler);
                if (SUCCEEDED(hr))
                    hr = handler->GetCurrentMediaType(&mediaType);

                ComPtr<IUnknown> streamUnknown;
                if (SUCCEEDED(hr))
                    hr = _stream.As(&streamUnknown);
                if (SUCCEEDED(hr))
                    hr = _eventQueue->QueueEventParamUnk(wasSelected ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK, streamUnknown.Get());
                if (SUCCEEDED(hr))
                    hr = _stream->Start(mediaType.Get()); // queues MEStreamStarted
            }
            else if (wasSelected)
            {
                hr = _presentationDescriptor->DeselectStream(0);
                if (SUCCEEDED(hr))
                    hr = _stream->Stop(false);
            }
        }

        if (SUCCEEDED(hr))
            hr = _eventQueue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &startTime);
        PropVariantClear(&startTime);

        if (SUCCEEDED(hr))
            _state = State::Started;
        else
            VLOG(L"MediaSource::Start failed 0x%08X", (unsigned)hr);
        return hr;
    }

    STDMETHODIMP MediaSource::Stop()
    {
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        if (_state != State::Started)
            return MF_E_INVALID_STATE_TRANSITION;

        PROPVARIANT stopTime;
        AC_RETURN_IF_FAILED(InitPropVariantFromInt64(MFGetSystemTime(), &stopTime));

        HRESULT hr = _stream->Stop(true);
        if (SUCCEEDED(hr))
            hr = _presentationDescriptor->DeselectStream(0);
        if (SUCCEEDED(hr))
            hr = _eventQueue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, &stopTime);
        PropVariantClear(&stopTime);

        _state = State::Stopped;
        return hr;
    }

    // IMFMediaSourceEx ----------------------------------------------------------------------------

    STDMETHODIMP MediaSource::GetSourceAttributes(IMFAttributes** attributes)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, attributes);
        *attributes = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        // Must return a reference to our store (not a copy): upstream components modify it.
        return _attributes.CopyTo(attributes);
    }

    STDMETHODIMP MediaSource::GetStreamAttributes(DWORD streamIdentifier, IMFAttributes** attributes)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, attributes);
        *attributes = nullptr;
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        if (streamIdentifier != _stream->Id())
            return MF_E_INVALIDSTREAMNUMBER;
        return _stream->GetAttributes(attributes);
    }

    STDMETHODIMP MediaSource::SetD3DManager(IUnknown*)
    {
        // Best effort hint from the pipeline; we deliver system-memory samples.
        return E_NOTIMPL;
    }

    // IMFMediaSource2 -----------------------------------------------------------------------------

    STDMETHODIMP MediaSource::SetMediaType(DWORD streamId, IMFMediaType* mediaType)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, mediaType);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        UINT32 width = 0, height = 0;
        MFGetAttributeSize(mediaType, MF_MT_FRAME_SIZE, &width, &height);
        VLOG(L"SetMediaType stream %u: %ux%u", streamId, width, height);
        return S_OK;
    }

    // IMFGetService -------------------------------------------------------------------------------

    STDMETHODIMP MediaSource::GetService(REFGUID, REFIID, LPVOID* object)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, object);
        *object = nullptr;
        return MF_E_UNSUPPORTED_SERVICE;
    }

    // IKsControl ----------------------------------------------------------------------------------

    STDMETHODIMP_(NTSTATUS) MediaSource::KsProperty(PKSPROPERTY property, ULONG propertyLength, LPVOID, ULONG, ULONG*)
    {
        if (!property || propertyLength < sizeof(KSPROPERTY))
            return E_INVALIDARG;
        // No camera controls are exposed through KS; the phone is controlled from AnyCam Desktop.
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP_(NTSTATUS) MediaSource::KsMethod(PKSMETHOD, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    STDMETHODIMP_(NTSTATUS) MediaSource::KsEvent(PKSEVENT, ULONG, LPVOID, ULONG, ULONG*)
    {
        return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
    }

    // IMFSampleAllocatorControl -----------------------------------------------------------------

    STDMETHODIMP MediaSource::SetDefaultAllocator(DWORD outputStreamId, IUnknown* allocator)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, allocator);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        if (outputStreamId != _stream->Id())
            return MF_E_INVALIDSTREAMNUMBER;
        return _stream->SetAllocator(allocator);
    }

    STDMETHODIMP MediaSource::GetAllocatorUsage(DWORD outputStreamId, DWORD* inputStreamId, MFSampleAllocatorUsage* usage)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, inputStreamId);
        AC_RETURN_HR_IF_NULL(E_POINTER, usage);
        AutoLock lock(_lock);
        AC_RETURN_IF_FAILED(CheckShutdown());
        if (outputStreamId != _stream->Id())
            return MF_E_INVALIDSTREAMNUMBER;
        *inputStreamId = outputStreamId;
        *usage = MFSampleAllocatorUsage_UsesProvidedAllocator;
        return S_OK;
    }
}
