#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shellapi.h>

#include <cstring>
#include <filesystem>
#include <fstream>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>

namespace fs = std::filesystem;

namespace {

fs::path current_executable() {
    std::vector<wchar_t> buffer(32768);
    DWORD size = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (size == 0 || size >= buffer.size()) throw std::runtime_error("Cannot locate ASIOsu.exe.");
    return fs::path(std::wstring(buffer.data(), size));
}

std::wstring quote(const std::wstring& value) {
    if (value.find_first_of(L" \t\n\v\"") == std::wstring::npos) return value;
    std::wstring result(1, L'\"');
    unsigned slashes = 0;
    for (wchar_t character : value) {
        if (character == L'\\') { ++slashes; continue; }
        if (character == L'\"') {
            result.append(slashes * 2U + 1U, L'\\');
            result.push_back(character);
            slashes = 0;
            continue;
        }
        result.append(slashes, L'\\');
        slashes = 0;
        result.push_back(character);
    }
    result.append(slashes * 2U, L'\\');
    result.push_back(L'\"');
    return result;
}

void launch(const fs::path& executable, const std::vector<std::wstring>& arguments, const fs::path& working_directory) {
    std::wstring command = quote(executable.wstring());
    for (const auto& argument : arguments) command += L" " + quote(argument);
    std::vector<wchar_t> mutable_command(command.begin(), command.end());
    mutable_command.push_back(L'\0');
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable.c_str(), mutable_command.data(), nullptr, nullptr, FALSE, 0, nullptr,
                        working_directory.c_str(), &startup, &process))
        throw std::runtime_error("Could not start ASIOsu runtime.");
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
}

std::optional<std::wstring> argument_value(int count, wchar_t** values, const wchar_t* name) {
    for (int index = 1; index + 1 < count; ++index)
        if (_wcsicmp(values[index], name) == 0) return values[index + 1];
    return std::nullopt;
}

bool has_argument(int count, wchar_t** values, const wchar_t* name) {
    for (int index = 1; index < count; ++index) if (_wcsicmp(values[index], name) == 0) return true;
    return false;
}

bool same_file(const fs::path& left, const fs::path& right) {
    if (!fs::is_regular_file(left) || !fs::is_regular_file(right) || fs::file_size(left) != fs::file_size(right)) return false;
    std::ifstream first(left, std::ios::binary), second(right, std::ios::binary);
    if (!first || !second) return false;
    std::vector<char> a(256U * 1024U), b(256U * 1024U);
    while (first) {
        first.read(a.data(), static_cast<std::streamsize>(a.size()));
        std::streamsize length = first.gcount();
        if (length == 0) break;
        second.read(b.data(), length);
        if (second.gcount() != length || std::memcmp(a.data(), b.data(), static_cast<size_t>(length)) != 0) return false;
    }
    return second.peek() == std::char_traits<char>::eof();
}

void copy_directory(const fs::path& source, const fs::path& destination) {
    fs::create_directories(destination);
    for (const auto& item : fs::recursive_directory_iterator(source)) {
        if (item.is_symlink()) throw std::runtime_error("Update package contains a symbolic link.");
        fs::path target = destination / fs::relative(item.path(), source);
        if (item.is_directory()) fs::create_directories(target);
        else if (item.is_regular_file()) {
            fs::create_directories(target.parent_path());
            fs::copy_file(item.path(), target, fs::copy_options::overwrite_existing);
            if (!same_file(item.path(), target)) throw std::runtime_error("A staged update file failed verification.");
        } else throw std::runtime_error("Update package contains an unsupported entry.");
    }
}

void wait_for(DWORD process_id) {
    if (process_id == 0) return;
    HANDLE process = OpenProcess(SYNCHRONIZE, FALSE, process_id);
    if (process == nullptr) {
        if (GetLastError() == ERROR_INVALID_PARAMETER) return;
        throw std::runtime_error("Cannot wait for the running ASIOsu process.");
    }
    DWORD result = WaitForSingleObject(process, 120000);
    CloseHandle(process);
    if (result != WAIT_OBJECT_0) throw std::runtime_error("Timed out while waiting for ASIOsu to close.");
}

void recover_runtime(const fs::path& root) {
    fs::path runtime = root / L"runtime";
    fs::path saved = root / L".asiosu-update-backup" / L"runtime";
    if (!fs::exists(runtime) && fs::exists(saved)) fs::rename(saved, runtime);
}

