#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_path="${CANX_DOTNET:-$repository_root/.cache/dotnet/dotnet}"
publish_dir="$repository_root/artifacts/publish/osx-arm64"
app="$repository_root/artifacts/macos/CanX.app"
symbols_dir="$repository_root/artifacts/symbols/osx-arm64"
mpv_frameworks="${CANX_MACOS_MPV_FRAMEWORKS:-/Applications/IINA.app/Contents/Frameworks}"
placebo="${CANX_MACOS_LIBPLACEBO:-$repository_root/.cache/macos/libplacebo-349-install/lib/libplacebo.349.dylib}"
quickjs="$repository_root/.cache/native/quickjs/macos-build/libcanx_quickjs.dylib"
aria2="$repository_root/.cache/mac-native/aria2-shim/libcanx_aria2.dylib"
homebrew_root="${CANX_MACOS_HOMEBREW_ROOT:-/opt/homebrew/opt}"

if [[ "${CANX_MACOS_SKIP_PUBLISH:-false}" != true ]]; then
  "$dotnet_path" publish "$repository_root/src/CanX.App/CanX.App.csproj" \
    --configuration Release --runtime osx-arm64 --self-contained \
    --property PublishAot=false --property CanXIncludeSmokeTests=false \
    --property CanXProductionPublish=true \
    --output "$publish_dir"
fi
if [[ ! -f "$placebo" ]]; then
  "$repository_root/scripts/build-macos-libplacebo.sh"
fi
for required in "$publish_dir/CanX.App" "$quickjs" "$aria2" "$mpv_frameworks/libmpv.2.dylib" "$placebo"; do
  [[ -f "$required" ]] || { echo "missing macOS bundle input: $required" >&2; exit 1; }
done

mkdir -p "$symbols_dir"
if [[ "${CANX_MACOS_SKIP_PUBLISH:-false}" != true ]]; then
  find "$symbols_dir" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete
fi
find "$publish_dir" -maxdepth 1 -type f \( -name '*.pdb' -o -name '*.dbg' \) \
  -exec mv -f {} "$symbols_dir/" \;
[[ -f "$symbols_dir/CanX.App.pdb" ]] || {
  echo "missing macOS application symbols: $symbols_dir/CanX.App.pdb" >&2
  exit 1
}

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Frameworks" "$app/Contents/Resources"
cp -R "$publish_dir"/. "$app/Contents/MacOS/"
find "$app/Contents/MacOS" -type f \( -name '*.pdb' -o -name '*.dbg' \) -delete
cp "$quickjs" "$aria2" "$app/Contents/MacOS/"
# Single-file publish can omit these package-provided native assets after an
# incremental build, even though they remain in the RID-specific build output.
native_runtime_dir="$repository_root/src/CanX.App/bin/Release/net10.0/osx-arm64"
for native_asset in libAvaloniaNative.dylib libHarfBuzzSharp.dylib libSkiaSharp.dylib; do
  [[ -f "$native_runtime_dir/$native_asset" ]] || {
    echo "missing macOS runtime asset: $native_runtime_dir/$native_asset" >&2
    exit 1
  }
  cp "$native_runtime_dir/$native_asset" "$app/Contents/MacOS/"
done
find "$mpv_frameworks" -maxdepth 1 -type f -name '*.dylib' \
  -exec cp -p {} "$app/Contents/Frameworks/" \;
rm -f "$app/Contents/Frameworks/libplacebo.338.dylib"
cp "$placebo" "$app/Contents/Frameworks/libplacebo.349.dylib"
install_name_tool -id @rpath/libplacebo.349.dylib \
  "$app/Contents/Frameworks/libplacebo.349.dylib"
placebo_lcms="$(otool -L "$app/Contents/Frameworks/libplacebo.349.dylib" | \
  awk '/\/liblcms2\.2\.dylib/ { print $1; exit }')"
if [[ -n "$placebo_lcms" ]]; then
  install_name_tool -change "$placebo_lcms" @loader_path/liblcms2.2.dylib \
    "$app/Contents/Frameworks/libplacebo.349.dylib"
fi
ln -s ../Frameworks/libmpv.2.dylib "$app/Contents/MacOS/libmpv.dylib"
install_name_tool -add_rpath @loader_path "$app/Contents/Frameworks/libmpv.2.dylib"
install_name_tool -change @rpath/libplacebo.338.dylib \
  @rpath/libplacebo.349.dylib "$app/Contents/Frameworks/libmpv.2.dylib"

cp "$homebrew_root/sqlite/lib/libsqlite3.dylib" \
  "$app/Contents/Frameworks/libsqlite3.dylib"
cp "$homebrew_root/libssh2/lib/libssh2.1.dylib" \
  "$app/Contents/Frameworks/libssh2.1.dylib"
