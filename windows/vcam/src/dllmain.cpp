#include "pch.h"
#include "Activator.h"
#include <wrl/module.h>

using Microsoft::WRL::ComPtr;

HMODULE g_module = nullptr;

namespace
{
    constexpr wchar_t kClsidKey[] = L"Software\\Classes\\CLSID\\" ANYCAM_VCAM_CLSID_STRING;
    constexpr wchar_t kInprocKey[] = L"Software\\Classes\\CLSID\\" ANYCAM_VCAM_CLSID_STRING L"\\InprocServer32";

    class ClassFactory : public Microsoft::WRL::RuntimeClass<
                             Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                             IClassFactory,
                             Microsoft::WRL::FtmBase>
    {
    public:
        STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            AC_RETURN_HR_IF_NULL(E_POINTER, object);
            *object = nullptr;
            if (outer)
                return CLASS_E_NOAGGREGATION;

            ComPtr<anycam::Activator> activator;
            AC_RETURN_IF_FAILED(Microsoft::WRL::MakeAndInitialize<anycam::Activator>(&activator));
            return activator.CopyTo(riid, object);
        }

        STDMETHODIMP LockServer(BOOL lock) override
        {
            auto& module = Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule();
            if (lock)
                module.IncrementObjectCount();
            else
                module.DecrementObjectCount();
            return S_OK;
        }
    };

    LSTATUS SetStringValue(HKEY key, const wchar_t* name, const wchar_t* value)
    {
        return RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value),
                              static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    }

    class ScopedMFStartup
    {
    public:
        ScopedMFStartup()
        {
            const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            _uninitializeCom = SUCCEEDED(co); // RPC_E_CHANGED_MODE: caller already has an STA, that's fine
            _hr = MFStartup(MF_VERSION, MFSTARTUP_LITE);
        }
        ~ScopedMFStartup()
        {
            if (SUCCEEDED(_hr))
                MFShutdown();
            if (_uninitializeCom)
                CoUninitialize();
        }
        HRESULT Result() const { return _hr; }

    private:
        HRESULT _hr = E_FAIL;
        bool _uninitializeCom = false;
    };

    HRESULT CreateVirtualCamera(BOOL systemLifetime, BOOL allUsers, LPCWSTR friendlyName, IMFVirtualCamera** camera)
    {
        return MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource,
                                     systemLifetime ? MFVirtualCameraLifetime_System : MFVirtualCameraLifetime_Session,
                                     allUsers ? MFVirtualCameraAccess_AllUsers : MFVirtualCameraAccess_CurrentUser,
                                     friendlyName && *friendlyName ? friendlyName : ANYCAM_VCAM_FRIENDLY_NAME,
                                     ANYCAM_VCAM_CLSID_STRING, nullptr, 0, camera);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

// COM entry points -----------------------------------------------------------------------------

_Use_decl_annotations_ STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* object)
{
    AC_RETURN_HR_IF_NULL(E_POINTER, object);
    *object = nullptr;
    if (clsid != CLSID_AnyCamVCam)
        return CLASS_E_CLASSNOTAVAILABLE;

    // Creating the module makes every WRL object count towards DllCanUnloadNow.
    Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule();

    ComPtr<ClassFactory> factory = Microsoft::WRL::Make<ClassFactory>();
    AC_RETURN_HR_IF_NULL(E_OUTOFMEMORY, factory);
    return factory.CopyTo(riid, object);
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    return Microsoft::WRL::Module<Microsoft::WRL::InProc>::GetModule().GetObjectCount() == 0 ? S_OK : S_FALSE;
}

// Registration must go to HKLM: the media source is loaded by the Frame Server (LocalService) and
// Frame Server Monitor (LocalSystem) services, which don't see the user's HKCU. Run regsvr32 elevated.
STDAPI DllRegisterServer()
{
    const std::wstring path = anycam::ModulePath();

    HKEY key = nullptr;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kClsidKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);
    status = SetStringValue(key, nullptr, L"AnyCam Virtual Camera");
    RegCloseKey(key);
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);

    status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, kInprocKey, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);
    status = SetStringValue(key, nullptr, path.c_str());
    if (status == ERROR_SUCCESS)
        status = SetStringValue(key, L"ThreadingModel", L"Both");
    RegCloseKey(key);

    VLOG(L"DllRegisterServer %s -> %u", path.c_str(), (unsigned)status);
    return HRESULT_FROM_WIN32(status);
}

STDAPI DllUnregisterServer()
{
    const LSTATUS status = RegDeleteTreeW(HKEY_LOCAL_MACHINE, kClsidKey);
    VLOG(L"DllUnregisterServer -> %u", (unsigned)status);
    return status == ERROR_FILE_NOT_FOUND ? S_OK : HRESULT_FROM_WIN32(status);
}

// Helpers used by AnyCam Desktop (P/Invoke) and AnyCamVCamCtl --------------------------------

// Creates (or re-opens) the virtual camera and starts it so apps can see it.
//  systemLifetime: TRUE  = stays registered after the calling process exits,
//                  FALSE = removed when the returned object is released.
//  allUsers:       TRUE requires administrator rights.
//  virtualCamera:  optional; receives the IMFVirtualCamera (keep it alive for session lifetime).
extern "C" HRESULT __stdcall AnyCamVCam_Register(BOOL systemLifetime, BOOL allUsers, LPCWSTR friendlyName, IUnknown** virtualCamera)
{
    if (virtualCamera)
        *virtualCamera = nullptr;

    ScopedMFStartup mf;
    AC_RETURN_IF_FAILED(mf.Result());

    ComPtr<IMFVirtualCamera> camera;
    AC_RETURN_IF_FAILED(CreateVirtualCamera(systemLifetime, allUsers, friendlyName, &camera));
    AC_RETURN_IF_FAILED(camera->Start(nullptr));
    VLOG(L"Virtual camera registered (system=%d, allUsers=%d)", systemLifetime, allUsers);

    if (virtualCamera)
        return camera.CopyTo(virtualCamera);
    return S_OK;
}

// Removes the virtual camera created with the same parameters.
extern "C" HRESULT __stdcall AnyCamVCam_Remove(BOOL systemLifetime, BOOL allUsers, LPCWSTR friendlyName)
{
    ScopedMFStartup mf;
    AC_RETURN_IF_FAILED(mf.Result());

    ComPtr<IMFVirtualCamera> camera;
    AC_RETURN_IF_FAILED(CreateVirtualCamera(systemLifetime, allUsers, friendlyName, &camera));
    const HRESULT hr = camera->Remove();
    VLOG(L"Virtual camera removed (system=%d, allUsers=%d) -> 0x%08X", systemLifetime, allUsers, (unsigned)hr);
    return hr;
}

// S_OK if the COM class is registered in HKLM (path receives the registered DLL), S_FALSE if not.
extern "C" HRESULT __stdcall AnyCamVCam_GetRegistration(LPWSTR path, UINT32 pathLength)
{
    if (path && pathLength)
        path[0] = L'\0';

    wchar_t value[MAX_PATH]{};
    DWORD size = sizeof(value);
    const LSTATUS status = RegGetValueW(HKEY_LOCAL_MACHINE, kInprocKey, nullptr, RRF_RT_REG_SZ, nullptr, value, &size);
    if (status == ERROR_FILE_NOT_FOUND)
        return S_FALSE;
    if (status != ERROR_SUCCESS)
        return HRESULT_FROM_WIN32(status);
    if (path && pathLength)
        StringCchCopyW(path, pathLength, value);
    return S_OK;
}
