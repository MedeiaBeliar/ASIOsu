#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <objbase.h>

#include <atomic>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>

#include "basswasapi.h"
#include "bassasio.h"

namespace fs = std::filesystem;

namespace {

std::atomic<unsigned> callback_count{0};

DWORD CALLBACK silence(void* buffer, DWORD length, void*) {
    if (buffer != nullptr) ZeroMemory(buffer, length);
    callback_count.fetch_add(1, std::memory_order_relaxed);
    return length;
}

template <typename Function>
Function require_export(HMODULE module, const char* name) {
    auto function = reinterpret_cast<Function>(GetProcAddress(module, name));
    if (function == nullptr) throw std::runtime_error(std::string("missing export: ") + name);
    return function;
}

void enumerate_drivers() {
    BASS_ASIO_SetUnicode(TRUE);
    BASS_ASIO_DEVICEINFO info{};
    for (DWORD index = 0; BASS_ASIO_GetDeviceInfo(index, &info); ++index) {
        const auto* name = reinterpret_cast<const wchar_t*>(info.name);
        std::wcout << index << L"\t" << (name == nullptr ? L"" : name) << L'\n';
    }
}

void probe_driver(const std::wstring& wanted) {
    BASS_ASIO_SetUnicode(TRUE);
    int found = -1;
    for (DWORD index = 0; index < 1024; ++index) {
        BASS_ASIO_DEVICEINFO info{};
        if (!BASS_ASIO_GetDeviceInfo(index, &info)) break;
        const auto* name = reinterpret_cast<const wchar_t*>(info.name);
        if (name != nullptr && _wcsicmp(name, wanted.c_str()) == 0) found = static_cast<int>(index);
    }
    if (found < 0) throw std::runtime_error("ASIO driver not found");
    if (!BASS_ASIO_Init(found, BASS_ASIO_THREAD))
        throw std::runtime_error("BASS_ASIO_Init failed with code " + std::to_string(BASS_ASIO_ErrorGetCode()));
    BASS_ASIO_INFO info{};
    if (!BASS_ASIO_GetInfo(&info)) throw std::runtime_error("BASS_ASIO_GetInfo failed");
    std::cout << "PASS: driver=" << found << " inputs=" << info.inputs << " outputs=" << info.outputs
              << " min=" << info.bufmin << " max=" << info.bufmax << " preferred=" << info.bufpref << '\n';
    BASS_ASIO_Free();
}

void verify_exports(HMODULE module) {
    constexpr const char* names[] = {
        "BASS_WASAPI_CheckFormat", "BASS_WASAPI_Free", "BASS_WASAPI_GetCPU", "BASS_WASAPI_GetData",
        "BASS_WASAPI_GetDevice", "BASS_WASAPI_GetDeviceInfo", "BASS_WASAPI_GetDeviceLevel", "BASS_WASAPI_GetInfo",
        "BASS_WASAPI_GetLevel", "BASS_WASAPI_GetLevelEx", "BASS_WASAPI_GetMute", "BASS_WASAPI_GetVersion",
        "BASS_WASAPI_GetVolume", "BASS_WASAPI_Init", "BASS_WASAPI_IsStarted", "BASS_WASAPI_Lock",
        "BASS_WASAPI_PutData", "BASS_WASAPI_SetDevice", "BASS_WASAPI_SetMute", "BASS_WASAPI_SetNotify",
        "BASS_WASAPI_SetVolume", "BASS_WASAPI_Start", "BASS_WASAPI_Stop"
    };
    for (WORD ordinal = 1; ordinal <= std::size(names); ++ordinal) {
        if (GetProcAddress(module, names[ordinal - 1]) == nullptr) throw std::runtime_error("named export missing");
        if (GetProcAddress(module, MAKEINTRESOURCEA(ordinal)) == nullptr) throw std::runtime_error("ordinal export missing");
    }
}

void verify_contract(const fs::path& directory) {
    const fs::path ini = directory / L"asiosu.ini";
    WritePrivateProfileStringW(L"ASIO", L"Enabled", L"0", ini.c_str());
    HMODULE module = LoadLibraryExW((directory / L"basswasapi.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) throw std::runtime_error("proxy load failed");
    verify_exports(module);
    using Version = DWORD (WINAPI*)();
    using DeviceInfo = BOOL (WINAPI*)(DWORD, BASS_WASAPI_DEVICEINFO*);
    if (require_export<Version>(module, "BASS_WASAPI_GetVersion")() == 0) throw std::runtime_error("version forwarding failed");
    BASS_WASAPI_DEVICEINFO info{};
    if (!require_export<DeviceInfo>(module, "BASS_WASAPI_GetDeviceInfo")(0, &info) || info.name == nullptr)
        throw std::runtime_error("device enumeration forwarding failed");
    FreeLibrary(module);
    std::cout << "PASS: 23 names, 23 ordinals, version and device enumeration\n";
}

void enumerate_wasapi(const fs::path& library) {
    HMODULE module = LoadLibraryExW(library.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) throw std::runtime_error("BASSWASAPI load failed with Windows error " + std::to_string(GetLastError()));
    using DeviceInfo = BOOL (WINAPI*)(DWORD, BASS_WASAPI_DEVICEINFO*);
    DeviceInfo get_info = require_export<DeviceInfo>(module, "BASS_WASAPI_GetDeviceInfo");
    unsigned found = 0;
    for (DWORD index = 0; index < 256; ++index) {
        BASS_WASAPI_DEVICEINFO info{};
        if (!get_info(index, &info)) break;
        std::cout << index << '\t' << (info.name == nullptr ? "" : info.name) << '\n';
        ++found;
    }
    FreeLibrary(module);
    if (found == 0) throw std::runtime_error("no BASSWASAPI devices found");
}

void verify_active(const fs::path& directory, const std::wstring& driver) {
    const fs::path ini = directory / L"asiosu.ini";
    WritePrivateProfileStringW(L"ASIO", L"Enabled", L"1", ini.c_str());
    WritePrivateProfileStringW(L"ASIO", L"Driver", driver.c_str(), ini.c_str());
    WritePrivateProfileStringW(L"ASIO", L"FirstOutput", L"0", ini.c_str());
    WritePrivateProfileStringW(L"ASIO", L"SampleRate", L"48000", ini.c_str());
    WritePrivateProfileStringW(L"ASIO", L"BufferFrames", L"32", ini.c_str());
    HMODULE module = LoadLibraryExW((directory / L"basswasapi.dll").c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (module == nullptr) throw std::runtime_error("proxy load failed");
    using Init = BOOL (WINAPI*)(int, DWORD, DWORD, DWORD, float, float, WASAPIPROC*, void*);
    using Info = BOOL (WINAPI*)(BASS_WASAPI_INFO*);
    using NoArgs = BOOL (WINAPI*)();
    using Stop = BOOL (WINAPI*)(BOOL);
    if (!require_export<Init>(module, "BASS_WASAPI_Init")(-1, 0, 0, BASS_WASAPI_AUTOFORMAT | BASS_WASAPI_EVENT,
            0.0F, 0.0F, silence, nullptr)) throw std::runtime_error("ASIO initialization failed");
    BASS_WASAPI_INFO info{};
    if (!require_export<Info>(module, "BASS_WASAPI_GetInfo")(&info) || info.freq != 48000 || info.chans != 2 ||
        info.format != BASS_WASAPI_FORMAT_FLOAT) throw std::runtime_error("bridge format mismatch");
    if (!require_export<NoArgs>(module, "BASS_WASAPI_Start")()) throw std::runtime_error("ASIO start failed");
    Sleep(250);
    if (callback_count.load(std::memory_order_relaxed) == 0) throw std::runtime_error("ASIO callback did not run");
    if (!require_export<Stop>(module, "BASS_WASAPI_Stop")(TRUE)) throw std::runtime_error("ASIO stop failed");
    if (!require_export<NoArgs>(module, "BASS_WASAPI_Free")()) throw std::runtime_error("ASIO free failed");
    FreeLibrary(module);
    std::cout << "PASS: ASIO callback bridge at 48000 Hz stereo / 32 frames\n";
}

}  // namespace

int wmain(int count, wchar_t** values) {
    try {
        const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(com) && com != RPC_E_CHANGED_MODE) throw std::runtime_error("COM initialization failed");
        if (count == 2 && _wcsicmp(values[1], L"--list") == 0) enumerate_drivers();
        else if (count == 3 && _wcsicmp(values[1], L"--asio-probe") == 0) probe_driver(values[2]);
        else if (count == 3 && _wcsicmp(values[1], L"--wasapi-list") == 0) enumerate_wasapi(fs::weakly_canonical(values[2]));
        else if (count == 3 && _wcsicmp(values[1], L"--contract") == 0) verify_contract(fs::weakly_canonical(values[2]));
        else if (count == 4 && _wcsicmp(values[1], L"--active") == 0) verify_active(fs::weakly_canonical(values[2]), values[3]);
        else throw std::runtime_error("usage: --list | --contract <directory> | --active <directory> <driver>");
        if (SUCCEEDED(com)) CoUninitialize();
        return 0;
    } catch (const std::exception& exception) {
        std::cerr << "FAIL: " << exception.what() << '\n';
        return 1;
    }
}

