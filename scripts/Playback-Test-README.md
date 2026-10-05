# Whose IPTV — playback test

This is the actual Whose IPTV app with local diagnostic recording. Video remains in its embedded player, including fullscreen. The app refuses to start playback when that player surface is unavailable. No standalone playback window is used.

The current changes stop hidden loading animations, cancel work when leaving Events/Movies/Series, retain unchanged Events cards, preserve channel selection during list updates, move catalog processing off the interface thread, and reduce repeated player-window positioning. Channel searches, including empty results and clearing the search, keep the current stream playing until an explicit channel selection. The Today’s Events button decoration is static. Events and Movies/Series navigation fades have been removed. Idle loading placeholders are hidden so their shimmer clocks stop. Existing buffering and hardware decoding remain selected.

Remaining picture drops during some window transitions have been reproduced. This build is for further verification; it is not a certified permanent fix.

1. Close the Store app before testing a live stream, so two copies do not use the TV connection.
2. Use the desktop shortcut `Whose IPTV - TEST`. Its title says `PLAYBACK TEST`. The shortcut selects a separate test profile containing the saved test playlist.
3. Change channels, visit Today’s Events and event details, and return to the player. There is no need to mark each brief cut. **F8** is optional.
4. Close the test app normally to finish recording.

The desktop test shortcut stores catalog/preferences under the workspace’s `artifacts/diagnostics/user-m3u-test-profile` directory. Launching the executable directly without the profile argument uses the default application profile. The test does not replace the Store installation.

Logs stay in the `diagnostics` folder beside this executable; nothing is uploaded. Selected diagnostic categories and counters are recorded without raw VLC messages or stream URLs. Log files roll at 10 MB, with at most 14 files retained.

Measurements have limits: VLC counters are batched, so a sample with no new frames does not establish a freeze. Native logging adds overhead and its managed delivery time is not a screen-presentation timestamp. Automated comparisons can disable periodic diagnostics and the startup sampler after loading, retaining phase-boundary counters and checks that the video belongs to this app’s window. Playback validation uses live channels only. An unavailable live input is reported as unavailable; recorded content is not substituted.
