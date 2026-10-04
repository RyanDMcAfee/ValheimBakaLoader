# The frozen launcher

`ValheimBakaLoader.exe` in this folder is the launcher, and it never changes again.

It is not the program. The .NET SDK generates this file for every build: 353,280 bytes
of generic startup code with one thing of its own in it, the name of the assembly to
load, which you can read in its own file properties as the internal name
`ValheimBakaLoader.dll`. Everything BakaLoader actually does lives in that DLL beside it.

## Why its file properties say 1.2.6

These are the exact bytes the SDK generated for 1.2.6, sha256
`365a8dec30b302a0b37851250d2089e646449c6017f7a2e2e59f2fd5a10ffcc4`, and the version
resource inside them still reads `1.2.6`. That is the launcher's own version and it is
the only place it appears. The app's version is the DLL's: it is what the splash
line, the sidebar and the status bar show, what the log says, and what the self-update holds
against the newest release on GitHub.

None of that moved in 1.2.8. `AssemblyHelper.ReadVersion` has always read the informational
version off the assembly it is compiled into, which is the DLL, and the self-update's
comparison has always been handed that. What 1.2.8 adds is a seam that pins it: the installed
side of that comparison is named rather than implied (`AppUpdateService.InstalledVersion`),
the log line says which version it compared, and a test refuses any reading of a version off
the exe anywhere in the code that ships.

So the file properties of the exe on a host's disk will say 1.2.6 while they are running
1.2.9. That is expected, and the wiki says so on the Updating BakaLoader page.

## Why it is frozen

On 2026-10-04 Windows Defender's local machine learning rule looked at a freshly
self-updated 1.2.7 exe on a host's PC, called it `Trojan:Win32/Bearfoos.A!ml`,
quarantined it, and took the app and the Valheim server it was running down with it. The
Run entry and the taskbar pin went too.

Comparing the 1.2.6 and 1.2.7 launchers byte for byte, they differ in fourteen bytes,
all of them inside the version resource: `1.2.6.0` became `1.2.7.0` and
`1.2.6+build...` became `1.2.7+build...`. Nothing else about the file changed. So what
the verdict was really about was a brand new hash of an unsigned launcher, written to
disk by a script, relaunched, with a Run key pointing at it. These 1.2.6 bytes have run
on about fifty machines since 2026-09-30 with no detection anywhere.

Freezing the launcher means every release from 1.2.8 on ships the same exe, and a
self-update from 1.2.8 on compares the two hashes, finds them equal, and does not write
the file at all. There is no new file for a local model to form an opinion about. The
release is not code signed yet; that is a separate track, and this change does not
replace it.

## What the build does with it

`ValheimBakaLoader.csproj` copies this file over the generated one twice, and the first
copy is the one that matters.

`FreezeAppHostIntermediate` runs straight after the SDK's own `_CreateAppHost`, the step
that generates the launcher and writes it into the intermediate folder as
`obj\<configuration>\net6.0-windows\apphost.exe`. Everything downstream takes its copy
from there: the output folder, a `dotnet publish`, and every project that references this
one. Freezing only the output folder is how that was found out. The test project's own
copy of the exe, brought in by the project reference, was still the generated one, and a
release cut with publish rather than build would have shipped a generated launcher too.

`FreezeAppHost` runs after `Build` and copies the file again, over
`$(OutDir)ValheimBakaLoader.exe`. That second copy is for the incremental case, where
nothing regenerates the apphost and the step that would have carried it into the output
folder is skipped as up to date.

Neither target is conditioned on the configuration. Every release of this app has been cut
from a Debug build, so a Release-only freeze would freeze nothing that ships.

The build also checks the three things the frozen launcher depends on:

- the assembly name is `ValheimBakaLoader`, because that name is baked into these bytes
  and a different one would make the launcher look for a DLL that is not there;
- the target framework is `net6.0-windows`, because the launcher and the runtime it
  starts are a matched pair;
- `Resources\ApplicationIcon.ico` still has the sha256 the frozen launcher was generated
  with, which is pinned as `FrozenAppHostIconSha256` in the csproj. The icon is written
  into the apphost's own resources rather than into the DLL, so a new icon file would
  build, pass and ship while every host went on seeing the old one. All seven images in
  that .ico sit inside these bytes today, and `FrozenAppHostTests` holds them there.

Change any of the three and the build stops with an error that points at this file. That
is deliberate: a launcher that silently stops matching its DLL is an app that does not
start, and one that silently stops matching its icon is a release that misrepresents
itself. If one of them genuinely has to change, generate a new apphost from the SDK,
replace this file with it, record its sha256 in `FrozenAppHostTests` and the icon's in the
csproj, and expect the first release carrying it to be a new hash in the world again.
