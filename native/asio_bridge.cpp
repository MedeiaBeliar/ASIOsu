#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <mutex>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>

#include "basswasapi.h"
#include "bassasio.h"

namespace {

namespace fs = std::filesystem;

enum class Backend { undecided, original, asio };

struct RuntimeState {
    std::mutex control;
    Backend backend = Backend::undecided;
    bool initialized = false;
    bool started = false;
    DWORD wasapi_device = static_cast<DWORD>(-1);
    DWORD sample_rate = 48000;
    DWORD channels = 2;
    DWORD buffer_frames = 32;
    DWORD init_flags = 0;
    std::atomic<WASAPIPROC*> source{nullptr};
    std::atomic<void*> source_context{nullptr};
    std::atomic<WASAPINOTIFYPROC*> notify{nullptr};
    std::atomic<void*> notify_context{nullptr};
    std::atomic<float> gain{1.0F};
    std::atomic<bool> muted{false};
    std::atomic<float> peak_left{0.0F};
    std::atomic<float> peak_right{0.0F};
};

RuntimeState runtime;
HMODULE module_instance = nullptr;
std::once_flag original_once;
HMODULE original_module = nullptr;

fs::path module_directory() {
    wchar_t buffer[32768]{};
    const DWORD length = GetModuleFileNameW(module_instance, buffer, static_cast<DWORD>(std::size(buffer)));
    if (length == 0 || length >= static_cast<DWORD>(std::size(buffer))) throw std::runtime_error("module path unavailable");
    return fs::path(std::wstring(buffer, length)).parent_path();
}

void load_original() {
    const fs::path path = module_directory() / L"basswasapi_original.dll";
    original_module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
}

template <typename Function>
Function original_function(const char* name) {
    std::call_once(original_once, load_original);
    if (original_module == nullptr) return nullptr;
    return reinterpret_cast<Function>(GetProcAddress(original_module, name));
}

template <typename Function, typename... Arguments>
auto call_original(const char* name, Arguments... arguments) -> decltype(std::declval<Function>()(arguments...)) {
    Function function = original_function<Function>(name);
    using Result = decltype(std::declval<Function>()(arguments...));
    if (function == nullptr) {
        if constexpr (std::is_pointer_v<Result>) return nullptr;
        else return Result{};
    }
    return function(arguments...);
}

struct Settings {
    bool enabled = false;
    std::wstring driver;
    DWORD first_output = 0;
    DWORD sample_rate = 48000;
    DWORD buffer_frames = 32;
};

Settings read_settings() {
    const fs::path ini = module_directory() / L"asiosu.ini";
    Settings result;
    result.enabled = GetPrivateProfileIntW(L"ASIO", L"Enabled", 0, ini.c_str()) != 0;
    wchar_t driver[512]{};
    GetPrivateProfileStringW(L"ASIO", L"Driver", L"", driver, static_cast<DWORD>(std::size(driver)), ini.c_str());
    result.driver = driver;
    result.first_output = GetPrivateProfileIntW(L"ASIO", L"FirstOutput", 0, ini.c_str());
    result.sample_rate = GetPrivateProfileIntW(L"ASIO", L"SampleRate", 48000, ini.c_str());
    result.buffer_frames = GetPrivateProfileIntW(L"ASIO", L"BufferFrames", 32, ini.c_str());
    if (result.sample_rate < 8000 || result.sample_rate > 384000) result.sample_rate = 48000;
    if (result.buffer_frames < 1 || result.buffer_frames > 8192) result.buffer_frames = 32;
    return result;
}

int find_driver(const std::wstring& wanted) {
    BASS_ASIO_SetUnicode(TRUE);
    for (DWORD index = 0; index < 1024; ++index) {
        BASS_ASIO_DEVICEINFO info{};
        if (!BASS_ASIO_GetDeviceInfo(index, &info)) break;
        const auto* name = reinterpret_cast<const wchar_t*>(info.name);
        if (name != nullptr && _wcsicmp(name, wanted.c_str()) == 0) return static_cast<int>(index);
    }
    return -1;
}

DWORD CALLBACK render_audio(BOOL input, DWORD, void* buffer, DWORD length, void*) noexcept {
    if (input || buffer == nullptr || length == 0) return 0;
    std::memset(buffer, 0, length);
    WASAPIPROC* source = runtime.source.load(std::memory_order_acquire);
    if (source == nullptr || source == WASAPIPROC_BASS) return length;

    DWORD written = source(buffer, length, runtime.source_context.load(std::memory_order_relaxed));
    written = std::min(written, length);
    if (written < length) std::memset(static_cast<unsigned char*>(buffer) + written, 0, length - written);

    const bool silence = runtime.muted.load(std::memory_order_relaxed);
    const float gain = silence ? 0.0F : runtime.gain.load(std::memory_order_relaxed);
    auto* samples = static_cast<float*>(buffer);
    const size_t sample_count = written / sizeof(float);
    float left = 0.0F;
    float right = 0.0F;
    for (size_t index = 0; index < sample_count; ++index) {
        const float value = samples[index] * gain;
        samples[index] = value;
        if ((index & 1U) == 0) left = std::max(left, std::abs(value));
        else right = std::max(right, std::abs(value));
    }
    runtime.peak_left.store(std::min(left, 1.0F), std::memory_order_relaxed);
    runtime.peak_right.store(std::min(right, 1.0F), std::memory_order_relaxed);
    return length;
}

bool start_asio(const Settings& settings, int wasapi_device, DWORD flags, WASAPIPROC* source, void* context) {
    if (source == nullptr || source == WASAPIPROC_BASS || settings.driver.empty()) return false;
    const int driver = find_driver(settings.driver);
    if (driver < 0 || !BASS_ASIO_Init(driver, BASS_ASIO_THREAD)) return false;

    BASS_ASIO_INFO info{};
    if (!BASS_ASIO_GetInfo(&info) || settings.first_output >= info.outputs || settings.first_output + 1 >= info.outputs) {
        BASS_ASIO_Free();
        return false;
    }
    BASS_ASIO_SetRate(settings.sample_rate);
    if (!BASS_ASIO_ChannelEnable(FALSE, settings.first_output, render_audio, nullptr) ||
        !BASS_ASIO_ChannelSetFormat(FALSE, settings.first_output, BASS_ASIO_FORMAT_FLOAT) ||
        !BASS_ASIO_ChannelSetRate(FALSE, settings.first_output, settings.sample_rate) ||
        !BASS_ASIO_ChannelJoin(FALSE, settings.first_output + 1, static_cast<int>(settings.first_output))) {
        BASS_ASIO_Free();
        return false;
    }

    runtime.source.store(source, std::memory_order_release);
    runtime.source_context.store(context, std::memory_order_release);
    runtime.wasapi_device = static_cast<DWORD>(wasapi_device);
    runtime.sample_rate = settings.sample_rate;
    runtime.channels = 2;
    runtime.buffer_frames = settings.buffer_frames;
    runtime.init_flags = flags;
    runtime.initialized = true;
    runtime.started = false;
    runtime.backend = Backend::asio;
    return true;
}

}  // namespace

