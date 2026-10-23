# FrostyAnimationEditor

Frosty Editor plugin for FIFA 17's AntStateAsset (`fifagame_win32_antstate`): every object of the container
(animation clips, controllers, state machines, blend spaces, expression kernels, curves, tags, ...) can be
viewed, edited, created, cloned and deleted, and the result is written back into the Frosty project.

## Use

1. In Frosty Editor, open a FIFA 17 project.
2. Tools > Animation Editor. The plugin finds the game's AntStateAsset (if there are several, it asks which).
3. The first run for a given content builds the editor cache (a few seconds to a minute: every object is decoded
   once). It is kept in `<Frosty>\Caches\AnimationEditor\` and used from then on. Writing to the project also
   writes the cache of the new content, so the next session starts without rebuilding.
4. Edit. "Save to project" (Ctrl+S) hands the edited container to Frosty; then save the Frosty project or
   export a mod as usual.

Window layout: classes | objects of the class (filter by name, or `0x...` key across all classes) | the selected
object. Tabs of the object:

- Fields: the object's fields as a tree; click a value to edit it, list buttons add / insert copies / move /
  remove elements, `+ new` creates an embedded object, `choose...` picks the object a key points to.
- References: what the object references, and (one index over the file) what references it.
- Animation (clips and ClipControllerAssets): export for Blender (`.antanim.json`, with the add-on
  `io_fifa17_antanim.py`, saved from the same tab) or BVH; replace the clip or add a new clip (and controller)
  from a file; skeleton preview.
- State machine: states, transitions (order = priority), conditions; add / remove / reorder / retarget; the
  runtime tables are rebuilt after each change.
- Blend space: the diagram (items, triangulation, hull), rebuild.
- Expression: the expression kernel as text (disassembled from the bytecode), check, write back, and run it in
  the editor's model of the game's VM with chosen inputs.
- Curves: DOF weight curves and float game-state tag curves, plotted as the game computes them, editable.

Toolbar: Open .res / Save .res (a `.res` exported from Frosty: 16-byte ResMeta + container), Undo / Redo (every
edit), New object, Check (count fields, two-way links, state machine tables, engine limits, dangling keys,
loader sizing H7 / H8). Edit JSON shows the whole object as JSON text. Graph shows the reference graph (or the
state machine) as boxes and arrows; it can be saved as an HTML page or Graphviz DOT.

## Build

The project sits next to FrostyEditor and FrostyModManager and is listed in `FrostyEditor\FrostyEditor.sln`.
It builds like the other plugins (net48, x64, `Release - Final` -> `bin\Release\Final\`), and the post-build step
copies `FrostyAnimationEditor.dll` to `FrostyEditor\bin\Release\Final\Plugins\`.

- `Core\`: the container format and everything the editor does with it (no UI, no Frosty).
- `Frosty\`: menu item, reading / writing the AntStateAsset through Frosty's asset manager, the cache.
- `Windows\`: the editor windows (WPF, built in code).
