# FrostyAnimationEditor

Frosty Editor plugin for FIFA 17's AntStateAsset (`fifagame_win32_antstate`): every object of the container
(animation clips, controllers, state machines, blend spaces, expression kernels, curves, tags, ...) can be
viewed, edited, created, cloned and deleted, and the result is written back into the Frosty project.

## Use

1. In Frosty Editor, open a FIFA 17 project.
2. Tools > Animation Editor. The plugin opens the game's AntStateAsset,
   `animations/antanimations/fifa/fifagame/fifagame_win32_antstate` (the one bank with content).
3. The first run for a given content builds the editor cache (a few seconds to a minute: every object is decoded
   once). It is kept in `<Frosty>\Caches\AnimationEditor\` and used from then on. Writing to the project also
   writes the cache of the new content, so the next session starts without rebuilding.
4. Edit. "Save to project" (Ctrl+S) hands the edited container to Frosty; then save the Frosty project or
   export a mod as usual.

Window layout: classes | objects of the class (filter by name, or `0x...` key across all classes) | the selected
object. Clips are listed and searched by their readable name: every AnimationAsset is named `<16 hex digits>.chan`,
the key of the one ClipControllerAsset that plays it (13,426 of 13,426 clips), and that controller's name
(e.g. `12C0311_UCC_Violin_Gilardino`) is shown, with the `.chan` name next to it. Tabs of the object:

- Fields: the object's fields as a tree; click a value to edit it, list buttons add / insert copies / move /
  remove elements, `+ new` creates an embedded object, `choose...` picks the object a key points to. Underlined
  field names have a tooltip with what the game does with the field, and "In the game" lists it for the object's
  classes (`Core\Data\field_semantics.tsv`). Orange text marks a field the game reads but whose effect is not known
  yet; fields the game never reads are marked as such.
- References: what the object references, and (one index over the file) what references it.
- Animation (clips and ClipControllerAssets): Player view (below); export as FBX, for Blender (`.antanim.json`,
  with the add-on `io_fifa17_antanim.py`, saved from the same tab) or BVH; replace the clip or add a new clip (and
  controller) from an FBX, `.antanim.json` or BVH file; skeleton preview.
- Sequence (SequenceContainerAsset, ActorControllerAsset, RelativeIkActorControllerAsset): each actor's tracks
  as a timeline, the actor's tags under them on the same time axis (next sections).
- Chooser (ChooserControllerAsset): the policy and how it picks (random by weight, by an enumeration game state,
  by signals), when it chooses, and the entries with what they play; weights editable (with the share of the pick),
  entries reorderable.
- Layers (LayersControllerAsset): each layer's controller, weight signal, blend mask, UpdateMode and time mode
  (UpdateRates), with what each value does in the game.
- Blend mask (BlendMaskAsset / BlendMaskListAsset): a weight per slot of the mask's layout, editable; for a list, which
  mask or default value covers each layout of the hierarchy.
- Steering (SteeringControllerAsset): the subject and the three axes side by side.
- Tags (every controller): the controller's tag collections on its timeline.
- Controllers (every controller): the controllers it plays, as an expandable tree (a state machine's states, a
  state's subject, a chooser's entries, a sequence's actors and clips, ...).
- State machine: states, transitions (order = priority), conditions; add / remove / reorder / retarget; the
  lookup tables are rebuilt after each change. Run steps it offline (below).
- Blend space: the diagram (items, triangulation, hull), rebuild.
- Expression: the expression kernel as text , check, write back, and run it in
  the editor's model of the game's VM with chosen inputs; Dataflow graph shows it as boxes and links (inputs, the
  natives it calls, the DOFs it writes).
- Curves: DOF weight curves and float game-state tag curves, plotted as the game computes them, editable.

Toolbar: Open .res / Save .res (a `.res` exported from Frosty: 16-byte ResMeta + container), Undo / Redo (every
edit), New object, Check (count fields, two-way links, state machine tables, engine limits, dangling keys,
loading memory). Edit JSON shows the whole object as JSON text. Graph shows the reference graph (or the
state machine) as boxes and arrows; it can be saved as an HTML page or Graphviz DOT.

## Sequences

The Sequence tab shows an actor's tracks: a bar per entry from its begin (StartTime + StartInTime) to its end
(StartTime + EndInTime), the blend-in at the start of each later entry, the length and loop point. Drag a bar to move
the entry, an edge to trim it; the inspector edits the timing fields, the mirror flag, the blend (time, curve, or an
own copy of a shared blend), and adds / removes / reorders entries and tracks. Player view… plays the sequence:
every actor of a container side by side, placed by its DefaultInitParam; one actor with the meshes. Clicking or
dragging on the ruler shows that time there.

How the game plays a sequence:

- an entry runs its controller at (time - StartTime) x Scale from StartInTime; the next entry blends in over its
  blend's BlendTime from its begin, with the blend's curve (0 cosine, 1 S-curve, 2 1 - sine, 3 linear). 82,948 of
  the 83,063 overlapping entries in the game overlap by exactly the blend time;
- a ClipController shows clip frame = its ticks x TimeScale + the clip's TrimOffset and lasts
  ceil(TrimmedDuration / TimeScale) ticks;
- tracks are merged, later tracks over earlier ones for the channels they have (the hand and face tracks);
- entry Flags bit 0 mirrors the entry (XOR with the actor's mirror flag).

The preview does not mirror entries or sync blend phases (the status line says when a sequence has them), and it
assumes 60 ticks a second (FPS 30 x TimeScale 0.5 of the game's clip controllers); the game's own tick rate is not
known for sure. RelativeIkActorControllerAssets (an actor plus per-frame relative IK targets on another
animatable, FIFA 17's multi-character contact data) open the actor they wrap and list their entries.

To put another celebration where one plays, point the object that plays the sequence at the other
SequenceContainerAsset rather than replacing the clip: the violin's two `UCCDistillAsset` objects
`Node_END_Violinist_distill` (0xc58a213984338dc1, 0x1eb709215b1985a0) name the container in ControllerAsset; the
Drogba celebration is `UCC_END_Drogba_Knee_Slide_Salute` (0xed30c21d4e29e378). Not yet tested in the game.

## Tags

The Tags tab (and the Sequence tab under the tracks) shows a controller's tags: ControllerAsset.TagCollectionSet ->
the set's collections (a row each) -> their tags (a bar from the start over the length; a diamond when the length is
0). Drag to move, drag the right edge to change the length; the inspector edits start, length, LinkFlags, copies,
reorders and removes; Add tag… copies the first tag of the chosen class in the file (a new tag starts as a copy
of one already in the file), Add collection… / Add a tag collection set create them. A DefaultTagCollectionAsset's
MonitoredTags list is rebuilt after each edit (the MonitoredTag tags of TagKeys, in order, as in 18,337 of the
18,354 shipped collections). ×N marks a tag listed by N collections; Own copy makes one only this collection uses.

How the game reads a tag's window: start = StartLinkOffset (0 when LinkFlags has 0x80), length = EndLinkOffset
(a length, not an end time), both fractions of the owner's length when LinkFlags has 0x40; times are the owner's
ticks (a sequence's for an ActorControllerAsset, the controller's for a ClipControllerAsset). What LinkFlags 0x10 / 0x20
and StartLinkUpdates change is not known.

## Databases

A context database is the table the game picks an animation from. The Database tab shows one: the columns with their
kind, unit, range and value names, then the rows, each with its cells and the asset(s) it plays. It appears on every
ContextDatabaseAsset and on the classes that have one as a base or a field (StepClipAsset, ContinuousContactAsset,
PriorityDatabaseAsset and the controllers that hold one).

Where the names come from: a column is either a RegistryFieldAsset, which names one of the engine's 462 built-in
context fields by ClassGuid + FieldId, or a FieldDescriptionAsset, a custom column driven by a game-state value.
Core/Data/context_fields.tsv lists them: the field's name, type, range, unit and - for enum and bitmap fields - its
value names. Core/Data/context_interfaces.tsv is the list of the 69 groups the ClassGuid picks (DribbleDatabaseQuery, TurnDatabaseQuery, WarpDatabaseQuery and so on).
All 4,633 columns of the 1,339 context databases in FIFA 17's file resolve in that registry.

The Fields tab of any object has a "Picked by…" button: it indexes the file's references once, then lists the database
rows that select that object, with the conditions that narrow each row (columns that match anything are left out). For
a celebration (a SequenceContainerAsset) that answers "when does the game play this?" - 601 of the file's 4,002
sequence containers are picked by a database row.

How a cell is read: a bool column takes Int0 as 1 true / 0 false / 2 any, an enum or int column as
the value with -1 meaning any, a bitmap column as the set of bits; a float column takes Float0 and Float1 as a range,
an angle column takes Float0 as the angle and Float1 as the tolerance either side of it. The game stores float and angle
cells at reduced precision (255 steps of the column's own range), so finer edits are rounded when the game loads them.

The Curves tab also shows a PiecewiseLinearAsset (8,598 of them in the file, used for reach curves and the like):
its points plotted with their titles, and the authored range and Slope below. Only PointValues reaches the game -
GraphTitle, XTitle, YTitle, XMin, XMax, YMin, YMax and Slope are not read by the game, and the points are not held
to the authored range. The game holds eight points, so a ninth is flagged as unsafe.

## Running a state machine

The State machine tab's Run section plays the machine step by step with the game's transition rules: in each update the node's FlattenedTransitions are tried as a tree; an entry needs its true
conditions on and its false ones off (the conditions are check boxes, starting at BoolAsset.Default /
SignalAsset.DefaultBool); its BranchTypeIndex says when it can fire: 0 at once, 1 at the end of the state's
controller minus the transition's BranchAtEndOffset, 2 after the state's first update, 3 and up inside a
BranchOutPointTag window of that branch type on the controller's timeline; the entry that fires earliest wins, the
earlier one in the table on a tie. The log lists each transition with its time and why it fired; the strip shows the
states over time.

What the run cannot know: a state whose controller is a chooser, a context database wrapper or locomotion picks what it
plays at run time, so its length and branch windows are unknown; "choose what it plays…" sets them from a sequence or
clip (the ones found under the state's controller are offered). Gate nodes are open unless closed with their check
boxes (the game asks the gate's chooser). Not modelled: branch tuning, nested state machines, branch-in windows and
destination phase (a state starts at 0), StateOps, conditions that change by themselves.

## Player view

A separate window (Animation tab > Player view…) that plays the clip on the player's render skeleton, the EBX
SkeletonAsset `content/character/rig/skeleton/player/skeleton_player` (438 bones), with character meshes of the
project (SkinnedMeshAssets under `content/character/`, chosen with Meshes…; the choice and the level of detail are
kept in `<Frosty>\Caches\AnimationEditor\player_parts.txt`). Meshes are skinned on the CPU (4-8 weights per
vertex, bone indices = skeleton bones, as MeshSetPlugin reads FIFA 17 meshes) and drawn in plain colours.
Playback: play / pause (Space), frame slider (arrow keys step), speed, loop, follow the player, in place (the
trajectory's movement on the ground removed), mesh / bones. Double-click the status line for the details.

How a clip poses that skeleton (checked against the data):

- `player_rig_17_rendering`'s skeleton has the same 438 joints, order and parents as the EBX skeleton; the gameplay
  and story rigs use the same joint names (the gameplay rig's `Left/RightAnkleEffectorAux` are added as extra bones).
- Rig translations are feet: EBX LocalPose translation = rig default / 3.281 (max error 2.9e-7 m over 437 joints).
- Clip channels are absolute local transforms, so animated joints take the clip's values, the others the rig
  defaults (the clip's rig, then `player_rig_17_rendering`). Joint rotations match the EBX bind pose except Hips (the
  gameplay rigs face +X, the EBX +Z), Trajectory, TrajectoryEnd, Prop and 32 shoulder / arm / hand / finger joints,
  whose rig default is another rest pose than the mesh bind pose.
- The violin clip on the EBX skeleton puts every joint within 2.9e-7 m of the rig-space evaluation.

Clips whose joints are mostly not in the player skeleton (ball, camera) are shown on a skeleton built from their rig.

## FBX

Export (Animation tab, or the Player view with the meshes): binary FBX 7.4, Y up, right-handed, 1 unit = 1 m,
the skeleton with its bind pose (BindPose, and cluster links when meshes are included), one key per frame
(linear) on every bone the clip poses, Euler XYZ in degrees. Import (Replace / Add with an `.fbx`): binary (7.x,
32- and 64-bit) or ASCII FBX 7; any axis system and unit scale (also a scale on a parent node such as Blender's
armature object); Lcl transforms, rotation / scaling pivots and offsets, Pre/PostRotation and rotation order (both
only when RotationActive is set, as the FBX SDK and Blender read them); constant and linear keys, cubic keys with
their stored slopes (weighted tangents are read as unweighted); bones matched by name (namespaces such as
`mixamorig:` or `Armature|` are ignored); when a file has several takes the editor asks which. The foot channels
are recomputed from the new motion (next section); other joints the file does not have and the other float channels
keep the clip's values. The joint axes must be the game's (a file exported from the editor and edited keeps them;
animation from another skeleton needs retargeting first).

Checked with Blender 5.2: an exported clip imported into Blender puts all 438 bones within 1.6e-6 m of the
editor's pose over the whole clip, skinned meshes deform within 2.2e-6 m of the editor's skinning; files with
Pre/PostRotation (FBX 7.5), ZXY rotation order or pivots are read by Blender and by the editor to the same bone
positions (within 2.2e-6 m); Blender's re-export (Y up, Z up, with leaf bones, and an ASCII copy of it) imports back
within 0.0002° and 3e-6 ft of the original channels. Blender's FBX export simplifies curves by default (Animation > Simplify 1.0, up to 0.6° here): set it to 0
to keep every key.

## Replacing a clip's motion

**Foot channels.** The player clips carry channels that are derived from the motion (checked on 1,147 shipped
clips at their keys, rig units = feet):

- `Left/RightAnkleEffectorAux.t` = the Left/RightFoot joint's position relative to AITrajectory (the foot IK
  targets; largest difference per clip 0.024 ft median, 0.087 ft at the 99th percentile);
- `<joint>.height` = the joint's height (0.006 ft median, 0.070 ft at the 99th percentile);
- `<joint>.vel` = the distance the joint moves to the next frame (mean difference per clip 0.019 median; single
  keys of short gameplay clips differ by up to a few feet, cause not known).

FBX and BVH files have no such channels, so an import used to keep the replaced clip's values. With the Drogba
celebration imported into the violin one, that left the IK targets up to 2.3 ft away from Drogba's feet (and the
height / speed channels up to 1.3 off), and the game's leg IK pulls the ankles toward those targets. They are now
recomputed from the imported joints: within 0.09 ft of Drogba's own targets and 0.15 of his height / speed values.
An `.antanim.json` carries every channel and is stored as it is.

**Encoding.** The VBR encoder follows the game's compressor: per 8-key block it picks the coarsest quantiser row whose
error stays within the tolerance, a fraction of each value group's range (the shipped clips: 0.00105). Each encode
adds that error again, so the dialog's default is 0.00025. The shipped clips also store keys only where the motion
needs them (the Drogba clip: 37 keys for 118 frames) and the game interpolates the frames in between (nlerp for
rotations, lerp for the rest). The rule EA's pipeline used to pick those keys is not in the game (its compressor takes
the key times from the source `.chan`); the editor drops every key the interpolation reproduces within the same
tolerance (every frame is checked after encoding, and the report gives the error over every frame).

Drogba exported as FBX and imported into the violin clip, compared with the Drogba clip:

| setting | data | keys | worst joint position | worst joint rotation |
|---|---|---|---|---|
| shipped Drogba clip | 3,112 bytes | 37 | | |
| 0.00105, every frame a key | 8,156 bytes | 118 | 10.2 mm | 0.30° |
| 0.00105, keys where needed | 5,980 bytes | 65 | 9.8 mm | 0.31° |
| 0.00025, every frame a key | 12,011 bytes | 118 | 2.5 mm | 0.075° |
| 0.00025, keys where needed (default) | 9,492 bytes | 77 | 2.5 mm | 0.083° |

The recomputed foot channels follow the motion more closely than linear interpolation does, so they keep more keys
than the shipped clip has. Re-encoding 300 random shipped clips from their decoded frames: 0.74x the shipped data
size at 0.00105 with every frame a key, 0.95x at the default, no failures. A BVH round trip now matches the FBX one
(the BVH export turned the decoder's non-unit quaternions into Euler angles without normalising them: up to 1.47°
off, now 0.08°).

**Trim window.** Replace keeps the clip's TrimOffset and the hold after its last frame (TrimmedDuration -
(EndFrame - TrimOffset)) on top of the new length; a clip of the same length keeps both values. It used to reset them
to 0 and EndFrame, which moved every frame the clip's controllers and sequence entries address (the violin clip has
TrimOffset -13: its sequence would have played 13 frames late). The import notes list the sequence entries that play
part of the clip and the frames they use.

Not changed: a ClipControllerAsset's `Distance` (violin 31.74, Drogba 18.53); what the game uses it for is not
known.

## Build

The project sits next to FrostyEditor and FrostyModManager and is listed in `FrostyEditor\FrostyEditor.sln`.
It builds like the other plugins (net48, x64, `Release - Final` -> `bin\Release\Final\`), and the post-build step
copies `FrostyAnimationEditor.dll` to `FrostyEditor\bin\Release\Final\Plugins\`.

The project references MeshSetPlugin (mesh sets, for the player view) like DuplicationPlugin does.

- `Core\`: the container format and everything the editor does with it (no UI, no Frosty); `Core\Anim\` the
  animation codecs, the player skeleton mapping and CPU skinning; `Core\Fbx\` the FBX reader / writer.
- `Frosty\`: menu item, reading / writing the AntStateAsset through Frosty's asset manager, the cache, the player
  skeleton and meshes.
- `Windows\`: the editor windows (WPF, built in code), the player view.
