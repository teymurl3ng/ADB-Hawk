#!/usr/bin/env bash
# adb-hawk-linux verification gate.
#
# Prints PASS / FAIL / SKIP per gate. A gate that is not implemented yet is reported as SKIP,
# never as PASS. --strict turns any SKIP into a failure, so "green" can't hide missing coverage.
#
#   tools/verify.sh              run every gate
#   tools/verify.sh --strict     SKIP counts as failure
#   tools/verify.sh --gate=G1    run only the named gate(s), comma separated
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

STRICT=0
ONLY=""
for a in "$@"; do
  case "$a" in
    --strict) STRICT=1 ;;
    --gate=*) ONLY="${a#--gate=}" ;;
    *) echo "unknown argument: $a" >&2; exit 2 ;;
  esac
done

CXX="${CXX:-g++}"
# Strict flags are fixed here so every target is built the same way.
CXXFLAGS=(-std=c++20 -O1 -g -Wall -Wextra -Wpedantic -Wconversion -Wshadow -Werror
          -D_GLIBCXX_ASSERTIONS -fstack-protector-strong -D_FORTIFY_SOURCE=3)
GIO_CFLAGS=(-I/usr/include/glib-2.0 -I/usr/lib/x86_64-linux-gnu/glib-2.0/include -I/usr/include/gio-unix-2.0)
GIO_LIBS=(-lgio-2.0 -lgobject-2.0 -lglib-2.0)

PASS=0; FAIL=0; SKIP=0
want() { [[ -z "$ONLY" || ",$ONLY," == *",$1,"* ]]; }
pass() { echo "PASS  $1  $2"; PASS=$((PASS+1)); }
fail() { echo "FAIL  $1  $2"; FAIL=$((FAIL+1)); }
skip() { echo "SKIP  $1  $2"; SKIP=$((SKIP+1)); }

mkdir -p build

# G0: the host probes compile warning-free. Running them against a live session is a separate,
# deliberate step (build/probe_g0, build/probe_sni) because they need the real desktop session.
if want G0; then
  ok=1
  for t in probe_g0 probe_sni; do
    if ! "$CXX" "${CXXFLAGS[@]}" "${GIO_CFLAGS[@]}" "tools/$t.cpp" -o "build/$t" "${GIO_LIBS[@]}" 2> "build/$t.err"; then
      ok=0; fail G0 "tools/$t.cpp did not compile (see build/$t.err)"
    fi
  done
  [[ $ok -eq 1 ]] && pass G0 "probes compile with -Werror"
fi

# G1: static rules from the spec. Only meaningful once src/ exists.
if want G1; then
  if [[ -d src ]]; then
    bad=0
    # R-14: nothing in src/ may set ADB_* in the environment.
    if grep -rnE '(setenv|putenv|g_setenv)\s*\(\s*"ADB_' src; then bad=1; fi
    # R-14: kill-server only in the one sanctioned repair file.
    if grep -rnE 'kill-server|host:kill' src --include='*.cpp' --include='*.hpp' \
         | grep -v 'src/platform/repair_restart_adb.cpp'; then bad=1; fi
    # No shell string execution.
    if grep -rnE '\b(system|popen)\s*\(' src; then bad=1; fi
    # R-03: src/core must not include GLib, GTK, X11, or POSIX/Linux OS headers.
    if grep -rnE '#include\s*<(glib|gio|gtk|gdk|X11|sys/|unistd\.h|fcntl\.h|poll\.h|netinet|arpa|linux/)' src/core; then bad=1; fi
    [[ $bad -eq 0 ]] && pass G1 "static rules clean" || fail G1 "static rule violated (see above)"
  else
    skip G1 "no src/ yet"
  fi
fi

CORE_SRCS=(
  src/core/vocabulary.cpp
  src/core/adb_wire.cpp
  src/core/protobuf_lite.cpp
  src/core/mdns_wire.cpp
  src/core/mdns_cache.cpp
  src/core/reduce.cpp
  src/core/notify_policy.cpp
  src/core/qr.cpp
  src/core/motion.cpp
  src/core/tray_pixmap.cpp
  src/core/shell_placement.cpp
)

PLATFORM_ADB_SRCS=(
  src/platform/adb_locator.cpp
  src/platform/repair_restart_adb.cpp
  src/platform/adb_io.cpp
  src/platform/usb_sentinel.cpp
)

PLATFORM_MDNS_SRCS=(
  src/platform/diagnostics.cpp
  src/platform/autostart.cpp
  src/platform/mdns_browser.cpp
)

PLATFORM_DBUS_SRCS=(
  src/platform/dbus/session_dbus.cpp
)

