#!/bin/sh
# Usage: build/brand/make-mac-icon.sh   (on a Mac)
# Builds src/Bibliotaph.Mac/Assets/Bibliotaph.icns from the app's monogram (src/Bibliotaph.App/Assets/Bibliotaph.svg),
# drawn at each size the Dock and Finder use: its "b." on the monogram's green, on the rounded square of Apple's
# 1024-point icon grid. An icon of another shape is put on a grey tile by macOS, which reads as foreign in the Dock.
set -e
root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
svg="$root/src/Bibliotaph.App/Assets/Bibliotaph.svg"
tile=$(sed -n 's/.*<path fill="\([^"]*\)".*/\1/p' "$svg" | head -1)
glyph=$(grep '<path' "$svg" | sed -n 2p)
cat > "$work/icon.svg" <<SVG
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
  <rect x="100" y="100" width="824" height="824" rx="185" fill="$tile"/>
  <g transform="translate(512 512) scale(19) translate(-18 -18)">$glyph</g>
</svg>
SVG
cat > "$work/render.swift" <<'SWIFT'
import AppKit
let args = CommandLine.arguments
let image = NSImage(contentsOfFile: args[1])!
let size = Int(args[3])!
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
    hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
image.draw(in: NSRect(x: 0, y: 0, width: size, height: size))
NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: args[2]))
SWIFT
mkdir "$work/Bibliotaph.iconset"
for size in 16 32 128 256 512; do
  swift "$work/render.swift" "$work/icon.svg" "$work/Bibliotaph.iconset/icon_${size}x${size}.png" $size
  swift "$work/render.swift" "$work/icon.svg" "$work/Bibliotaph.iconset/icon_${size}x${size}@2x.png" $((size * 2))
done
iconutil -c icns -o "$root/src/Bibliotaph.Mac/Assets/Bibliotaph.icns" "$work/Bibliotaph.iconset"
