using System;
using System.IO;
using System.Threading;
using Assimp;
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
        private SaveFileDialog saveFileDialog;
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
            Assimp.Unmanaged.AssimpLibrary.Instance.LoadLibrary(Path.Join(assemblypath, "assimp.dll"));

            //Set Context To Importer/Exporter classes
            AssimpExporter.PluginRef = this;
            AssimpImporter.PluginRef = this;
            
            AssimpContext _ctx = new();

            //Fetch Supported file formats
            string[] ImportFormats =  _ctx.GetSupportedImportFormats();
            Assimp.ExportFormatDescription[] ExportFormatDescriptions = _ctx.GetSupportedExportFormats();
            string[] ExportFormats = new string[ExportFormatDescriptions.Length];
            string[] ExportFormatExtensions = new string[ExportFormatDescriptions.Length];
            for (int i=0;i< ExportFormatDescriptions.Length; i++)
            {
                ExportFormats[i] = ExportFormatDescriptions[i].FormatId;
                ExportFormatExtensions[i] = ExportFormatDescriptions[i].FileExtension;
            }
            _ctx.Dispose();

            openFileDialog = new("assimp-open-file", string.Join('|', ImportFormats), false); //Initialize OpenFileDialog
            saveFileDialog = new("assimp-save-file", ExportFormats, ExportFormatExtensions); //Initialize OpenFolderDialog
            
            //openFileDialog.SetDialogPath(assemblypath);
            openFileDialog.SetDialogPath("G:\\Downloads\\glTF-Sample-Models-master\\2.0\\RiggedFigure\\glTF");
            //saveFileDialog.SetDialogPath("G:\\Downloads");

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

            if (saveFileDialog != null) //TODO Check if plugin loaded instead of that
            {
                if (saveFileDialog.Draw(new System.Numerics.Vector2(600, 400)))
                {
                    Export(saveFileDialog.GetSaveFilePath(), saveFileDialog.GetSelectedFormat());
                }
            }
        }

        public override void DrawExporters(SceneGraph scn)
        {
            if (ImGuiCore.MenuItem("Assimp Export", "", false, true))
            {
                saveFileDialog.Open();
            }
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

        public void Export(string filepath, string format)
        {
            try
            {
                AssimpExporter.ExportScene(EngineRef.GetActiveSceneGraph(), filepath, format);
                Log($"Active Scene was exported in {filepath}", LogVerbosityLevel.INFO);
            } catch (Exception ex)
            {
                Log(ex.StackTrace, LogVerbosityLevel.ERROR);
                Log(ex.Message, LogVerbosityLevel.ERROR);
            }
        }

        public override void Import(string filepath)
        {
            try
            {
                SceneGraphNode root = AssimpImporter.Import(filepath);
                EngineRef.ImportScene(root);
            } catch (Exception ex)
            {
                Log(ex.Message, LogVerbosityLevel.ERROR);
            }
        }

        public override void OnUnload()
        {
            
        }
    }
}
