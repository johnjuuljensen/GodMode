# GodMode.HeadsetSpike

Issue #382's spike: a small Windows-only test app for a Bluetooth headset (the Shokz OpenRun Pro 2), separate from the
MAUI app. It answers the questions that decide steps 3 and 4 of #382's flow: which button gestures reach Windows,
whether GodMode can pause and resume Spotify, how long the A2DP↔HFP switches take, whether a button in HFP reaches
`CallControl`, and whether the headset has LE Audio.

**The answer is in [Conclusions for steps 3 and 4 of #382](#conclusions-for-steps-3-and-4-of-382)**, after six trials
by the user on 2026-10-02, with the [results](#results) table. The trials' own sections, in order, record how it got there.

It is **manual**: no test can press a headset button. The one piece of pure logic, the gesture classifier
(`GestureClassifier.cs`), has unit tests in `tests/GodMode.HeadsetSpike.Tests`.

## Run it

```powershell
git submodule update --init --recursive   # once per checkout: it uses VoiceBot's Windows audio
dotnet run --project tools/GodMode.HeadsetSpike/GodMode.HeadsetSpike.csproj
```

Or build it and start `tools/GodMode.HeadsetSpike/bin/Debug/net10.0-windows10.0.26100.0/GodMode.HeadsetSpike.exe`.
No install, keys or admin rights are needed. Close the GodMode app's voice first, so the two do not hold the mic at
once.

The log goes to `%LOCALAPPDATA%\GodMode.HeadsetSpike\logs\headset-<date>-<time>.log`, one file per run (**Open log
folder** shows it). Attach it to #382.

## What it shows and logs

The top of the window is the state: the mic (open or closed, and its level), the headset's profile as far as Windows
tells (a guess, below), what plays (GSMTC's current session), the call state, the app's own media controls, the key it
swallows, the last gesture, and the time since the last reference. Under it is each headset endpoint, its mix format
and whether it carries sound.

Every log line has the wall clock, the time since start, and the time since the last **reference** (`mic open+1234ms`).
A reference is a mic open or close, or an announcement test. So a switch's timing reads straight off the lines after it.

| Tag | Source |
|---|---|
| `HOOK` | Media and volume keys from a low-level keyboard hook (`WH_KEYBOARD_LL`), down and up, `injected` when software sent them, `SWALLOWED` when the app ate them |
| `RAW` | HID consumer-control reports (raw input, page 0x0C), in the background too, with the device that sent them: Windows' AVRCP transport is such a device |
| `APPCMD` | `WM_APPCOMMAND`, only while the window has focus |
| `GESTUR` | The classifier's reading of `HOOK` or `RAW` presses: single, double, triple, long, with how long it took |
| `SMTC` | The app's own `SystemMediaTransportControls`: `ButtonPressed`, while it has claimed them |
| `GSMTC` | Media sessions (`GlobalSystemMediaTransportControlsSessionManager`): sessions, the current one, each one's playback state and track, and each pause or play the app sent and what it returned |
| `CALL` | `Windows.Media.Devices.CallControl`: what the app reported, and `AnswerRequested`, `HangUpRequested` and the rest |
| `AUDIO` | Every endpoint at start, defaults, endpoints added, removed or changing state, the headset's property changes and mix formats, sound starting and stopping on each headset endpoint (its peak meter), and each headset endpoint's volume and mute, at first and on every change |
| `MIC` | WaveIn (the same capture GodMode.Maui's voice uses: VoiceBot's `NativeAudioSource`, `MicCapture.WaveIn`, 16 kHz mono, 100 ms buffers), opened and closed on a worker thread so the hook and Mark keep running: the moment you asked (the reference), the moment the open or close returned, the first buffer, the first buffer with sound in it, and once a second the loudest sample of that second (`level`) |
| `VOIP` | VoIP calls (Trial 4): the coordinator, the call control devices, a reported call, and `EndRequested`, `AnswerRequested`, `RejectRequested`, `HoldRequested`, `ResumeRequested`, `MuteStateChanged`. `--voip-check` on the command line reports a 4 s call and closes the app; `--reclaim-check` (tag `CHECK`) checks taking the media session back from a resumed Spotify |
| `LISTEN` | Listen mode (Trial 3): started, stopped, each play/pause turning the mic on or off, and each button it ignored |
| `PROXY` | The proxy: started, stopped, and each button it caught; the buttons it passes on are `GSMTC forwarded …` lines, and the status it mirrors from Spotify `SMTC status -> …` |
| `TONE` | The tone's start and end, its device and format |
| `LEAUD` | The LE Audio probe |
| `MARK`, `NOTE` | You: **Mark** (or Ctrl+Alt+M anywhere) when you hear something, a note when you want to say what |

**The profile** is a guess, because Windows has no API that names the Bluetooth profile in use. The OpenRun Pro 2 shows
as `Headphones (OpenRun Pro 2 by Shokz)` (A2DP, render) and `Headset (OpenRun Pro 2 by Shokz)` (HFP, capture, and a
render endpoint of that name where Windows splits them). The guess is HFP while the app has the headset's mic open or
sound plays on a `Headset`/`Hands-Free` endpoint, and A2DP otherwise. Your ears are the check: the switch to HFP is
audible as the music dropping to phone quality.

## Before the trial

1. Pair the headset and connect it. Start Spotify and play something on the headset.
2. Start the app. Check the top: `Playing: SpotifyAB.SpotifyMusic…: Playing`, and the headset's endpoints listed under
   it. If the list is empty, type part of the headset's name in **Headset: name has** and press **Rescan endpoints**.
3. Pick the headset's mic in **Mic** (the default communications mic may be the laptop's), and in **Tone** the speaker
   the music plays on (*Default speaker*, if the headset is the default).
4. Write a **note** before each step below (`Q1 single press`), so the log says what you did.

## Trial script

Do them in this order. For each one, record what the table at the end asks for.

### Q1. Which gestures reach Windows as media keys while on A2DP? Can one be swallowed, while Spotify keeps play/pause?

Mic closed (A2DP), Spotify playing, the app's window **not** focused (click another window).

1. Single press. Then wait 2 s. Double press. Wait. Triple press. Wait. Long press (hold 2 s), and again holding 4 s.
   Then any other button the headset has (volume +/−, and their long presses).
   - Record, per gesture: the `HOOK` lines (which key, down and up), the `RAW` lines (which device), the `GESTUR` line,
     and what Spotify did (paused, next track, nothing).
   - The headset may classify gestures itself: a double press may arrive as one `NextTrack`, not as two `PlayPause`.
     Then `GESTUR` says *single NextTrack*, and the answer is "double press = NextTrack".
2. Pick the key a double or triple press sends (say `PreviousTrack` or `NextTrack`) in **Keys: swallow**. Repeat that
   gesture, then a single press.
   - Record: does Spotify still skip on the swallowed gesture (it should not), and does a single press still play/pause
     Spotify (it should)? The `HOOK` line says `SWALLOWED`.
   - If `RAW` shows a press and `HOOK` shows nothing, the key does not go through the keyboard path, and the hook cannot
     swallow it: say so.
3. Set swallow back to *(none)*. Press **Claim SMTC (playing)**: the app's own media controls become a playing
   session. Repeat the single and double press.
   - Record: the `SMTC ButtonPressed` lines, and whether Spotify still reacted. Expected from Windows' design: buttons go
     to the current session, so while the app claims it, Spotify gets nothing. Press **Release SMTC** after.

### Q2. Can it pause and resume Spotify through GSMTC?

1. Spotify playing. **Pause current**, wait 2 s, **Play current**. Then **Pause all playing**, **Resume paused**.
   - Record: did Spotify pause and resume, the time from the `TryPauseAsync` line to Spotify's `Paused` line, and from
     `TryPlayAsync` to `Playing` (subtract the `t=` values).
2. **Announcement test**: it pauses what plays, plays three tones over the speaker, and resumes.
   - Record: whether the tones were clear on the headset (A2DP), and the gap between `Paused` and the first tone.

### Q3. How long do A2DP→HFP and HFP→A2DP take, are they audible, and when can the tone play?

Spotify playing, so you hear the switch.

1. **Open mic**. Press **Mark** (or Ctrl+Alt+M) the moment you hear the music drop to phone quality. Wait 5 s.
   **Close mic**, and **Mark** when the music sounds full again. Do it three times.
   - Record: the `MARK` time after `mic open` (heard switch), after `mic close` (heard switch back), the `MIC open returned`,
     `first buffer` and `first sound` times after the open (and `close returned` after the close), the `AUDIO` lines (endpoint state, format, sound starting or
     stopping) around each, and whether there was a gap, a click or a pause in the music.
2. Tone offsets. Set **tone offset ms** to 0 and press **Open mic + rising tone**; then **Close mic + falling tone**.
   Repeat with offsets 250, 500, 1000, 1500 and 2000 ms. Then tick **tone waits for first mic sound**, with offset 0,
   and again with 200: speak or hum right after pressing, since the mic's first sound is a peak above 200 of 32767
   (without it the tone plays after 10 s anyway).
   - Record, per offset: did you hear the whole tone, part of it (cut off), or nothing; and the `TONE start` time after
     `mic open`. The smallest offset where the whole tone plays is the answer.
3. Repeat 2 with the speaker in **Tone** set to *Default communications speaker*, if that is a different endpoint.

### Q4. While in HFP, does a button press reach `CallControl` once the app reports an active call? Does Windows show a call?

1. **Open mic** (HFP). Press **Report active call**. Look at the taskbar and the system tray (a call indicator, a
   microphone-in-use icon). Single press the headset's button, then long press it.
   - Record: any `CALL` line (`HangUpRequested`, `AnswerRequested`), any `HOOK`/`RAW` line, and what Windows showed.
2. **End call**, then **Report incoming (silent)**, and single press: an `AnswerRequested` is the headset's answer.
   Then **Report incoming (ringing)**: does the headset ring? **End call**, **Close mic**.
   - If the log says `no CallControl`, set the headset as Windows' default communications device (Sound settings) and
     press **Report active call** again: the app looks for it at each request.
3. Without opening the mic, press **Report active call**: does reporting a call by itself switch the headset to HFP?

### Q5. Does the headset support LE Audio (LC3)?

1. Press **Probe LE Audio**. Record the `LEAUD` lines.
2. Look in Windows' Settings › Bluetooth & devices › Devices for a *Use LE Audio when available* switch, and record
   whether it is there.

## What documentation and code already say

- **LE Audio (Q5): almost certainly no.** The OpenRun Pro 2's specification lists Bluetooth 5.3 with A2DP, AVRCP and
  HFP, and SBC as its only codec ([EE's spec page](https://ee.co.uk/help/tech/device/Shokz/OpenRunPro2), and retailers'
  copies of Shokz's spec sheet); no LE Audio, LC3 or TMAP. Windows has no public API that says which codec or transport
  an audio endpoint uses: Microsoft's own check is the *Use LE Audio* switch in Settings, there only when both the PC
  and the device support it ([Windows Insider blog on LE Audio super wideband stereo](https://techcommunity.microsoft.com/tag/le%20audio)).
  What an app can see is the paired device: an LE Audio headset is a paired Bluetooth LE device with the LE Audio GATT
  services (Published Audio Capabilities 0x1850, Audio Stream Control 0x184E, from the Bluetooth SIG's assigned
  numbers), which **Probe LE Audio** looks for. On this machine's first run the headset showed only as Classic endpoints
  (`Headphones (OpenRun Pro 2 by Shokz)` and `Headset (OpenRun Pro 2 by Shokz)`).
- **SMTC and the current session (Q1.3)**: Windows sends media buttons to one session, the current one, so an app's own
  SMTC hears them only while it is that session, and Spotify does not get them then. That is Windows' design, which the
  trial confirms or not.
- **GSMTC (Q2)**: `TryPauseAsync` and `TryPlayAsync` are requests the app may refuse, and return whether it took them;
  Spotify's desktop app is a GSMTC session (on this machine: `SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify`, read by
  the app's first run). Whether it obeys is the trial's.
- **CallControl (Q4)**: on this machine, with the headset off, `CallControl.GetDefault()` gave none: Windows offers call
  control only for an audio device whose driver takes call commands, which an HFP headset's may.
- **Switch timing (Q3)**: the app times from the mic's open (WaveIn start), as GodMode.Maui's voice would open it. The
  `first sound` line is when the mic's audio stops being silence: a Bluetooth link that is not up yet gives zeros first.
  Its resolution is the 100 ms buffer.

## First trial (2026-10-02, Windows 11 build 26200)

Read from the log `headset-20261002-114444.log` and the user's notes. The headset showed as one A2DP render endpoint,
`Headphones (OpenRun Pro 2 by Shokz)`, and one capture endpoint, `Headset (OpenRun Pro 2 by Shokz)`, both 48 kHz
stereo in shared mode. There was no separate hands-free render endpoint: this is Windows 11's unified Bluetooth
endpoint.

- **The headset's buttons are not keys.** No `HOOK`, `RAW` or `APPCMD` line came from any button press, though the hook
  works: an injected Volume Up and Volume Down showed as `HOOK` lines and `GESTUR single`. Windows sends the headset's
  AVRCP commands straight to the current media session. So a low-level hook can neither see nor swallow them (Q1.2).
- **Through the app's own SMTC, while it claimed the current session**, they came as `ButtonPressed Pause` (or `Play`
  while it said it was paused), `Next` and `Previous`. The headset classifies gestures itself. The trial had no notes,
  so which press sent which button is still open (Trial 2, A). While the app held the session, **Spotify got nothing**.
  When the app said it was paused and Spotify paused, Windows made the app the current session, and the next `Play`
  presses went to the app, not to Spotify.
- **A long press arrived nowhere**: no SMTC button, no key, no HID report (the user's note too).
- **GSMTC works with Spotify**: `TryPauseAsync`/`TryPlayAsync`/`TryTogglePlayPauseAsync` returned true every time.
  Spotify's state changed 55–290 ms later, and the meter followed within about 400 ms. The announcement test paused
  Spotify in 289 ms, played its tones from 439 ms, and Spotify played again at 1022 ms.
- **Opening the mic** (WaveIn on `Headset`): the open returned in 195–570 ms with nothing playing, and 881–994 ms with
  Spotify playing (the switch to HFP happens inside the open). The first buffer came 0.7–1.1 s after the ask. The
  A2DP endpoint's meter went silent about 1 s after the ask, so the music stops reaching the headset there.
- **Closing the mic**: the close returned in 30–160 ms. The **music came back on the A2DP endpoint 5.3 s after the
  close**, every time (5286, 5293, 5283 ms). So Windows holds HFP about 5 s after the mic closes.
- **The mic never heard sound**: every first buffer had peak 1 of 32767, and no `first sound` came. So each *tone waits
  for first mic sound* fell back to its 10 s timeout. Either nobody spoke, or WaveIn gets silence from this endpoint.
  Trial 2, C tells which.
- **`CallControl` is not available**: `GetDefault()` and `FromId` (the headset as default communications speaker) gave
  none, at every request, mic open or closed. So Q4's manual way out by a call button does not exist here.
- Not run: **Probe LE Audio**, and no **Mark** was pressed, so how the switches sounded is open.

## Trial 2: the open points

Start the app as before (Spotify playing on the headset). Before each step, type its letter and number in the note box
and press **Add note** (`A1 single`). Then the log says what you did.

**A. Which press sends which button** (the app catches nothing yet)
1. Press **Claim SMTC (playing)**.
2. Single press, wait 3 s. Double press, wait 3 s. Triple press, wait 3 s. Long press for 2 s, then for 5 s. Note each
   one first.
3. Press **Release SMTC**.

**B. The proxy: catch one gesture, keep Spotify's play/pause.** The deciding scenario for step 3.
1. In **Proxy: catch**, pick `Previous`, which the triple press probably sends. Press **Start proxy**.
2. Single press: Spotify should pause (`GSMTC forwarded Pause`). Single press again: it should play.
3. Double press: Spotify should skip a track (`forwarded Next`).
4. Triple press: Spotify should **not** go back. You hear the rising tone instead (`PROXY CAUGHT Previous`).
5. Leave it 3 minutes with Spotify playing, across a track change. Then single press. Does it still reach the proxy, or
   has Windows given the session back to Spotify? Pause and play Spotify from its own window once, then press again.
6. **Stop proxy**. Note: does Windows' media flyout (the volume flyout) show the spike or Spotify while the proxy runs?

**C. Does the mic hear you?**
1. **Open mic**, wait 3 s, then count aloud to five, then be silent 3 s. Watch `Mic: OPEN, level …` at the top.
2. **Close mic**. Note whether the level moved. The log has the loudest sample of each second (`MIC level`).

**D. How the switches sound** (Mark is the button or Ctrl+Alt+M)
1. Spotify playing. **Open mic**. Press **Mark** the moment the music changes (drops to phone quality, or stops). Wait
   10 s, and note what the music does while the mic is open (plays at phone quality? silent?).
2. **Close mic**. Press **Mark** when the music sounds full again (the log suggests about 5 s). Note what you heard in
   between (silence, phone quality?).
3. **Close mic + falling tone** with the tone offset at 0, then 2000, then 6000 ms (open the mic in between). Note
   whether you heard the falling tone each time: the tone may be lost while Windows still holds HFP.

**E. Volume buttons**
1. Mic closed. Press volume + twice, volume − twice, and hold volume + for 2 s. Note whether Windows' own volume moved.
   The log has the headset endpoints' volume changes (`AUDIO volume of …`).

**F. LE Audio**
1. Press **Probe LE Audio**. Note whether Settings › Bluetooth & devices shows a *Use LE Audio* switch.

Attach the log and your notes to #382.

## Second trial (2026-10-02)

Read from the log `headset-20261002-121327.log` and the user's notes in it.

- **A. The headset's gestures** (the app's own SMTC claimed, Spotify unaffected throughout): **single press → `Pause`**
  (`Play` while the session says it is paused), **double → `Next`**, **triple → `Previous`, but unreliably**. Of 22
  presses during the triple-press test, 13 came as `Previous`, 8 as `Next` and 1 as `Pause`; the user called it "finicky". A long press sends nothing (first
  trial).
- **B. The proxy works, until Spotify is paused.** A forwarded `Pause` paused Spotify (in 290 ms), a forwarded `Next`
  skipped a track, and a caught `Previous` played the tone with Spotify unaffected. But after the forwarded `Pause`,
  **no button reached the spike at all**, single, double or triple, until the proxy stopped. The spike still said
  *playing* while nothing played. In the first trial, a spike saying *paused* with nothing playing did get `Play`. So
  the proxy now mirrors Spotify's play/pause as its own status (*mirror Spotify's play/pause*, on by default): Trial 3.
- **C. The mic hears speech.** Counting aloud gave peaks of 16000–32737 of 32767 each second. In the first trial
  nobody spoke. `first sound` came when the user started counting (3 s after the open), so it says nothing of the
  switch's own timing.
- **D. How the switches sound** (the user's note and Marks): **opening the mic, the sound quality drops at once**,
  marked 1.2–1.9 s after the ask (the open returned at about 0.97 s). **Closing it, full quality comes back after a few
  seconds**: marked 5.9 s after the close, every time, against 5.24–5.31 s in the log. A precise marker of both
  switches: **the headset's Windows volume changes with the profile**. It went to 73 % 0.28–0.40 s after each open
  (HFP's own volume), and back to 50 % 5.22–5.31 s after each close (A2DP). Whether the tones were heard was not noted
  (D3 not run).
- **E. Volume buttons**: volume + and − change Windows' volume of the A2DP endpoint in 6 % steps (AVRCP absolute
  volume), with no key or HID report. Holding volume − does nothing, and holding volume + turns the headset off. So the
  volume buttons are no gesture for GodMode.
- **F. LE Audio: no.** The headset is one paired Bluetooth Classic device (`OpenRun Pro 2 by Shokz`, class
  AudioVideo), with RFCOMM services Hands-Free (0x111E), 0xFEF0 and a vendor UUID, and no paired Bluetooth LE device,
  so it has no LE Audio services. This matches the specification (SBC only).

## The user's decision after the second trial

**`Previous` (the triple press) is no control**: it is too brittle, and too hard to tell from `Next`. Instead, while
GodMode listens, **it captures the media buttons and play/pause turns the mic on and off**, and the user runs Spotify
with the mouse. So GodMode passes nothing on to Spotify (the proxy stays in the app only as the second trial's
evidence). The spike's **Listen** row is that design. It holds the media session, its status mirrors Spotify's
play/pause (the second trial: a session saying "playing" with nothing playing gets no button), and `Play`/`Pause` open
the mic with the rising tone or close it with the falling one. With *pause Spotify while the mic is open* ticked, it
pauses Spotify for the mic and resumes it after.

## Trial 3: listen mode, and the tones

Note each step first (`L2 single`), as before. Leave the tone offset at 0 unless a step says otherwise.

**L. Listen mode**
1. Spotify playing, by mouse. **Start listen mode** (*pause Spotify while the mic is open* ticked, *status while mic
   open* `mirror Spotify`).
2. Single press: the music pauses, the mic opens (quality drop), the rising tone. Log: `LISTEN Pause: mic on`.
3. Wait 5 s, then single press again. **Does the press reach the app while the mic is open (HFP)?** Look for `LISTEN
   …: mic off` and the falling tone, and Spotify resuming once A2DP is back. This decides whether the headset can
   close the mic, or whether only silence and "færdig" can (steps 3 and 4 of #382).
4. If 3 did nothing: close the mic with **Close mic**. Set *status while mic open* to `Playing`, and repeat 2 and 3.
   Then set it to `Paused`, and repeat again.
5. Pause Spotify by mouse, then single press: does the mic open with nothing playing? Single press again to close it.
6. Start Spotify by mouse while listen mode runs, and wait a minute. Single press: does it still reach the app (mic
   on), or did Windows hand the buttons to Spotify (Spotify pauses, no mic)?
7. Double press, with the mic closed and with it open: the log should say `Next ignored`; Spotify must not skip.
8. **Stop listen mode**.

**U. The tones around the switches**
1. Spotify playing. **Open mic + rising tone** at offset 0. Did you hear the rising tone whole, cut, or not at all?
2. **Close mic + falling tone** at offset 0, then (opening the mic in between) at 3000 and 6000 ms. Did you hear the
   falling tone each time?
3. Repeat 1 with offset 1000.

## Third trial (2026-10-02)

Read from the logs `headset-20261002-123944.log` and `headset-20261002-124239.log`, and the user's notes in them.

- **Listen mode opens the mic from the headset**: in A2DP a single press reached the spike (`Pause`, or `Play` with
  Spotify paused), paused Spotify, opened the mic (the switch to HFP) and played the rising tone.
- **In HFP no press reaches it, whatever the spike's status**: none with its status mirrored as *paused* (first log),
  none with it *playing* (second log). The user's note: "single click while mic open doesn't work". **In call mode the
  headset's button is no media button any more.** So listen mode can turn the mic on, but not off.
- The user's note: stopping listen mode left the mic open. Now it closes it.
- **The way left is a call API.** `Windows.Media.Devices.CallControl` is not available here (all trials). Windows 11
  24H2 (10.0.26100) added **call control devices to VoIP calls**: `VoipCallCoordinator.GetDeviceSelectorForCallControl`,
  `IsCallControlDeviceKindSupportedForAssociation`, and `VoipPhoneCall.NotifyCallActive(deviceIds)`, after which the
  device's call button raises the call's `EndRequested`
  ([VoipPhoneCall](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.voipphonecall),
  [VoipCallCoordinator](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.calls.voipcallcoordinator)).
  Teams uses HFP call control on Windows. The spike now targets the 26100 SDK (`Microsoft.Windows.SDK.NET.Ref`
  10.0.26100.87: the projection the .NET SDK picks by default, .57, lacks these APIs).
- **Checked here, with no button** (`--voip-check`, the unpackaged app): the coordinator is available, Bluetooth is
  supported for association, and **the OpenRun Pro 2 is a call control device** (its Hands-Free service, 0x111E). A
  reported VoIP call went active with the headset associated (`using the list: True`), and ended without error. So the
  `voipCall` capability does not stop an unpackaged app.

## Trial 4: the headset's button in a VoIP call

Listen mode now reports a VoIP call while the mic is open (*report a VoIP call while the mic is open*, ticked), with
the headset as its call control device. Its `EndRequested` closes the mic. Note each step first (`V2 single`).

**V. Listen mode with a VoIP call**
1. Spotify playing. **Start listen mode**.
2. Single press: the music pauses, the mic opens, the rising tone, and `VOIP call active`. Does Windows show a call
   (taskbar, a notification)?
3. Wait 5 s, then **single press**. **Does `VOIP EndRequested` come**, then `LISTEN VoIP EndRequested: mic off`, the
   falling tone, and Spotify resuming once A2DP is back?
4. If 3 did nothing: try a long press, then a double press, with the mic still open. Note which (if any) gives a `VOIP`
   line. Then close the mic with **Close mic**, and **End VoIP call**.
5. Repeat 2 and 3 twice. Then mute the headset in the call, if it has a way to (its manual says how), with the mic
   open: does `MuteStateChanged` come?
6. **Stop listen mode**.

**W. The call on its own** (if V3 failed)
1. Mic closed. **Report VoIP call**: does the headset switch to HFP by itself (quality drop)? Single press: any `VOIP`
   line? **End VoIP call**.

## Fourth trial (2026-10-02)

Read from the log `headset-20261002-130433.log` and the user's report.

- **The headset's button closes the mic in HFP, through the VoIP call.** Each of the three presses with the mic open gave
  the call's `EndRequested`. Listen mode then closed the mic, played the falling tone and
  resumed Spotify. **So the design works: a press in A2DP opens the mic (media session), and a press in HFP closes it
  (VoIP call control).** The call went active 1.6–1.8 s after the press that opened the mic.
- **But only once per listen start**, as the user reported. The cause is not the call: when the spike resumed Spotify,
  Windows made Spotify the current media session (`current session: Spotify`, 1 ms before the spike mirrored
  *playing*). So the next press went to Spotify and paused it instead of opening the mic. With Spotify paused the
  session fell back to the spike, and the press after that worked again.
- Fixed in the spike: whenever Spotify starts playing in listen mode, the spike checks after 300 ms and 1 s whether it
  is still Windows' current session. If not, it takes it back by going paused and playing again (`SMTC reclaim`), as it
  did at each listen start.

## Trial 5: listen mode, round after round

1. Spotify playing. **Start listen mode**.
2. Press, wait for the rising tone, wait 3 s, press (falling tone, Spotify resumes). Do this **five times in a row**.
   Note each round that fails, and what happened instead (Spotify paused? nothing?).
3. Once more, but pause Spotify by mouse first: press (mic on), press (mic off; Spotify stays paused).
4. Start Spotify by mouse, wait 5 s, then do one round.
5. **Stop listen mode**.

## Fifth trial (2026-10-02)

Read from the log `headset-20261002-144415.log` and the user's report.

- **The first round worked**: press, Spotify paused, mic open, rising tone; press (`EndRequested`), mic closed,
  Spotify resumed. Every press with the mic open gave `EndRequested`, in all seven rounds.
- **Spotify resumed at phone quality first** ("16 kHz, then 48 kHz"): the spike resumed it 0.9 s after closing the
  mic, while Windows held HFP for another 5.2–5.3 s.
- **The second round failed as in the fourth trial**: when Spotify resumed, it took the current session, and the
  reclaim (paused, then playing again) did not take it back. The next press paused Spotify. After that, Spotify was
  paused by that press, not by the spike, so the spike did not resume it in the rounds that followed: "spotify doesn't
  start".
- Fixed in the spike, and checked here without a button (`--reclaim-check`, which plays Spotify for a few seconds and
  leaves it as it found it):
  - **Reclaim** now closes the spike's session and brings it back, playing. In the check, a Spotify resumed after the
    spike mirrored its pause took the current session (`own is current: False`). After the reclaim the spike was
    current again (`True`), and still was a second later.
  - **Resume once A2DP is back** (*resume Spotify once A2DP is back*, ticked): after the mic closes, the spike waits
    for the headset endpoint's volume to change (Windows keeps a volume per profile, so that change marks A2DP's
    return, 5.2–5.3 s in every trial), at most 8 s, and only then resumes Spotify.

## Trial 6: listen mode, round after round (Trial 5 again)

1. Spotify playing. **Start listen mode**.
2. Press, wait for the rising tone, wait 3 s, press (falling tone). Spotify should resume only once full quality is
   back, about 5 s later. Do this **five times in a row**, and note each round that fails and what happened instead.
3. One round with Spotify paused by mouse first: press (mic on), press (mic off; Spotify stays paused).
4. Start Spotify by mouse, wait 5 s, then do one round.
5. **Stop listen mode**.

## Sixth trial (2026-10-02)

Read from the log `headset-20261002-150829.log`. The user: "all points worked as expected".

- **Seven rounds of listen mode, every one complete**: a press opened the mic (8 mic-ons, the last one closed by
  stopping), and a press in HFP closed it through the VoIP call's `EndRequested` (7 of 7).
- **Spotify resumed at full quality**: in each round the spike paused Spotify, and resumed it once the endpoint's volume
  marked A2DP's return, 5.16–5.20 s after the mic closed. The 8 s fallback was never needed.
- **The reclaim worked every time**: after each resume Spotify took the current session, the spike reopened its own
  (300 ms later), and was current at the next check, 1 s after (7 of 7). So every next press reached the spike.
- **Spotify paused by mouse stayed paused** across a round. **Spotify started by mouse** was taken back from in the
  same way, and the next press opened the mic.
- No error in the log.

## Conclusions for steps 3 and 4 of #382

The spike's answer, for the OpenRun Pro 2 on Windows 11 (build 26200):

1. **Normally the mic is closed, and GodMode leaves the media buttons to Spotify.** Windows sends a Bluetooth
   headset's buttons only to the current media session (SMTC), never as keys or HID reports. No hook sees them.
2. **While GodMode listens, it holds the media session, and the headset's play/pause is the mic's switch** (the user's
   design). The user runs Spotify with the mouse. Next and Previous are ignored: the triple press is too unreliable to
   be a control, a long press sends nothing, and the volume buttons only change Windows' volume. Holding the session
   needs two things:
   - GodMode's media session **mirrors Spotify's play/pause** as its status. A session saying "playing" with nothing
     playing got no button at all.
   - Whenever another session starts playing, Windows makes it current. **GodMode takes it back by closing its session
     and opening it again, playing.** Going paused and playing again does not take it back.
3. **Stepping in (a press in A2DP)**: GodMode pauses Spotify through GSMTC (Spotify paused 284–330 ms later), opens the
   mic (WaveIn, the switch to HFP: the quality drop is heard at once), plays the rising tone (heard), and reports a
   **VoIP call whose call control device is the headset** (active about 1.6 s after the press).
4. **Back to music by a press in HFP**: in HFP the headset's button is no media button. It reaches Windows only as the
   VoIP call's `EndRequested`, through Windows 11 24H2's call control devices (`VoipCallCoordinator`, CallsVoipContract
   v5). `Windows.Media.Devices.CallControl` is not available. GodMode ends the call, closes the mic and plays the
   falling tone. Then it **waits for A2DP's return**, marked by the headset endpoint's Windows volume changing back
   (Windows keeps one per profile), 5.2–5.3 s after the close in every trial, before it resumes Spotify. Resumed sooner,
   Spotify plays at HFP quality until then.
5. **What it needs**: Windows 11 24H2 (10.0.26100) or later at run time, and a build against the 26100 SDK projection
   `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.87 or later (the .NET SDK's default, .57, lacks these APIs). No app
   package or capability declaration: the unpackaged spike reported calls and got `EndRequested`. On an older Windows
   only the automatic way back (silence, "færdig") is left.
6. **Open, not tested**: whether Windows shows the VoIP call anywhere (the user noted nothing), the falling tone's
   audibility during the 5 s HFP hold, other headsets, and Android.

## Results

Final, from the six trials.

| # | Question | Result |
|---|---|---|
| 1a | Single press on A2DP | `Pause`, or `Play` while the current session says paused; as a media-session (SMTC) button only, never a key or HID report |
| 1b | Double press | `Next` |
| 1c | Triple press | `Previous`, unreliably: 8 of 22 came as `Next`. The user's decision: no control |
| 1d | Long press | Nothing reaches Windows |
| 1e | Volume buttons | Windows' volume in 6 % steps (AVRCP absolute volume); no key or HID report. Hold − does nothing, hold + is power off |
| 1f | Catch one gesture, keep Spotify's play/pause | Not by a keyboard hook (the buttons are no keys). The proxy forwarded and caught while Spotify played. Dropped by the user: while listening GodMode takes every button, and Spotify is run by mouse |
| 1g | Own SMTC claimed | Gets every button; Spotify gets none while the app holds the session |
| 1h | Listen mode: play/pause turns the mic on | Yes: a press in A2DP opens the mic, with GodMode's session mirroring Spotify's play/pause and taken back (closed and reopened) whenever Spotify starts playing |
| 1i | A press in HFP turns the mic off | Not as a media button (none arrives in HFP). Yes through a VoIP call associated with the headset (Windows 11 24H2 call control devices): `EndRequested` on every press, 7 of 7 rounds in the sixth trial, unpackaged |
| 2a | GSMTC pause and resume Spotify | Works every time; Spotify's state 55–330 ms later |
| 2b | Announcement test | Paused at 289 ms, tones from 439 ms, playing again at 1022 ms |
| 3a | A2DP→HFP (mic open) | Heard at once, as a quality drop (marked 1.2–1.9 s after the ask); WaveIn's open returns in 0.2–1.1 s; the endpoint's Windows volume changes 0.2–0.5 s after the ask |
| 3b | HFP→A2DP (mic close) | Full quality back 5.2–5.3 s after the close (the endpoint's volume and meter), heard at about 5.9 s, every time, by itself. Spotify resumed on that volume change plays at full quality |
| 3c | Rising tone after open | Heard ("ping plays"), played about 0.4–0.7 s after the ask, with the switch under way |
| 3d | Falling tone after close | Played 0.2 s after the close, inside the 5 s HFP hold; its audibility was not noted |
| 3e | Does the mic hear speech? | Yes: peaks 16000–32737 of 32767 while counting |
| 4 | Button in HFP via `CallControl` | `CallControl` unavailable (GetDefault and FromId give none). The VoIP route instead: 1i |
| 5 | LE Audio | No: a Bluetooth Classic device only, no LE device or LE Audio service; the spec lists SBC only |
