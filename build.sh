#!/bin/sh
# Builds VoxelCoop.dll (BepInEx 6 IL2CPP plugin) and installs it into the game.
set -e
cd "$(dirname "$0")"
G="${GAME_DIR:-/c/Program Files (x86)/Steam/steamapps/common/VoxelPlayground}"
# Roslyn csc.dll from any recent .NET SDK; the game must have been started once with BepInEx (for BepInEx/interop).
CSC="${CSC:-../VoxelPunch/tools/roslyn/tasks/netcore/bincore/csc.dll}"
export DOTNET_ROLL_FORWARD=Major
mkdir -p out
RSP=out/refs.rsp
: > "$RSP"
for f in "$G"/dotnet/System*.dll "$G"/dotnet/mscorlib.dll "$G"/dotnet/netstandard.dll "$G"/dotnet/Microsoft.Win32.Primitives.dll "$G"/BepInEx/core/*.dll "$G"/BepInEx/interop/*.dll; do
  case "$(basename "$f")" in
    dobby.dll|Gee.External.Capstone.dll|System.IO.Compression.Native.dll|System.Private.CoreLib.dll) ;;
    *) echo "-r:\"$(cygpath -w "$f")\"" >> "$RSP" ;;
  esac
done
echo "-r:\"$(cygpath -w "$G/dotnet/System.Private.CoreLib.dll")\"" >> "$RSP"
dotnet "$CSC" -nologo -noconfig -nostdlib -target:library -langversion:latest -nullable:disable -unsafe -optimize \
  -nowarn:CS1701,CS1702,CS0618 -out:out/VoxelCoop.dll @"$RSP" src/*.cs ${EXTRA_SRC}
mkdir -p "$G/BepInEx/plugins/VoxelCoop"
cp out/VoxelCoop.dll "$G/BepInEx/plugins/VoxelCoop/"
echo "installed $(ls -la out/VoxelCoop.dll | awk '{print $5}') bytes"