to_objs() {
  local out_dir="$1"; shift
  for s in "$@"; do
    local rel="${s#src/}"
    echo "$out_dir/${rel%.cpp}.o"
  done
}

CORE_OBJS=($(to_objs build/obj "${CORE_SRCS[@]}"))
PLATFORM_ADB_OBJS=($(to_objs build/obj "${PLATFORM_ADB_SRCS[@]}"))
PLATFORM_MDNS_OBJS=($(to_objs build/obj "${PLATFORM_MDNS_SRCS[@]}"))
PLATFORM_DBUS_OBJS=($(to_objs build/obj "${PLATFORM_DBUS_SRCS[@]}"))

CORE_OBJS_SAN=($(to_objs build/obj_san "${CORE_SRCS[@]}"))
PLATFORM_ADB_OBJS_SAN=($(to_objs build/obj_san "${PLATFORM_ADB_SRCS[@]}"))
PLATFORM_MDNS_OBJS_SAN=($(to_objs build/obj_san "${PLATFORM_MDNS_SRCS[@]}"))

compile_parallel() {
  local out_dir="$1"; shift
  local extra_flags=("$@")
  local newest_hpp
  newest_hpp=$(find src -name '*.hpp' -exec stat -c %Y {} + 2>/dev/null | sort -n | tail -1 || echo 0)
  local all_srcs=("${CORE_SRCS[@]}" "${PLATFORM_ADB_SRCS[@]}" "${PLATFORM_MDNS_SRCS[@]}" "${PLATFORM_DBUS_SRCS[@]}")
  local pids=()
  for src in "${all_srcs[@]}"; do
    local rel="${src#src/}"
    local obj="$out_dir/${rel%.cpp}.o"
    mkdir -p "$(dirname "$obj")"
    local need=0
    if [[ ! -f "$obj" ]]; then
      need=1
    elif [[ $(stat -c %Y "$src") -gt $(stat -c %Y "$obj") ]]; then
      need=1
    elif [[ -n "$newest_hpp" && "$newest_hpp" -gt $(stat -c %Y "$obj") ]]; then
      need=1
    fi
    if [[ $need -eq 1 ]]; then
      local local_cflags=()
      if [[ "$src" == *"session_dbus.cpp"* ]]; then
        local_cflags=("${GIO_CFLAGS[@]}")
      fi
      "$CXX" "${CXXFLAGS[@]}" "${extra_flags[@]}" "${local_cflags[@]}" -pthread -Isrc -c "$src" -o "$obj" &
      pids+=($!)
    fi
  done
  local failed=0
  for pid in "${pids[@]}"; do
    wait "$pid" || failed=1
  done
  if [[ $failed -ne 0 ]]; then
    echo "Parallel compilation of object files failed" >&2
    exit 1
  fi
}

# G2: pure core unit tests compiled with strict -Werror flags.
if want G2; then
  compile_parallel build/obj
  if "$CXX" "${CXXFLAGS[@]}" -Isrc "${CORE_OBJS[@]}" tests/unit/test_core.cpp -o build/test_core 2> build/test_core.err \
     && build/test_core > build/test_core.out 2>&1; then
    pass G2 "core unit tests ($(tr -d '\n' < build/test_core.out))"
  else
    fail G2 "core unit tests failed (see build/test_core.err / build/test_core.out)"
  fi
fi

# G3: ASan + UBSan unit tests + 15,000-iteration untrusted parser fuzzer.
if want G3; then
  SAN_FLAGS=(-fsanitize=address,undefined -fno-omit-frame-pointer)
  compile_parallel build/obj_san "${SAN_FLAGS[@]}"
  if "$CXX" "${CXXFLAGS[@]}" "${SAN_FLAGS[@]}" -Isrc "${CORE_OBJS_SAN[@]}" tests/unit/test_core.cpp -o build/test_core_san 2> build/test_core_san.err \
     && "$CXX" "${CXXFLAGS[@]}" "${SAN_FLAGS[@]}" -Isrc "${CORE_OBJS_SAN[@]}" tests/fuzz/fuzz_parsers.cpp -o build/fuzz_parsers_san 2> build/fuzz_parsers_san.err \
     && build/test_core_san > build/test_core_san.out 2>&1 \
     && build/fuzz_parsers_san > build/fuzz_parsers_san.out 2>&1; then
    pass G3 "ASan+UBSan unit + parser fuzz ($(tr -d '\n' < build/fuzz_parsers_san.out))"
  else
    fail G3 "ASan+UBSan suite failed (see build/*_san.err / build/*_san.out)"
  fi
fi

