#include "pch.h"
#include "Activator.h"
#include "MediaSource.h"

using Microsoft::WRL::ComPtr;

namespace anycam
{
    Activator::Activator() = default;
    Activator::~Activator() = default;

    HRESULT Activator::RuntimeClassInitialize()
    {
        AC_RETURN_IF_FAILED(MFCreateAttributes(&_attributes, 4));
        AC_RETURN_IF_FAILED(_attributes->SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
        AC_RETURN_IF_FAILED(_attributes->SetGUID(MFT_TRANSFORM_CLSID_Attribute, CLSID_AnyCamVCam));
        return S_OK;
    }

    STDMETHODIMP Activator::ActivateObject(REFIID riid, void** object)
    {
        AC_RETURN_HR_IF_NULL(E_POINTER, object);
        *object = nullptr;

        AutoLock lock(_lock);
        if (!_source)
        {
            UINT32 clientPid = 0;
            if (SUCCEEDED(_attributes->GetUINT32(ANYCAM_MF_FRAMESERVER_CLIENTCONTEXT_CLIENTPID, &clientPid)) && clientPid)
                VLOG(L"ActivateObject for client process %u", clientPid);
            else
                VLOG(L"ActivateObject");

            AC_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<MediaSource>(&_source, _attributes.Get()));
        }
        return _source->QueryInterface(riid, object);
    }

    STDMETHODIMP Activator::ShutdownObject()
    {
        AutoLock lock(_lock);
        if (_source)
        {
            _source->Shutdown();
            _source.Reset();
        }
        return S_OK;
    }

    STDMETHODIMP Activator::DetachObject()
    {
        AutoLock lock(_lock);
        _source.Reset();
        return S_OK;
    }
}
