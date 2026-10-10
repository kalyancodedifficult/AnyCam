#include "pch.h"

namespace
{
    std::mutex g_logLock;
    HANDLE g_logFile = INVALID_HANDLE_VALUE;
    bool g_logFileTried = false;

    void OpenLogFileLocked()
    {
        g_logFileTried = true;

        wchar_t programData[MAX_PATH]{};
        if (!GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH))
            StringCchCopyW(programData, MAX_PATH, L"C:\\ProgramData");

        wchar_t directory[MAX_PATH]{};
        StringCchPrintfW(directory, MAX_PATH, L"%s\\AnyCam\\logs", programData);
        if (GetFileAttributesW(directory) == INVALID_FILE_ATTRIBUTES)
            return; // logging to file is opt-in: create the folder to enable it

        wchar_t processPath[MAX_PATH]{};
        GetModuleFileNameW(nullptr, processPath, MAX_PATH);
        const wchar_t* processName = wcsrchr(processPath, L'\\');
        processName = processName ? processName + 1 : processPath;

        wchar_t path[MAX_PATH]{};
        StringCchPrintfW(path, MAX_PATH, L"%s\\vcam-%s-%u.log", directory, processName, GetCurrentProcessId());
        g_logFile = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    }
}

namespace anycam::log
{
    void Write(const wchar_t* format, ...)
    {
        wchar_t message[1024];
        va_list args;
        va_start(args, format);
        StringCchVPrintfW(message, ARRAYSIZE(message), format, args);
        va_end(args);

        SYSTEMTIME time;
        GetLocalTime(&time);

        wchar_t line[1200];
        StringCchPrintfW(line, ARRAYSIZE(line), L"%02u:%02u:%02u.%03u [%u:%u] AnyCamVCam: %s\r\n",
                         time.wHour, time.wMinute, time.wSecond, time.wMilliseconds,
                         GetCurrentProcessId(), GetCurrentThreadId(), message);
        OutputDebugStringW(line);

        std::lock_guard<std::mutex> lock(g_logLock);
        if (!g_logFileTried)
            OpenLogFileLocked();
        if (g_logFile != INVALID_HANDLE_VALUE)
        {
            char utf8[2400];
            int length = WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, sizeof(utf8), nullptr, nullptr);
            if (length > 1)
            {
                DWORD written = 0;
                WriteFile(g_logFile, utf8, static_cast<DWORD>(length - 1), &written, nullptr);
            }
        }
    }
}

namespace anycam
{
    std::wstring GuidToString(REFGUID guid)
    {
        wchar_t buffer[64]{};
        StringFromGUID2(guid, buffer, ARRAYSIZE(buffer));
        return buffer;
    }

    std::wstring ModulePath()
    {
        wchar_t path[MAX_PATH]{};
        GetModuleFileNameW(g_module, path, MAX_PATH);
        return path;
    }
}