# G4: FakeAdbServer loopback TCP integration test (text fallback, proto tracker, 2s grace, metadata, pair).
if want G4; then
  compile_parallel build/obj
  if "$CXX" "${CXXFLAGS[@]}" -pthread -Isrc "${CORE_OBJS[@]}" "${PLATFORM_ADB_OBJS[@]}" tests/fake_adbd/test_fake_adbd.cpp -o build/test_fake_adbd 2> build/test_fake_adbd.err \
     && build/test_fake_adbd > build/test_fake_adbd.out 2>&1; then
    pass G4 "fake_adbd integration ($(tr -d '\n' < build/test_fake_adbd.out))"
  else
    fail G4 "fake_adbd integration failed (see build/test_fake_adbd.err / build/test_fake_adbd.out)"
  fi
fi

# G5: mDNS browser (SO_REUSEADDR coexistence, PTR/SRV/TXT/A correlation, TTL=0 goodbye), diagnostics, and autostart.
if want G5; then
  compile_parallel build/obj
  if "$CXX" "${CXXFLAGS[@]}" -pthread -Isrc "${CORE_OBJS[@]}" "${PLATFORM_ADB_OBJS[@]}" "${PLATFORM_MDNS_OBJS[@]}" tests/netns_mdns/test_mdns_platform.cpp -o build/test_mdns_platform 2> build/test_mdns_platform.err \
     && build/test_mdns_platform > build/test_mdns_platform.out 2>&1; then
    pass G5 "mDNS + diagnostics + autostart ($(tr -d '\n' < build/test_mdns_platform.out))"
  else
    fail G5 "mDNS + diagnostics + autostart failed (see build/test_mdns_platform.err / build/test_mdns_platform.out)"
  fi
fi

# G6: Session D-Bus adapters (org.adbhawk.Instance, StatusNotifierItem, dbusmenu under GTestDBus).
if want G6; then
  compile_parallel build/obj
  if "$CXX" "${CXXFLAGS[@]}" "${GIO_CFLAGS[@]}" -pthread -Isrc "${CORE_OBJS[@]}" "${PLATFORM_ADB_OBJS[@]}" "${PLATFORM_MDNS_OBJS[@]}" "${PLATFORM_DBUS_OBJS[@]}" tests/dbus/test_dbus.cpp -o build/test_dbus "${GIO_LIBS[@]}" 2> build/test_dbus.err \
     && build/test_dbus > build/test_dbus.out 2>&1; then
    pass G6 "D-Bus adapters ($(tr -d '\n' < build/test_dbus.out))"
  else
    fail G6 "D-Bus adapters failed (see build/test_dbus.err / build/test_dbus.out)"
  fi
fi

# G7: Shell placement, multi-monitor bounds, and dismiss race debouncing.
if want G7; then
  compile_parallel build/obj
  if "$CXX" "${CXXFLAGS[@]}" -Isrc "${CORE_OBJS[@]}" tests/unit/test_shell_placement.cpp -o build/test_shell_placement 2> build/test_shell_placement.err \
     && build/test_shell_placement > build/test_shell_placement.out 2>&1; then
    pass G7 "shell placement & debounce ($(tr -d '\n' < build/test_shell_placement.out))"
  else
    fail G7 "shell placement & debounce failed (see build/test_shell_placement.err / build/test_shell_placement.out)"
  fi
fi

# G8: Resource and descriptor leak check (ASan + LSan + /proc/self/fd tracking).
if want G8; then
  SAN_FLAGS=(-fsanitize=address,undefined -fno-omit-frame-pointer)
  compile_parallel build/obj_san "${SAN_FLAGS[@]}"
  if "$CXX" "${CXXFLAGS[@]}" "${SAN_FLAGS[@]}" -pthread -Isrc "${CORE_OBJS_SAN[@]}" "${PLATFORM_ADB_OBJS_SAN[@]}" "${PLATFORM_MDNS_OBJS_SAN[@]}" tests/resource/test_resource_leaks.cpp -o build/test_resource_leaks_san 2> build/test_resource_leaks_san.err \
     && ASAN_OPTIONS=detect_leaks=1 build/test_resource_leaks_san > build/test_resource_leaks_san.out 2>&1; then
    pass G8 "resource leak check ($(tr -d '\n' < build/test_resource_leaks_san.out))"
  else
    fail G8 "resource leak check failed (see build/test_resource_leaks_san.err / build/test_resource_leaks_san.out)"
  fi
fi

echo "---"
echo "pass=$PASS fail=$FAIL skip=$SKIP"
[[ $FAIL -eq 0 ]] || exit 1
[[ $STRICT -eq 0 || $SKIP -eq 0 ]] || { echo "strict: skipped gates count as failure"; exit 1; }
