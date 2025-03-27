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
using System.Threading.Tasks;

namespace SVGImporter;

public class SVGImporter : ResoniteMod
{
    public override string Name => "SVGImporter";
    public override string Author => "dfgHiatus";
    public override string Version => "2.0.1";
    public override string Link => "https://github.com/dfgHiatus/ResoniteSVGImporter/";
    public static ModConfiguration Config;
    private static readonly string TrueCachePath = Path.Combine(Engine.Current.CachePath, "Cache");
    private const string SVG_FILE_EXTENSION = "svg";

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> Enabled =
        new("enabled", "Enabled", () => true);

    [AutoRegisterConfigKey]
    internal static readonly ModConfigurationKey<bool> SkipImportDialogue =
        new("skipImportDialogue", "Skip Import Dialogue", () => false);

    public override void OnEngineInit()
    {
        new Harmony("net.dfgHiatus.SVGImporter").PatchAll();
        Config = GetConfiguration();
        Engine.Current.OnReady += AssetPatch;
    }

    public static void AssetPatch()
    {
        // Revised implementation using reflection to handle API changes
        // Resonite changed their API AGAIN without any documentation this will probably break in 3 months...
        try
        {
            Debug("Attempting to add SVG support to import system");

            // Get ImportExtension type via reflection since it's now a struct inside AssetHelper
            var assHelperType = typeof(AssetHelper);
            var importExtType = assHelperType.GetNestedType("ImportExtension",
                System.Reflection.BindingFlags.NonPublic);

            if (importExtType == null)
            {
                Error("ImportExtension type not found. This mod is toast.");
                return;
            }

            // Create an ImportExtension instance with reflection
            // Constructor args: (string ext, bool autoImport)
            var importExt = System.Activator.CreateInstance(importExtType,
                new object[] { SVG_FILE_EXTENSION, true });

            // Get the associatedExtensions field via reflection
            var extensionsField = assHelperType.GetField("associatedExtensions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            if (extensionsField == null)
            {
                Error("Could not find associatedExtensions field");
                return;
            }

            // Get the dictionary and add our extension to the Special asset class
            var extensions = extensionsField.GetValue(null);
            var dictType = extensions.GetType();
            var specialValue = dictType.GetMethod("get_Item").Invoke(extensions, new object[] { AssetClass.Special });

            if (specialValue == null)
            {
                Error("Couldn't get Special asset class list");
                return;
            }

            // Add our ImportExtension to the list
            specialValue.GetType().GetMethod("Add").Invoke(specialValue, new[] { importExt });

            Debug("SVG import extension added successfully");
        }
        catch (System.Exception ex)
        {
            Error($"Failed to add SVG to special import formats: {ex}");
        }
    }

    [HarmonyPatch(typeof(UniversalImporter), "Import", typeof(AssetClass), typeof(IEnumerable<string>),
    typeof(World), typeof(float3), typeof(floatQ), typeof(bool))]
    public class UniversalImporterPatch
    {
        public static bool Prefix(ref IEnumerable<string> files,
            World world, float3 position)
        {
            if (!Config.GetValue(Enabled)) return true;

            if (!BlenderInterface.IsAvailable)
            {
                NotificationMessage.SpawnTextMessage(
                    "Blender was not installed or detected.\nSVG Importer will not run",
                    colorX.Red);
                return true;
            }

            List<string> notSVG = new();
            int index = 0;
            const int RowSize = 10;
            foreach (var file in files)
            {
                var fileName = Path.GetFileName(file);
                if (!fileName.ToLower().EndsWith(SVG_FILE_EXTENSION))
                {
                    notSVG.Add(file);
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
                    ProgressBarInterface pbi = await root.World.
                        AddSlot("Import Indicator").
                        SpawnEntity<ProgressBarInterface, LegacySegmentCircleProgress>
                            (FavoriteEntity.ProgressBar);
                    pbi.Slot.PositionInFrontOfUser();
                    pbi.Initialize(canBeHidden: true);
                    pbi.UpdateProgress(0.0f, "Got SVG! Starting conversion...", string.Empty);

                    await default(ToBackground);
                    await SVGToGLB(blenderInput, blenderOutput, pbi);
                    await default(ToWorld);

                    pbi.ProgressDone("Conversion complete!");
                    pbi.Slot.RunInSeconds(2.5f, delegate
                    {
                        pbi.Slot.Destroy();
                    });

                    UniversalImporter.Import(
                        AssetClass.Model,
                        new[] { blenderOutput },
                        world,
                        position + UniversalImporter.GridOffset(ref index, RowSize),
                        floatQ.Identity,
                        Config.GetValue(SkipImportDialogue));
                });
            }

            if (notSVG.Count <= 0) return false;
            files = notSVG.ToArray();
            return true;
        }

        private static bool ContainsUnicodeCharacter(string input)
        {
            const int MaxAnsiCode = 255;
            return input.Any(c => c > MaxAnsiCode);
        }

        private static async Task SVGToGLB(string input, string output, IProgressIndicator pbi)
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
            var blenderArgs = string.Format("-b -P \"{0}\"", tempBlenderScript);
            blenderArgs = "--disable-autoexec " + blenderArgs;

            var process = new Process();
            process.StartInfo.FileName = BlenderInterface.Executable;
            process.StartInfo.Arguments = blenderArgs;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            process.OutputDataReceived += (object sender, DataReceivedEventArgs e) =>
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