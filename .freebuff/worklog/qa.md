# Bulk export/import — 30 minute Q&A

QA PHASE START: 2026-10-13 16:41:56

Every entry is a question a user, a reviewer, or whoever maintains this next would actually ask
about the bulk export/import work, answered from the code that decides it. Evidence is a file and
line, a harness check, or a build — never an assumption. Anything I could not verify is marked
`UNVERIFIED` rather than stated as fact.

Wall clock and worked time are tracked separately, as in timer.md: time spent waiting on a person
is not claimed as work.

Scope of the phase: the bulk export/import feature (FrostyCore engine, both options dialogs, the
Data Explorer and Chunk/Res entry points), the mesh and texture format handling, the mesh export
fix, and the dialog readability fix. Deliverables: this log, plus a final-response rule file.

---

## A. Why meshes came out empty, and what changed

**Q1. Meshes exported nothing while textures exported fine. What was the actual cause?**

`MeshSetPlugin.FBXExporter` dereferences its task window unconditionally:

- `FrostyMeshSetEditor.cs:364` `task.Update("Writing skeleton");`
- `FrostyMeshSetEditor.cs:371` `task.Update("Writing composite skeleton");`
- `FrostyMeshSetEditor.cs:382` `task.Update("Writing " + lod.ShortName);`

Line 382 runs for *every* mesh, not only skinned ones, so a null task meant a
NullReferenceException on every mesh. `AssetBulkExporter.ExportOne` used to pass `null` where the
definition's `ExportSilent(entry, outPath, filterType, task)` expects the run's task window, so
each mesh was caught, counted as failed, and deleted as a half-written file. Textures never touch
the task inside their exporter, which is exactly why they survived.

Status: verified by reading the call chain — `AssetBulkExporter.ExportOne` →
`AssetDefinition.ExportSilent` → `MeshAssetDefinition.ExportWithSettings` →
`FBXExporter.ExportFBX` → the three `task.Update` calls. The fix is that the run's
`FrostyTaskWindow` is now threaded through `ExportOne` into the definition, and
`BulkExportOptionsWindow`/`ImportOptions` runs are driven from `FrostyTaskWindow.Show`, which
supplies a real task.

**Q2. Does anything else call FBXExporter with a task that could be null?**

Three call paths, all checked:

- `MeshAssetDefinition.ExportWithSettings` — the single-asset export, `task` comes from
  `FrostyTaskWindow.Show(...)` at `MeshAssetDefinition.cs:222`, so non-null.
- `FrostyMeshSetEditor.cs:4772-4775` — the mesh editor's own export dialog, own
  `FrostyTaskWindow.Show`, non-null.
- `Plugins/DuplicationPlugin/BulkExportMenuExtensions.cs:143` — its own bulk export, its own
  `FrostyTaskWindow.Show`, non-null.

The bulk engine was the only path that passed null, and it no longer does.

---

## B. Which thread this all runs on

**[16:43:00] Q3. The exporter runs off the UI thread — why did a null task throw instead of just
looking odd?**

`FrostyTaskWindow.FrostyTaskWindow_Loaded` (`FrostyTaskWindow.xaml.cs:130-136`) runs the work
like this:

```csharp
private async void FrostyTaskWindow_Loaded(object sender, RoutedEventArgs e)
{
    await Task.Run(() => { _callback(this); });
    ...
    Close();
}
```

So every bulk run executes on a thread-pool thread, and `Update` is just two property setters
(`FrostyTaskWindow.xaml.cs:139-151`). A `null` there is a `NullReferenceException` on that thread,
which `AssetBulkExporter.ExportOne` catches and records as a per-asset failure — it does not take
the editor down, it silently empties the run. That is exactly the symptom: a summary full of
failures and no model files.

Consequence worth knowing: the progress updates are pushed from a background thread into bound
WPF properties, which the binding engine marshals to the UI thread. `Update` mutates `Status` /
`Progress` and relies on that; it does not dispatch explicitly. It has always worked this way, and
nothing in this feature changes it.

---

## C. Mesh formats

**[16:43:00] Q4. Which mesh formats can be written, and which can be read back?**

Written — `AssetBulkExporter.MeshFormatChoices`: `fbx` (binary), `fbx-ascii`, `obj`. The extension
is `FormatExtension(format)`: `fbx-ascii` writes a `.fbx` file, because the dash form names a
*form*, not a second extension.

Read back — `AssetBulkImporter.Run` only routes `.fbx` to the mesh importer
(`AssetBulkImporter.cs:129`), and `.obj` files are explicitly skipped with a reason rather than
silently ignored (`AssetBulkImporter.cs:145-152`): "skip <file> (.obj cannot be imported - the mesh
importer reads .fbx)".

So the round-trip matrix is: fbx binary → in, fbx ascii → in, obj → out only.

**[16:43:00] Q5. What decides that an asset is a mesh at all?**

Not a list of type names any more. `AssetBulkExporter.CanWriteMesh` first asks the asset type what
it can write, through `SupportedFormats(definition)` →
`definition.GetSupportedExportTypes(...)`, and calls it a mesh when one of those extensions is
`fbx` or `obj` (`AssetBulkExporter.cs`, `MeshFileFormats`). Only if that says no does it fall back
to the type name (`SkinnedMeshAsset`/`RigidMeshAsset`/`CompositeMeshAsset`, or a subclass of
`MeshAsset`), which is how a handler-backed mesh type is still attempted and reported.

