# Bulk import/export + copy ids — review log

Features requested
1. Bulk import/export on right-clicking a folder in a tree (key: file right-click must keep working).
2. Bulk export/import of chunk files in the Chunk/Res explorer.
3. Bulk export/import of res files in the Chunk/Res explorer.
4. "Copy Chunk Id" on any asset that has an associated chunk.
5. "Copy Res Id" in the same manner.

## Phase 1 — "would a user's action work?" audit (started 13:26:17)

Actions traced through the code, with the outcome:

| # | User action | Outcome |
|---|---|---|
| A1 | Right-click a folder node in the Legacy tree -> Bulk Export Folder | works (was previously impossible: command demanded a selected file) |
| A2 | Right-click the root node -> Bulk Export Folder | exports every legacy file, after a confirmation |
| A3 | Right-click a file in the list -> Bulk Export Folder | unchanged: exports that file's folder |
| A4 | Right-click a folder -> Bulk Import Folder | files matched in-folder, then by name, else duplicated from a same-extension asset in the folder |
| A5 | Right-click a file -> Bulk Import Folder | file is still the duplication template (original behaviour) |
| A6 | Copy Chunk Id on a legacy file | enabled when the entry has a chunk |
| A7 | Copy Chunk Id on a res asset | chunk found by scanning the resource header (cached) |
| A8 | Copy Chunk Id on an ebx asset | enabled only when a chunk is linked to it (sound/texture imported this session) |
| A9 | Copy Res Id | enabled only for resources; copies `0x…` hex |
| A10 | Copy Chunk Id in the chunk list | copies the clicked chunk, not the previously selected one |
| A11 | Bulk Export/Import Chunks | round-trips `<chunk id>.chunk`; respects list filter + "only modified" |
| A12 | Bulk Export/Import Res | exports `header + data` as `.res`, import matches by filename in the target folder |
| A13 | Right-click a res folder in the Chunk/Res editor | bulk commands enabled, single-asset commands disabled |
| A14 | Bulk export of a whole game | asks for confirmation first |

### Defects found and fixed in phase 1
1. `GetRes` fully decompresses a resource, and the copy-id enable check ran it on every menu
   open -> memoised per asset, invalidated when the asset's `ModifiedEntry` changes.
2. Right-clicking a list row assigned a `ListViewItem` to `SelectedItem` (expects the data item)
   -> select the container instead.
3. `Open`/`Revert` used `m_currentExplorer` (tab-based) rather than the explorer whose menu was
   opened -> use the menu's explorer.
4. Chunk "Copy Chunk Id" depended on selection timing -> command parameter is now the clicked
   chunk itself.
5. Importing into a folder could modify a same-named asset in a *different* folder
   (legacy and ebx) -> folder-scoped match now wins, with the old name match as fallback.
6. EBX bulk export called each asset type's own `Export`, and `FrostyTaskWindow` runs its
   callback on a background thread: MeshSet's exporter shows a modal settings dialog (cross-thread
   UI), AntState/AntAnimationSet ignore the requested format. -> EBX bulk now uses the plain
   `AssetDefinition` and writes uniform raw ebx (`.bin`, the format bulk import reads back).
7. `AssetDefinition.Import` is never overridden anywhere, so raw-ebx import is the only supported
   path -> unsupported extensions are skipped with a clear log instead of attempted.
8. Chunk/Res editor: right-clicking a tree folder then clicking Export/Import threw
   NullReferenceException -> items disabled for folders and the handlers guard a null asset.
9. Plugin-contributed menu items (`Duplicate`) act on `App.SelectedAsset` and would throw on a
   folder -> hidden for folder contexts (hidden, not disabled, because those items carry a
   Command whose requery would re-enable them).
10. 0-byte `.chunk` files and `.res` files shorter than the 16-byte header are now skipped.
11. "Copy Chunk Id" / "Copy Res Id" stayed greyed out for every texture and mesh in the EBX Explorer:
    those assets hold no data themselves, they point at the resource that does (`Resource`,
    `MeshSetResource`). Both commands now follow that reference (cached), so a texture's chunk and
    res id can be copied from the asset the user actually right-clicked.
12. Chunk list commands dereferenced the selected row directly; they now report "select a chunk
    first" (and "has not been modified" for Revert) instead of throwing.
13. Single-select checks verified against the code that decides paths: `AssetEntry.Path` and
    `LegacyFileEntry.Path` both split `Name` on '/', so the folder matching used by every bulk
    command is separator-safe on Windows (`AssetEntry.Path`, AssetManager.cs:150).

### Verified
- Both projects build green (FrostyEditor.exe, ChunkResEditorPlugin.dll).
- New members and menu headers present in the built binaries.
- Logic harness (`/tmp/fv`): GUID byte order round-trips through a byte-granular scan, chunk
  filename round-trip, folder-prefix matching (root/exact/partial-segment), export relative-path
  round-trip, and the import matching priority incl. the shadowed-name case -> ALL PASSED.
