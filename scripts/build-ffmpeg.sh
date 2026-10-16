#!/usr/bin/env bash
# Run in MSYS2 MINGW64. Sources and build settings accompany the binaries.
set -euo pipefail
project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd "$project_root"
case "$(uname -s)" in MINGW*|MSYS*) ;; *) echo 'Use MSYS2 MINGW64 on Windows.' >&2; exit 1 ;; esac

ffmpeg_revision=38b88335f9
x264_revision=31e19f92f00c7003fa115047ce50978bc98c3a0d
dav1d_version=1.5.4
ffmpeg_sha=e3b61890625162aa7d1bc554663c5b604595baf78bce83a4425646e57d4d6d3f
x264_sha=d053c9d86988d6bc78237ca5205865c5ddf99c98ef4cd9927eec8f6d388f6dd9
dav1d_sha=686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5
build_root="$project_root/artifacts/ffmpeg-build"
native_root="$project_root/tools/windows"
source_root="$build_root/corresponding-source"
prefix_root="$build_root/prefix"
# Rebuild the source bundle from selected files; never retain old configure logs.
rm -rf -- "$source_root"
mkdir -p artifacts/downloads "$native_root/licenses/ffmpeg" "$source_root" "$prefix_root"

download() {
    local url=$1 target=$2 checksum=$3
    if ! test -f "$target" || ! printf '%s  %s\n' "$checksum" "$target" | sha256sum -c - >/dev/null 2>&1; then
        curl --fail --location --retry 3 --connect-timeout 20 --max-time 600 --output "$target.part" "$url"
        printf '%s  %s\n' "$checksum" "$target.part" | sha256sum -c -
        mv "$target.part" "$target"
    fi
}
ffmpeg_archive="artifacts/downloads/ffmpeg-$ffmpeg_revision.tar.gz"
x264_archive="artifacts/downloads/x264-$x264_revision.tar.gz"
dav1d_archive="artifacts/downloads/dav1d-$dav1d_version.tar.xz"
download "https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/$ffmpeg_revision" "$ffmpeg_archive" "$ffmpeg_sha"
download "https://codeload.github.com/mirror/x264/tar.gz/$x264_revision" "$x264_archive" "$x264_sha"
download "https://download.videolan.org/pub/videolan/dav1d/$dav1d_version/dav1d-$dav1d_version.tar.xz" "$dav1d_archive" "$dav1d_sha"
tar -xzf "$x264_archive" -C "$build_root"
tar -xzf "$ffmpeg_archive" -C "$build_root"
tar -xJf "$dav1d_archive" -C "$build_root"

cd "$build_root/dav1d-$dav1d_version"
meson setup build --prefix="$prefix_root" --libdir=lib --default-library=static \
    --buildtype=release --wrap-mode=nodownload -Denable_tools=false \
    -Denable_tests=false -Denable_examples=false -Denable_docs=false
meson compile -C build -j "${NUMBER_OF_PROCESSORS:-2}"
meson install -C build
cp COPYING "$native_root/licenses/ffmpeg/dav1d-COPYING"
cp COPYING "$source_root/dav1d-COPYING"
cp build/config.h "$source_root/dav1d-config.h"
cp build/meson-info/intro-buildoptions.json "$source_root/dav1d-buildoptions.json"

cd "$build_root/x264-$x264_revision"
./configure --prefix="$prefix_root" --host=x86_64-w64-mingw32 --enable-static --disable-cli --disable-opencl
make -j"${NUMBER_OF_PROCESSORS:-2}"
make install
cp config.mak "$source_root/x264-config.mak"
cp COPYING "$source_root/x264-COPYING"

cd "$build_root/FFmpeg-$ffmpeg_revision"
export PKG_CONFIG_PATH="$prefix_root/lib/pkgconfig"
./configure --prefix="$prefix_root" --arch=x86_64 --target-os=mingw32 \
    --enable-gpl --enable-version3 --enable-libx264 --enable-libdav1d --enable-static --disable-shared \
    --disable-autodetect --disable-network --disable-debug --disable-doc --disable-ffplay \
    --pkg-config=pkgconf --pkg-config-flags=--static \
    --extra-cflags="-I$prefix_root/include" --extra-ldflags="-L$prefix_root/lib -static -static-libgcc"
make -j"${NUMBER_OF_PROCESSORS:-2}"
cp ffmpeg.exe ffprobe.exe "$native_root/"
cp COPYING.GPLv3 "$native_root/licenses/ffmpeg/LICENSE"
cp COPYING.GPLv3 "$source_root/ffmpeg-COPYING.GPLv3"
cp ffbuild/config.mak config.h "$source_root/"
./ffmpeg.exe -version > "$source_root/ffmpeg-version.txt"
./ffmpeg.exe -buildconf > "$source_root/ffmpeg-buildconf.txt" 2>&1
for executable in ffmpeg.exe ffprobe.exe; do
    objdump -p "$executable" | sed -n 's/.*DLL Name: //p' > "$source_root/$executable-dlls.txt"
    if grep -Ei 'libwinpthread|libgcc|libstdc\+\+' "$source_root/$executable-dlls.txt"; then
        echo "Unexpected non-system runtime DLL: $executable" >&2; exit 1
    fi
done
cd "$project_root"
cp "$ffmpeg_archive" "$x264_archive" "$dav1d_archive" "$source_root/"
cp scripts/build-ffmpeg.sh "$source_root/"
pacman -Q > "$source_root/msys2-packages.txt"
cat > "$native_root/licenses/ffmpeg/README.txt" <<EOF
GuiaFotos FFmpeg build: FFmpeg $ffmpeg_revision + x264 $x264_revision + dav1d $dav1d_version.
License: GPLv3; dav1d is BSD-2-Clause (see dav1d-COPYING).
External codec libraries: x264 (H.264 encoding), dav1d (AV1 software decoding).
Complete source archives, build script, configuration and compiler package versions:
https://github.com/marcelstfgit/guiafotos/releases/tag/v0.2.0
Asset: GuiaFotos-0.2.0-fontes-dependencias.zip
EOF
cp "$native_root/licenses/ffmpeg/README.txt" "$source_root/README.txt"
python - "$project_root" "$ffmpeg_revision" "$x264_revision" "$dav1d_version" <<'PY'
import hashlib, json, sys, zipfile
from pathlib import Path
root = Path(sys.argv[1])
native = root / 'tools/windows'
source = root / 'artifacts/ffmpeg-build/corresponding-source'
archive = root / 'artifacts/GuiaFotos-0.2.0-fontes-dependencias.zip'
def digest(p): return hashlib.file_digest(p.open('rb'), 'sha256').hexdigest()
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for p in sorted(source.rglob('*')):
        if p.is_file(): z.write(p, p.relative_to(source))
manifest = {
    'FfmpegCommit': sys.argv[2], 'X264Commit': sys.argv[3],
    'Dav1dVersion': sys.argv[4],
    'License': 'GPL-3.0-or-later', 'ExternalCodecs': ['x264', 'dav1d'],
    'Binaries': {name: digest(native / name) for name in ('ffmpeg.exe', 'ffprobe.exe')},
    'SourceArchive': archive.name, 'SourceSha256': digest(archive),
}
(native / 'ffmpeg-build.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('PASS FFmpeg build with complete corresponding sources:', archive.name)
PY