extern "C" {

DWORD WINAPI BASS_WASAPI_GetVersion() {
    using Fn = DWORD (WINAPI*)();
    const DWORD version = call_original<Fn>("BASS_WASAPI_GetVersion");
    return version != 0 ? version : 0x02040400;
}

BOOL WINAPI BASS_WASAPI_SetNotify(WASAPINOTIFYPROC* proc, void* user) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) {
        runtime.notify.store(proc, std::memory_order_release);
        runtime.notify_context.store(user, std::memory_order_release);
        return TRUE;
    }
    using Fn = BOOL (WINAPI*)(WASAPINOTIFYPROC*, void*);
    return call_original<Fn>("BASS_WASAPI_SetNotify", proc, user);
}

BOOL WINAPI BASS_WASAPI_GetDeviceInfo(DWORD device, BASS_WASAPI_DEVICEINFO* info) {
    using Fn = BOOL (WINAPI*)(DWORD, BASS_WASAPI_DEVICEINFO*);
    return call_original<Fn>("BASS_WASAPI_GetDeviceInfo", device, info);
}

float WINAPI BASS_WASAPI_GetDeviceLevel(DWORD device, int channel) {
    using Fn = float (WINAPI*)(DWORD, int);
    return call_original<Fn>("BASS_WASAPI_GetDeviceLevel", device, channel);
}

