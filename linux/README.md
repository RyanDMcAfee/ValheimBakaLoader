# BakaLoader on Linux

The Linux build is a headless service: the same page as the Windows app, served by the
service itself and opened in any browser, with the game logic behind a WebSocket instead of
a window. docs/linux-port.md is the plan; this folder is the part a host touches.

## Install

1. Unpack the linux-x64 build somewhere, for example `~/bakaloader-1.2.17`.
2. `sudo ~/bakaloader-1.2.17/linux/install.sh ~/bakaloader-1.2.17`

The script makes a `valheim` user, puts the binaries under `/opt/bakaloader/<stamp>` with
`/opt/bakaloader/current` pointing at the newest, keeps the data (service.json, the atlas,
language packs, TLS material) under `/var/lib/bakaloader`, installs `bakaloader.service`
and starts it. Run it again on a newer build to upgrade; the data folder is left alone.

## First login

By default the service listens on `127.0.0.1:8471` only. From your own machine:

    ssh -L 8471:127.0.0.1:8471 you@the-box

and open `http://127.0.0.1:8471/`. No password exists yet, so the login page asks for the
one-time setup token the service printed to the journal on start:

    sudo journalctl -u bakaloader -n 50 | grep "setup token"

Paste it, choose a password, and you are in. The token is good for exactly one login.
To set or change the password from the shell instead:

    sudo -u valheim BAKALOADER_DATA_DIR=/var/lib/bakaloader /opt/bakaloader/current/ValheimBakaLoader.Service passwd

or put it in `BAKALOADER_PASSWORD` for a script. A changed password ends every open session.

## Reaching it from outside

A login is full control of the `valheim` account, so the service refuses to listen beyond
loopback without TLS. The choices, in order of preference:

- Keep the loopback bind and use the SSH tunnel above. Nothing to certify.
- Put Caddy or nginx with a real certificate in front of the loopback bind.
- Bind outward yourself: `--bind 0.0.0.0:8471` (or `BAKALOADER_BIND`). With `--cert FILE
  --key FILE` the service serves your certificate; with neither it makes a self-signed one
  into `/var/lib/bakaloader/tls` on first start and logs its SHA256 fingerprint, which you
  compare with the browser's warning before accepting it.

`--no-tls` beyond loopback is refused on purpose.

## Day to day

    sudo systemctl status bakaloader
    sudo journalctl -u bakaloader -f
    sudo systemctl stop bakaloader      # SIGTERM: every server is stopped gently and the worlds saved first

The unit gives the stop 180 seconds (`TimeoutStopSec`) and the service hands the game side
a little less, so a large world save finishes before anything is killed.

Command line:

    ValheimBakaLoader.Service [run|passwd|version] [--bind HOST:PORT] [--data-dir DIR]
                              [--cert FILE --key FILE] [--no-tls] [--lang CODE] [--version]

## Build

The service and its tests are `net10.0` and live in `ValheimBakaLoader.Linux.sln` beside the
shared core and tools. The Windows solution stays on the .NET 6 SDK its build and its CI use,
so the two never have to agree on an SDK. With the .NET 10 SDK installed:

    dotnet test ValheimBakaLoader.Linux.sln
    dotnet publish ValheimBakaLoader.Service -c Release -r linux-x64 --self-contained false -o out/linux-x64

The publish folder is what `install.sh` takes. The box needs the ASP.NET Core 10 runtime
(`aspnetcore-runtime-10.0` on Debian and Ubuntu). The page is served out of the `WebUI`
folder beside the binary, or wherever `--webui DIR` points.
