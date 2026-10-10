#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <windows.h>
#include <ole2.h>
// WRL (and anything pulling in cguid.h) must come before ks.h, which redefines GUID_NULL as a macro.
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/ftm.h>

#include <initguid.h>
#include <sddl.h>
#include <strsafe.h>
#include <propvarutil.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "AnyCamVCamShared.h"
#include "Common.h"