cp "$homebrew_root/c-ares/lib/libcares.2.dylib" \
  "$app/Contents/Frameworks/libcares.2.dylib"
cp "$homebrew_root/openssl@3/lib/libssl.3.dylib" \
  "$app/Contents/Frameworks/libssl.3.dylib"
cp "$homebrew_root/openssl@3/lib/libcrypto.3.dylib" \
  "$app/Contents/Frameworks/libcrypto.3.dylib"
install_name_tool \
  -change "$homebrew_root/sqlite/lib/libsqlite3.dylib" \
    @loader_path/../Frameworks/libsqlite3.dylib \
  -change "$homebrew_root/libssh2/lib/libssh2.1.dylib" \
    @loader_path/../Frameworks/libssh2.1.dylib \
  -change "$homebrew_root/c-ares/lib/libcares.2.dylib" \
    @loader_path/../Frameworks/libcares.2.dylib \
  "$app/Contents/MacOS/libcanx_aria2.dylib"
install_name_tool \
  -change "$homebrew_root/openssl@3/lib/libssl.3.dylib" \
    @loader_path/libssl.3.dylib \
  -change "$homebrew_root/openssl@3/lib/libcrypto.3.dylib" \
    @loader_path/libcrypto.3.dylib \
  "$app/Contents/Frameworks/libssh2.1.dylib"
install_name_tool \
  -change "$(otool -L "$app/Contents/Frameworks/libssl.3.dylib" | \
    awk '/\/libcrypto\.3\.dylib/ { print $1; exit }')" \
    @loader_path/libcrypto.3.dylib \
  "$app/Contents/Frameworks/libssl.3.dylib"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>CanX.App</string>
  <key>CFBundleIdentifier</key><string>org.canx.app</string>
  <key>CFBundleName</key><string>CanX</string>
  <key>CFBundleDisplayName</key><string>CanX</string>
  <key>CFBundleShortVersionString</key><string>0.1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleIconFile</key><string>CanX.icns</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.video</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
PLIST

# Keep these lists aligned with CanXFileFormats.cs, which routes Finder files
# to the player or the download task dialog.
video_formats=(3gp asf avi flv m2ts m4v mkv mov mp4 mpeg mpg mts ogm ogv ts webm wmv)
audio_formats=(aac ac3 aiff ape flac m4a mka mp2 mp3 oga ogg opus wav wma)
playlist_formats=(m3u m3u8)
download_formats=(torrent meta4 metalink)

append_document_type() {
  local name="$1"
  shift
  cat >> "$app/Contents/Info.plist" <<PLIST
    <dict>
      <key>CFBundleTypeName</key><string>$name</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>CFBundleTypeExtensions</key><array>
PLIST
  for extension in "$@"; do
    printf '        <string>%s</string>\n' "$extension" >> "$app/Contents/Info.plist"
  done
  cat >> "$app/Contents/Info.plist" <<'PLIST'
      </array>
    </dict>
PLIST
}

cat >> "$app/Contents/Info.plist" <<'PLIST'
  <key>CFBundleDocumentTypes</key><array>
PLIST
append_document_type "CanX video" "${video_formats[@]}"
append_document_type "CanX audio" "${audio_formats[@]}"
append_document_type "CanX playlist" "${playlist_formats[@]}"
append_document_type "CanX download metadata" "${download_formats[@]}"
cat >> "$app/Contents/Info.plist" <<'PLIST'
  </array>
  <key>UTImportedTypeDeclarations</key><array>
PLIST
for extension in "${video_formats[@]}" "${audio_formats[@]}" \
  "${playlist_formats[@]}" "${download_formats[@]}"; do
  cat >> "$app/Contents/Info.plist" <<PLIST
    <dict>
      <key>UTTypeIdentifier</key><string>org.canx.format.$extension</string>
      <key>UTTypeConformsTo</key><array><string>public.data</string></array>
      <key>UTTypeTagSpecification</key><dict>
        <key>public.filename-extension</key><string>$extension</string>
      </dict>
    </dict>
PLIST
done
cat >> "$app/Contents/Info.plist" <<'PLIST'
  </array>
</dict></plist>
PLIST

iconset="$repository_root/.cache/macos/CanX.iconset"
mkdir -p "$iconset"
logo="$repository_root/src/CanX.App/Assets/CanXLogo.png"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$logo" \
    --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
for size in 16 32 128 256 512; do
  doubled=$((size * 2))
  sips -s format png -z "$doubled" "$doubled" "$logo" \
    --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/CanX.icns"

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"
plutil -lint "$app/Contents/Info.plist"
printf 'CANX_MACOS_APP_OK app=%s symbols=%s\n' "$app" "$symbols_dir"
