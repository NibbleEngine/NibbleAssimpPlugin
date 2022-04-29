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
            
            //Generate Assimp Context
            _ctx = new();
            
            //Set Context To Importer/Exporter classes
            AssimpImporter._ctx = _ctx;
            AssimpImporter.PluginRef = this;
            
            //Fetch Supported file formats
            string[] ImportFormats =  _ctx.GetSupportedImportFormats();
                
            openFileDialog = new("assimp-open-file", string.Join('|', ImportFormats), false); //Initialize OpenFileDialog
            openFileDialog.SetDialogPath(assemblypath);

            Log("Loaded Assimp Plugin", LogVerbosityLevel.INFO);
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
            throw new NotImplementedException();
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
            foreach (SceneGraphNode child in root.Children)
                EngineRef.ImportScene(child);
            root.Dispose(); //Dispose local root
        }

        public override void OnUnload()
        {
            //Dispose Assimp Context
            _ctx.Dispose();
        }
    }
}
