using System;
using System.IO;
using NbCore;
using NbCore.Plugins;
using NbCore.UI.ImGui;
using ImGuiCore = ImGuiNET.ImGui;

namespace NibbleAssimpPlugin
{
    public class Plugin : PluginBase
    {
        private static readonly string PluginName = "AssimpPlugin";
        private static readonly string PluginVersion = "1.0.0";
        private static readonly string PluginDescription = "Assimp Plugin for Nibble Engine. Created by gregkwaste";
        private static readonly string PluginCreator = "gregkwaste";
        
        private OpenFileDialog openFileDialog;
        private Assimp.AssimpContext _ctx;

        public Plugin(Engine e) : base(e)
        {
            Name = PluginName;
            Version = PluginVersion;
            Description = PluginDescription;
            Creator = PluginCreator;
        }

        public override void OnLoad()
        {
            var assemblypath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

            //Load native assimp.dll
            System.Runtime.InteropServices.NativeLibrary.Load(Path.Join(assemblypath, "assimp.dll"));

            //Set Context To Importer/Exporter classes
            AssimpImporter.PluginRef = this;

            Assimp.AssimpContext _ctx = new();

            //Fetch Supported file formats
            string[] ImportFormats =  _ctx.GetSupportedImportFormats();
            Assimp.ExportFormatDescription[] ExportFormatDescriptions = _ctx.GetSupportedExportFormats();
            string[] ExportFormats = new string[ExportFormatDescriptions.Length];
            for (int i=0;i< ExportFormatDescriptions.Length; i++)
                ExportFormats[i] = ExportFormatDescriptions[i].FormatId;
            _ctx.Dispose();

            openFileDialog = new("assimp-open-file", string.Join('|', ImportFormats), false); //Initialize OpenFileDialog
            //openFileDialog.SetDialogPath(assemblypath);
            openFileDialog.SetDialogPath("C:\\Users\\Greg\\Downloads\\glTF-Sample-Models\\2.0\\RiggedFigure\\glTF");
            

            Log($"Supported Import Formats: {string.Join(' ', ImportFormats)}", LogVerbosityLevel.INFO);
            Log($"Supported Export Formats: {string.Join(' ', ExportFormats)}", LogVerbosityLevel.INFO);
            Log("Plugin Loaded", LogVerbosityLevel.INFO);
        }

        public override void Draw()
        {
            if (openFileDialog != null) //TODO Check if plugin loaded instead of that
            {
                if (openFileDialog.Draw(new System.Numerics.Vector2(600, 400)))
                {
                    Import(openFileDialog.GetSelectedFile());
                }
            }
        }

        public override void DrawExporters(SceneGraph scn)
        {
            
        }

        public override void DrawImporters()
        {
            if (ImGuiCore.MenuItem("Assimp Import", "", false, true))
            {
                openFileDialog.Open();
            }
        }

        public override void Export(string filepath)
        {
            throw new NotImplementedException();
        }

        public override void Import(string filepath)
        {
            SceneGraphNode root = AssimpImporter.Import(filepath);
            EngineRef.ImportScene(root);
        }

        public override void OnUnload()
        {
            
        }
    }
}
