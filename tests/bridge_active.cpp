#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <filesystem>
#include <iostream>
#include <stdexcept>

#include "basswasapi.h"

namespace fs = std::filesystem;
namespace {
unsigned callbacks = 0;
DWORD CALLBACK source(void* buffer, DWORD length, void*) {
    auto* samples = static_cast<float*>(buffer);
    for (DWORD index = 0; index < length / sizeof(float); ++index) samples[index] = index % 2 == 0 ? 0.25F : -0.25F;
    ++callbacks;
    return length;
}
template <typename Function> Function get(HMODULE module, const char* name) {
    auto result = reinterpret_cast<Function>(GetProcAddress(module, name));
    if (result == nullptr) throw std::runtime_error(std::string("missing export: ") + name);
    return result;
}
}

int wmain(int count, wchar_t** values) {
    try {
        if (count != 2) throw std::runtime_error("fixture directory argument is required");
        fs::path fixture = fs::weakly_canonical(values[1]);
        fs::path ini = fixture / L"asiosu.ini";
        WritePrivateProfileStringW(L"ASIO", L"Enabled", L"1", ini.c_str());
        WritePrivateProfileStringW(L"ASIO", L"Driver", L"ASIO 2.0 - Fake", ini.c_str());
        WritePrivateProfileStringW(L"ASIO", L"FirstOutput", L"0", ini.c_str());
        WritePrivateProfileStringW(L"ASIO", L"SampleRate", L"48000", ini.c_str());
        WritePrivateProfileStringW(L"ASIO", L"BufferFrames", L"32", ini.c_str());
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
        DLL_DIRECTORY_COOKIE cookie = AddDllDirectory(fixture.c_str());
        if (cookie == nullptr) throw std::runtime_error("cannot add fixture DLL directory");
        HMODULE bridge = LoadLibraryExW((fixture / L"basswasapi.dll").c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_USER_DIRS | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        if (bridge == nullptr) throw std::runtime_error("cannot load bridge");
        using Init = BOOL (WINAPI*)(int, DWORD, DWORD, DWORD, float, float, WASAPIPROC*, void*);
        using Info = BOOL (WINAPI*)(BASS_WASAPI_INFO*);
        using Plain = BOOL (WINAPI*)();
        using Stop = BOOL (WINAPI*)(BOOL);
        if (!get<Init>(bridge, "BASS_WASAPI_Init")(-1, 0, 0, BASS_WASAPI_EVENT | BASS_WASAPI_AUTOFORMAT, 0, 0, source, nullptr))
            throw std::runtime_error("bridge init failed");
        BASS_WASAPI_INFO info{};
        if (!get<Info>(bridge, "BASS_WASAPI_GetInfo")(&info) || info.freq != 48000 || info.chans != 2 || info.buflen != 256)
            throw std::runtime_error("bridge info mismatch");
        if (!get<Plain>(bridge, "BASS_WASAPI_Start")() || callbacks != 1) throw std::runtime_error("bridge callback failed");
        if (!get<Stop>(bridge, "BASS_WASAPI_Stop")(TRUE) || !get<Plain>(bridge, "BASS_WASAPI_Free")())
            throw std::runtime_error("bridge shutdown failed");
        FreeLibrary(bridge);
        RemoveDllDirectory(cookie);
        std::cout << "PASS: independent bridge init, stereo render callback, start, stop and free\n";
        return 0;
    } catch (const std::exception& exception) {
        std::cerr << "FAIL: " << exception.what() << '\n';
        return 1;
    }
}

