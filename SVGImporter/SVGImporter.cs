using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;
using SkyFrost.Base;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SVGImporter;

public class SvgImporter : ResoniteMod
{
    public override string Name => "SVGImporter";
    public override string Author => "dfgHiatus";
    public override string Version => "2.0.2";
    public override string Link => "https://github.com/dfgHiatus/ResoniteSVGImporter/";
    
    private static ModConfiguration _config;
    private static readonly string TrueCachePath = Path.Combine(Engine.Current.CachePath, "Cache");
    private const string SvgFileExtension = "svg";

    // Reflection members for AssetHelper patching
    private static readonly System.Type AssetHelperType = typeof(AssetHelper);
    private static readonly System.Type ImportExtensionType = 
        AssetHelperType.GetNestedType("ImportExtension", BindingFlags.NonPublic);
    private static readonly FieldInfo AssociatedExtensionsField = 
        AssetHelperType.GetField("associatedExtensions", BindingFlags.NonPublic | BindingFlags.Static);

    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<bool> Enabled =
        new("enabled", "Enabled", () => true);

    [AutoRegisterConfigKey] private static readonly ModConfigurationKey<bool> SkipImportDialogue =
        new("skipImportDialogue", "Skip Import Dialogue", () => false);

    public override void OnEngineInit()
    {
        new Harmony("net.dfgHiatus.SVGImporter").PatchAll();
        _config = GetConfiguration();
        
        Engine.Current.OnReady += () =>
        {
            try
            {
                Debug("Attempting to add SVG support to import system");

                if (ImportExtensionType == null)
                {
                    Error("ImportExtension type not found. This mod is toast.");
                    return;
                }

                if (AssociatedExtensionsField == null)
                {
                    Error("Could not find associatedExtensions field");
                    return;
                }

                // Create an ImportExtension instance with reflection
                // Constructor args: (string ext, bool autoImport)
                var importExt = System.Activator.CreateInstance(ImportExtensionType, SvgFileExtension, true);

                // Get the dictionary - it's Dictionary<AssetClass, List<ImportExtension>>
                var extensions = AssociatedExtensionsField.GetValue(null);
                
                // Use reflection to get the Special asset class list
                var dictType = extensions.GetType();
                var getItemMethod = dictType.GetMethod("get_Item");
                var specialList = getItemMethod!.Invoke(extensions, [AssetClass.Special]);

                if (specialList == null)
                {
                    Error("Couldn't get Special asset class list");
                    return;
                }

                // Add our ImportExtension to the list using reflection
                var addMethod = specialList.GetType().GetMethod("Add");
                addMethod!.Invoke(specialList, [importExt]);

                Debug("SVG import extension added successfully");
            }
            catch (System.Exception ex)
            {
                Error($"Failed to add SVG to special import formats: {ex}");
            }
        };
    }

    [HarmonyPatch(typeof(UniversalImporter), "Import", typeof(AssetClass), typeof(IEnumerable<string>),
    typeof(World), typeof(float3), typeof(floatQ), typeof(bool))]
    public class UniversalImporterPatch
    {
        public static bool Prefix(ref IEnumerable<string> files,
            World world, float3 position)
        {
            if (!_config.GetValue(Enabled)) return true;

            if (!BlenderInterface.IsAvailable)
            {
                NotificationMessage.SpawnTextMessage(
                    "Blender was not installed or detected.\nSVG Importer will not run",
                    colorX.Red);
                return true;
            }

            List<string> notSvg = [];
            var index = 0;
            const int rowSize = 10;
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.ToLower().EndsWith(SvgFileExtension))
                {
                    notSvg.Add(file);
                    continue;
                }

                if (ContainsUnicodeCharacter(file))
                {
                    Warn("Imported SVG cannot have unicode characters in its file name.");
                    continue;
                }

                var blenderInput = file.Replace("\\", "/");
                var blenderOutput = Path.Combine(TrueCachePath, $"{fileName}.glb").Replace("\\", "/");

                var root = Engine.Current.WorldManager.FocusedWorld.RootSlot;
                root.StartGlobalTask(async delegate
                {
                    var pbi = await root.World.
                        AddSlot("Import Indicator").
                        SpawnEntity<ProgressBarInterface, LegacySegmentCircleProgress>
                            (FavoriteEntity.ProgressBar);
                    pbi.Slot.PositionInFrontOfUser();
                    pbi.Initialize(canBeHidden: true);
                    pbi.UpdateProgress(0.0f, "Got SVG! Starting conversion...", string.Empty);

                    await default(ToBackground);
                    await SvgToGlb(blenderInput, blenderOutput, pbi);
                    await default(ToWorld);

                    pbi.ProgressDone("Conversion complete!");
                    pbi.Slot.RunInSeconds(2.5f, delegate
                    {
                        pbi.Slot.Destroy();
                    });

                    UniversalImporter.Import(
                        AssetClass.Model,
                        [blenderOutput],
                        world,
                        position + UniversalImporter.GridOffset(ref index, rowSize),
                        floatQ.Identity,
                        _config.GetValue(SkipImportDialogue));
                });
            }

            if (notSvg.Count <= 0) return false;
            files = notSvg.ToArray();
            return true;
        }

        private static bool ContainsUnicodeCharacter(string input)
        {
            const int maxAnsiCode = 255;
            return input.Any(c => c > maxAnsiCode);
        }

        private static async Task SvgToGlb(string input, string output, IProgressIndicator pbi)
        {
            // Deleting the default cube is necessary for this script to function
            // Last tested against Blender 3.5.0
            await RunBlenderScript(@$"import bpy
bpy.ops.import_curve.svg(filepath = '{input}')
objs = bpy.data.objects
try:    
    objs.remove(objs['Cube'], do_unlink = True)
except:
    pass
for obj in bpy.data.objects:
    if type(obj.data).__name__ == 'Curve':
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.convert(target = 'MESH')
        obj.select_set(False)
bpy.ops.export_scene.gltf(filepath = '{output}')", pbi);
        }

        private static async Task RunBlenderScript(string script, IProgressIndicator pbi)
        {
            var tempBlenderScript = Path.Combine(Path.GetTempPath(), Path.GetTempFileName() + ".py");
            File.WriteAllText(tempBlenderScript, script);
            var blenderArgs = $"-b -P \"{tempBlenderScript}\"";
            blenderArgs = "--disable-autoexec " + blenderArgs;

            var process = new Process();
            process.StartInfo.FileName = BlenderInterface.Executable;
            process.StartInfo.Arguments = blenderArgs;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.OutputDataReceived += (_, e) =>
            {
                pbi.UpdateProgress(0.0f, e.Data, string.Empty);
                Msg(e.Data);
            };
            process.Start();
            process.BeginOutputReadLine();
            await process.WaitForExitAsync();

            File.Delete(tempBlenderScript);
        }
    }
}