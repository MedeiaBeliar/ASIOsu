#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <numeric>
#include <stdexcept>
#include <string>
#include <vector>

#include "basswasapi.h"

namespace fs = std::filesystem;

namespace {

using GetDeviceInfo = BOOL(WINAPI*)(DWORD, BASS_WASAPI_DEVICEINFO*);
using Init = BOOL(WINAPI*)(int, DWORD, DWORD, DWORD, float, float, WASAPIPROC*, void*);
using GetInfo = BOOL(WINAPI*)(BASS_WASAPI_INFO*);
using Plain = BOOL(WINAPI*)();
using Stop = BOOL(WINAPI*)(BOOL);

struct Api {
    HMODULE bass = nullptr;
    HMODULE module = nullptr;
    GetDeviceInfo get_device_info = nullptr;
    Init init = nullptr;
    GetInfo get_info = nullptr;
    Plain start = nullptr;
    Stop stop = nullptr;
    Plain free = nullptr;
};

struct CaptureState {
    std::atomic<long long> pending_key{0};
    std::atomic<bool> signal_high{false};
    std::atomic<bool> finished{false};
    std::mutex mutex;
    std::vector<double> milliseconds;
    long long frequency = 0;
    DWORD sample_rate = 48000;
    DWORD channels = 2;
    unsigned wanted = 12;
    float threshold = 0.02F;
    DWORD main_thread = 0;
};

struct MonitorState {
    std::atomic<float> peak{0};
    std::atomic<unsigned long long> callbacks{0};
};

CaptureState* active_state = nullptr;
HHOOK keyboard_hook = nullptr;
bool key_down[256]{};

template <typename Function>
Function require(HMODULE module, const char* name) {
    auto function = reinterpret_cast<Function>(GetProcAddress(module, name));
    if (function == nullptr) throw std::runtime_error(std::string("Missing export: ") + name);
    return function;
}

Api load_api(const fs::path& directory) {
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
    if (AddDllDirectory(directory.c_str()) == nullptr) throw std::runtime_error("Could not add the capture DLL directory.");
    HMODULE bass = LoadLibraryExW((directory / L"bass.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_USER_DIRS | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (bass == nullptr) throw std::runtime_error("Could not load the official BASS capture runtime.");
    using SetConfig = BOOL(WINAPI*)(DWORD, DWORD);
    if (!require<SetConfig>(bass, "BASS_SetConfig")(BASS_CONFIG_UNICODE, TRUE))
        throw std::runtime_error("Could not enable UTF-8 device names.");
    HMODULE module = LoadLibraryExW((directory / L"basswasapi.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_USER_DIRS | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) throw std::runtime_error("Could not load the official BASSWASAPI capture runtime.");
    return {bass, module,
        require<GetDeviceInfo>(module, "BASS_WASAPI_GetDeviceInfo"),
        require<Init>(module, "BASS_WASAPI_Init"),
        require<GetInfo>(module, "BASS_WASAPI_GetInfo"),
        require<Plain>(module, "BASS_WASAPI_Start"),
        require<Stop>(module, "BASS_WASAPI_Stop"),
        require<Plain>(module, "BASS_WASAPI_Free")};
}

bool osu_is_foreground() {
    DWORD process_id = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &process_id);
    if (process_id == 0) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (process == nullptr) return false;
    std::vector<wchar_t> path(32768);
    DWORD length = static_cast<DWORD>(path.size());
    const bool queried = QueryFullProcessImageNameW(process, 0, path.data(), &length) != FALSE;
    CloseHandle(process);
    return queried && _wcsicmp(fs::path(std::wstring(path.data(), length)).filename().c_str(), L"osu!.exe") == 0;
}

bool accepted_key(DWORD key) {
    return key == 'S' || key == 'D' || key == 'L' || key == VK_OEM_1;
}

LRESULT CALLBACK keyboard(int code, WPARAM message, LPARAM parameter) {
    if (code == HC_ACTION && active_state != nullptr) {
        auto* event = reinterpret_cast<KBDLLHOOKSTRUCT*>(parameter);
        const DWORD key = event->vkCode;
        if (key < std::size(key_down) && accepted_key(key)) {
            if (message == WM_KEYUP || message == WM_SYSKEYUP) key_down[key] = false;
            else if ((message == WM_KEYDOWN || message == WM_SYSKEYDOWN) && !key_down[key]) {
                key_down[key] = true;
                const bool foreground = osu_is_foreground();
                std::cout << "key=" << static_cast<unsigned>(key) << " foreground=" << (foreground ? "yes" : "no") << '\n' << std::flush;
                if (foreground) {
                    LARGE_INTEGER now{};
                    QueryPerformanceCounter(&now);
                    active_state->pending_key.store(now.QuadPart, std::memory_order_release);
                }
            }
        }
    }
    return CallNextHookEx(keyboard_hook, code, message, parameter);
}

DWORD CALLBACK capture_audio(void* buffer, DWORD length, void* user) {
    auto& state = *static_cast<CaptureState*>(user);
    if (buffer == nullptr || state.channels == 0) return 1;
    LARGE_INTEGER received{};
    QueryPerformanceCounter(&received);
    const auto* samples = static_cast<const float*>(buffer);
    const size_t sample_count = length / sizeof(float);
    const size_t frames = sample_count / state.channels;
    bool high = state.signal_high.load(std::memory_order_relaxed);
    for (size_t frame = 0; frame < frames; ++frame) {
        float peak = 0;
        for (DWORD channel = 0; channel < state.channels; ++channel)
            peak = std::max(peak, std::abs(samples[frame * state.channels + channel]));
        if (!high && peak >= state.threshold) {
            high = true;
            const long long key = state.pending_key.exchange(0, std::memory_order_acq_rel);
            if (key != 0) {
                const double remaining = static_cast<double>(frames - frame) / state.sample_rate;
                const long long sample = received.QuadPart - static_cast<long long>(remaining * state.frequency);
                const double elapsed = static_cast<double>(sample - key) * 1000.0 / state.frequency;
                if (elapsed >= 0.0 && elapsed <= 1000.0) {
                    unsigned number = 0;
                    {
                        std::lock_guard lock(state.mutex);
                        state.milliseconds.push_back(elapsed);
                        number = static_cast<unsigned>(state.milliseconds.size());
                    }
                    std::cout << "sample " << number << '/' << state.wanted << ": " << std::fixed
                              << std::setprecision(4) << elapsed << " ms\n" << std::flush;
                    if (number >= state.wanted && !state.finished.exchange(true))
                        PostThreadMessageW(state.main_thread, WM_QUIT, 0, 0);
                }
            }
        } else if (high && peak < state.threshold * 0.35F) {
            high = false;
        }
    }
    state.signal_high.store(high, std::memory_order_relaxed);
    return 1;
}

DWORD CALLBACK monitor_audio(void* buffer, DWORD length, void* user) {
    auto& state = *static_cast<MonitorState*>(user);
    state.callbacks.fetch_add(1, std::memory_order_relaxed);
    const auto* samples = static_cast<const float*>(buffer);
    const size_t count = buffer == nullptr ? 0 : length / sizeof(float);
    float observed = 0;
    for (size_t index = 0; index < count; ++index) observed = std::max(observed, std::abs(samples[index]));
    float previous = state.peak.load(std::memory_order_relaxed);
    while (observed > previous && !state.peak.compare_exchange_weak(previous, observed, std::memory_order_relaxed)) { }
    return 1;
}

BOOL WINAPI console_control(DWORD type) {
    if ((type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT) && active_state != nullptr) {
        PostThreadMessageW(active_state->main_thread, WM_QUIT, 0, 0);
        return TRUE;
    }
    return FALSE;
}

void list_devices(const Api& api) {
    for (DWORD index = 0; index < 1024; ++index) {
        BASS_WASAPI_DEVICEINFO info{};
        if (!api.get_device_info(index, &info)) break;
        if ((info.flags & BASS_DEVICE_INPUT) == 0 || (info.flags & BASS_DEVICE_ENABLED) == 0) continue;
        std::cout << index << '\t' << (info.name == nullptr ? "" : info.name) << '\t' << info.mixfreq << " Hz / "
                  << info.mixchans << " ch\tflags=0x" << std::hex << info.flags << std::dec << "\n";
    }
}

unsigned parse_unsigned(const wchar_t* value) {
    wchar_t* end = nullptr;
    const unsigned long result = wcstoul(value, &end, 10);
    if (end == value || *end != L'\0' || result > 100000) throw std::runtime_error("Invalid numeric argument.");
    return static_cast<unsigned>(result);
}

void print_summary(std::vector<double> values) {
    std::sort(values.begin(), values.end());
    const double mean = std::accumulate(values.begin(), values.end(), 0.0) / values.size();
    const double median = values.size() % 2 == 0 ? (values[values.size() / 2 - 1] + values[values.size() / 2]) / 2.0
                                                  : values[values.size() / 2];
    const size_t p95_index = static_cast<size_t>(std::ceil(values.size() * 0.95)) - 1;
    std::cout << "\nmedian=" << std::fixed << std::setprecision(4) << median << " ms, mean=" << mean
              << " ms, p95=" << values[p95_index] << " ms\nsorted=";
    for (size_t index = 0; index < values.size(); ++index)
        std::cout << (index == 0 ? "" : ", ") << values[index];
    std::cout << "\n";
}

void run_capture(const Api& api, unsigned device, unsigned wanted, float threshold) {
    CaptureState state;
    LARGE_INTEGER frequency{};
    QueryPerformanceFrequency(&frequency);
    state.frequency = frequency.QuadPart;
    state.wanted = wanted;
    state.threshold = threshold;
    state.main_thread = GetCurrentThreadId();
    active_state = &state;

    if (!api.init(static_cast<int>(device), 48000, 2, BASS_WASAPI_EVENT, 0, 0, capture_audio, &state) &&
        !api.init(static_cast<int>(device), 48000, 2, 0, 0.05F, 0.005F, capture_audio, &state))
        throw std::runtime_error("Could not initialize the selected input device.");
    BASS_WASAPI_INFO info{};
    if (!api.get_info(&info)) throw std::runtime_error("Could not read the capture format.");
    state.sample_rate = info.freq;
    state.channels = info.chans;
    keyboard_hook = SetWindowsHookExW(WH_KEYBOARD_LL, keyboard, GetModuleHandleW(nullptr), 0);
    if (keyboard_hook == nullptr) throw std::runtime_error("Could not install the keyboard timing hook.");
    SetConsoleCtrlHandler(console_control, TRUE);
    if (!api.start()) throw std::runtime_error("Could not start input capture.");
    std::cout << "Capture active: " << info.freq << " Hz / " << info.chans << " ch / " << info.buflen
              << " bytes. Keep osu!lazer focused and press only S, D, L, or ; with spacing.\n";
    MSG message{};
    while (GetMessageW(&message, nullptr, 0, 0) > 0) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    api.stop(TRUE);
    UnhookWindowsHookEx(keyboard_hook);
    keyboard_hook = nullptr;
    api.free();
    active_state = nullptr;
    SetConsoleCtrlHandler(console_control, FALSE);
    std::lock_guard lock(state.mutex);
    if (!state.milliseconds.empty()) print_summary(state.milliseconds);
}

void run_monitor(const Api& api, unsigned device, unsigned seconds) {
    MonitorState state;
    if (!api.init(static_cast<int>(device), 48000, 2, BASS_WASAPI_EVENT, 0, 0, monitor_audio, &state) &&
        !api.init(static_cast<int>(device), 48000, 2, 0, 0.05F, 0.005F, monitor_audio, &state))
        throw std::runtime_error("Could not initialize the selected input device.");
    if (!api.start()) throw std::runtime_error("Could not start input monitoring.");
    Sleep(seconds * 1000);
    api.stop(TRUE);
    api.free();
    std::cout << "callbacks=" << state.callbacks.load() << " peak=" << std::fixed << std::setprecision(6)
              << state.peak.load() << '\n';
}

}  // namespace

int wmain(int count, wchar_t** values) {
    try {
        SetConsoleOutputCP(CP_UTF8);
        if (count < 3) throw std::runtime_error("Usage: --list <runtime-directory> | --monitor <runtime-directory> <device-index> [seconds] | --capture <runtime-directory> <device-index> [sample-count] [threshold]");
        const fs::path directory = fs::weakly_canonical(values[2]);
        Api api = load_api(directory);
        if (_wcsicmp(values[1], L"--list") == 0 && count == 3) list_devices(api);
        else if (_wcsicmp(values[1], L"--monitor") == 0 && count >= 4 && count <= 5) {
            run_monitor(api, parse_unsigned(values[3]), count == 5 ? parse_unsigned(values[4]) : 3);
        } else if (_wcsicmp(values[1], L"--capture") == 0 && count >= 4 && count <= 6) {
            const unsigned device = parse_unsigned(values[3]);
            const unsigned samples = count >= 5 ? parse_unsigned(values[4]) : 12;
            wchar_t* end = nullptr;
            const float threshold = count >= 6 ? wcstof(values[5], &end) : 0.02F;
            if (threshold <= 0 || threshold >= 1 || (count >= 6 && (end == values[5] || *end != L'\0')))
                throw std::runtime_error("Invalid threshold.");
            run_capture(api, device, samples, threshold);
        } else throw std::runtime_error("Invalid arguments.");
        FreeLibrary(api.module);
        FreeLibrary(api.bass);
        return 0;
    } catch (const std::exception& exception) {
        std::cerr << "FAIL: " << exception.what() << '\n';
        return 1;
    }
}
