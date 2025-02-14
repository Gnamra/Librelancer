// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibreLancer.Data;
using LibreLancer.Sounds;

namespace LibreLancer.Interface
{
    public class UiData
    {
        //Data
        public string DataPath;
        public GameResourceManager ResourceManager;
        public InfocardManager Infocards;
        public FontManager Fonts;
        public FileSystem FileSystem;
        public Dictionary<string,string> NavbarIcons;
        public SoundManager Sounds;
        //Ui
        public Stylesheet Stylesheet;
        public InterfaceResources Resources;
        //TODO: Make configurable
        public INavmapIcons NavmapIcons;
        public string XInterfacePath;
        //Editor-only
        public string FlDirectory;
        public UiData()
        {
        }
        public UiData(FreelancerGame game)
        {
            ResourceManager = game.ResourceManager;
            FileSystem = game.GameData.VFS;
            Infocards = game.GameData.Ini.Infocards;
            Fonts = game.Fonts;
            NavbarIcons = game.GameData.GetBaseNavbarIcons();
            Sounds = game.Sound;
            DataPath = game.GameData.Ini.Freelancer.DataPath;
            if (game.GameData.Ini.Navmap != null)
                NavmapIcons = new IniNavmapIcons(game.GameData.Ini.Navmap);
            else
                NavmapIcons = new NavmapIcons();
            if (!string.IsNullOrWhiteSpace(game.GameData.Ini.Freelancer.XInterfacePath))
                OpenFolder(game.GameData.Ini.Freelancer.XInterfacePath);
            else
                OpenDefault();
        }

        public string GetNavbarIconPath(string icon)
        {
            string ic;
            if (!NavbarIcons.TryGetValue(icon, out ic))
            {
                FLLog.Warning("Interface", $"Could not find icon {icon}");
                ic = NavbarIcons.Values.First();
            }
            return ic;
        }
        public string GetFont(string fontName)
        {
            if (fontName[0] == '$') fontName = Fonts.ResolveNickname(fontName.Substring(1));
            return fontName;
        }

        public InterfaceColor GetColor(string color)
        {
            var clr = Resources.Colors.FirstOrDefault(x => x.Name.Equals(color, StringComparison.OrdinalIgnoreCase));
            return clr ?? new InterfaceColor() {
                Color = Parser.Color(color)
            };
        }

        Dictionary<string, Texture2D> loadedFiles = new Dictionary<string,Texture2D>();
        public Texture2D GetTextureFile(string filename)
        {
            try
            {
                var file = DataResolve(filename);
                if (!loadedFiles.ContainsKey(file))
                {
                    loadedFiles.Add(file, LibreLancer.ImageLib.Generic.FromFile(file));
                }
                return loadedFiles[file];
            }
            catch (Exception)
            {
                return null;
            }
        }

        public RigidModel GetModel(string path)
        {
            if(string.IsNullOrEmpty(path)) return null;
            try
            {
                return ((IRigidModelFile) ResourceManager.GetDrawable(DataResolve(path))).CreateRigidModel(true, ResourceManager);
            }
            catch (Exception e)
            {
                FLLog.Error("UiContext",$"{e.Message}\n{e.StackTrace}");
                return null;
            }
        }
        public void OpenFolder(string xinterfacePath)
        {
            XInterfacePath = xinterfacePath;
            OpenResources();
        }

        public void OpenDefault()
        {
            XInterfacePath = null;
            OpenResources();
        }

        void OpenResources()
        {
            Resources = InterfaceResources.FromXml(ReadAllText("resources.xml"));
            LoadLibraries();
        }

        private InterfaceTextBundle uibundle;

        void ReadBundle()
        {
            if (uibundle == null)
            {
                using (var reader =
                    new StreamReader(
                        typeof(UiContext).Assembly.GetManifestResourceStream($"LibreLancer.Interface.Default.interface.json")))
                {
                    uibundle = InterfaceTextBundle.FromJSON(reader.ReadToEnd());
                }
            }
        }

        public void SetBundle(InterfaceTextBundle tb)
        {
            uibundle = tb;
        }

        public string ReadAllText(string file)
        {
            if (uibundle == null && !string.IsNullOrEmpty(XInterfacePath))
            {
                var path = FileSystem.Resolve(Path.Combine(XInterfacePath, file));
                return File.ReadAllText(path);
            }
            else
            {
                ReadBundle();
                return uibundle.GetStringCompressed(file);
            }
        }

        public bool FileExists(string file)
        {
            if (!string.IsNullOrEmpty(XInterfacePath))
            {
                var path = FileSystem.Resolve(Path.Combine(XInterfacePath, file), false);
                return path != null;
            }
            else
            {
                ReadBundle();
                return uibundle.Exists(file);
            }
        }

        //some info is using fully-qualified paths instead of data-relative paths.
        //maybe fix? but not likely
        public string DataResolve(string file)
        {
            string path;
            if((path = FileSystem.Resolve(file, false)) != null) {
                return path;
            }
            return FileSystem.Resolve(DataPath + file);
        }

        public void LoadLibraries()
        {
            foreach (var file in Resources.LibraryFiles)
            {
                ResourceManager.LoadResourceFile(DataResolve(file));
            }
            foreach (var file in NavmapIcons.Libraries())
            {
                ResourceManager.LoadResourceFile(DataResolve(file));
            }
        }
    }
}
