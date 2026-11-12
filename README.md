# Claude Opus 5.5: Unity project

The source project of the battle royale game Claude Opus 5.5 built in the AI comparison. This ZIP has no playable build.

## Open it

1. Extract the whole ZIP to a local folder.
2. Install Unity Editor 6000.5.6f1 through Unity Hub. Add the Windows build support modules if you want to build an EXE.
3. In Unity Hub choose Add, then Add project from disk, and select the folder claude-opus-5-5-test2. It is the one that holds Assets, Packages and ProjectSettings.
4. Open it with that exact editor version and wait for the first import to finish. Restoring the packages needs internet.
5. Open Assets/Scenes/Island.unity and press Play. Start a match from the in-game lobby.

## Build a Windows game

Go to File, Build Profiles and select Windows. Check that Assets/Scenes/Island.unity is enabled in the scene list, then build to a new folder such as Builds/Windows inside the project.

## Good to know

Library, Temp, Logs, UserSettings, old builds and recordings are left out. Unity recreates its local cache on the first import. This ZIP has not been imported into a fresh Unity install, so the first open may need a small fix.

Some editor scripts that generated the island, captured videos or wrote reports still point at folders on the machine they ran on. You don't need them to play or build. Don't run a full island generation over this saved project, because some generators replace Assets/Generated.

## Third-party assets

The characters and animations come from Mixamo (Adobe). They are here because the game needs them, so please don't share them as separate files. For your own projects, download them from mixamo.com.

Source and license notes:

- Assets/Resources/Sfx/SOURCE.txt
- Assets/ThirdParty/Mixamo/SOURCE.txt
