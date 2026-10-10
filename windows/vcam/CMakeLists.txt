cmake_minimum_required(VERSION 3.24)
project(AnyCamVCam LANGUAGES CXX)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Windows")
    message(FATAL_ERROR "AnyCamVCam is Windows-only")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static CRT: the DLL is loaded into the Frame Server service, which must not depend on a VC++ redist.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_compile_definitions(UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0A00)
add_compile_options(/W4 /permissive- /utf-8 /Zc:__cplusplus /EHsc)

add_library(AnyCamVCam SHARED
    src/Activator.cpp
    src/dllmain.cpp
    src/FrameBridge.cpp
    src/Log.cpp
    src/MediaSource.cpp
    src/MediaStream.cpp
    src/Placeholder.cpp
    src/AnyCamVCam.def
)
target_include_directories(AnyCamVCam PRIVATE include src)
target_precompile_headers(AnyCamVCam PRIVATE src/pch.h)
target_link_libraries(AnyCamVCam PRIVATE mfplat mf mfuuid mfsensorgroup propsys ole32 advapi32 gdi32 user32 runtimeobject)

add_executable(AnyCamVCamCtl tools/vcamctl.cpp)
target_include_directories(AnyCamVCamCtl PRIVATE include)
target_link_libraries(AnyCamVCamCtl PRIVATE mfplat mf mfreadwrite mfuuid ole32)
