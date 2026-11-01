#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <cstring>
#include <string>
#include <vector>

struct BASS_ASIO_DEVICEINFO {
    const char* name;
    const char* driver;
};

struct BASS_ASIO_INFO {
    char name[32];
    DWORD version;
    DWORD inputs;
    DWORD outputs;
    DWORD bufmin;
    DWORD bufmax;
    DWORD bufpref;
    int bufgran;
    DWORD initflags;
};

constexpr DWORD bass_asio_format_float = 19;
using ASIOPROC = DWORD(CALLBACK)(BOOL input, DWORD channel, void* buffer, DWORD length, void* user);

namespace {
bool unicode = false;
bool initialized = false;
bool started = false;
ASIOPROC* callback = nullptr;
void* callback_user = nullptr;
constexpr wchar_t wide_name[] = L"ASIO 2.0 - Fake";
constexpr char narrow_name[] = "ASIO 2.0 - Fake";
}

extern "C" {
__declspec(dllexport) BOOL WINAPI BASS_ASIO_SetUnicode(BOOL value) { unicode = value != FALSE; return TRUE; }
__declspec(dllexport) BOOL WINAPI BASS_ASIO_GetDeviceInfo(DWORD device, BASS_ASIO_DEVICEINFO* info) {
    if (device != 0 || info == nullptr) return FALSE;
    info->name = unicode ? reinterpret_cast<const char*>(wide_name) : narrow_name;
    info->driver = narrow_name;
    return TRUE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_Init(int device, DWORD) { initialized = device == 0; return initialized ? TRUE : FALSE; }
__declspec(dllexport) BOOL WINAPI BASS_ASIO_Free() { initialized = false; started = false; callback = nullptr; return TRUE; }
__declspec(dllexport) BOOL WINAPI BASS_ASIO_Lock(BOOL) { return initialized ? TRUE : FALSE; }
__declspec(dllexport) BOOL WINAPI BASS_ASIO_GetInfo(BASS_ASIO_INFO* info) {
    if (!initialized || info == nullptr) return FALSE;
    *info = {};
    strncpy_s(info->name, narrow_name, _TRUNCATE);
    info->outputs = 6; info->bufmin = 16; info->bufmax = 2048; info->bufpref = 32; info->bufgran = -1;
    return TRUE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_SetRate(double) { return initialized ? TRUE : FALSE; }
__declspec(dllexport) BOOL WINAPI BASS_ASIO_ChannelEnable(BOOL input, DWORD channel, ASIOPROC* proc, void* user) {
    if (!initialized || input || channel >= 6) return FALSE;
    callback = proc; callback_user = user; return TRUE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_ChannelJoin(BOOL input, DWORD channel, int first) {
    return initialized && !input && channel == 1 && first == 0 ? TRUE : FALSE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_ChannelSetFormat(BOOL input, DWORD channel, DWORD format) {
    return initialized && !input && channel == 0 && format == bass_asio_format_float ? TRUE : FALSE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_ChannelSetRate(BOOL input, DWORD channel, double) {
    return initialized && !input && channel == 0 ? TRUE : FALSE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_Start(DWORD frames, DWORD) {
    if (!initialized || callback == nullptr || frames == 0) return FALSE;
    std::vector<float> samples(static_cast<size_t>(frames) * 2U);
    DWORD bytes = static_cast<DWORD>(samples.size() * sizeof(float));
    if (callback(FALSE, 0, samples.data(), bytes, callback_user) != bytes) return FALSE;
    started = true;
    return TRUE;
}
__declspec(dllexport) BOOL WINAPI BASS_ASIO_Stop() { bool was_started = started; started = false; return was_started ? TRUE : FALSE; }
__declspec(dllexport) float WINAPI BASS_ASIO_GetCPU() { return 0.25F; }
}