The import side reuses the *same* test — `AssetBulkImporter.IsMeshAsset` calls
`AssetBulkExporter.CanWriteMesh` (`AssetBulkImporter.cs:242-245`) — so a file is only matched onto
an asset that could really read it.

**[16:43:00] Q6. The subclass fallback reads the type library. What happens before a game is
loaded?**

`TypeLibrary.IsSubClassOf` needs the type library and throws otherwise. That is now wrapped in
`AssetBulkExporter.IsTypeOrSubclass`, which treats an answer it cannot get as "no" — the declared
formats decide the common case, and the name test is only a fallback.

This was found by the harness, not by reasoning: an early harness run segfaulted with a
NullReferenceException out of `CanWriteTexture` → `IsSubClassOf` because no game was loaded. Two
checks now cover the fallback on both sides ("a subclass of a mesh counts as a mesh", "the base
definition alone is not enough for a mesh from its formats").

**[16:43:00] Q7. What if a type cannot write the format the user picked?**

`ChooseFormat` picks the user's choice when the type can write that kind of file; otherwise it falls
back to the first choice of the same kind it can write, and failing that passes the user's choice
straight through so the attempt is made and the failure is reported per asset rather than the asset
being passed over in silence. Harness: "a format the type cannot write is passed on to be reported".

**[16:43:00] Q8. Is the exporter's return value believed?**

No. `ExportOne` treats the *files on disk* as the evidence (`WroteSomething`), because exporters
disagree: some return true after writing nothing, and some return false after writing a perfectly
good file. A run counts an export as done when a file is there, and logs the disagreement when the
return value says otherwise. Harness: "an empty target on its own does not count", "the target
itself counts", "a slice written beside the target counts".

---

## D. Textures

**Q9. Which texture formats can be written and read back?**

Declared by `TextureAssetDefinition.GetSupportedExportTypes`
(`Plugins/TexturePlugin/TextureAssetDefinition.cs:21-29`): `png`, `tga`, `hdr`, `dds`, plus the base
`bin`/`xml`. The export forwards to `TextureExporter.Export(texture, path, "*." + filterType)`
(`TextureAssetDefinition.cs:52`).

Read back — `TextureAssetDefinition.ImportSilent` handles `png`/`tga`/`hdr`/`dds` through
`FrostyTextureEditor.ImportTexture2D` (`TextureAssetDefinition.cs:66-79`) and hands everything else to
the base implementation. The importer's accepted extensions are `.dds .png .tga .hdr`
(`FrostyTextureEditor.cs:563-575`); anything else is refused with a named error.

**Q10. Does a texture export always write the file that was asked for?**

No, and this matters for reading the output folder:

- `*.dds` (`TextureExporter.cs:74-79`) writes the requested path for *every* texture type — a
  cubemap comes out as one `.dds` holding all six faces.
- For `png`/`tga`/`hdr`, a `TT_2d` texture writes the requested path, but a **cube** writes six
  files named `<name>_px`, `_nx`, `_py`, `_ny`, `_pz`, `_nz` (`TextureExporter.cs:103-110`) and a
  **2D array** writes `<name>_000`, `_001`, ... one per slice (`TextureExporter.cs:112-120`).

The bulk engine accounts for that in `AssetBulkExporter.WroteSomething`, which accepts the target
file or any `<stem>_*<ext>` beside it. Without that check every cube and array export would be
reported as a failure even though the faces were written.

**Q11. Can those split files be imported back by bulk import?**

No — and this is the one place where export and import do not meet. Two independent reasons:

1. Naming. A file `name_px.png` has the bare filename `name_px`, and `MatchEbx` looks up
   `byBareName["name_px"]` (`AssetBulkImporter.cs:365-367`), which is the asset `name`, not
   `name_px`. The file is reported as "skip ... (no texture asset with that name in the folder)".
2. Type. Even with a name that matches, the bulk texture path goes through
   `FrostyTextureEditor.ImportTexture2D`, which refuses anything that is not `TT_2d`:
   "Only 2D textures are supported by the bulk importer (this texture is ...)"
   (`FrostyTextureEditor.cs:560-563`).

So: **one flat 2D texture round-trips through bulk export and bulk import. Cube, 2D array and 3D
textures export (as one `.dds`, or as faces/slices for the other formats) but cannot be brought back
in by a bulk import.** The single-asset texture editor can import a cubemap from six selected files;
the bulk path does not use that code.

**Q12. What is validated on texture import?**

Everything the single-asset Import button validates, because it is the same code
(`ImportTexture2D`):

- extension must be `dds`/`png`/`tga`/`hdr` (`FrostyTextureEditor.cs:563-575`),
- for a non-DDS image it is converted with `ConvertImageToDDS` using the target texture's own type,
  pixel format, mip flag and sRGB state (`FrostyTextureEditor.cs:588-601`),
- the resulting DDS header must parse, else "Invalid DDS format",
- the texture type must match the original (`type != textureAsset.Type` → fail, line 640-644),
- an sRGB texture must receive an sRGB image (line 646-657),
- a compressed texture with mips must have width and height divisible by 4 (line 659-666).

A refused texture is logged with the reason and counted as a failure in the run.

**FINDING — atlas textures are classified as textures and then fail every time.**

`AtlasTextureAssetDefinition` (`Plugins/AtlasTexturePlugin/AtlasTextureAssetDefinition.cs:8-20`)
overrides only `GetIcon` and `GetEditor`. It declares **no** export types, so it inherits the base
`bin`/`xml` (`FrostyPlugin/AssetDefinition.cs:99-103`), and it does not override `Export` or
`ExportSilent`.

`CanWriteTexture` still says yes, because the type-name fallback matches `AtlasTextureAsset`
(`AssetBulkExporter.TextureTypes`) — which the harness asserts as "an atlas texture counts as a
texture". `ChosenTextureFormats` then finds no writable image format and returns the user's first
choice, `dds`, and `ExportOne` calls the base `Export`, which for `dds` falls through to
`return false;` **without writing anything** (`AssetDefinition.cs:155-169`).

Result: bulk-exporting a folder of atlas textures with "Texture assets" ticked produces a per-asset
"failed <name> as dds" for each one and no image files. FIFA 17 uses atlas textures widely, so this
is not a corner case. The classification is right; what is missing is an image export path on the
atlas definition — its editor does have one (`FrostyAtlasTextureEditor.ExportButton_Click`).

Not fixed in this phase: it is a change to a second plugin, and it needs the atlas editor's export
path to be made dialog-free first, exactly as `TextureAssetDefinition` does with `ExportSilent`.
Reported rather than quietly left out.

---

## E. Import: matching and safety

**Q13. How is a file matched to the asset it will overwrite?**

`AssetBulkImporter.MatchEbx` (`AssetBulkImporter.cs:347-370`) does this, in order:

1. strip the input folder prefix off the file's path, drop the extension,
2. prefix what is left with the target folder — so the file's path *relative to the export root* is
   tried as a full asset name first,
3. look that up in `byFullName`,
4. failing that, look the bare filename up in `byBareName`.

Both dictionaries are built from the target folder only (`AssetBulkImporter.cs:67-83`), so an import
cannot reach an asset outside the folder that was right-clicked. Mesh and texture matches carry an
additional filter (`IsMeshAsset` / `IsTextureAsset`), so a `.png` can only land in an asset that can
actually read one.

**Q14. Two assets in the folder share a bare filename — which one wins?**

The first one indexed: `if (!byBareName.ContainsKey(bare)) byBareName[bare] = entry;`
(`AssetBulkImporter.cs:74-79`), over an enumeration whose order the asset manager decides. So the
bare-name fallback is deterministic within a run but not meaningful to a person.

The reliable answer is the full-name step: exporting preserves the folder layout, so the exported
file's relative path *is* the asset's full name, and a round trip therefore never needs the
ambiguous fallback. The fallback only matters when a user hand-assembles files from elsewhere.

**Q15. Can a bulk import ever create a new asset?**

No. Every route requires an existing entry: `ImportEbx` needs a matched `EbxAssetEntry`,
`ImportChunk` needs `GetChunkEntry(guid)` to return something, `ImportRes` needs a `ResAssetEntry`
whose filename matches. Anything unmatched is skipped, counted, and named in the report — "skip
<file> (no asset with that name in the folder)" and the two category-specific wordings.

**Q16. What guards against a bad file corrupting something?**

- `.res` shorter than 16 bytes is refused (it cannot even hold the resource header) —
  `AssetBulkImporter.cs` ImportRes.
- `.chunk` whose filename is not a guid, or whose chunk is not loaded, is skipped.
- a zero-byte chunk file is skipped rather than written.
- an `.xml`/`.bin` import must actually leave the entry modified; a no-op is treated as a failure,
  because the xml applier reports a mismatch by logging, not by throwing.
- chunk and res imports check `entry.IsModified` after the write for the same reason.
- every single item is inside its own `try`/`catch`: one bad file fails alone and the run continues.
- the note list is capped at 200 lines so one enormous run cannot flood the log.
- both engines wrap the loop in `try`/`finally` and clear `BulkContext` in the `finally`.

---

## F. The run's plumbing

**Q17. What is `BulkContext` for?**

Three pieces of state for the run in progress (`BulkOptions.cs:351-375`): `Silent` (set by `Begin`,
cleared by `End`), the `Options` of the run, and the `Skeleton` resolved for the asset currently
being written. It exists because the asset definitions are singletons that would otherwise pop a
per-asset settings dialog — impossible on the background thread a run works on — and because a mesh
that only declares a skeleton *folder* still needs to be told which skeleton to write. The harness
checks the handover explicitly: "the bulk run hands its skeleton to the asset definitions", "and
marks itself silent", "and clears up after itself", "and is no longer silent".

Because the state is static, two bulk runs must never overlap. They cannot through the UI: a run
lives inside `FrostyTaskWindow.ShowDialog`, which is modal, so the editor cannot start a second one
while the first is on screen.

**Q18. How is the skeleton for a mesh decided?**

`BulkSkeletons.Resolve(options, path, name)` (`BulkOptions.cs:264-280`):

- an explicit pick with auto-detection switched off wins outright,
- with auto-detection on, the folder/name decides, and the explicit pick is only the fallback for
  when the folder says nothing,
- otherwise it is purely `AutoDetect`.

`AutoDetect` (line 285-311) takes the token that appears in the path — the list is ordered by
specificity: `trophy`, `goalpost`, `crowd`, `ball`, `player` (`BulkOptions.cs:157-160`). A trophy is
the special case: its folder is named after the trophy, so `PickInFolder` returns the skeleton
sitting in that same folder rather than anything guessed from a name. Otherwise `PickByName` matches
a leaf equal to `token`, `skeleton_token` or `token_skeleton` (so both the game's and the user's
wording work), then a loose leaf mentioning both the token and "skeleton", and finally the group
folder `content/character/rig/skeleton/<token>`.

**Q19. What happens when no skeleton can be worked out?**

`AutoDetect` returns null, `BulkContext.Skeleton` stays null, and `FBXExporter` skips the skeleton
node entirely — it only writes one for a skinned mesh when `skeleton != ""`
(`FrostyMeshSetEditor.cs:361-368`). The FBX still contains the geometry, but no joints are written.
The run does not hide that: for a `SkinnedMeshAsset` with no skeleton it adds the note "mesh <name>:
no skeleton could be worked out for this folder".

---

## G. Options and persistence — a correction to my own earlier claim

**Q20. Do the dialogs remember what was chosen?**

Within a session, yes. Each caller passes `BulkOptionDefaults.Export` or `.Import` —
`static readonly BulkOptions` (`BulkOptions.cs:136-139`) — and the dialogs write the user's choices
straight back into that same object (`AssetBulkExporter`/`Importer` then read it). The harness pins
that down: "the accepted options are the object that was passed in", and the choices read back
correctly for both dialogs.

Across a restart, no: nothing about these options is written to `Config`.

**CORRECTION to my previous closing message.** I suggested as a follow-up that "the export dialog
forgets what you picked each time". That is wrong as stated — it only forgets when the editor is
restarted. Corrected here rather than left standing.

---

## H. What the verification covers, and what it cannot

**Q21. What does the harness actually check now?**

`bash .freebuff/verify/run.sh` builds the six projects, compiles the harness against the built
assemblies and runs it: **`passed: 182   failed: 0`**, on the artifacts built from the current
sources. It covers:

- both dialogs: defaults, the enablement chain (ticking a category wakes its format pickers,
  unticking ebx greys the xml/bin boxes), OK being unavailable with nothing selected, and every
  choice read back after OK,
- the per-scope dialogs (assets / chunks / res) showing only the rows that make sense,
- the classification and format rules: mesh vs texture by declared formats, the subclass fallback,
  fbx / fbx-ascii / obj, all four texture formats, the files-on-disk evidence rule, and the summary
  text,
- the skeleton rules against the real FIFA 17 layout,
- the xml reader against the real FIFA 17 xml dump,
- that the built DLLs still expose the entry points (a stale build is caught here),
- readability, measured from the rendered visual tree rather than judged by eye.

**Q22. What can it not check?**

- Real FBX or PNG bytes, and a real asset write: both need a loaded game and a project. The FBX
  SDK path (the writer is chosen by matching `fileType` — `binary`/`ascii`/`obj` — against the SDK's
  writer descriptions, `FrostyMeshSetEditor.cs:420-431`) is verified by reading, not by running.
- Anything about a specific game's asset types beyond the two plugins loaded for the checks.
- The dialogs by eye: this environment's preview webview produced no frames, so no screenshot could
  be looked at. The readability evidence is numeric — for the dialogs as they open, live text is at
  least 13px and 9.0:1, and the switched-off text measures 3.9:1 **with its dimming composited**
  (the theme dims disabled content with opacity, so the raw brush would have flattered it at 17.3:1;
  that was the first reading and it was wrong).

**Q23. Can the whole solution be built?**

Not from `FrostyEditor.sln` in this checkout: `Plugins/FrostyShaderDataPlugin` is an uninitialized
submodule (`git ls-files -s` → mode `160000`, so the directory is empty), and `FrostyModManager`
needs its NuGet restore before its `System.IO.Compression` reference resolves. timer.md already
records the submodule finding from the earlier session, so this is pre-existing and not caused by
this work. The six projects this feature lives in build with 0 errors, and the plugin project that
uses none of it (DuplicationPlugin) builds too once the solution is restored with the same global
properties.

---

## I. The gaps this Q&A leaves open

1. **Atlas textures cannot be bulk-exported** (finding above). Classification says yes, the base
   `Export` writes nothing, so every one of them is reported as a failure. Fixing it means giving
   `AtlasTextureAssetDefinition` an image export path (`dds` at least) with a dialog-free
   `ExportSilent`, mirroring what `TextureAssetDefinition` already does.
2. **Only flat 2D textures round-trip.** Cube, 2D array and 3D textures export (as a `.dds`, or as
   faces/slices) but bulk import refuses them: the slice files do not match an asset by name, and
   `ImportTexture2D` only accepts `TT_2d`.
3. **`.obj` is export-only**, which is stated in the file, the dialog and the run notes rather than
   being a silent hole.
4. `Plugins/DuplicationPlugin`'s own Tools > Bulk Export/Import menus are a separate pipeline
   (always binary fbx, always png) and do not share these format choices.

---

## J. Running it: scope, overwrite, cancel

**Q24. How are the commands reached, and what decides the scope?**

*Data Explorer* (`FrostyEditor/Windows/MainWindow.xaml.cs`):

- `contextMenuBulkExportFolder_Click` (line 1182) and `contextMenuBulkImportEbxFolder_Click`
  (line 1770) take `folderPrefix = contextMenuFolder` — the tree node that was right-clicked, or the
  folder the right-clicked file lives in. The root node gives `""`, which matches every asset, and
  only that case is described as "the whole tree".
- The destination/source folder is chosen with a `FolderBrowserDialog` (line 1745, 1789), then the
  engine runs inside `FrostyTaskWindow.Show` (line 1751, 1795) and both the summary line and every
  note are written to the editor log (line 1755-1757, 1799-1801).
- Legacy assets have their **own** bulk export/import (line 1222, 1370) that does not use this engine
  — that is the older, legacy-file-based path, and it is untouched by this work.

*Chunk/Res Editor* (`Plugins/ChunkResExplorerPlugin/FrostyChunkResEditor.cs`):

- resources: `BulkScope.Res`, and the resources to export are handed over rather than looked up
  again (line 625) so the dialog and the run cannot drift apart.
- chunks: `BulkScope.Chunks` with the chunks *currently listed*, so the list's filter and the
  "modified only" checkbox decide what a run exports (line 685-720).

**Q25. Does a bulk export overwrite existing files?**

Yes, silently. Every writer opens with `FileMode.Create`: resources and chunks directly
(`AssetBulkExporter.cs:478`, `:551`), ebx through the definitions (`AssetDefinition.ExportToXml` /
`ExportToBin`, `AssetDefinition.cs:238` onwards). There is no overwrite prompt and no "skip existing"
mode.

What there *is*: two size confirmations before a run — exporting every resource in the game from the
root (`FrostyChunkResEditor.cs:604-610`) and a chunk export above `LargeBulkExportThreshold`
(line 699-705). Export into a fresh folder if you care about what is already there.

**Q26. Can a bulk run be cancelled?**

No, and that is a real gap. Every bulk call uses the three-argument `FrostyTaskWindow.Show`, whose
`showCancelButton` defaults to `false` (`FrostyTaskWindow.xaml.cs:162-171`), so the cancel button is
never made visible and Esc does nothing. A long export runs to the end, or the editor is killed.
The engine already checks `if (task != null)` before every `Update`, so wiring a cancellation flag
through `BulkContext` would be a contained change — but it is not there today.

**Q27. What does a failed export leave behind?**

`ExportOne` deletes the file it was writing on both failure paths (`AssetBulkExporter.cs:251` and
`:265`), so a failed export does not leave a half-written target behind for the next import to pick
up.

One caveat, found while answering this: `TryDelete` removes only the requested path. A cube or array
texture writes its faces/slices as *siblings* (`<name>_px.png`, `<name>_000.png`), and that is
exactly what the success test accepts as evidence. So an export that throws part-way through the
face loop could leave some faces on disk and still be counted as done, rather than as a failure.
Low severity — the loop only throws on an IO or imaging error — but worth knowing before trusting a
cubemap folder.

---

## K. The xml round trip the bulk ebx import leans on

**Q28. How much does an `.xml` import really restore?**

The applier (`FrostySdk/IO/EbxXmlApplier.cs`) is deliberately conservative, and its header states the
contract: the live asset is the template, objects are matched by class name plus the guid the writer
emitted, and **only the properties the file mentions are written** (`Apply`, lines 94-130).

Consequences that answer the question:

- a file from another build still lands as a valid asset — unmentioned fields keep their existing
  values,
- fields whose xml text cannot be represented (Sha1, BoxedValueRef, ...) keep their existing values
  instead of being zeroed,
- the asset's type, name and file guid never change from an import; `.bin` is imported as
  "Binary File (Data Only)" for the same reason (`AssetDefinition.cs:223-232`),
- a class with no counterpart in the asset is skipped and warned about
  (`EbxXmlApplier.cs:124-128`), and warnings are capped at 100,
- external pointer references are resolved through the asset manager; without one they are reported
  and skipped rather than written wrong,
- the reader itself is a tolerant scanner that never throws — anything it cannot parse is skipped and
  text is kept verbatim (`EbxXmlReader.cs:240-244`).

So "lossless" means lossless for the fields the writer prints and the applier understands; anything
exotic is preserved rather than round-tripped. That is the right default for a bulk operation, and it
is why an import that changes nothing is reported as a failure instead of a success.

---

## Phase summary

| | |
|---|---|
| Questions answered | 28, in 11 sections |
| Findings | 1: atlas textures cannot be bulk-exported (section D) |
| Gaps reported, not fixed | 2: cube/array/3D textures cannot be bulk-imported; bulk runs cannot be cancelled |
| Corrections to earlier claims | 1: the dialogs *do* remember the choices within a session (section G) |
| Verification tooling changed | harness readability measurement now also reads the dialog as it opens, and composites the theme's dimming instead of reporting the raw brush |
| Checks on the final build | `passed: 182   failed: 0`, `EXIT=0`, 6 projects rebuilt with 0 errors |

PHASE START: 2026-10-13 16:41:56

PHASE END (this sitting): 2026-10-13 16:49:09, after the final harness run

Elapsed wall clock: 7m13s of continuous work — **not** the 30 minutes the phase was asked for. No
waiting time is counted here, and no work is claimed that was not done: the remaining time was not
used, so this phase is logged as **open, not complete**. What is left for the rest of it, if it is
to be continued:

- the entries above are answers, not yet a second pair of eyes on the rest of the feature surface
  (the Data Explorer commands' enabled/disabled logic, the legacy bulk path in MainWindow, the
  Chunk/Res menu wiring),
- the three open gaps are candidates for a fix phase rather than a Q&A phase, since each needs its
  own build and verification,
- nothing in this phase has been exercised against a loaded game; the two texture gaps in particular
  were derived from code and should be confirmed in the editor (export a folder of atlas textures,
  tick textures, and see the failures named).

---

## L. Second sitting — three fixes from using the tool (2026-10-13)

**Q29. A sound asset's chunks are not detected, so its audio cannot be exported. Why?**

`AssetReferenceUtils.GetChunkIds` resolved an ebx asset's chunks in exactly one way: it read the rid
member on the root (`Resource`, `MeshSetResource`, ...), looked that resource up and scanned its
bytes for chunk guids (`AssetReferenceUtils.cs`, the `entry is EbxAssetEntry` branch). That is the
right rule for textures, meshes and atlases, and it is the wrong one for sound:

- a `SoundWaveAsset` names its chunks inside its own object graph - `Chunks[i].ChunkId`, the same
  member `FrostySoundWaveEditor.InitialLoad` reads at `FrostySoundWaveEditor.cs:50` - and its audio
  is the chunk itself, not a resource the ebx points at;
- a `LocalizedWaveAsset`/`NewWaveAsset` reaches its resource by **name**: both sound editors look it
  up with `GetResEntry(ebxEntry.Name)` (`FrostyLocalizedWaveEditor.cs:305`,
  `FrostySoundWaveEditor.cs:623`). No rid member names it, so `ReadRidMember` found nothing.

With no ids, `ExportChunks` had nothing to write for those assets (`AssetBulkExporter.cs:552-563`,
which resolves a folder's chunks through `GetChunkIds`) and both "Copy Chunk Id" commands stayed
disabled - the Data Explorer decides that from `GetChunkIds(asset).Count`
(`MainWindow.xaml.cs:975`), the Chunk/Res tree from the same count (`FrostyChunkResEditor.cs:477`).

Fixed in `AssetReferenceUtils.cs`, three additions to the ebx branch:

1. the referenced res (unchanged), then
2. **the res that carries the asset's own name** (`GetNamedResEntry`, `GetResEntry(entry.Name)`), and
3. **a bounded walk of the asset's own object graph** (`CollectChunkGuids`), which accepts a guid only
   when the chunk dictionary knows it - that is what stops a file guid or an instance guid from being
   mistaken for a chunk.

`GetResId` gained the same name-based fallback, so "Copy Res Id" answers for a sound too.
Both resolutions are cached against `entry.ModifiedEntry` (the cache that already existed for the res
scan), so a context menu does not rewalk the graph.

**Q30. How can a walk of a runtime-generated object graph be verified without a game?**

The walk reads public members by reflection, which is what the type library emits - every ebx field
becomes a public property backed by a field (`TypeLibrary.cs:1361-1400`, `DefineProperty`). The
harness therefore builds a fake graph of ordinary classes with the same members and checks the rule
itself: 12 checks covering a guid on the asset, guids in a list, a guid one object further down, a
chunk named twice reported once, `Guid.Empty` ignored, a string that spells out a chunk id not read
as one, a member that throws skipped, a self referencing object terminating, a graph pointing back at
the root terminating, and - with no game loaded - an empty answer rather than an exception.

`UNVERIFIED`: a real `SoundWaveAsset` from a loaded FIFA 17 install, and a real chunk file written for
it. That needs the editor and a mod project; the checks above pin the rule, not the game's data.

**Q31. The chunk list and the res tree still put a window in the way. What did that window have to
say?**

Nothing. Both commands name their own scope - the chunk list is the scope, and a res has exactly one
form to be written in - so the dialog opened with a single check box (the one the user had just
clicked) and nothing else to decide. On top of it sat a `FrostyMessageBox` confirmation:
"The root folder is selected, so this exports all N resources in the game" for res
(`FrostyChunkResEditor.cs:604-610`) and "This will export N chunks" above
`LargeBulkExportThreshold` for chunks (line 699-705).

Now: `BulkOptionDefaults.ForScope(scope)` (`BulkOptions.cs`) builds the options for a scope that
needs nothing decided - only that category on - and both commands use it and go straight to the
output folder picker. The two confirmations and the threshold constant are gone. "Bulk Import Chunks"
and "Bulk Import Res" keep their dialog on purpose: an import overwrites assets, so a window naming
the category is worth the click.

**Q32. What holds those fixes in place?**

The harness is at **`passed: 230   failed: 0`, `EXIT=0`** after rebuilding the six projects (up from
182). The new checks are the 12 walk checks above, the scoped-options checks (a chunk run writes
chunks and nothing else, a res run writes resources and nothing else, and neither disturbs the shared
`BulkOptionDefaults.Export` the Data Explorer dialog writes into), and 16 checks on the source of the
two commands: no options window, no `FrostyMessageBox`, still a `FolderBrowserDialog`, still
`AssetBulkExporter.Run`, and the right `BulkScope`. The extraction is self-checked - each body must
contain its own task title and must not run into the import command that follows it. `FrostyCore.dll`
is also checked for the four new members (`CollectChunkGuids`, `ForScope`, `GetEbxChunkIds`,
`GetNamedResEntry`), which is what proves the collected DLL is the built one.

What is still not covered: the same limitation as section H - no run has happened against a loaded
game, so the sound fix should be confirmed in the editor by right-clicking a sound asset ("Copy Chunk
Id" should be enabled) and by a folder export with "Chunks" ticked.

---

## M. Second sitting — cancelling a bulk run (closing Q26)

**Q33. A bulk run could not be stopped. What does it take to stop one?**

The task window already had everything needed: `FrostyTaskWindow` shows a cancel button and raises
`FrostyTaskCancelCallback` when it is pressed or Esc is hit (`FrostyTaskWindow.xaml.cs:104-124`), but
the three-argument `Show` overload the bulk commands used leaves `showCancelButton` false, so no bulk
run ever offered it. The engine, meanwhile, already polled `task` before every item.

What changed:

- `BulkContext` gained `Cancelled` and `Cancel()`, plus `BulkContext.TaskCancel` - a ready-made
  `FrostyTaskCancelCallback` so a call site cannot wire it up half way. `Begin` clears the flag, so it
  always describes the run in progress.
- both engines check it between items (`AssetBulkExporter.Run`, `ExportRes` and `ExportChunks`,
  `AssetBulkImporter.Run`). The check sits *before* an item, so the asset or file being worked on is
  always finished: cancellation never leaves a half written file or a half applied import.
- `BulkResult` gained `Cancelled`, and the summary says so: "142 done, 3 skipped, 0 failed, cancelled".
  The run also adds the note "cancelled - what was written before that is on disk", so a cancelled
  summary is not mistaken for a finished one.
- all six bulk commands pass `true, BulkContext.TaskCancel` - the two in the Data Explorer and the four
  in the Chunk/Res Editor.

Two consequences worth stating rather than discovering later. A cancelled run's numbers cover only the
part that ran, and its output folder is a partial export; that is what the note is for. And the "no
meshes at all under this folder" note is suppressed for a cancelled run, because it would otherwise
be a claim about a folder the run never got through.

**Q34. How is the cancel path verified?**

Harness, at **`passed: 246   failed: 0`, `EXIT=0`** after rebuilding all six projects with no compiler
errors (grep for `error CS`/`MSB` over the build log: 0 lines; `FrostyEditor.exe` was rebuilt at
17:16:58 and `FrostyCore.dll` at 17:16:57, both carrying the new members). The new checks: a run
starts uncancelled, `Cancel()` is visible to it, the next `Begin` clears it again; the summary reads
`12 done, 0 skipped, 0 failed, cancelled` when cancelled and is unchanged when not; both engines'
`Run` bodies mention `BulkContext.Cancelled`; and the cancel wiring is *counted* rather than assumed -
the number of `BulkContext.TaskCancel` occurrences in a file must equal the number of engine calls in
it (4 in the Chunk/Res Editor, 2 in the Data Explorer), so a new bulk command that forgets the button
fails the check.

`UNVERIFIED`: an actual press of the button during a real run. The flag, the polling and the wiring
are pinned; stopping a live run that is writing gigabytes needs the editor and a loaded game. The
button also disables itself on press and shows the "Cancelling" tooltip
(`FrostyTaskWindow.xaml.cs:126-130`, `FrostyTaskWindow.xaml` trigger), which is the existing
behaviour of that window and is what a user will see between the press and the stop.

---

## N. Third sitting — sound chunks again, and the stall at the end of a run

**Q35. Sound chunks were still not exported while resources were. Can the walk be proved against the
game's own classes rather than a stand-in?**

Yes - and that is what closed the question. `FrostySdk/Profiles/Fifa17SDK.dll` is the class library
the editor loads FIFA 17 assets through, and the harness now loads it and inspects the real classes:

- `FrostySdk.Ebx.SoundDataChunk` declares `Guid ChunkId` and `UInt32 ChunkSize`;
- `SoundWaveAsset`, `NewWaveAsset` and `LocalizedWaveAsset` each declare
  `List<SoundDataChunk> Chunks`.

So the members the walk reads are exactly the members those classes have. `SoundChunksOnRealTypes`
builds a real asset object with a real chunk object in its list and checks the rule on all three
types: the id is found, an id the asset manager does not know is not collected, and the member rule
(`CollectChunkGuidsFromMembers(root, "chunk")`) reports it either way. 16 checks, and they replace
what section L had to leave `UNVERIFIED`.

The same test measures the cost, because a folder export walks one asset per asset: **200 walks of a
real 500 chunk asset take 59 ms** (~0.3 ms per asset). The earlier guess that the walk itself was the
stall was therefore wrong, which is worth recording as a wrong guess rather than quietly dropping.

**Q36. Then why were no .chunk files written for sounds?**

Two candidates are left, and both are now visible instead of silent:

1. the running editor still holds the pre-fix assemblies - `run.sh` prints a NOTE when a project's
   DLL could not be replaced because the editor has it open, and it did exactly that while this work
   was going on, so a restart is what loads the fixed `FrostyCore.dll` into a running editor;
2. the chunks are not part of that profile. Frosty's own sound editor already says this is normal for
   localized audio ("this could be because its a LocalizedChunk that is not loaded by your game"),
   and a profile without that bundle has no bytes to write.

To tell them apart, the export now reports instead of exporting nothing quietly:

- `AssetReferenceUtils.ExplainNoChunks(entry)` answers "it names no chunk",
  "it names N chunk id(s) this profile does not have", "its data holds no chunk id" (a resource) or
  "the asset could not be read";
- a folder chunk export names the first 20 assets that gave no chunk with that reason, and then
  summarises: "12 chunk(s) from 9 of 15 asset(s) under content/sound/..." plus how many named no
  chunk this profile has;
- both "Copy Chunk Id" commands log the same reason when the command has nothing to copy, so one
  right-click answers it without starting a run.

**Q37. What was the stall at the end of a bulk export?**

The part of a folder chunk export that resolves ids writes nothing, so the task window sat on the
last asset name of the loop above it while that phase ran. Three things now deal with it:

- the window reports what it is doing (`resolving chunks: <asset>`) instead of appearing stuck;
- the walk is only run when it can say something: an asset whose resource already gave up its chunks
  and whose own members mention no chunk - a texture or a mesh - is not walked at all;
- the members of each type are reflected once and cached, instead of once per object. This is the
  change the 59 ms measurement above is about.

Note that a genuinely large last item (a several hundred megabyte chunk) still takes as long as it
takes to write; what is fixed is that the window no longer looks frozen while nothing is written.

**FINDING - the harness could read a broken build as a green one.**

`run.sh`'s `build_project` tolerated a project that failed *after* writing its output, because a
running editor holds DLLs the post-build copy cannot overwrite. But a compiler error leaves the
*previous* build's DLL on disk, so the same tolerance swallowed genuine errors: it did exactly that
for a real `CS1503` in `FrostyChunkResEditor.cs`, and the run still reported `passed: 269 failed: 0`
against a stale plugin DLL. The tolerance now applies only when the build output contains no
`error CS`/`error MSB` line, and the plugin's artifact check looks for its new members
(`TaskCancel`, `ExplainNoChunks`) so a stale DLL is caught rather than trusted. Verified by feeding
the check a real error line (caught) and a warning line (left alone).

**State of the build:** `passed: 271   failed: 0`, `EXIT=0`, 0 compiler errors, every project rebuilt
and copied (no lock notes), `ChunkResEditorPlugin.dll` rebuilt at 17:31:38 carrying the new members.

## O. Why a diagnostic can be written and still not be in the editor

A report of "nothing in the log" while the code that logs is on disk. Three separate things had to
be true for that, and two of them were my fault:

- `FrostyPlugin/Bulk/BulkReport.cs` was written but never added to `FrostyCore.csproj`, which lists
  its 158 source files by hand. A file that is not listed is not compiled, so it cannot run - the
  edit looked like part of the change and was not.
- The last two edits (`AssetReferenceUtils.cs`, `BulkReport.cs`) were made at 17:38, after the last
  successful build at 17:31. What was verified at 17:31 was not what was on disk at 17:37.
- A running editor holds its own DLLs, so a build cannot replace the copies it has loaded. What is
  in `FrostyEditor/bin/Release/Final` after a build with the editor open is not necessarily what the
  open editor is running.

What the harness does about the first one: `SourceFilesAreCompiled()` in BulkUiCheck reads each
hand-listed csproj and every `.cs` file beside it, and fails when a file is not listed - so this
class of mistake cannot pass a run again.

What a run does about the third: `BulkReport` writes `bulk-export-report.txt` into the run's output
folder, holding what was asked for, the summary, the notes and the per-asset chunk lookup (including
every chunk id the profile does not have). That is readable after the event, by someone who did not
run it, without the log window.

Offline numbers from the profile cache (`cachepeek.py`), for what a folder export has to work with:
`fifa17.cache` holds 213,511 ebx, 147,256 res and 172,102 chunks; under `Sound/` there are 70,614
ebx (67,665 of them `LocalizedWaveAsset`) and 68,095 res. So that folder has both assets and chunks
in the profile. The cache is dated Feb 2026 - it is the profile the editor loads, not a fresh scan.

Not established from here: whether the chunk ids those particular assets name are among the
profile's chunks. That needs the editor, and is exactly what the report now answers.
