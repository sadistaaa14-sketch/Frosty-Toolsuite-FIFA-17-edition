using System.Runtime.InteropServices;
using Frosty.Core.Attributes;
using FrostyAnimationEditor.Plugin;
using FrostySdk;

// Setting ComVisible to false makes the types in this assembly not visible to COM components.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("6f1c2b7e-3a54-4e8b-9c21-5d7a0e4f9b13")]

[assembly: PluginDisplayName("FIFA 17 Animation Editor")]
[assembly: PluginAuthor("FIFA 17 Frosty edition")]
[assembly: PluginVersion("1.0.0.0")]

// the antstate container format is FIFA 17's
[assembly: PluginValidForProfile((int)ProfileVersion.Fifa17)]

// Tools > Animation Editor
[assembly: RegisterMenuExtension(typeof(AnimationEditorMenuExtension))]