- `/FrostyCore;component/Images/Copy.png` exists in `FrostyCore.g.resources` (checked with
  `System.Resources.ResourceReader`, not an ASCII grep) so the new menu icons resolve.

### Not verified
- No click-through of the running editor: launching it needs a game profile/install, which is
  not available here. Menu enable/disable logic was audited by reading the code paths instead.

=== PHASE 2: IS THE UI CORRECT? (resumed pass, 14:56 - 15:02) ===
QUESTIONS ASKED OF EVERY NEW MENU ENTRY, AND THE ANSWERS:

Q: Does the folder right-click really open the explorer's menu? (the riskiest assumption)
A: YES - proven with a real WPF harness (/tmp/uitest) that builds a FrostyDataExplorer-shaped
   ControlTemplate whose ItemContainerStyle sets ContextMenu with a FindAncestor binding to the control's
   AssetContextMenu DP. The generated TreeViewItem receives the control's own ContextMenu instance, Tag
   intact. 11/11 checks passed.

Q: Do the icons the new entries ask for exist?
A: YES - verified with System.Resources.ResourceReader against the built assemblies (not by grepping bytes,
   which is unreliable): images/copy.png is in FrostyCore.dll; images/export.png and images/import.png are
   in FrostyEditor.exe. Nothing new renders as a blank square.

Q: Can a menu entry be reached that would do nothing?
A: NO - asset-only entries are disabled when the menu was raised on a folder, Copy Chunk/Res Id are disabled
   unless the asset really has a chunk/rid, plugin-contributed entries are hidden on folders (their Commands
   would re-enable themselves), and bulk entries are disabled for a file sitting at the root level, where
   "the folder" would silently mean "the whole game".

Q: Is the right-clicked node the one acted on?
A: YES - right-click now selects the tree node (new PreviewMouseRightButtonDown handlers), and the menu reads
   the selection at open time, so it no longer acts on whatever happened to be selected in the list.

Q: Any cosmetic leftovers?
A: NO - separators are hidden when the group below them is gone (proven in the harness, including the
   trailing-separator case), and the plural header ("Copy Chunk Ids (3)") always overwrites the previous
   header, including back to the singular form.

Q: Naming consistency with the rest of the editor?
A: KEPT - "Bulk Export Folder" / "Bulk Import Folder" reuse the labels the Legacy Explorer already used for
   the same commands; "Copy Chunk Id" / "Copy Res Id" match the plugin's existing wording. The Chunk/Res
   Editor uses "Bulk Export Res" / "Bulk Import Res" / "Bulk Export Chunks" / "Bulk Import Chunks" so the
   user can tell which explorer a menu belongs to.

Q: Multi-selection behaviour?
A: Deliberately unchanged - the EBX and Res explorers are single-select; the Legacy Explorer is multi-select
   and its pre-existing Export/Import/Duplicate entries already act on the right-clicked asset only, so the
   new Copy entries do the same rather than inventing a different rule inside one menu.

KNOWN, REPORTED, NOT MINE: bulk-starhead-import.patch shows as deleted in git status. It was already listed
as a changed file when this session started, the repo reflog contains only the original clone (no checkout,
reset or stash was run), and no command of mine referenced it. It is left exactly as found.

=== ROUND 2: WHY "COPY CHUNK ID" / "COPY RES ID" WERE GREY IN THE DATA EXPLORER ===
FrostySdk/Ebx/ResourceRef.cs declares `public struct ResourceRef` with a private ulong and
`public static implicit operator ulong(ResourceRef value)`. The type library hands out those structs for
members such as a texture's "Resource" and a mesh's "MeshSetResource" (see Plugins/TexturePlugin and
Plugins/MeshSetPlugin, which read them through `dynamic`, i.e. through the implicit conversion).
AssetReferenceUtils used reflection + Convert.ToUInt64(value) with value typed as object, and
Convert.ToUInt64(object) checks IConvertible only - ResourceRef is not IConvertible, so it threw
InvalidCastException and the catch silently produced rid 0. Result: GetResId -> 0 (item greyed) and,
because an ebx asset's chunk belongs to the resource it points at, GetChunkIds -> empty (item greyed).
The legacy explorer was never affected: LegacyFileEntry.ChunkId is read directly and legacy files have no
res id, so only "Copy Res Id" is (correctly) greyed there.
Fix: TryConvertToRid() unwraps ResourceRef first, then falls back to Convert.ToUInt64. No behaviour was
weakened for the other shapes: the numeric path and the "no rid member returns 0" path are unchanged and
are covered by the harness checks above.
