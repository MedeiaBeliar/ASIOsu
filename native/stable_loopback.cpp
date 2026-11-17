#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmsystem.h>
#include <wrl.h>
#include <wrl/implements.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>
#include "bassasio.h"

using namespace Microsoft::WRL;

namespace {
constexpr DWORD capture_rate = 44100;
constexpr DWORD capacity = 32768;
constexpr DWORD minimum_fill = 882;
constexpr DWORD maximum_fill = 1764;

struct Handle {
    HANDLE value;
    ~Handle() { if (value) CloseHandle(value); }
    operator HANDLE() const { return value; }
};

void check(HRESULT result, const char* operation) {
    if (FAILED(result)) throw std::runtime_error(std::string(operation) + " failed (HRESULT " + std::to_string(result) + ").");
}

void check_asio(BOOL result, const char* operation) {
    if (!result) throw std::runtime_error(std::string(operation) + " failed (ASIO " + std::to_string(BASS_ASIO_ErrorGetCode()) + ").");
}

class Activation final : public RuntimeClass<RuntimeClassFlags<ClassicCom>, IActivateAudioInterfaceCompletionHandler, FtmBase> {
public:
    Handle ready{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    ComPtr<IAudioClient> client;
    HRESULT result = E_PENDING;

    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> activated;
        HRESULT status = operation->GetActivateResult(&result, &activated);
        if (FAILED(status)) result = status;
        if (SUCCEEDED(result)) result = activated.As(&client);
        SetEvent(ready);
        return S_OK;
    }
};

struct AudioQueue {
    std::array<float, capacity * 2> samples{};
    std::atomic<DWORD> read{0}, write{0};
    std::atomic<bool> sent_signal{false};
    bool primed = false;

    void push(const short* data, DWORD frames) {
        const DWORD start = write.load(std::memory_order_relaxed);
        frames = std::min(frames, capacity - (start - read.load(std::memory_order_acquire)));
        for (DWORD i = 0; i < frames; ++i) {
            const DWORD offset = ((start + i) % capacity) * 2;
            samples[offset] = data ? data[i * 2] / 32768.0F : 0.0F;
            samples[offset + 1] = data ? data[i * 2 + 1] / 32768.0F : 0.0F;
        }
        write.store(start + frames, std::memory_order_release);
    }

    static DWORD CALLBACK output(BOOL, DWORD, void* buffer, DWORD bytes, void* user) noexcept {
        auto& queue = *static_cast<AudioQueue*>(user);
        auto* pcm = static_cast<float*>(buffer);
        const DWORD frames = bytes / (2 * sizeof(float));
        DWORD start = queue.read.load(std::memory_order_relaxed);
        const DWORD end = queue.write.load(std::memory_order_acquire);
        const DWORD target = std::max(minimum_fill, frames + 441);
        if (end - start > std::max(maximum_fill, target + 882)) start = end - target;
        if (end - start < frames) queue.primed = false;
        else if (end - start >= target) queue.primed = true;
        const DWORD available = queue.primed ? frames : 0;
        bool signal = false;
        for (DWORD i = 0; i < available; ++i) {
            const DWORD offset = ((start + i) % capacity) * 2;
            pcm[i * 2] = queue.samples[offset];
            pcm[i * 2 + 1] = queue.samples[offset + 1];
            signal = signal || std::abs(pcm[i * 2]) > 0.001F || std::abs(pcm[i * 2 + 1]) > 0.001F;
        }
        std::fill(pcm + available * 2, pcm + frames * 2, 0.0F);
        queue.read.store(start + available, std::memory_order_release);
        if (signal) queue.sent_signal.store(true, std::memory_order_relaxed);
        return bytes;
    }
};

int driver_index(const wchar_t* name) {
    check_asio(BASS_ASIO_SetUnicode(TRUE), "Configure ASIO driver enumeration");
    BASS_ASIO_DEVICEINFO info{};
    int match = -1;
    for (DWORD i = 0; BASS_ASIO_GetDeviceInfo(i, &info); ++i) {
        if (!info.name || _wcsicmp(name, reinterpret_cast<const wchar_t*>(info.name)) != 0) continue;
        if (match >= 0) throw std::runtime_error("Duplicate ASIO driver name.");
        match = static_cast<int>(i);
    }
    if (match < 0) throw std::runtime_error("The selected 32-bit ASIO driver was not found.");
    return match;
}

DWORD number(const wchar_t* input, DWORD minimum, DWORD maximum) {
    const std::wstring_view digits(input);
    if (digits.empty() || digits.find_first_not_of(L"0123456789") != digits.npos)
        throw std::runtime_error("Invalid numeric argument.");
    const auto value = std::stoul(input);
    if (value < minimum || value > maximum) throw std::runtime_error("Numeric argument is out of range.");
    return value;
}

struct TestTone {
    std::array<short, capture_rate * 4> samples{};
    HWAVEOUT device = nullptr;
    WAVEHDR header{};
    bool prepared = false;

