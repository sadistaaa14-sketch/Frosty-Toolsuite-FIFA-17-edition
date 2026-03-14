using Frosty.Core.Attributes;
using System.Runtime.InteropServices;
using System.Windows;
using BundleRefTablePlugin.Handlers;
using BundleRefTablePlugin;

[assembly: ComVisible(false)]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly
)]

[assembly: Guid("4b612468-9b6a-4304-88a5-055c3575eb3e")]

[assembly: PluginDisplayName("BundleRefTable Plugin")]
[assembly: PluginAuthor("WiiMaster / Andrii")]
[assembly: PluginVersion("1.0.0.0")]

[assembly: RegisterCustomHandler(CustomHandlerType.Res, typeof(BundleRefTableCustomActionHandler), resType: FrostySdk.Managers.ResourceType.BundleRefTableResource)]
[assembly: RegisterOptionsExtension(typeof(BRTOptions), Frosty.Core.PluginManagerType.Editor)]
