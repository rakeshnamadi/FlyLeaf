# Flyleaf Technical Documentation

*A note on every file.*

| | |
|---|---|
| **Product** | Flyleaf, a Windows utility that attaches text notes to files and folders |
| **Version** | 1.0.0 (pre-release) |
| **Platform** | 64-bit Windows, .NET 10 |
| **Last updated** | 5 November 2026 |

**Contents:** [Overview](#1-overview) · [SDLC summary](#2-sdlc-summary) · [Requirements](#3-requirements) · [Design](#4-design) · [Sequence diagrams](#5-sequence-diagrams) · [Implementation](#6-implementation) · [Testing](#7-testing) · [Deployment](#8-deployment) · [Maintenance](#9-maintenance) · [Risks](#10-risks-and-limitations) · [Decision log](#11-decision-log)

---

## 1. Overview

Flyleaf lets a user attach a note to any file or folder. Resting the pointer on that item in File Explorer shows the note, with no need to open the file.

The project contains two editions. They share the same note editor, data model and `notes.json` file.

| | Classic edition | Store edition |
|---|---|---|
| **Parts** | Tray app, Windows service, Explorer DLL | One tray app, plus a menu DLL in the package |
| **Tooltip** | Native Explorer tooltip | Flyleaf's own note card |
| **Admin rights** | Required to install | Not required |
| **Distribution** | `Flyleaf-Setup.exe` | Microsoft Store package (`Flyleaf.msix`) |
| **Signing** | Needs your own trusted certificate | Signed by the Store when published |
| **Status** | Blocked on unsigned PCs with Smart App Control | Current direction |

### Current status

| Area | Status |
|---|---|
| Note editor, lists, history, missing-path highlighting | Working |
| Store edition hover detection and note card in real File Explorer | Working (light theme) |
| Store edition right-click item from a non-packaged copy | Working (under **Show more options**) |
| Main-menu DLL | Works when called directly through COM. Not yet seen in a real menu |
| Installed MSIX package | **Not yet tested** |
| Microsoft Store approval | **Not yet submitted** |
| Classic edition install | Rollback confirmed when Windows blocks it. A successful install is **not yet tested** |

---

## 2. SDLC summary

The project followed an iterative model. Each phase below ran more than once, because testing on a real PC changed the design.

```mermaid
flowchart LR
    A[Requirements] --> B[Design]
    B --> C[Implementation]
    C --> D[Testing]
    D --> E[Deployment]
    E --> F[Maintenance]
    D -- Smart App Control findings --> B
    F -- User feedback --> A
```

| Phase | Output | Where |
|---|---|---|
| Requirements | Functional and non-functional requirements | [Section 3](#3-requirements) |
| Design | Architecture, data model, pipe contract, security model | [Sections 4–5](#4-design) |
| Implementation | Five source projects and a test project | [Section 6](#6-implementation) |
| Testing | Automated tests and real Explorer checks | [Section 7](#7-testing) |
| Deployment | Single-file setup and MSIX package | [Section 8](#8-deployment) |
| Maintenance | Diagnostics, updates and known issues | [Section 9](#9-maintenance) |

The largest design change came from testing. Smart App Control blocked the unsigned service, which led to the Store edition with fewer parts and no admin rights.

---

## 3. Requirements

### Functional

| ID | Requirement | Classic | Store |
|---|---|---|---|
| FR-1 | Attach a text note to any file or folder | Yes | Yes |
| FR-2 | Show the note when hovering over the item in File Explorer | Built, not seen in Explorer | Yes |
| FR-3 | Keep a timestamped history of earlier notes | Yes | Yes |
| FR-4 | Add items by drag-and-drop or Browse | Built, not tested by hand | Built, not tested by hand |
| FR-5 | Highlight paths that no longer exist in red with "path missing" | Yes | Yes |
| FR-6 | Run from the system tray with no window at sign-in | Yes | Built, needs an installed package to confirm |
| FR-7 | Format notes as bullet lists | Yes | Yes |
| FR-8 | Open a note directly from the right-click menu | No | Yes |

### Non-functional

| ID | Requirement | Result |
|---|---|---|
| NFR-1 | Never write any file next to the user's files | Met by design |
| NFR-2 | No external database, with all data in one readable JSON file | Met |
| NFR-3 | Store only the path and note, never file contents | Met |
| NFR-4 | Lookup in under 1 ms (Classic service) | Met in tests with 10,000 notes |
| NFR-5 | Explorer tooltip gives up after 80 ms (Classic DLL) | Measured at 84–100 ms with the service stopped, slightly over the target |
| NFR-6 | Never lose notes because of a damaged or locked file | Atomic saves, and damaged files are moved aside |
| NFR-7 | Nothing leaves the PC | Met: no network code in any component |

---

## 4. Design

### 4.1 Classic edition architecture

```mermaid
flowchart TB
    user([User])
    explorer[File Explorer]
    tray["Flyleaf.exe<br/>tray app and note editor"]
    shell["FlyShell.dll<br/>IQueryInfo, native AOT"]
    service["FlyWatcher.exe<br/>Windows service, LocalService"]
    notes[("%AppData%\Flyleaf\notes.json")]

    user --> tray
    user -- hover --> explorer
    explorer -- GetInfoTip --> shell
    shell -- "named pipe, 80 ms limit" --> service
    tray -- atomic save --> notes
    service -- "file watcher, reload" --> notes
```

### 4.2 Store edition architecture

```mermaid
flowchart TB
    user([User])
    explorer[File Explorer]
    menu["FlyleafMenu.dll<br/>IExplorerCommand"]
    app["Flyleaf.exe<br/>tray, editor, hover watcher, note card"]
    notes[("%AppData%\Flyleaf\notes.json")]

    user -- hover or right-click --> explorer
    app -- "UI Automation and Shell windows" --> explorer
    explorer -- menu command --> menu
    menu -- "flyleaf.exe --note path" --> app
    app -- atomic save and file watcher --> notes
```

### 4.3 Data model

All data lives in `%AppData%\Flyleaf\notes.json`. The file is indented, human-readable UTF-8.

```json
{
  "version": 1,
  "entries": [
    {
      "path": "D:\\Projects\\Apollo\\release plan.docx",
      "description": "Final plan for the November release.",
      "lastModified": "2026-11-05T10:00:00+00:00",
      "history": [
        { "text": "Draft for team review.", "timestamp": "2026-10-28T09:00:00+00:00" }
      ]
    }
  ]
}
```

```mermaid
classDiagram
    class NotesFile {
        int Version
        List~NoteEntry~ Entries
    }
    class NoteEntry {
        string Path
        string Description
        DateTimeOffset LastModified
        List~NoteHistoryItem~ History
    }
    class NoteHistoryItem {
        string Text
        DateTimeOffset Timestamp
    }
    NotesFile "1" --> "*" NoteEntry
    NoteEntry "1" --> "*" NoteHistoryItem
```

**Path normalisation.** Every lookup key is absolute, lowercase and backslash-separated, with no trailing separator except on a drive root. `\\?\` prefixes are removed. See `PathNormalizer.cs`.

### 4.4 Pipe contract (Classic edition)

| Direction | Content | Encoding |
|---|---|---|
| Client to server | Absolute path, then `\n` | UTF-8 |
| Server to client | Note, then `\n` (an empty line means no note) | UTF-8 |

- **Pipe name:** `\\.\pipe\FlyWatcherPipe`
- **Escaping:** inside a line, `\` is written as `\\`, and line breaks as `\n` and `\r`. This keeps a multi-line note on one line.
- **Limits:** 80 ms on the client, 1 second per client on the server, 256 KB per line, 8 simultaneous connections.

### 4.5 Security design

| Concern | Decision |
|---|---|
| Service privileges | Runs as `LocalService`, with read access only to the notes folder |
| Who can read notes over the pipe | Only the installing user's SID |
| Impersonation | The DLL connects anonymously, so the service can't act as the user |
| Elevation | Setup starts unelevated to read the real user's details, then elevates a second copy |
| Tampered system tools | Setup runs `sc.exe`, `icacls.exe` and `regsvr32.exe` by their full System32 paths |
| Planted DLLs | Setup restricts DLL loading to System32 at startup |
| Failed install | Setup rolls back every machine-wide change |
| Second-launch channel (Store) | Pipe limited to the current user with `PipeOptions.CurrentUserOnly` |
| Hover data | Compared with local notes only, and never stored or sent |

---

## 5. Sequence diagrams

### 5.1 Saving a note

```mermaid
sequenceDiagram
    actor User
    participant Form as MainForm
    participant Model as NotesFile
    participant Repo as NotesRepository
    participant Disk as notes.json

    User->>Form: Edit the note and click Save
    Form->>Model: UpdateDescription(entry, text, now)
    Model->>Model: Move the old text into History
    Form->>Repo: SaveAsync(notes)
    Repo->>Repo: Write notes.json.tmp and flush
    Repo->>Disk: Move the temp file over notes.json
    Repo-->>Form: Saved
    Form-->>User: Show "Saved" and refresh History
```

### 5.2 Classic edition: showing a tooltip

```mermaid
sequenceDiagram
    actor User
    participant Explorer as File Explorer
    participant Shell as FlyShell.dll
    participant Service as FlyWatcher
    participant Index as NotesIndex

    User->>Explorer: Hover over a file
    Explorer->>Shell: IPersistFile.Load(path)
    Explorer->>Shell: IQueryInfo.GetInfoTip()
    Shell->>Service: Connect to the pipe and send the path
    Service->>Index: Lookup(path)
    Index-->>Service: Note, or empty
    Service-->>Shell: Escaped note line
    alt The item has a note
        Shell-->>Explorer: S_OK with the tooltip text
        Explorer-->>User: Show the tooltip
    else No note, or no reply within 80 ms
        Shell-->>Explorer: S_FALSE
    end
```

### 5.3 Classic edition: reloading after a save

```mermaid
sequenceDiagram
    participant Tray as Flyleaf.exe
    participant Disk as notes.json
    participant Watcher as WatcherService
    participant Index as NotesIndex

    Tray->>Disk: Atomic save
    Disk-->>Watcher: File change events
    Watcher->>Watcher: Wait 100 ms for events to settle
    Watcher->>Disk: Load the file
    alt The file is valid
        Watcher->>Index: Build a new dictionary and swap it in
    else The file is damaged
        Watcher->>Watcher: Log an error and keep the last good notes
    end
```

### 5.4 Classic edition: installing with rollback

```mermaid
sequenceDiagram
    actor User
    participant Setup as Setup, unelevated
    participant Elevated as Setup, elevated
    participant Windows

    User->>Setup: Run Flyleaf-Setup.exe and click OK
    Setup->>Setup: Read the user's SID and AppData path
    Setup->>Elevated: Relaunch with administrator approval
    Elevated->>Windows: Extract files and register the event source
    Elevated->>Windows: Grant LocalService read access to the notes folder
    Elevated->>Windows: Create and start the service
    alt Everything succeeds
        Elevated->>Windows: Register the DLL and Add/Remove Programs entry
        Elevated-->>Setup: Exit code 0
        Setup->>Windows: Add the startup entry and start the tray app
        Setup-->>User: Flyleaf is installed
    else Windows blocks a component or a step fails
        Elevated->>Windows: Undo every machine-wide change
        Elevated-->>Setup: Exit code 1 and the reason
        Setup-->>User: Show the reason
    end
```

### 5.5 Store edition: hovering over an item

```mermaid
sequenceDiagram
    actor User
    participant Watcher as HoverWatcher
    participant Detector as HoverDetector
    participant Explorer as File Explorer
    participant Lookup as NoteLookup
    participant Card as NoteTooltip

    loop Every 100 ms
        Watcher->>Watcher: Read the cursor position
    end
    User->>Explorer: Rest the pointer on an item
    Note over Watcher: Pointer still for 400 ms
    Watcher->>Detector: Detect(position)
    Detector->>Explorer: Is this an Explorer or desktop window?
    Detector->>Explorer: UI Automation: list item at this point
    Detector->>Explorer: Shell windows: folder of the active tab
    Detector-->>Watcher: Folder, display name and bounds
    Watcher->>Lookup: Find(folder, displayName)
    alt Notes found
        Lookup-->>Watcher: One or more entries
        Watcher->>Card: ShowNotes(entries, position)
        Card-->>User: Fade in the note card
    else No note
        Lookup-->>Watcher: Nothing
    end
    User->>Explorer: Move the pointer off the item
    Watcher->>Card: HideNote()
```

### 5.6 Store edition: Add Flyleaf note from the right-click menu

```mermaid
sequenceDiagram
    actor User
    participant Explorer as File Explorer
    participant Menu as FlyleafMenu.dll
    participant New as New Flyleaf.exe
    participant Running as Running Flyleaf.exe
    participant Form as MainForm

    User->>Explorer: Right-click and choose Add Flyleaf note
    Explorer->>Menu: IExplorerCommand.Invoke(items)
    Menu->>Menu: Read the selected paths and allow foreground
    Menu->>New: Start flyleaf.exe --note path
    alt Flyleaf is already running
        New->>Running: Send the path over the user-only pipe
        New->>New: Exit
        Running->>Form: OpenPathAsync(path)
    else Flyleaf is not running
        New->>Form: Start up and call OpenPathAsync(path)
    end
    Form-->>User: Note open with the cursor ready for typing
```

---

## 6. Implementation

### 6.1 Technology

| Area | Choice |
|---|---|
| Language and runtime | C# on .NET 10 LTS |
| User interface | Windows Forms |
| JSON | `System.Text.Json` with source generation |
| Native components | Native AOT with `GeneratedComInterface` |
| Service host | `Microsoft.Extensions.Hosting.WindowsServices` |
| Tests | xUnit (test project only) |

### 6.2 Projects

| Project | Output | Purpose |
|---|---|---|
| `src/Flyleaf.Core` | Library | Models, path rules, `notes.json` access, pipe protocol, lookup, list formatting |
| `src/Flyleaf` | `Flyleaf.exe` | Classic tray app. Owns the shared `MainForm` and tray code |
| `src/FlyWatcher` | `FlyWatcher.exe` | Classic service: index, file watcher, pipe server |
| `src/FlyShell` | `FlyShell.dll` | Classic Explorer tooltip handler |
| `src/FlySetup` | `Flyleaf-Setup.exe` | Classic installer, also built without files as the uninstaller |
| `src/FlyleafStore` | `Flyleaf.exe` | Store edition: hover detection, note card, single-instance channel |
| `src/FlyleafMenu` | `FlyleafMenu.dll` | Store edition main-menu command |
| `tests/Flyleaf.Tests` | Test assembly | Unit and end-to-end tests |
| `installer/` | Scripts | Build scripts, icon generator, test-install script |

### 6.3 Coding standards

- `async`/`await` with cancellation tokens throughout, with blocking only at the synchronous COM boundary, bounded by a timeout.
- XML documentation comments on public types and members.
- File access wrapped so that a missing or locked file never crashes the app.
- Exceptions never escape into Explorer from a shell component.

---

## 7. Testing

### 7.1 Automated tests

Run with `dotnet test tests/Flyleaf.Tests/Flyleaf.Tests.csproj -c Release`.

| Group | Tests | Latest result on this PC |
|---|---|---|
| Path rules, pipe escaping, note model, repository, lookup, list formatting | 40 | Passed |
| Real Explorer hover detection (runs only with `FLYLEAF_UI_TESTS=1`) | 1 | Skipped in the latest run. Passed when last run on request, before the tooltip and menu changes |
| `FlyShell.dll` with the service stopped | 1 | Passed |
| Service index and pipe tests, and `FlyShell.dll` through the pipe | 8 | **Could not run:** Smart App Control blocks `FlyWatcher.dll`. All passed in earlier builds |

### 7.2 Real-world checks

These ran on Windows 11 with Smart App Control on, using temporary notes files.

| Check | Result |
|---|---|
| Hover on a noted file, a noted folder and a file with no note | Passed |
| Hidden extensions, and two files that share a name | Passed: separate labelled sections |
| Note card with bullets and wrapped lines | Passed in the light theme |
| Typing a list: Enter continues it, Enter on an empty bullet ends it | Passed |
| Save, with the old text moving to History | Passed |
| Right-click verb opening the note in the running copy | Passed |
| `FlyleafMenu.dll` title, icon and command through COM | Passed |
| Classic setup blocked by Windows, then rollback | Passed: only an empty notes folder remained |

### 7.3 Still to test

- Installing and starting the MSIX, including start at sign-in.
- The item in Windows 11's main right-click menu, and its icon.
- A successful Classic install, the native tooltip and uninstall.
- The dark theme, desktop hover and multiple Explorer tabs.
- Drag-and-drop, Browse and the tray menu by hand.

---

## 8. Deployment

### 8.1 Build

| Output | Command |
|---|---|
| Classic: `dist\Flyleaf-Setup.exe` | `powershell -ExecutionPolicy Bypass -File installer/build.ps1` |
| Classic, signed | Add `-CertThumbprint YOUR_THUMBPRINT` |
| Store: `dist\store\Flyleaf.msix` | `powershell -ExecutionPolicy Bypass -File installer/build-store.ps1` |

Building needs the .NET 10 SDK and the Visual Studio C++ build tools. Build output isn't stored in Git.

### 8.2 Publishing to the Microsoft Store

1. Create a Partner Center account and reserve the app name.
2. Copy the three values under **Product identity**, then build with them:

   ```bash
   powershell -ExecutionPolicy Bypass -File installer/build-store.ps1 -IdentityName "VALUE" -Publisher "CN=VALUE" -PublisherDisplayName "VALUE"
   ```

3. Upload `Flyleaf.msix`, and add a description, screenshots and a privacy policy.
4. Explain the restricted capabilities in the submission notes:
   - `runFullTrust`: Flyleaf is a desktop tray app that reads the item under the cursor in File Explorer.
   - `unvirtualizedResources`: notes stay in `%AppData%\Flyleaf`, so they're easy to back up and survive an uninstall.

### 8.3 Testing a package before the Store

`installer/test-install-package.ps1` signs the package with a temporary self-signed certificate and installs it. Trusting that certificate is a machine-wide security change, so Windows asks for approval. `-Remove` undoes it. This script has not been run yet.

---

## 9. Maintenance

| Task | How |
|---|---|
| Back up notes | Copy `%AppData%\Flyleaf\notes.json` |
| Damaged notes file | The tray app moves it to `notes.corrupt-<date>.json` and starts empty |
| Classic service logs | Windows Event Viewer, Application log, source `Flyleaf` |
| Code-integrity blocks | Event Viewer, `Microsoft-Windows-CodeIntegrity/Operational`, events 3033 and 3077 |
| Certificate renewal (Classic) | Certificates now last at most 460 days |
| Remove a trial right-click item | `Flyleaf.exe --remove-menu` |

---

## 10. Risks and limitations

| Risk | Impact | Mitigation |
|---|---|---|
| Unsigned builds are blocked by Smart App Control | Flyleaf can't run on those PCs | Publish through the Store, or sign with a trusted certificate |
| The Store rejects the app or its capabilities | Delays distribution | Explain each capability, and keep the Classic edition available |
| A Windows update changes how Explorer exposes its window | Store edition hover detection could stop working | Automated Explorer test, and a Classic edition using the official interface |
| Hover detection can't see virtual views | No note in Home, search results or libraries | Documented limit |
| Notes are tied to full paths | Moving or renaming a file breaks the link | "Path missing" highlight in the list |
| Notes are per user and per PC | Colleagues don't see each other's notes | Documented limit. Sharing is a possible future feature |
| The Classic service serves one user | A second user's install takes over the service | Documented limit |
| The name "Flyleaf" may be in use elsewhere | A Store name or search conflict | Check availability when reserving the name |

---

## 11. Decision log

| Decision | Why |
|---|---|
| .NET 10 instead of .NET 8 | .NET 8 support ended in November 2026, and .NET 10 is supported until November 2028 |
| Native AOT instead of SharpShell | Explorer never has to load the .NET runtime, and SharpShell doesn't fit .NET 8 or later |
| `LocalService` instead of `LocalSystem` | Least privilege, with access to only one folder of the profile |
| Pipe restricted to one user's SID | Other accounts on the PC can't read the notes |
| Escaped lines in the pipe protocol | Multi-line notes would otherwise be cut off at the first line |
| File watcher instead of a pipe "changed" signal | Simpler, with no extra message type |
| Custom setup instead of ClickOnce, WiX or NSIS | ClickOnce can't install a service or shell extension, and the custom setup needs no extra tooling |
| Added a Store edition | Smart App Control blocks unsigned files whichever language they're written in, and the Store signs packages |
| Own note card in the Store edition | Packaged apps can't load an in-process tooltip handler into Explorer |
| Manifest-based menu command instead of registry verbs | It reaches Windows 11's main menu and is removed on uninstall |
