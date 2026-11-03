# ASIOsu

ASIOsu routes the complete osu!lazer mix to a native ASIO output on Windows. It is an independent implementation built around the public BASSWASAPI and BASSASIO interfaces. It does not patch managed game assemblies or inject code into a running process.

This project is unofficial and is not affiliated with osu! or ppy Pty Ltd.

## How it works

osu!lazer already creates one final floating-point mix callback when its experimental Windows audio mode is enabled. ASIOsu replaces only `basswasapi.dll` with an ABI-compatible bridge. The bridge receives that callback and supplies it to a joined stereo BASSASIO output pair.

`bass.dll` remains the original game file. The selected Windows playback-device name remains unchanged in osu!lazer. When ASIOsu encounters the older two-DLL proxy, installation first restores the verified original BASS file and then installs the new single-DLL bridge.

```text
osu!lazer mix -> original bass.dll -> ASIOsu basswasapi.dll -> BASSASIO -> stereo ASIO outputs
```

## Features

- Routes music, effects, hitsounds, previews, and interface audio through the same ASIO callback.
- Preserves the existing Windows playback-device selection shown by osu!lazer.
- Automatically selects the driver when only one ASIO driver is installed.
- Reads named ASIO output channels and creates stereo pairs.
- Validates the x64 BASSWASAPI export names and ordinals before installation.
- Creates a local restore transaction before changing the game directory.
- Restores the original BASSWASAPI file and the previous `framework.ini` bytes.
- Checks stable GitHub Releases and verifies the release-asset digest and every packaged file before updating.
- Exits when the window closes. There is no tray process or resident service.

## Usage

1. Extract `ASIOsu-win-x64.zip` into a new directory.
2. Close osu!lazer.
3. Run `ASIOsu.exe`.
4. Open **Audio route** and read the driver channels.
5. Choose a stereo output pair, sample rate, and buffer size, then save the route.
6. Return to **Current status**, run the compatibility check, and install the bridge.
7. Start osu!lazer normally.

The app refuses installation, restoration, and installed-route writes while osu!lazer is running. An ASIO driver may also need to be released by other applications before its channels can be inspected.

## Files used in the game directory

| File | Purpose |
|---|---|
| `bass.dll` | Original osu!lazer BASS library; never replaced by the new bridge |
| `basswasapi.dll` | Independent ASIOsu BASSWASAPI-compatible bridge |
| `basswasapi_original.dll` | Verified original used for local forwarding and restoration |
| `bassasio.dll` | Official x64 BASSASIO runtime from Un4seen Developments |
| `asiosu.ini` | Driver, output pair, sample rate, and buffer frames |

Restore transactions are stored under `%LOCALAPPDATA%\ASIOsu\backups`. The updater uses `%LOCALAPPDATA%\ASIOsu\downloads` and a short-lived native update host.

## In-game latency measurement

The measurement was taken on 2026-11-03 KST with osu!lazer `2026.1005.0-lazer+9a10d935b05a3b896f4bd811d620f2023331db93` in osu!mania 4K. Only the `S`, `D`, `L`, and `;` key-down events were accepted. Music volume was set to zero and effect volume to one so that each accepted key could be paired with the first hitsound sample.

Both paths were routed into the same 48,000 Hz stereo digital input and captured through the same endpoint. The capture callback used an 8,448-byte buffer in both runs. Twelve samples were collected per path while the game remained in the foreground.

```text
Original: BASSWASAPI / WDM output 1+2 -> digital input 3+4
ASIOsu:   ASIO output 1+2            -> digital input 3+4
```

| Path | Median | Mean | P95 |
|---|---:|---:|---:|
| Original BASSWASAPI | 149.5518 ms | 149.4803 ms | 157.3078 ms |
| ASIOsu, 48 kHz / 32 frames | 38.1420 ms | 39.3402 ms | 47.4539 ms |

The median fell by **111.4098 ms**, or **74.5%** relative to the original path.

Sorted samples in milliseconds:

```text
Original BASSWASAPI
145.9046, 146.3971, 146.6022, 147.3405, 148.6314, 149.4917,
149.6119, 149.7624, 149.7692, 150.7997, 152.1450, 157.3078

ASIOsu
36.6559, 37.2202, 37.3147, 37.3844, 37.7641, 38.0448,
38.2392, 39.6028, 39.8258, 40.9681, 41.6084, 47.4539
```

Timing starts at the low-level keyboard event and ends at the first matching sample on the digital input. The absolute values include the common capture path and its input-buffer delay. The comparison does not include analog conversion and should not be treated as a universal result for other configurations.

The recorder is a development test under `tests/latency`; it is not included in the portable application or release package.

## Automatic updates

At startup, ASIOsu requests the latest stable release metadata from `api.github.com`. An update is accepted only when the release contains the exact `ASIOsu-win-x64.zip` asset with a GitHub SHA-256 digest. The ZIP is downloaded through GitHub-owned HTTPS hosts, checked against that digest, extracted with path-boundary and expanded-size checks, and checked file-by-file against `manifest.json`.

After ASIOsu exits, a temporary native host stages the new runtime, keeps a rollback copy, switches the application files, and restarts the new version. A failed switch restores the previous runtime. Application updates do not modify the osu!lazer directory or its restore transaction.

The updater sends no game files, audio-device information, settings, or telemetry.

## Build

Requirements:

- Windows x64
- Visual Studio 2022 C++ build tools and a Windows 10/11 SDK
- .NET 8 SDK
- PowerShell 7

Build the native bridge and contract tests:

```powershell
./build-native.ps1 -Configuration Release
```

Build the portable WinUI 3 application:

```powershell
./build-app.ps1 -Configuration Release
```

Create and verify the release ZIP:

```powershell
./package.ps1 -Configuration Release
```

The package root contains one executable. WinUI, .NET, probe, payload, and language resources are kept under `runtime`.

## Tests

```powershell
dotnet run --project tests/core/ASIOsu.CoreTests.csproj -c Release -- "$PWD"
artifacts/native/native_contract.exe --contract <fixture-directory>
```

The core suite uses a temporary game directory and covers compatibility checks, transactional installation, original-file preservation, device-name preservation, route serialization, exact configuration restoration, release parsing, package hashing, and tamper rejection. The native contract test checks all 23 BASSWASAPI export names and ordinals and verifies forwarding to the original library.

## Network and game scope

Only the WinUI updater contacts GitHub. The native audio bridge does not create network connections. ASIOsu does not read gameplay memory, inject a DLL into the running game, or transmit modified-file information.

Replacing a native audio library is still a game-directory modification. Game policy and enforcement decisions are outside this project's control.

## Licensing

ASIOsu source code is licensed under the MIT License. The bundled BASSASIO runtime has separate terms and is free for non-commercial use; review [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `third_party/un4seen/bassasio.txt` before redistribution or commercial use.

