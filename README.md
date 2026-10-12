# IS#274 evidence (side branch, not for merging)

Investigation of "MAUI Debug app on the Android emulator segfaults right after adding a server". Summary and conclusions are in the pull request body (IS#274/PR#576). Here:

- `debug-unloaded.txt`, `debug-host-loaded.txt`, `release-launch.txt`, `release-settled.txt`: one line per add (pid before/after, notifications posted, crash lines in `logcat -b all`). `release-settled #15` is void: the test server stopped (its 2 h task limit) during it.
- `form-at-launch.txt`: the real Add Server form at launch, button and route over time (IS#259's Windows symptom).
- `probes/`: the logcat signature of each deliberate crash kind, from a temporary probe (`scripts/CrashProbe.cs.txt`, `scripts/probe-shellbridge.diff`; never committed to the PR branch), and the SecureStorage hammer results.
- `is221-app-log-2026-09-25.txt`: the app's own log around IS#221's crash (10:44:39.05), read from its AVD `godmode-is221`.
- `scripts/`: the drivers (CDP over the WebView's devtools socket for Debug, `input tap` for Release), the FakeClaude script, the test server config (an isolated server on 127.0.0.1:31374 with a temp root and a throwaway key).
