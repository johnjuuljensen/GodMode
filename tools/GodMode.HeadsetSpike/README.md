# GodMode.HeadsetSpike

Issue #382's spike: a small Windows-only test app for a Bluetooth headset (the Shokz OpenRun Pro 2), separate from the
MAUI app. It answers the questions that decide steps 3 and 4 of #382's flow: which button gestures reach Windows,
whether GodMode can pause and resume Spotify, how long the A2DP↔HFP switches take, whether a button in HFP reaches
`CallControl`, and whether the headset has LE Audio.

It is **manual**: no test can press a headset button. The one piece of pure logic, the gesture classifier
(`GestureClassifier.cs`), has unit tests in `tests/GodMode.HeadsetSpike.Tests`.

## Run it

```powershell
git submodule update --init --recursive   # once per checkout: it uses VoiceBot's Windows audio
dotnet run --project tools/GodMode.HeadsetSpike/GodMode.HeadsetSpike.csproj
```

Or build it and start `tools/GodMode.HeadsetSpike/bin/Debug/net10.0-windows10.0.19041.0/GodMode.HeadsetSpike.exe`.
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

## Trial 3: the proxy that mirrors Spotify, and the tones

Two short scenarios. Note each step first (`T1 single`), as before.

**T. The mirroring proxy**
1. Spotify playing. Pick `Previous` in **Proxy: catch**, leave *mirror Spotify's play/pause* ticked, **Start proxy**.
2. Single press: Spotify pauses, and the log says `SMTC status -> Paused (mirrors …)`. **Single press again: does
   Spotify play?** This is what failed in Trial 2.
3. Repeat pause/play three times. Then double press (Spotify skips), and triple press (tone, Spotify stays).
4. Pause Spotify from its own window, then single press on the headset: does it play?
5. Leave it 3 minutes across a track change, then single press twice.
6. Untick *mirror*, **Stop proxy**, **Start proxy**, and repeat 2 once: the log then shows the Trial 2 behaviour again,
   for comparison.
7. **Stop proxy**.

**U. The tones around the switches**
1. Spotify playing. Tone offset 0: **Open mic + rising tone**. Did you hear the rising tone, whole, cut, or not?
2. **Close mic + falling tone** at offset 0, then (opening the mic in between) 3000 and 6000 ms. Did you hear the
   falling tone each time?
3. Repeat 1 with offset 1000.

## Results

Filled in from the first and second trials. The rest waits for Trial 3.

| # | Question | Result |
|---|---|---|
| 1a | Single press on A2DP | `Pause`, or `Play` while the current session says paused; as a media-session (SMTC) button only, never a key or HID report |
| 1b | Double press | `Next` |
| 1c | Triple press | `Previous`, unreliably: about a third of triple presses came as `Next` |
| 1d | Long press | Nothing reaches Windows |
| 1e | Volume buttons | Windows' volume in 6 % steps (AVRCP absolute volume); no key or HID report. Hold − does nothing, hold + is power off |
| 1f | Catch one gesture, keep Spotify's play/pause | Not by a keyboard hook. By the proxy: forwarding and catching work while Spotify plays; after a pause no button arrived with the proxy saying "playing". Mirroring: Trial 3, T |
| 1g | Own SMTC claimed | Gets every button; Spotify gets none while the app holds the session |
| 2a | GSMTC pause and resume Spotify | Works every time; Spotify's state 55–290 ms later |
| 2b | Announcement test | Paused at 289 ms, tones from 439 ms, playing again at 1022 ms |
| 3a | A2DP→HFP (mic open) | Heard at once, as a quality drop (marked 1.2–1.9 s after the ask); WaveIn's open returns in 0.9–1.1 s with music; Windows' volume of the endpoint changes 0.28–0.40 s after the ask |
| 3b | HFP→A2DP (mic close) | Full quality back after 5.2–5.3 s (volume change and meter), heard at about 5.9 s, every time, by itself |
| 3c | Rising tone after open | Trial 3, U |
| 3d | Falling tone after close | Trial 3, U (it plays inside the 5 s HFP hold) |
| 3e | Does the mic hear speech? | Yes: peaks 16000–32737 of 32767 while counting |
| 4 | Button in HFP via `CallControl` | `CallControl` unavailable (GetDefault and FromId give none): no manual way out by a call button |
| 5 | LE Audio | No: a Bluetooth Classic device only, no LE device or LE Audio service; the spec lists SBC only |