int apply_update(int count, wchar_t** values, const fs::path& helper) {
    auto source_text = argument_value(count, values, L"--source");
    auto target_text = argument_value(count, values, L"--target");
    auto pid_text = argument_value(count, values, L"--wait-pid");
    auto tag = argument_value(count, values, L"--tag");
    if (!source_text || !target_text || !pid_text || !tag) throw std::runtime_error("Update arguments are incomplete.");
    fs::path source = fs::weakly_canonical(*source_text);
    fs::path target = fs::weakly_canonical(*target_text);
    if (!fs::is_directory(source) || !fs::is_directory(target) || source == target || target == target.root_path())
        throw std::runtime_error("Update paths are invalid.");
    if (!fs::is_regular_file(source / L"ASIOsu.exe") || !fs::is_regular_file(source / L"manifest.json") ||
        !fs::is_regular_file(source / L"runtime" / L"ASIOsu.exe")) throw std::runtime_error("Update package is incomplete.");
    for (const auto& item : fs::directory_iterator(source))
        if (item.is_directory() && _wcsicmp(item.path().filename().c_str(), L"runtime") != 0)
            throw std::runtime_error("Update package contains an unexpected root directory.");

    wchar_t* end = nullptr;
    unsigned long pid = wcstoul(pid_text->c_str(), &end, 10);
    if (end == pid_text->c_str() || *end != L'\0') throw std::runtime_error("Update process identifier is invalid.");
    wait_for(static_cast<DWORD>(pid));

    fs::path staging = target / L".asiosu-update-staging";
    fs::path backup = target / L".asiosu-update-backup";
    if (fs::exists(staging)) fs::remove_all(staging);
    if (fs::exists(backup)) fs::remove_all(backup);
    copy_directory(source / L"runtime", staging);
    fs::create_directories(backup);

    std::vector<fs::path> root_files;
    for (const auto& item : fs::directory_iterator(source)) if (item.is_regular_file()) root_files.push_back(item.path().filename());
    bool old_runtime_saved = false;
    bool new_runtime_active = false;
    bool roots_started = false;
    try {
        for (const auto& name : root_files)
            if (fs::is_regular_file(target / name)) fs::copy_file(target / name, backup / name, fs::copy_options::overwrite_existing);
        if (fs::exists(target / L"runtime")) {
            fs::rename(target / L"runtime", backup / L"runtime");
            old_runtime_saved = true;
        }
        fs::rename(staging, target / L"runtime");
        new_runtime_active = true;
        roots_started = true;
        for (const auto& name : root_files) {
            fs::copy_file(source / name, target / name, fs::copy_options::overwrite_existing);
            if (!same_file(source / name, target / name)) throw std::runtime_error("Updated root file failed verification.");
        }
        fs::remove_all(backup);
    } catch (...) {
        try {
            if (new_runtime_active && fs::exists(target / L"runtime")) fs::remove_all(target / L"runtime");
            if (old_runtime_saved && fs::exists(backup / L"runtime")) fs::rename(backup / L"runtime", target / L"runtime");
            if (roots_started) for (const auto& name : root_files) {
                if (fs::exists(target / name)) fs::remove(target / name);
                if (fs::exists(backup / name)) fs::copy_file(backup / name, target / name, fs::copy_options::overwrite_existing);
            }
        } catch (...) { }
        throw;
    }

    if (!has_argument(count, values, L"--no-restart")) launch(target / L"ASIOsu.exe", {L"--updated", *tag}, target);
    if (helper.filename().wstring().rfind(L"ASIOsu.UpdateHost.", 0) == 0)
        MoveFileExW(helper.c_str(), nullptr, MOVEFILE_DELAY_UNTIL_REBOOT);
    return 0;
}

int launch_runtime(int count, wchar_t** values, const fs::path& executable) {
    fs::path root = executable.parent_path();
    recover_runtime(root);
    fs::path application = root / L"runtime" / L"ASIOsu.exe";
    if (!fs::is_regular_file(application)) throw std::runtime_error("The runtime folder is missing beside ASIOsu.exe.");
    std::vector<std::wstring> arguments;
    for (int index = 1; index < count; ++index) arguments.emplace_back(values[index]);
    launch(application, arguments, application.parent_path());
    return 0;
}

void show_error(const std::string& message) {
    int required = MultiByteToWideChar(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), nullptr, 0);
    std::wstring wide(static_cast<size_t>(required), L'\0');
    if (required > 0) MultiByteToWideChar(CP_UTF8, 0, message.c_str(), static_cast<int>(message.size()), wide.data(), required);
    MessageBoxW(nullptr, wide.c_str(), L"ASIOsu", MB_OK | MB_ICONERROR | MB_SETFOREGROUND);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    try {
        int count = 0;
        wchar_t** values = CommandLineToArgvW(GetCommandLineW(), &count);
        if (values == nullptr) throw std::runtime_error("Cannot parse the command line.");
        fs::path executable = current_executable();
        int result = 0;
        try {
            result = has_argument(count, values, L"--apply-update")
                ? apply_update(count, values, executable)
                : launch_runtime(count, values, executable);
        } catch (...) { LocalFree(values); throw; }
        LocalFree(values);
        return result;
    } catch (const std::exception& exception) {
        show_error(exception.what());
        return 1;
    }
}

