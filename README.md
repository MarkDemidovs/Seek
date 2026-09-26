# Seek

A small, fast file search for Windows.

Press **Alt+Space**, start typing, and the 5 newest matching files on your PC show up under a see-through search bar.

## Searching

| Type | Finds |
| --- | --- |
| `.pdf` | every PDF, newest first |
| `thing.pdf` | files named thing.pdf first, then names that contain it |
| `thing*.pdf` | names that start with "thing" and end with ".pdf" |
| `thing*thing2.pdf` | "thing", then anything, then "thing2.pdf" |

`*` means any text and `?` means any one character. Upper and lower case don't matter, and neither do accents.

## Keys

- **Enter**: open
- **Ctrl+Enter** or **right click**: show in Explorer
- **Up / Down**: pick a result
- **Tab** or **Right** on a folder: search only inside that folder
- **Backspace**, **Left** or **Esc**: go back out of the folder
- **Esc**: hide
- **Ctrl+Shift+C**: copy the path
- **Drag** a result somewhere to copy the file there

## How it works

1. Seek goes through every file and folder on your drives and saves a list of them in `%LOCALAPPDATA%\Seek\index.bin`, sorted from newest to oldest.
2. When you type, Seek reads that list from the top. The newest files come first, so it can stop as soon as it finds 5 matches. Most searches take a few milliseconds.
3. While it runs, Seek watches your drives, so new, renamed and deleted files show up right away.
4. The list stays on disk instead of in memory, so Seek never uses more than 128 MB of RAM. While the bar is hidden it usually uses around 10 MB.
5. It skips places nobody searches, like the Recycle Bin, Windows system folders, `node_modules` and `.git`.

Seek lives in the tray and starts with Windows. You can turn that off from the tray menu.

## Build

Run `build.cmd`. It uses the C# compiler that already comes with Windows, so there is nothing to install. The app ends up in `bin\Seek.exe`.

The `tools` folder has the test programs used while building Seek.