    void start(WAVEFORMATEX format) {
        for (unsigned i = 0; i < samples.size() / 2; ++i) {
            const auto sample = static_cast<short>(500 * std::sin(i * 440.0 * 6.283185307 / capture_rate));
            samples[i * 2] = samples[i * 2 + 1] = sample;
        }
        if (waveOutOpen(&device, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR)
            throw std::runtime_error("Could not open the Windows test-tone playback device.");
        header.lpData = reinterpret_cast<LPSTR>(samples.data());
        header.dwBufferLength = sizeof(samples);
        prepared = waveOutPrepareHeader(device, &header, sizeof(header)) == MMSYSERR_NOERROR;
        if (!prepared || waveOutWrite(device, &header, sizeof(header)) != MMSYSERR_NOERROR)
            throw std::runtime_error("Could not play the Windows test tone.");
    }

    ~TestTone() {
        if (!device) return;
        waveOutReset(device);
        if (prepared) waveOutUnprepareHeader(device, &header, sizeof(header));
        waveOutClose(device);
    }
};

void run(DWORD pid, const wchar_t* driver, DWORD first, DWORD rate, DWORD frames, bool self_test) {
    Handle target{OpenProcess(SYNCHRONIZE, FALSE, pid)};
    Handle packet{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    auto activation = Make<Activation>();
    if (!target.value || !packet.value || !activation || !activation->ready.value)
        throw std::runtime_error("Could not open the target process or allocate capture resources.");
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams = {pid, PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE};
    PROPVARIANT variant{};
    variant.vt = VT_BLOB;
    variant.blob = {sizeof(params), reinterpret_cast<BYTE*>(&params)};
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    check(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient),
        &variant, activation.Get(), &operation), "Activate WASAPI process loopback");
    if (WaitForSingleObject(activation->ready, 5000) != WAIT_OBJECT_0)
        throw std::runtime_error("WASAPI activation did not complete.");
    check(activation->result, "Open WASAPI process loopback");
    auto audio = activation->client;
    WAVEFORMATEX format{WAVE_FORMAT_PCM, 2, capture_rate, capture_rate * 4, 4, 16, 0};
    check(audio->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK |
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, &format, nullptr), "Initialize loopback");
    check(audio->SetEventHandle(packet), "Set WASAPI capture event");
    ComPtr<IAudioCaptureClient> capture;
    check(audio->GetService(IID_PPV_ARGS(&capture)), "Open WASAPI capture client");

    AudioQueue queue;
    check_asio(BASS_ASIO_Init(driver_index(driver), BASS_ASIO_THREAD), "Open ASIO driver");
    const struct AsioCleanup { ~AsioCleanup() { BASS_ASIO_Free(); } } asio_cleanup;
    check_asio(BASS_ASIO_SetRate(rate), "Set ASIO hardware sample rate");
    check_asio(BASS_ASIO_ChannelEnable(FALSE, first, AudioQueue::output, &queue), "Enable ASIO output");
    check_asio(BASS_ASIO_ChannelJoin(FALSE, first + 1, static_cast<int>(first)), "Join stereo output pair");
    check_asio(BASS_ASIO_ChannelSetFormat(FALSE, first, BASS_ASIO_FORMAT_FLOAT), "Set ASIO sample format");
    check_asio(BASS_ASIO_ChannelSetRate(FALSE, first, capture_rate), "Set ASIO stream sample rate");
    check_asio(BASS_ASIO_Start(frames, 1), "Start ASIO output");
    check(audio->Start(), "Start WASAPI capture");
    const struct CaptureCleanup {
        IAudioClient* audio;
        ~CaptureCleanup() { audio->Stop(); }
    } capture_cleanup{audio.Get()};

    TestTone tone;
    if (self_test) tone.start(format);
    std::cout << "READY" << std::endl;
    const HANDLE events[] = {target, packet};
    const ULONGLONG deadline = GetTickCount64() + 3500;
    while (!self_test || GetTickCount64() < deadline) {
        const DWORD wait = WaitForMultipleObjects(2, events, FALSE, self_test ? 100 : INFINITE);
        if (wait == WAIT_OBJECT_0) break;
        if (wait == WAIT_TIMEOUT) continue;
        if (wait != WAIT_OBJECT_0 + 1) throw std::runtime_error("Waiting for WASAPI audio failed.");
        UINT32 next = 0;
        while (check(capture->GetNextPacketSize(&next), "Read WASAPI packet length"), next != 0) {
            BYTE* data = nullptr;
            UINT32 count = 0;
            DWORD flags = 0;
            check(capture->GetBuffer(&data, &count, &flags, nullptr, nullptr), "Read WASAPI audio");
            queue.push(flags & AUDCLNT_BUFFERFLAGS_SILENT ? nullptr : reinterpret_cast<const short*>(data), count);
            check(capture->ReleaseBuffer(count), "Release WASAPI audio packet");
        }
        if (self_test && queue.sent_signal.load(std::memory_order_relaxed)) break;
    }
    if (self_test && !queue.sent_signal.load(std::memory_order_relaxed))
        throw std::runtime_error("No test signal reached the ASIO output callback.");
    if (self_test) std::cout << "PASS: Windows test tone reached the real ASIO callback.\n";
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 6) throw std::runtime_error("Usage: ASIOsu.Loopback.exe <PID|--self-test> <driver name> <first output (0-based)> <rate> <frames>");
        const bool self_test = std::wstring_view(argv[1]) == L"--self-test";
        check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "Initialize COM");
        const struct ComCleanup { ~ComCleanup() { CoUninitialize(); } } com_cleanup;
        run(self_test ? GetCurrentProcessId() : number(argv[1], 1, MAXDWORD), argv[2],
            number(argv[3], 0, 1023), number(argv[4], 8000, 384000), number(argv[5], 1, 8192), self_test);
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
