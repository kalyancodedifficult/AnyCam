// AnyCamVCamCtl — command-line helper for the AnyCam virtual camera.
//
//   AnyCamVCamCtl status                  show COM registration and list video capture devices
//   AnyCamVCamCtl install [--all-users]   create + start the "AnyCam" virtual camera (system lifetime)
//   AnyCamVCamCtl uninstall [--all-users] remove it
//   AnyCamVCamCtl read [out.bmp] [WxH] [frames]
//                                         open the camera like an app would, read frames, save a BMP
//   AnyCamVCamCtl feed [seconds]          act as AnyCam Desktop: write a moving test pattern into the
//                                         shared memory while the camera is in use
//
// COM registration itself is done with `regsvr32 AnyCamVCam.dll` (elevated).

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wrl/client.h>

#include <chrono>
#include <cstdio>
#include <cmath>
#include <string>
#include <vector>

#include "AnyCamVCamShared.h"

using Microsoft::WRL::ComPtr;

namespace
{
    using RegisterFn = HRESULT(__stdcall*)(BOOL, BOOL, LPCWSTR, IUnknown**);
    using RemoveFn = HRESULT(__stdcall*)(BOOL, BOOL, LPCWSTR);
    using GetRegistrationFn = HRESULT(__stdcall*)(LPWSTR, UINT32);

    HMODULE LoadVCamDll()
    {
        // Prefer the DLL next to this executable.
        wchar_t path[MAX_PATH]{};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        if (wchar_t* slash = wcsrchr(path, L'\\'))
            *(slash + 1) = L'\0';
        wcscat_s(path, L"AnyCamVCam.dll");
        HMODULE module = LoadLibraryW(path);
        if (!module)
            wprintf(L"Cannot load %s (error %lu)\n", path, GetLastError());
        return module;
    }

    bool HasFlag(int argc, wchar_t** argv, const wchar_t* flag)
    {
        for (int i = 2; i < argc; i++)
            if (_wcsicmp(argv[i], flag) == 0)
                return true;
        return false;
    }

    void PrintHr(const wchar_t* what, HRESULT hr)
    {
        wchar_t* message = nullptr;
        FormatMessageW(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                       nullptr, hr, 0, reinterpret_cast<LPWSTR>(&message), 0, nullptr);
        wprintf(L"%s: 0x%08X %s\n", what, (unsigned)hr, message ? message : L"");
        if (message)
            LocalFree(message);
    }

