# Live Walk — 5.0.1-beta.3

A live walk is one sitting with a test build, logged in to the game, following the steps in order. Each step says what to do and what to expect. It takes about 30 minutes and is the quickest way to help test a pre-release.

This page always holds the walk for the pre-release that is out for testing. This one is for **5.0.1-beta.3**, which becomes 5.0.1 on **Wednesday 2 December** if testing finds nothing. The test period is a full week because of the holiday and people travelling, so there is no rush.

This is the third test build of 5.0.1. Part 11 is the only part new since beta.2, and part 4 the only one new since beta.1: if you walked beta.2, do parts 1 and 11 and report those; if you walked beta.1, do parts 1, 2, 4 and 11.

> **Walk or checklist?** The walk is one pass through everything, in order. The pinned **Test v5.0.1-beta.3** issue on the [issue tracker](https://github.com/GenieClient/Genie5/issues) has the same changes as separate sections with more detail, for when you want to dig into one of them. Either helps. Report both in that issue.

**You need:** a character you can log in, and five minutes somewhere safe to stand, with a room or two you can walk to. Parts 8 and 9 are for Linux and macOS only.

**If a step does not match its Expect:** note the step number and carry on. One odd step does not spoil the rest.

## 1. Get the build (2 min)

1. **Help ▸ Check for Updates**, set **Channel** to **beta**, and update. **Expect:** Genie restarts. (5.0.0 moved every install to the stable channel, so the build is not offered until you do this. On macOS and Linux you can also download it from the [release page](https://github.com/GenieClient/Genie5/releases/tag/v5.0.1-beta.3).)
2. **Help ▸ About Genie 5**. **Expect:** `5.0.1-beta.3`.
3. Look at your windows. **Expect:** your layout, profiles and settings as you left them.

## 2. Connect (2 min)

4. Log in the way you normally do. **Expect:** the room, vitals and hands show.
5. Type `look`. **Expect:** a normal room description.

## 3. The mapper switch (5 min)

`#config automapper` is the mapper's on/off switch again, as in Genie 4. In 5.0.0 it turned on Record.

6. Type `#config list`. **Expect:** `automapper = True` and `automapperrecord = False`.
7. Type `#config automapper true` and look at the ⏺ Record button on the map. **Expect:** Record is off.
8. Type `#config automapper false`, then `#goto 1`. **Expect:** a message that the mapper is disabled. You do not move.
9. Type `#config automapper true`. **Expect:** the map finds your room again at once.
10. `#goto` a room in a neighbouring zone, if one is close. **Expect:** you arrive and the map switches to the new zone.

## 4. The map as a small panel (5 min)

Four new switches in the **Maps** menu let the Mapper sit as a small docked minimap. Until you change one, the map looks and works as it did. The map's wheel zoom is also fixed for high-resolution wheels and touchpads.

11. Open the **Maps** menu. **Expect:** **Show Map Toolbar**, **Show Map Status Bar** and **Open Details on Hover** are ticked, and **Right-Click Walks to Room** is not.
12. Untick **Show Map Toolbar**. **Expect:** the bar above the map (zone, level, zoom, edit) goes and the map takes its space.
13. Untick **Show Map Status Bar**. **Expect:** the strip below the map (room title, exit buttons) goes.
14. Untick **Open Details on Hover**, then move the pointer across the **DETAILS** strip on the map's right edge. **Expect:** nothing opens.
15. Click the **DETAILS** strip. **Expect:** the panel opens and stays open. The 📌 pin inside closes it again.
16. Tick **Right-Click Walks to Room**, then right-click a room next to yours on the map. **Expect:** you walk there. No menu opens.
17. Hold Shift and right-click a room. **Expect:** the menu with **Go Here** opens, as before.
18. Close Genie and start it again. **Expect:** the four switches are as you left them.
19. Put the pointer over the map and roll the mouse wheel one notch, or make one short two-finger scroll on a touchpad. **Expect:** the map zooms by one small step, not several at once.

## 5. Scripts and variables (5 min)

A script now sends a `$variable`'s value when it sits inside another variable.

20. Type `#edit vartest`, paste these three lines and save:

        var sigil $
        var line look %sigiltestword
        put %line

21. Type `#var testword hello`, then `.vartest`. **Expect:** the command shown is `look hello`.
22. Type `#unvar testword`, then `.vartest`. **Expect:** the command shown is `look $testword`. A name that is not defined is sent as written.
23. Run a script you use every day for a few minutes. **Expect:** it behaves as it did on 5.0.0.

## 6. Save and load (3 min)

`#save` and `#load` are back.

24. Type `#var savetest 1`, then `#save vars`. **Expect:** `Variables Saved`.
25. Type `#unvar savetest`, then `#load vars`, then `#echo $savetest`. **Expect:** `1`.
26. Type `#save all`. **Expect:** no `Unknown command`.
27. Type `#save nosuch`. **Expect:** `#save: unknown list 'nosuch'`.

## 7. Windows and updates (4 min)

28. Open **Window ▸ Experience**. Type `#config experienceconfigbar off`. **Expect:** the strip at the top of the window disappears at once.
29. Type `#config experienceconfigbar on`. **Expect:** it comes back.
30. Double-click the Combat tab to float it, then right-click inside the floated window. **Expect:** the menu says **Re-dock** the first time it opens.
31. Click **Re-dock**. **Expect:** Combat returns to its tab group.
32. **Maps ▸ Update from Official Repo…** **Expect:** it finishes with no "Refused to fetch" message.

## 8. Linux and macOS only: saved logins (1 min)

33. Close Genie and run the line for your system. **Expect:** `600`.
    - Linux: `stat -c %a ~/.local/share/Genie5/Config/profiles.json`
    - macOS: `stat -f %Lp ~/Library/Application\ Support/Genie5/Config/profiles.json`
    - Portable install: the file is in the `Config` folder beside the app.

## 9. Linux only: memory (10 min, optional)

34. Set a game window's font to one that is not installed, such as `Cascadia Mono`. (`fc-match "Cascadia Mono"` answers with a different family when it is not installed.)
35. Note memory with `ps -o rss= -p $(pgrep Genie5)`, play for ten minutes with heavy text, and read it again. **Expect:** it stays within about 50 MB of where it started.

## 10. Clean up (1 min)

36. Type `#unvar savetest`, then `#save vars`, and delete `vartest.cmd` from your Scripts folder.
37. In the **Maps** menu, put the four switches from part 4 back (the first three ticked, **Right-Click Walks to Room** unticked), unless you prefer the map the new way.

## 11. Back to stable, and back again (5 min, optional)

New in beta.3. On earlier test builds, setting **Channel** to **stable** answered "up to date" and kept you on the test build. This part takes you off the test build, so do it last; step 41 brings you back.

38. **Help ▸ Check for Updates**, **Core** tab: set **Channel** to **stable** and click **Check**. **Expect:** the Status line says `5.0.0` is the latest stable release and that you have `5.0.1-beta.3`, a newer pre-release. It does not say "up to date".
39. Click **Update and Restart**. **Expect:** Genie restarts.
40. **Help ▸ About Genie 5**. **Expect:** `5.0.0`, with your layout, profiles and settings as you left them. Note anything that looks different.
41. To carry on testing: **Help ▸ Check for Updates**, set **Channel** to **beta**, click **Check** and update. **Expect:** you are back on `5.0.1-beta.3`. To stay on 5.0.0, leave **Channel** on **stable**.

## 12. Report

42. Comment on the pinned **Test v5.0.1-beta.3** issue with this filled in:

        Live walk: 5.0.1-beta.3
        OS:
        Parts done: 1 2 3 4 5 6 7 8 9 11
        Steps that did not match:
        Notes:

To go back to stable builds for good, do part 11 and stop after step 40. If you leave **Channel** on **stable** without updating, you keep this build until 5.0.1 is out.
