#!/usr/bin/env bash
# Rebuilds the bulk export/import verification harness and runs it.
#
# It checks the dialogs for real (they are shown, clicked and their choices read back), the format
# and classification rules, the skeleton auto-detection rules against the real FIFA 17 xml dump, the
# xml reader against that dump, the readability of both dialogs as measured from their visual tree,
# and that the built artifacts still carry the bulk export/import entry points.
#
# The editor is usually running while this is used, holding its own copies of the plugin DLLs, so
# assemblies are taken from each project's output folder rather than from the editor's working set,
# and a post-build copy that cannot overwrite a loaded file is reported and tolerated.
#
# Usage:  bash .freebuff/verify/run.sh
set -euo pipefail

cd "$(dirname "$0")/../.."

MSB="/c/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe"
CSC="/c/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/Roslyn/csc.exe"
REFS="/c/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8"
SOL='C:\Users\andre\Downloads\Frosty-Toolsuite-FIFA-17-edition\FrostyEditor\'
OUT="FrostyEditor/bin/Release/Final"
VERIFY="$(pwd)/.freebuff/verify"

# Builds one project and accepts a failure that happened *after* its output was written: the editor
# keeps MeshSetPlugin.dll and friends loaded, so the project's own post-build copy into
# FrostyEditor/bin/Final/Plugins cannot overwrite them. The build itself has already succeeded.
build_project () {
  local proj="$1" out="$2"; shift 2
  local log status=0
  log="$(mktemp)"

  "$MSB" "$proj" -t:Build -p:Configuration="Release - Final" -p:Platform=x64 "$@" -v:m -nologo -m > "$log" 2>&1 || status=$?
  cat "$log"

  # A compiler error is never tolerated. The tolerance below exists for the post-build copy that
  # cannot overwrite a DLL a running editor holds - but the output file from the *previous* build is
  # still on disk, so without this check a broken build reads as a successful one.
  if grep -qE "error (CS|MSB)[0-9]+" "$log"; then
    rm -f "$log"
    echo "FATAL: $proj did not compile (see the errors above)."
    return 1
  fi

  if [ $status -eq 0 ]; then
    rm -f "$log"
    return 0
  fi

  if [ -f "$out" ]; then
    rm -f "$log"
    echo "NOTE: $proj reported a failure after writing its output ($out); a running editor holds the"
    echo "      copy the post-build step could not overwrite. The build output is current."
    return 0
  fi

  rm -f "$log"
  echo "FATAL: $proj failed and produced no output."
  return 1
}

copy_if_possible () {
  local from="$1"
  if cp "$from" "$VERIFY"/ 2>/dev/null; then
    return 0
  fi

  echo "NOTE: could not refresh $(basename "$from") (in use); the harness will use the existing copy."
}

echo "--- build"
build_project FrostySdk/FrostySdk.csproj "FrostySdk/bin/Release/Final/FrostySdk.dll"
build_project FrostyPlugin/FrostyCore.csproj "FrostyPlugin/bin/x64/Release - Final/FrostyCore.dll"
for P in Plugins/MeshSetPlugin/MeshSetPlugin.csproj Plugins/TexturePlugin/TexturePlugin.csproj Plugins/ChunkResExplorerPlugin/ChunkResEditorPlugin.csproj; do
  DIR="$(dirname "$P")"
  build_project "$P" "$DIR/bin/Release/Final/$(basename "$P" .csproj).dll" -p:SolutionDir="$SOL"
done
build_project FrostyEditor/FrostyEditor.csproj "FrostyEditor/bin/Release/Final/FrostyEditor.exe" -p:SolutionDir="$SOL"

echo "--- collect the freshly built assemblies next to the harness"
copy_if_possible "FrostySdk/bin/Release/Final/FrostySdk.dll"
copy_if_possible "FrostyPlugin/bin/x64/Release - Final/FrostyCore.dll"
copy_if_possible "Plugins/MeshSetPlugin/bin/Release/Final/MeshSetPlugin.dll"
copy_if_possible "Plugins/TexturePlugin/bin/Release/Final/TexturePlugin.dll"
copy_if_possible "Plugins/ChunkResExplorerPlugin/bin/Release/Final/ChunkResEditorPlugin.dll"
copy_if_possible "$OUT/FrostyEditor.exe"
copy_if_possible "$OUT/FrostyControls.dll"
copy_if_possible "$OUT/FrostyHash.dll"
copy_if_possible "$OUT/FrostyModSupport.dll"
copy_if_possible "$OUT/Newtonsoft.Json.dll"

echo "--- compile the harness"
"$CSC" -nologo -target:exe -platform:x64 -langversion:7.3 -out:"$VERIFY/BulkUiCheck.exe" \
  -r:"$REFS/mscorlib.dll" -r:"$REFS/System.dll" -r:"$REFS/System.Core.dll" -r:"$REFS/WindowsBase.dll" \
  -r:"$REFS/PresentationCore.dll" -r:"$REFS/PresentationFramework.dll" -r:"$REFS/System.Xaml.dll" \
  -r:"$VERIFY/FrostyCore.dll" -r:"$VERIFY/FrostyControls.dll" -r:"$VERIFY/FrostySdk.dll" \
  ".freebuff/verify/BulkUiCheck.cs"

echo "--- run"
cd "$VERIFY"
./BulkUiCheck.exe
