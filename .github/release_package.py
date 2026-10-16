"""Assemble the Windows playtest ZIP with notices and verified contents."""

from pathlib import Path
import hashlib
import json
import os
import re
import shutil
import zipfile


SOURCE_COMMIT = "f0d24179ae00fe9e3fdf3e25c85c2cae98fc9b40"
FOLDER = "ChessVsZombies-Playtest-2026-10-16-win-x64"
root = Path.cwd()
published = root / "publish"
previous = root / "prior/extracted/ChessVsZombies-Playtest-2026-10-14-win-x64"
output = root / "out"
package = output / FOLDER
archive = output / f"{FOLDER}.zip"

deps = json.loads((published / "ChessVsZombies.deps.json").read_text())
runtime = next(
    re.fullmatch(r"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.win-x64/(\d+\.\d+\.\d+)", key).group(1)
    for key in deps["libraries"]
    if re.fullmatch(r"runtimepack\.Microsoft\.NETCore\.App\.Runtime\.win-x64/(\d+\.\d+\.\d+)", key)
)
notices = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages"))) / "microsoft.netcore.app.runtime.win-x64" / runtime
assert (published / "ChessVsZombies.exe").is_file()
assert (published / "Levels/001-first-contact.json").is_file()
assert previous.is_dir()
assert (notices / "LICENSE.TXT").is_file()

output.mkdir(exist_ok=True)
shutil.copytree(published, package)
for directory in ("Licenses", "ThirdPartySources"):
    shutil.copytree(previous / directory, package / directory)
shutil.copy2(notices / "LICENSE.TXT", package / "Licenses/DotNet.txt")
shutil.copy2(notices / "THIRD-PARTY-NOTICES.TXT", package / "Licenses/DotNet-ThirdParty.txt")
readme = (package / "Licenses/README.txt").read_text().replace("runtime 8.0.31", f"runtime {runtime}")
(package / "Licenses/README.txt").write_text(readme)

start = (previous / "START-HERE.txt").read_text().replace("2026-10-14", "2026-10-16")
start = start.replace("SAVING\n", """UPGRADES AND JUMP FORMATIONS
Six component types have separate point balances. Pulls add one matching point.
In the upgrade template, choose point counts and apply to a tower with middle-click.
Spend now applies an affordable batch once. Queue full waits for the entire batch,
then gives you three unpaused game seconds to cancel before spending all its points.
J opens the 3D jump editor. Left-click the height plane to add a destination;
click a destination to remove it. Middle-drag orbits, the wheel zooms, and the Y
buttons or Page Up/Page Down change height. Section shows one height at a time.
Use in template saves the pattern; middle-click a turret to apply it. The farthest
destination radius determines the total Jump point requirement. Allocated Jump
points count toward it; expanding charges the shortfall and shrinking gives no
refund. Collection caps still limit each turret's allocations.

SAVING
""")
(package / "START-HERE.txt").write_text(start)
(package / "BUILD-INFO.txt").write_text(f"""Chess vs Zombies - Playtest 2026-10-16
Target: Windows x64, self-contained .NET {runtime}
MonoGame DesktopGL: 3.8.5.1
Configuration: Release; no trimming; development symbols omitted
Source commit: {SOURCE_COMMIT}
Includes typed turret batch upgrades and the 3D jump formation editor.
Verification: 1330 tests passed; full solution build had 0 warnings.
Linux software OpenGL editor launch and interaction checks passed.
Windows executable has not been launched in this environment.

Published with:
dotnet publish ChessVsZombies/ChessVsZombies.csproj --configuration Release --runtime win-x64 --self-contained true --output <package-folder> -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
""")

files = sorted(path for path in package.rglob("*") if path.is_file())
(package / "FILES.sha256").write_text("".join(
    f"{hashlib.sha256(path.read_bytes()).hexdigest().upper()}  {path.relative_to(package).as_posix()}\n"
    for path in files
))
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9, allowZip64=True) as zip_file:
    for path in sorted(path for path in package.rglob("*") if path.is_file()):
        zip_file.write(path, f"{FOLDER}/{path.relative_to(package).as_posix()}")
with zipfile.ZipFile(archive) as zip_file:
    assert zip_file.testzip() is None
print(f"Prepared {archive} ({archive.stat().st_size} bytes, SHA-256 {hashlib.sha256(archive.read_bytes()).hexdigest()})")