BOOL WINAPI BASS_WASAPI_SetDevice(DWORD device) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) {
        runtime.wasapi_device = device;
        return TRUE;
    }
    using Fn = BOOL (WINAPI*)(DWORD);
    return call_original<Fn>("BASS_WASAPI_SetDevice", device);
}

DWORD WINAPI BASS_WASAPI_GetDevice() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return runtime.wasapi_device;
    using Fn = DWORD (WINAPI*)();
    return call_original<Fn>("BASS_WASAPI_GetDevice");
}

DWORD WINAPI BASS_WASAPI_CheckFormat(DWORD device, DWORD frequency, DWORD channels, DWORD flags) {
    using Fn = DWORD (WINAPI*)(DWORD, DWORD, DWORD, DWORD);
    return call_original<Fn>("BASS_WASAPI_CheckFormat", device, frequency, channels, flags);
}

BOOL WINAPI BASS_WASAPI_Init(int device, DWORD frequency, DWORD channels, DWORD flags, float buffer, float period,
                             WASAPIPROC* proc, void* user) {
    const Settings settings = read_settings();
    if (!settings.enabled) {
        std::lock_guard lock(runtime.control);
        runtime.backend = Backend::original;
        using Fn = BOOL (WINAPI*)(int, DWORD, DWORD, DWORD, float, float, WASAPIPROC*, void*);
        return call_original<Fn>("BASS_WASAPI_Init", device, frequency, channels, flags, buffer, period, proc, user);
    }
    std::lock_guard lock(runtime.control);
    if (runtime.initialized) return FALSE;
    return start_asio(settings, device, flags, proc, user) ? TRUE : FALSE;
}

BOOL WINAPI BASS_WASAPI_Free() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = BOOL (WINAPI*)();
        return call_original<Fn>("BASS_WASAPI_Free");
    }
    if (!runtime.initialized) return FALSE;
    if (runtime.started) BASS_ASIO_Stop();
    const BOOL result = BASS_ASIO_Free();
    runtime.source.store(nullptr, std::memory_order_release);
    runtime.source_context.store(nullptr, std::memory_order_release);
    runtime.initialized = false;
    runtime.started = false;
    runtime.backend = Backend::undecided;
    return result;
}

BOOL WINAPI BASS_WASAPI_GetInfo(BASS_WASAPI_INFO* info) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = BOOL (WINAPI*)(BASS_WASAPI_INFO*);
        return call_original<Fn>("BASS_WASAPI_GetInfo", info);
    }
    if (!runtime.initialized || info == nullptr) return FALSE;
    *info = {};
    info->initflags = runtime.init_flags;
    info->freq = runtime.sample_rate;
    info->chans = runtime.channels;
    info->format = BASS_WASAPI_FORMAT_FLOAT;
    info->buflen = runtime.buffer_frames * runtime.channels * sizeof(float);
    info->volmax = 1.0F;
    info->volmin = 0.0F;
    info->volstep = 0.01F;
    return TRUE;
}

float WINAPI BASS_WASAPI_GetCPU() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return BASS_ASIO_GetCPU();
    using Fn = float (WINAPI*)();
    return call_original<Fn>("BASS_WASAPI_GetCPU");
}

BOOL WINAPI BASS_WASAPI_Lock(BOOL lock) {
    std::lock_guard guard(runtime.control);
    if (runtime.backend == Backend::asio) return BASS_ASIO_Lock(lock);
    using Fn = BOOL (WINAPI*)(BOOL);
    return call_original<Fn>("BASS_WASAPI_Lock", lock);
}

