#pragma once

// {E39221D4-60F2-41F3-B09B-3156B16205BB}
DEFINE_GUID(CLSID_AnyCamVCam, 0xe39221d4, 0x60f2, 0x41f3, 0xb0, 0x9b, 0x31, 0x56, 0xb1, 0x62, 0x05, 0xbb);

#define ANYCAM_VCAM_CLSID_STRING L"{E39221D4-60F2-41F3-B09B-3156B16205BB}"
#define ANYCAM_VCAM_FRIENDLY_NAME L"AnyCam"

// Undocumented frame-server attribute carrying the process id of the app that opened the camera.
// Only used for logging.
DEFINE_GUID(ANYCAM_MF_FRAMESERVER_CLIENTCONTEXT_CLIENTPID, 0x5f8d322e, 0x0fe4, 0x43e4, 0x9e, 0x50, 0xd8, 0x3e, 0xcd, 0x9f, 0xc2, 0xb8);

extern HMODULE g_module;

namespace anycam::log
{
    // Writes to OutputDebugString and, if C:\ProgramData\AnyCam\logs exists and is writable,
    // to vcam-<process>.log in that folder (the Frame Server runs as LocalService).
    void Write(_Printf_format_string_ const wchar_t* format, ...);
}

#define VLOG(...) ::anycam::log::Write(__VA_ARGS__)

#define AC_RETURN_IF_FAILED(expr)                                                                   \
    do                                                                                              \
    {                                                                                               \
        HRESULT _hr_ = (expr);                                                                      \
        if (FAILED(_hr_))                                                                           \
        {                                                                                           \
            VLOG(L"%hs(%d): '%hs' failed 0x%08X", __FILE__, __LINE__, #expr, (unsigned)_hr_);       \
            return _hr_;                                                                            \
        }                                                                                           \
    } while (0)

#define AC_RETURN_HR_IF(hr, condition)                                                              \
    do                                                                                              \
    {                                                                                               \
        if (condition)                                                                              \
            return (hr);                                                                            \
    } while (0)

#define AC_RETURN_HR_IF_NULL(hr, pointer) AC_RETURN_HR_IF(hr, (pointer) == nullptr)

namespace anycam
{
    // Small RAII lock around a CRITICAL_SECTION (SRW locks can't be acquired recursively and
    // MF may call back into us while we hold the lock from the same thread).
    class CriticalSection
    {
    public:
        CriticalSection() { InitializeCriticalSectionEx(&_cs, 4000, 0); }
        ~CriticalSection() { DeleteCriticalSection(&_cs); }
        CriticalSection(const CriticalSection&) = delete;
        CriticalSection& operator=(const CriticalSection&) = delete;
        void Lock() { EnterCriticalSection(&_cs); }
        void Unlock() { LeaveCriticalSection(&_cs); }

    private:
        CRITICAL_SECTION _cs;
    };

    class AutoLock
    {
    public:
        explicit AutoLock(CriticalSection& cs) : _cs(cs) { _cs.Lock(); }
        ~AutoLock() { _cs.Unlock(); }
        AutoLock(const AutoLock&) = delete;
        AutoLock& operator=(const AutoLock&) = delete;

    private:
        CriticalSection& _cs;
    };

    std::wstring GuidToString(REFGUID guid);
    std::wstring ModulePath();
}
