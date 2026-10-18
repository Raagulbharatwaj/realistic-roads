using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ColossalFramework.UI;
using UnityEngine;

namespace RealisticRoads
{
    // The tool's icons (Icons/*.png, embedded in the DLL) as a UI atlas.
    public static class Icons
    {
        public const string Junction = "junction";
        public const string Bridge = "bridge";
        public const string Overpass = "overpass";
        public const string Underpass = "underpass";
        public const string Tunnel = "tunnel";
        public const string Plus = "plus";
        public const string Minus = "minus";
        public const string Background = "bg";
        public const string BackgroundHover = "bgHover";
        public const string BackgroundSelected = "bgSelected";
        public const string Logo = "logo";
        public const string LogoSmall = "logoSmall";
        public const string ButtonBackground = "button";
        public const string ButtonBackgroundHover = "buttonHover";
        public const string ButtonBackgroundPressed = "buttonPressed";
        public const string Build = "build";
        public const string BuildOff = "buildOff";
        public const string Undo = "undo";
        public const string UndoOff = "undoOff";
        public const string GameTool = "gameTool";
        public const string BackgroundOff = "bgOff";
        public const string PageBack = "pageBack";
        public const string PageNext = "pageNext";

        private static readonly string[] Names =
        {
            Junction, Bridge, Overpass, Underpass, Tunnel, Plus, Minus, Background, BackgroundHover, BackgroundSelected,
            Logo, LogoSmall, ButtonBackground, ButtonBackgroundHover, ButtonBackgroundPressed,
            Build, BuildOff, Undo, UndoOff, GameTool, BackgroundOff, PageBack, PageNext,
        };

        private static UITextureAtlas atlas;
        private static bool failed;
        private static readonly HashSet<string> loaded = new HashSet<string>();

        // True if the atlas exists and has every one of these sprites (a PNG
        // missing from the DLL is skipped when the atlas is made).
        public static bool Has(params string[] names)
        {
            if (Atlas == null) return false;
            foreach (var name in names) if (!loaded.Contains(name)) return false;
            return true;
        }

        // Main thread. Null if the icons could not be loaded (buttons then show text).
        public static UITextureAtlas Atlas
        {
            get
            {
                if (atlas == null && !failed) atlas = Create();
                return atlas;
            }
        }

        private static UITextureAtlas Create()
        {
            try
            {
                var assembly = typeof(Icons).Assembly;
                var textures = new List<Texture2D>();
                foreach (var name in Names)
                {
                    var texture = Load(assembly, name);
                    if (texture == null) continue;
                    textures.Add(texture);
                    loaded.Add(name);
                }

                var created = ScriptableObject.CreateInstance<UITextureAtlas>();
                created.name = "RealisticRoadsIcons";
                created.material = UnityEngine.Object.Instantiate(UIView.GetAView().defaultAtlas.material);
                created.material.mainTexture = new Texture2D(1, 1);
                created.padding = 2;
                Rect[] regions = created.texture.PackTextures(textures.ToArray(), created.padding, 1024, false);
                for (int i = 0; i < textures.Count; i++)
                {
                    created.AddSprite(new UITextureAtlas.SpriteInfo
                    {
                        name = textures[i].name,
                        texture = textures[i],
                        region = regions[i],
                    });
                }
                Mod.Log("Loaded " + textures.Count + " icons");
                return created;
            }
            catch (Exception e)
            {
                failed = true;
                Mod.LogError("Loading icons failed, using text buttons", e);
                return null;
            }
        }

        private static Texture2D Load(Assembly assembly, string name)
        {
            string path = null;
            foreach (var resource in assembly.GetManifestResourceNames())
                if (resource.EndsWith("." + name + ".png")) path = resource;
            if (path == null)
            {
                Mod.Log("Icon missing from the DLL: " + name);
                return null;
            }

            using (var stream = assembly.GetManifestResourceStream(path))
            using (var memory = new MemoryStream())
            {
                var buffer = new byte[4096];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) memory.Write(buffer, 0, read);
                var texture = new ColossalFramework.Importers.Image(memory.ToArray()).CreateTexture();
                texture.name = name;
                texture.wrapMode = TextureWrapMode.Clamp;
                return texture;
            }
        }
    }
}
