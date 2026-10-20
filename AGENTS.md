# AGENTS.md — rules for agents working on adb-hawk-linux

Carried over from Windows ADB-Hawk's R-01…R-16, adapted. Rules marked **[auto]** are enforced by `tools/verify.sh`; the rest by review. Violating a rule is a defect even if the gate is green.

- **R-01 Design is law.** `DESIGN.md` here plus the referenced Windows `DESIGN.md`/§41. No redesign, no variant components; density means scrolling.
- **R-02 Scope.** Observe → explain → notify → pair. No shell, logcat, install, transfer, mirroring, reboot, forwarding, screenshots, history, analytics, settings screens.
- **R-03 Boundaries.** `src/core` has no I/O, GLib, GTK or OS headers. `src/platform` touches the OS. `src/shell` is the only code that knows the toolkit and contains no ADB knowledge or semantic text. No product state in `platform`.
- **R-04 No polling where a stream or event exists.** Sanctioned periodic work: adb-server probe backoff; battery refresh while the flyout is pinned **and** visible; 1 s age ticker while visible. One-shot deadline timers armed from reducer output (notification holds) are not periodic. A new timer needs a spec edit and evidence that no event exists.
- **R-05 One writer, latest snapshot wins.** State changes only on the GLib main thread inside `Reduce`. Emit one snapshot per drained batch. Never render optimistic state; never delay truth behind an animation.
- **R-06 No GTK/GLib calls off the main thread.** Debug builds assert it.
- **R-07 The shell has no business logic.** All dynamic user-visible words come from `Vocabulary`. No jargon (TLS, mDNS, daemon, udev, transport id, enum names, exception text) in any user-visible surface.
- **R-08 Never swallow a failure.** Every error changes state, retries under its owner's backoff, or is logged with subsystem and reason. No empty `catch`, no ignored return codes. No exceptions across C callbacks.
- **R-09 Untrusted input.** Everything from the ADB socket and the LAN is hostile: length caps (1 MiB frames), bounded DNS name-compression depth, caps on cache entries and datagrams per wake-up. Parsers live in `core/` and are fuzzed.
- **R-10 ADB citizenship. [auto in part]** Never `kill-server`/`host:kill` except the single user-initiated restart in `src/platform/repair_restart_adb.cpp`; never set `ADB_*`; never bundle/download platform-tools; never send a device-mutating command; pairing is the only write and only on explicit user action. Never spawn an adb *client* whose protocol version differs from the running server's (it can restart the server). Read `ANDROID_ADB_SERVER_PORT`, never set it. Refuse to show devices from a listener owned by another uid.
- **R-11 Secrets.** Pairing codes/QR secrets never in argv, logs, disk or snapshots. Serials are redacted at Information level. Log files 0600 in 0700 directories.
- **R-12 Process hygiene. [auto in part]** No `system`/`popen`/`sh -c`. Spawn with an argv array, `CLOEXEC` on every fd, ignore `SIGPIPE` and use `MSG_NOSIGNAL`, reap children with a GLib child watch.
- **R-13 No silent dependencies.** The runtime links only OS libraries on the allow-list in `tools/verify.sh`; adding one requires a spec edit.
- **R-14 Tests ship with the change.** New behavior → unit test; new failure mode → fake-adbd scenario; reliability claims need a test that exercises them.
- **R-15 Be honest about the gate.** Never claim success from a partial run. A SKIP is not a PASS. Never loosen a flag, disable a test, or add a suppression to turn the gate green. State what was *not* run.
- **R-16 Evidence labels.** Spec claims are tagged observed / source / inferred / unverified. Close an *unverified* item with a probe result, not an argument. A UI that was never launched is not "working".
- **R-17 Small verifiable increments, in the order in `ENGINEERING.md` §6.**
