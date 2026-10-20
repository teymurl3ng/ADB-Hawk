#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="1.0.0"
ARCH="amd64"
PKG_NAME="adb-hawk"
PKG_DIR="$ROOT_DIR/build/pkg"
DEB_FILE="$ROOT_DIR/build/${PKG_NAME}_${VERSION}_${ARCH}.deb"

echo "==> Building ${PKG_NAME} binary..."
mkdir -p "$ROOT_DIR/build"
g++ -std=c++20 -O2 -g -Wall -Wextra -Wpedantic -Wconversion -Wshadow -Werror \
  -D_GLIBCXX_ASSERTIONS -fstack-protector-strong -D_FORTIFY_SOURCE=3 \
  $(pkg-config --cflags gtk+-3.0 gio-2.0 glib-2.0 x11) -pthread -I"$ROOT_DIR/src" \
  "$ROOT_DIR/src/core/vocabulary.cpp" "$ROOT_DIR/src/core/adb_wire.cpp" "$ROOT_DIR/src/core/protobuf_lite.cpp" \
  "$ROOT_DIR/src/core/mdns_wire.cpp" "$ROOT_DIR/src/core/mdns_cache.cpp" "$ROOT_DIR/src/core/reduce.cpp" \
  "$ROOT_DIR/src/core/notify_policy.cpp" "$ROOT_DIR/src/core/qr.cpp" "$ROOT_DIR/src/core/motion.cpp" \
  "$ROOT_DIR/src/core/tray_pixmap.cpp" "$ROOT_DIR/src/core/shell_placement.cpp" \
  "$ROOT_DIR/src/platform/adb_locator.cpp" "$ROOT_DIR/src/platform/repair_restart_adb.cpp" \
  "$ROOT_DIR/src/platform/adb_io.cpp" "$ROOT_DIR/src/platform/usb_sentinel.cpp" \
  "$ROOT_DIR/src/platform/diagnostics.cpp" "$ROOT_DIR/src/platform/autostart.cpp" \
  "$ROOT_DIR/src/platform/mdns_browser.cpp" "$ROOT_DIR/src/platform/dbus/session_dbus.cpp" \
  "$ROOT_DIR/src/shell/gtk_shell.cpp" "$ROOT_DIR/src/app/main.cpp" \
  -o "$ROOT_DIR/build/adb-hawk" \
  $(pkg-config --libs gtk+-3.0 gio-2.0 glib-2.0 x11)

echo "==> Preparing Debian package tree at $PKG_DIR..."
rm -rf "$PKG_DIR"
mkdir -p "$PKG_DIR/DEBIAN"
mkdir -p "$PKG_DIR/usr/bin"
mkdir -p "$PKG_DIR/usr/share/applications"
mkdir -p "$PKG_DIR/usr/share/icons/hicolor/scalable/apps"
mkdir -p "$PKG_DIR/etc/xdg/autostart"

cat << EOF > "$PKG_DIR/DEBIAN/control"
Package: ${PKG_NAME}
Version: ${VERSION}
Section: devel
Priority: optional
Architecture: ${ARCH}
Depends: libgtk-3-0 (>= 3.24.0), libglib2.0-0 (>= 2.64.0), libx11-6
Maintainer: Teymur Gasimov <teymur.qasimovfb@gmail.com>
Description: Observe, explain, notify, and pair ADB connections
 ADB Hawk is a fast, lightweight system-tray utility for Linux that monitors
 ADB connections, explains connection states, notifies on drops and
 authorizations, and pairs wireless debugging devices.
EOF

install -m 755 "$ROOT_DIR/build/adb-hawk" "$PKG_DIR/usr/bin/adb-hawk"
install -m 644 "$ROOT_DIR/packaging/adb-hawk.desktop" "$PKG_DIR/usr/share/applications/adb-hawk.desktop"
install -m 644 "$ROOT_DIR/packaging/adb-hawk.desktop" "$PKG_DIR/etc/xdg/autostart/adb-hawk.desktop"
install -m 644 "$ROOT_DIR/packaging/icons/adb-hawk.svg" "$PKG_DIR/usr/share/icons/hicolor/scalable/apps/adb-hawk.svg"

echo "==> Packaging .deb archive..."
dpkg-deb --build "$PKG_DIR" "$DEB_FILE"

echo "==> Successfully created $DEB_FILE"
dpkg-deb -I "$DEB_FILE"
