# Live Walk — 5.0.1-beta.1

A live walk is one sitting with a test build, logged in to the game, following the steps in order. Each step says what to do and what to expect. It takes about 25 minutes and is the quickest way to help test a pre-release.

This page always holds the walk for the pre-release that is out for testing. This one is for **5.0.1-beta.1**, which becomes 5.0.1 on **Wednesday 2 December** if testing finds nothing. The test period is a full week because of the holiday and people travelling, so there is no rush.

> **Walk or checklist?** The walk is one pass through everything, in order. The pinned **Test v5.0.1-beta.1** issue on the [issue tracker](https://github.com/GenieClient/Genie5/issues) has the same changes as separate sections with more detail, for when you want to dig into one of them. Either helps. Report both in that issue.

**You need:** a character you can log in, and five minutes somewhere safe to stand. Parts 7 and 8 are for Linux and macOS only.

**If a step does not match its Expect:** note the step number and carry on. One odd step does not spoil the rest.

## 1. Get the build (2 min)

1. **Help ▸ Check for Updates**, set **Channel** to **beta**, and update. **Expect:** Genie restarts. (5.0.0 moved every install to the stable channel, so the build is not offered until you do this. On macOS and Linux you can also download it from the [release page](https://github.com/GenieClient/Genie5/releases/tag/v5.0.1-beta.1).)
2. **Help ▸ About Genie 5**. **Expect:** `5.0.1-beta.1`.
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

## 4. Scripts and variables (5 min)

A script now sends a `$variable`'s value when it sits inside another variable.

11. Type `#edit vartest`, paste these three lines and save:

        var sigil $
        var line look %sigiltestword
        put %line

12. Type `#var testword hello`, then `.vartest`. **Expect:** the command shown is `look hello`.
13. Type `#unvar testword`, then `.vartest`. **Expect:** the command shown is `look $testword`. A name that is not defined is sent as written.
14. Run a script you use every day for a few minutes. **Expect:** it behaves as it did on 5.0.0.

## 5. Save and load (3 min)

`#save` and `#load` are back.

15. Type `#var savetest 1`, then `#save vars`. **Expect:** `Variables Saved`.
16. Type `#unvar savetest`, then `#load vars`, then `#echo $savetest`. **Expect:** `1`.
17. Type `#save all`. **Expect:** no `Unknown command`.
18. Type `#save nosuch`. **Expect:** `#save: unknown list 'nosuch'`.

## 6. Windows and updates (4 min)

19. Open **Window ▸ Experience**. Type `#config experienceconfigbar off`. **Expect:** the strip at the top of the window disappears at once.
20. Type `#config experienceconfigbar on`. **Expect:** it comes back.
21. Double-click the Combat tab to float it, then right-click inside the floated window. **Expect:** the menu says **Re-dock** the first time it opens.
22. Click **Re-dock**. **Expect:** Combat returns to its tab group.
23. **Maps ▸ Update from Official Repo…** **Expect:** it finishes with no "Refused to fetch" message.

## 7. Linux and macOS only: saved logins (1 min)

24. Close Genie and run the line for your system. **Expect:** `600`.
    - Linux: `stat -c %a ~/.local/share/Genie5/Config/profiles.json`
    - macOS: `stat -f %Lp ~/Library/Application\ Support/Genie5/Config/profiles.json`
    - Portable install: the file is in the `Config` folder beside the app.

## 8. Linux only: memory (10 min, optional)

25. Set a game window's font to one that is not installed, such as `Cascadia Mono`. (`fc-match "Cascadia Mono"` answers with a different family when it is not installed.)
26. Note memory with `ps -o rss= -p $(pgrep Genie5)`, play for ten minutes with heavy text, and read it again. **Expect:** it stays within about 50 MB of where it started.

## 9. Clean up and report

27. Type `#unvar savetest`, then `#save vars`, and delete `vartest.cmd` from your Scripts folder.
28. Comment on the pinned **Test v5.0.1-beta.1** issue with this filled in:

        Live walk: 5.0.1-beta.1
        OS:
        Parts done: 1 2 3 4 5 6 7 8
        Steps that did not match:
        Notes:

To go back to stable builds afterwards, set **Channel** to **stable** in **Help ▸ Check for Updates**. You keep this build until 5.0.1 is out.