    int Status()
    {
        HMODULE dll = LoadVCamDll();
        if (dll)
        {
            auto getRegistration = reinterpret_cast<GetRegistrationFn>(GetProcAddress(dll, "AnyCamVCam_GetRegistration"));
            wchar_t path[MAX_PATH]{};
            const HRESULT hr = getRegistration ? getRegistration(path, MAX_PATH) : E_NOINTERFACE;
            if (hr == S_OK)
                wprintf(L"COM registration: %s\n", path);
            else if (hr == S_FALSE)
                wprintf(L"COM registration: NOT registered (run: regsvr32 AnyCamVCam.dll as administrator)\n");
            else
                PrintHr(L"COM registration", hr);
        }

        ComPtr<IMFAttributes> attributes;
        MFCreateAttributes(&attributes, 1);
        attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        IMFActivate** devices = nullptr;
        UINT32 count = 0;
        const HRESULT hr = MFEnumDeviceSources(attributes.Get(), &devices, &count);
        if (FAILED(hr))
        {
            PrintHr(L"MFEnumDeviceSources", hr);
            return 1;
        }
        wprintf(L"Video capture devices (%u):\n", count);
        for (UINT32 i = 0; i < count; i++)
        {
            wchar_t* name = nullptr;
            UINT32 length = 0;
            devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &length);
            wprintf(L"  [%u] %s\n", i, name ? name : L"?");
            CoTaskMemFree(name);
            devices[i]->Release();
        }
        CoTaskMemFree(devices);
        return 0;
    }

    int Install(bool install, bool allUsers)
    {
        HMODULE dll = LoadVCamDll();
        if (!dll)
            return 1;
        HRESULT hr;
        if (install)
        {
            auto registerFn = reinterpret_cast<RegisterFn>(GetProcAddress(dll, "AnyCamVCam_Register"));
            hr = registerFn ? registerFn(TRUE, allUsers, nullptr, nullptr) : E_NOINTERFACE;
        }
        else
        {
            auto removeFn = reinterpret_cast<RemoveFn>(GetProcAddress(dll, "AnyCamVCam_Remove"));
            hr = removeFn ? removeFn(TRUE, allUsers, nullptr) : E_NOINTERFACE;
        }
        PrintHr(install ? L"Install virtual camera" : L"Remove virtual camera", hr);
        return SUCCEEDED(hr) ? 0 : 1;
    }

    HRESULT FindAnyCam(IMFMediaSource** source)
    {
        ComPtr<IMFAttributes> attributes;
        MFCreateAttributes(&attributes, 1);
        attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        IMFActivate** devices = nullptr;
        UINT32 count = 0;
        HRESULT hr = MFEnumDeviceSources(attributes.Get(), &devices, &count);
        if (FAILED(hr))
            return hr;

        hr = MF_E_NOT_FOUND;
        for (UINT32 i = 0; i < count; i++)
        {
            wchar_t* name = nullptr;
            UINT32 length = 0;
            devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &length);
            if (hr == MF_E_NOT_FOUND && name && wcsstr(name, L"AnyCam"))
            {
                wprintf(L"Opening '%s'\n", name);
                hr = devices[i]->ActivateObject(IID_PPV_ARGS(source));
            }
            CoTaskMemFree(name);
            devices[i]->Release();
        }
        CoTaskMemFree(devices);
        return hr;
    }

    bool WriteBmp(const wchar_t* path, const std::vector<uint8_t>& bgr, uint32_t width, uint32_t height)
    {
        const uint32_t rowSize = (width * 3 + 3) & ~3u;
        BITMAPFILEHEADER file{};
        BITMAPINFOHEADER info{};
        file.bfType = 0x4D42;
        file.bfOffBits = sizeof(file) + sizeof(info);
        file.bfSize = file.bfOffBits + rowSize * height;
        info.biSize = sizeof(info);
        info.biWidth = static_cast<LONG>(width);
        info.biHeight = static_cast<LONG>(height); // bottom-up
        info.biPlanes = 1;
        info.biBitCount = 24;
        FILE* f = nullptr;
        if (_wfopen_s(&f, path, L"wb") != 0 || !f)
            return false;
        fwrite(&file, sizeof(file), 1, f);
        fwrite(&info, sizeof(info), 1, f);
        std::vector<uint8_t> row(rowSize, 0);
        for (uint32_t y = 0; y < height; y++)
        {
            memcpy(row.data(), bgr.data() + size_t(height - 1 - y) * width * 3, size_t(width) * 3);
            fwrite(row.data(), rowSize, 1, f);
        }
        fclose(f);
        return true;
    }

    uint8_t Clamp(int v) { return static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v)); }

    int Read(const wchar_t* outPath, uint32_t wantWidth, uint32_t wantHeight, int frames)
    {
        ComPtr<IMFMediaSource> source;
        HRESULT hr = FindAnyCam(&source);
        if (FAILED(hr))
        {
            PrintHr(L"Find AnyCam camera", hr);
            return 1;
        }

        ComPtr<IMFSourceReader> reader;
        hr = MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader);
        if (FAILED(hr))
        {
            PrintHr(L"MFCreateSourceReaderFromMediaSource", hr);
            return 1;
        }

        // Pick a native type: the requested size if given, otherwise the default (first) one.
        ComPtr<IMFMediaType> chosen;
        for (DWORD i = 0;; i++)
        {
            ComPtr<IMFMediaType> type;
            if (FAILED(reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), i, &type)))
                break;
            UINT32 w = 0, h = 0, num = 0, den = 0;
            GUID subtype{};
            MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);
            MFGetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, &num, &den);
            type->GetGUID(MF_MT_SUBTYPE, &subtype);
            wprintf(L"  native type %u: %ux%u %s %u/%u fps\n", i, w, h, subtype == MFVideoFormat_NV12 ? L"NV12" : L"RGB32", num, den);
            if (!chosen && (!wantWidth || (w == wantWidth && h == wantHeight)))
                chosen = type;
        }
        if (!chosen)
        {
            wprintf(L"No matching media type\n");
            return 1;
        }
        hr = reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, chosen.Get());
        if (FAILED(hr))
        {
            PrintHr(L"SetCurrentMediaType", hr);
            return 1;
        }

        UINT32 width = 0, height = 0;
        GUID subtype{};
        MFGetAttributeSize(chosen.Get(), MF_MT_FRAME_SIZE, &width, &height);
        chosen->GetGUID(MF_MT_SUBTYPE, &subtype);
        const bool nv12 = subtype == MFVideoFormat_NV12;

        std::vector<uint8_t> last;
        LONG lastPitch = 0;
        auto start = std::chrono::steady_clock::now();
        int received = 0;
        LONGLONG firstTime = -1, lastTime = 0;
        for (int i = 0; i < frames; i++)
        {
            DWORD streamIndex = 0, flags = 0;
            LONGLONG timestamp = 0;
            ComPtr<IMFSample> sample;
            hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &streamIndex, &flags, &timestamp, &sample);
            if (FAILED(hr))
            {
                PrintHr(L"ReadSample", hr);
                return 1;
            }
            if (!sample)
                continue;
            received++;
            if (firstTime < 0)
                firstTime = timestamp;
            lastTime = timestamp;

            ComPtr<IMFMediaBuffer> buffer;
            sample->ConvertToContiguousBuffer(&buffer);
            ComPtr<IMF2DBuffer> buffer2d;
            BYTE* data = nullptr;
            LONG pitch = 0;
            if (SUCCEEDED(buffer.As(&buffer2d)) && SUCCEEDED(buffer2d->Lock2D(&data, &pitch)))
            {
                const size_t size = nv12 ? size_t(std::abs(pitch)) * height * 3 / 2 : size_t(std::abs(pitch)) * height;
                last.assign(data, data + size);
                lastPitch = pitch;
                buffer2d->Unlock2D();
            }
            else
            {
                DWORD length = 0;
                if (SUCCEEDED(buffer->Lock(&data, nullptr, &length)))
                {
                    last.assign(data, data + length);
                    lastPitch = static_cast<LONG>(nv12 ? width : width * 4);
                    buffer->Unlock();
                }
            }
        }
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        wprintf(L"Read %d frames %ux%u %s in %.2f s (%.1f fps wall clock, %.1f fps by timestamps)\n",
                received, width, height, nv12 ? L"NV12" : L"RGB32", seconds, received / seconds,
                received > 1 ? (received - 1) * 1e7 / double(lastTime - firstTime) : 0.0);

        if (last.empty() || !outPath)
            return 0;

        std::vector<uint8_t> bgr(size_t(width) * height * 3);
        const LONG pitch = std::abs(lastPitch);
        for (uint32_t y = 0; y < height; y++)
        {
            for (uint32_t x = 0; x < width; x++)
            {
                uint8_t* out = &bgr[(size_t(y) * width + x) * 3];
                if (nv12)
                {
                    const int Y = last[size_t(y) * pitch + x] - 16;
                    const uint8_t* uv = &last[size_t(pitch) * height + size_t(y / 2) * pitch + (x & ~1u)];
                    const int U = uv[0] - 128, V = uv[1] - 128;
                    out[2] = Clamp((298 * Y + 459 * V + 128) >> 8);
                    out[1] = Clamp((298 * Y - 55 * U - 136 * V + 128) >> 8);
                    out[0] = Clamp((298 * Y + 541 * U + 128) >> 8);
                }
                else
                {
                    const uint8_t* p = &last[size_t(y) * pitch + size_t(x) * 4];
                    out[0] = p[0];
                    out[1] = p[1];
                    out[2] = p[2];
                }
            }
        }
        if (WriteBmp(outPath, bgr, width, height))
            wprintf(L"Saved %s\n", outPath);
        return 0;
    }

    // Test producer: fills the shared memory with a moving pattern at whatever size the camera asks.
    int Feed(int seconds)
    {
        wchar_t name[128];
        anycam::FramesMappingName(name, ARRAYSIZE(name), 0);
        wprintf(L"Waiting for %s (open the AnyCam camera in an app)...\n", name);

        HANDLE mapping = nullptr;
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(seconds);
        while (!mapping && std::chrono::steady_clock::now() < deadline)
        {
            mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
            if (!mapping)
                Sleep(200);
        }
        if (!mapping)
        {
            wprintf(L"Timed out (error %lu)\n", GetLastError());
            return 1;
        }
        auto* header = static_cast<anycam::SharedHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
        anycam::FrameReadyEventName(name, ARRAYSIZE(name), 0);
        HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, name);
        if (!header || !ready || header->magic != anycam::kSharedMagic)
        {
            wprintf(L"Shared memory not usable (header=%p event=%p)\n", (void*)header, ready);
            return 1;
        }
        wprintf(L"Connected. Feeding frames for %d s...\n", seconds);

        uint64_t frame = 0;
        while (std::chrono::steady_clock::now() < deadline)
        {
            const uint32_t width = header->requestedWidth, height = header->requestedHeight;
            const auto format = static_cast<anycam::PixelFormat>(header->requestedFormat);
            if (!width || !height || format == anycam::PixelFormat_None)
            {
                Sleep(50);
                continue;
            }

            const int32_t latest = header->latestSlot, reading = header->readingSlot;
            uint32_t slot = 0;
            while (int32_t(slot) == latest || int32_t(slot) == reading)
                slot++;

            anycam::SlotInfo& info = header->slots[slot];
            InterlockedIncrement64(&info.seq);
            uint8_t* data = anycam::SlotData(header, slot);
            const uint32_t bar = static_cast<uint32_t>((frame * 8) % width);
            if (format == anycam::PixelFormat_NV12)
            {
                for (uint32_t y = 0; y < height; y++)
                    for (uint32_t x = 0; x < width; x++)
                        data[size_t(y) * width + x] = static_cast<uint8_t>(x >= bar && x < bar + width / 16 ? 235 : 16 + (y * 200) / height);
                uint8_t* uv = data + size_t(width) * height;
                for (uint32_t y = 0; y < height / 2; y++)
                    for (uint32_t x = 0; x < width; x += 2)
                    {
                        uv[size_t(y) * width + x] = static_cast<uint8_t>(64 + (x * 128) / width);
                        uv[size_t(y) * width + x + 1] = static_cast<uint8_t>(192 - (y * 256) / height);
                    }
                info.stride = width;
                info.dataSize = width * height * 3 / 2;
            }
            else
            {
                for (uint32_t y = 0; y < height; y++)
                    for (uint32_t x = 0; x < width; x++)
                    {
                        uint8_t* p = data + (size_t(y) * width + x) * 4;
                        const bool inBar = x >= bar && x < bar + width / 16;
                        p[0] = inBar ? 255 : static_cast<uint8_t>((x * 255) / width);
                        p[1] = inBar ? 255 : static_cast<uint8_t>((y * 255) / height);
                        p[2] = inBar ? 255 : 128;
                        p[3] = 255;
                    }
                info.stride = width * 4;
                info.dataSize = width * height * 4;
            }
            info.width = width;
            info.height = height;
            info.format = format;
            info.frameNumber = ++frame;
            info.timestamp = static_cast<int64_t>(MFGetSystemTime());
            InterlockedIncrement64(&info.seq);

            InterlockedExchange(reinterpret_cast<volatile LONG*>(&header->latestSlot), static_cast<LONG>(slot));
            header->producerPid = GetCurrentProcessId();
            header->producerHeartbeat = static_cast<int64_t>(GetTickCount64());
            InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&header->framesProduced));
            SetEvent(ready);

            const uint32_t fps = header->requestedFpsNum ? header->requestedFpsNum / (header->requestedFpsDen ? header->requestedFpsDen : 1) : 30;
            Sleep(1000 / (fps ? fps : 30));
        }
        wprintf(L"Done: %llu frames, consumer delivered %llu\n", frame, header->framesDelivered);
        UnmapViewOfFile(header);
        CloseHandle(ready);
        CloseHandle(mapping);
        return 0;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        wprintf(L"usage: AnyCamVCamCtl status | install [--all-users] | uninstall [--all-users] | read [out.bmp] [WxH] [frames] | feed [seconds]\n");
        return 2;
    }

    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    int result = 2;
    const std::wstring command = argv[1];
    if (command == L"status")
        result = Status();
    else if (command == L"install")
        result = Install(true, HasFlag(argc, argv, L"--all-users"));
    else if (command == L"uninstall")
        result = Install(false, HasFlag(argc, argv, L"--all-users"));
    else if (command == L"read")
    {
        uint32_t w = 0, h = 0;
        if (argc > 3)
            swscanf_s(argv[3], L"%ux%u", &w, &h);
        result = Read(argc > 2 ? argv[2] : nullptr, w, h, argc > 4 ? _wtoi(argv[4]) : 90);
    }
    else if (command == L"feed")
        result = Feed(argc > 2 ? _wtoi(argv[2]) : 30);
    else
        wprintf(L"unknown command\n");

    MFShutdown();
    CoUninitialize();
    return result;
}
