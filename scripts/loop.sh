#!/bin/bash
# usage: loop.sh <mode: launch|settled> <N> <tag>
export MSYS_NO_PATHCONV=1
A="/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe"
D="$A -s emulator-5600"
MODE=$1; N=$2; TAG=$3; OUT=C:/tmp/gm274drive/runs/$TAG; mkdir -p $OUT
PKG=com.godmode.maui
ADD="window.HybridWebView.SendRawMessage(JSON.stringify({Type:'servers.add',Id:'t274',Payload:{Type:'local',DisplayName:'T274',Urls:['http://10.0.2.2:31374'],Username:null,AccessToken:'test274key-not-secret-0123456789'}})); 'sent'"
for i in $(seq 1 $N); do
  "$A" -s emulator-5600 shell am force-stop $PKG
  if [ "$RESET" = clear ]; then "$A" -s emulator-5600 shell pm clear $PKG >/dev/null; "$A" -s emulator-5600 shell pm grant $PKG android.permission.POST_NOTIFICATIONS; else "$A" -s emulator-5600 shell "run-as $PKG sh -c 'rm -f files/.godmode/servers.json shared_prefs/$PKG.microsoft.maui.essentials.preferences.xml files/.godmode/logs/*'"; fi
  "$A" -s emulator-5600 shell cmd notification cancel_all $PKG >/dev/null 2>&1
  "$A" -s emulator-5600 logcat -b all -c
  t0=$(date +%s%3N)
  "$A" -s emulator-5600 shell am start -n $PKG/crc64d809cabfef7648d3.MainActivity >/dev/null
  pid=""; for k in $(seq 1 100); do pid=$("$A" -s emulator-5600 shell pidof $PKG | tr -d '\r'); [ -n "$pid" ] && break; sleep 0.1; done
  "$A" -s emulator-5600 forward --remove tcp:9274 >/dev/null 2>&1
  sent=""
  for k in $(seq 1 300); do
    if "$A" -s emulator-5600 shell cat /proc/net/unix | grep -q "webview_devtools_remote_$pid"; then
      "$A" -s emulator-5600 forward tcp:9274 localabstract:webview_devtools_remote_$pid >/dev/null
      r=$(node C:/tmp/gm274drive/cdp.mjs 9274 "typeof window.HybridWebView === 'object' && document.readyState" 2>/dev/null)
      if [ "$r" = '"complete"' ] || [ "$r" = '"interactive"' ]; then
        [ "$MODE" = settled ] && sleep 15
        r2=$(node C:/tmp/gm274drive/cdp.mjs 9274 "$ADD" 2>&1); sent=$(( $(date +%s%3N) - t0 )); break
      fi
    fi
    sleep 0.1
  done
  sleep 15
  now=$("$A" -s emulator-5600 shell pidof $PKG | tr -d '\r')
  "$A" -s emulator-5600 logcat -b all -d > $OUT/logcat-$i.txt
  "$A" -s emulator-5600 shell "run-as $PKG sh -c 'cat files/.godmode/logs/*'" > $OUT/app-$i.txt 2>/dev/null
  crash=$(grep -c -E "Fatal signal|SIGSEGV|exited due to signal|FATAL EXCEPTION|Abort message|mono-rt" $OUT/logcat-$i.txt)
  notes=$(grep -c "Question, notifies" $OUT/app-$i.txt)
  reg=$(grep -c "Registered local server" $OUT/app-$i.txt)
  echo "$TAG #$i mode=$MODE pid=$pid after=$now sent_ms=${sent:-never} registered=$reg notified=$notes crashlines=$crash"
done