BOOL WINAPI BASS_WASAPI_Start() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = BOOL (WINAPI*)();
        return call_original<Fn>("BASS_WASAPI_Start");
    }
    if (!runtime.initialized || runtime.started) return FALSE;
    const BOOL result = BASS_ASIO_Start(runtime.buffer_frames, 1);
    if (result) runtime.started = true;
    return result;
}

BOOL WINAPI BASS_WASAPI_Stop(BOOL reset) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = BOOL (WINAPI*)(BOOL);
        return call_original<Fn>("BASS_WASAPI_Stop", reset);
    }
    if (!runtime.initialized || !runtime.started) return FALSE;
    const BOOL result = BASS_ASIO_Stop();
    if (result) runtime.started = false;
    return result;
}

BOOL WINAPI BASS_WASAPI_IsStarted() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return runtime.started ? TRUE : FALSE;
    using Fn = BOOL (WINAPI*)();
    return call_original<Fn>("BASS_WASAPI_IsStarted");
}

BOOL WINAPI BASS_WASAPI_SetVolume(DWORD mode, float volume) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) {
        runtime.gain.store(std::clamp(volume, 0.0F, 1.0F), std::memory_order_relaxed);
        return TRUE;
    }
    using Fn = BOOL (WINAPI*)(DWORD, float);
    return call_original<Fn>("BASS_WASAPI_SetVolume", mode, volume);
}

float WINAPI BASS_WASAPI_GetVolume(DWORD mode) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return runtime.gain.load(std::memory_order_relaxed);
    using Fn = float (WINAPI*)(DWORD);
    return call_original<Fn>("BASS_WASAPI_GetVolume", mode);
}

BOOL WINAPI BASS_WASAPI_SetMute(DWORD mode, BOOL mute) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) {
        runtime.muted.store(mute != FALSE, std::memory_order_relaxed);
        return TRUE;
    }
    using Fn = BOOL (WINAPI*)(DWORD, BOOL);
    return call_original<Fn>("BASS_WASAPI_SetMute", mode, mute);
}

BOOL WINAPI BASS_WASAPI_GetMute(DWORD mode) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return runtime.muted.load(std::memory_order_relaxed) ? TRUE : FALSE;
    using Fn = BOOL (WINAPI*)(DWORD);
    return call_original<Fn>("BASS_WASAPI_GetMute", mode);
}

DWORD WINAPI BASS_WASAPI_PutData(void* buffer, DWORD length) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return 0;
    using Fn = DWORD (WINAPI*)(void*, DWORD);
    return call_original<Fn>("BASS_WASAPI_PutData", buffer, length);
}

DWORD WINAPI BASS_WASAPI_GetData(void* buffer, DWORD length) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend == Backend::asio) return 0;
    using Fn = DWORD (WINAPI*)(void*, DWORD);
    return call_original<Fn>("BASS_WASAPI_GetData", buffer, length);
}

DWORD WINAPI BASS_WASAPI_GetLevel() {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = DWORD (WINAPI*)();
        return call_original<Fn>("BASS_WASAPI_GetLevel");
    }
    const auto scale = [](float value) { return static_cast<DWORD>(std::clamp(value, 0.0F, 1.0F) * 32768.0F); };
    return scale(runtime.peak_left.load(std::memory_order_relaxed)) |
           (scale(runtime.peak_right.load(std::memory_order_relaxed)) << 16U);
}

BOOL WINAPI BASS_WASAPI_GetLevelEx(float* levels, float length, DWORD flags) {
    std::lock_guard lock(runtime.control);
    if (runtime.backend != Backend::asio) {
        using Fn = BOOL (WINAPI*)(float*, float, DWORD);
        return call_original<Fn>("BASS_WASAPI_GetLevelEx", levels, length, flags);
    }
    if (levels == nullptr) return FALSE;
    const float left = runtime.peak_left.load(std::memory_order_relaxed);
    const float right = runtime.peak_right.load(std::memory_order_relaxed);
    if ((flags & BASS_LEVEL_MONO) != 0) levels[0] = std::max(left, right);
    else {
        levels[0] = left;
        levels[1] = right;
    }
    return TRUE;
}

}  // extern "C"

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        module_instance = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

