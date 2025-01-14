﻿// MIT License - Copyright (c) Callum McGing
// This file is subject to the terms and conditions defined in
// LICENSE, which is part of this source code package

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using LibreLancer.Utf.Vms;
using LibreLancer.Utf.Mat;
using LibreLancer.Utf.Cmp;
using LibreLancer.Utf.Dfm;
using LibreLancer.Vertices;
using LibreLancer.Primitives;
using LibreLancer.Fx;
using LibreLancer.Render;
using LibreLancer.Sur;

namespace LibreLancer
{
    //TODO: Allow for disposing and all that Jazz
    public abstract class ResourceManager
    {
        Dictionary<string, SurFile> surs = new Dictionary<string, SurFile>(StringComparer.OrdinalIgnoreCase);

        public abstract VertexResource AllocateVertices<T>(T[] vertices, ushort[] indices)
            where T : struct, IVertexType;
        public abstract QuadSphere GetQuadSphere(int slices);
        public abstract OpenCylinder GetOpenCylinder(int slices);
        public abstract Dictionary<string, Texture> TextureDictionary { get; }
        public abstract Dictionary<uint, Material> MaterialDictionary { get; }
        public abstract Dictionary<string, TexFrameAnimation> AnimationDictionary { get; }
        public Material DefaultMaterial;
        public Texture2D NullTexture;
        public Texture2D WhiteTexture;
        public Texture2D GreyTexture;
        public const string NullTextureName = "$$LIBRELANCER.Null";
        public const string WhiteTextureName = "$$LIBRELANCER.White";
        public const string GreyTextureName = "$$LIBRELANCER.Grey";
        public abstract Texture FindTexture(string name);
        public abstract Material FindMaterial(uint materialId);
        public abstract VMeshResource FindMesh(uint vMeshLibId);
        public abstract VMeshData FindMeshData(uint vMeshLibId);
        public abstract IDrawable GetDrawable(string filename, MeshLoadMode loadMode = MeshLoadMode.GPU);
        public abstract void LoadResourceFile(string filename, MeshLoadMode loadMode = MeshLoadMode.GPU);
        public abstract Fx.ParticleLibrary GetParticleLibrary(string filename);

        public abstract bool TryGetShape(string name, out TextureShape shape);
        public abstract bool TryGetFrameAnimation(string name, out TexFrameAnimation anim);

        public SurFile GetSur(string filename)
        {
            SurFile sur;
            if (!surs.TryGetValue(filename, out sur))
            {
                using (var stream = File.OpenRead(filename))
                {
                    sur = SurFile.Read(stream);
                }
                surs.Add(filename, sur);
            }
            return sur;
        }
    }

    public class ServerResourceManager : ResourceManager
    {
        Dictionary<string, IDrawable> drawables = new Dictionary<string, IDrawable>(StringComparer.OrdinalIgnoreCase);
        public override Dictionary<string, Texture> TextureDictionary => throw new InvalidOperationException();
        public override Dictionary<uint, Material> MaterialDictionary => throw new InvalidOperationException();
        public override Dictionary<string, TexFrameAnimation> AnimationDictionary => throw new InvalidOperationException();

        public override VertexResource AllocateVertices<T>(T[] vertices, ushort[] indices)
        {
            throw new InvalidOperationException();
        }

        public override OpenCylinder GetOpenCylinder(int slices) => throw new InvalidOperationException();
        public override ParticleLibrary GetParticleLibrary(string filename) => throw new InvalidOperationException();
        public override QuadSphere GetQuadSphere(int slices) => throw new InvalidOperationException();
        public override Material FindMaterial(uint materialId) => throw new InvalidOperationException();
        public override VMeshResource FindMesh(uint vMeshLibId) => throw new InvalidOperationException();
        public override VMeshData FindMeshData(uint vMeshLibId) => throw new InvalidOperationException();
        public override Texture FindTexture(string name) => throw new InvalidOperationException();

        public override bool TryGetShape(string name, out TextureShape shape) => throw new InvalidOperationException();
        public override bool TryGetFrameAnimation(string name, out TexFrameAnimation anim) => throw new InvalidOperationException();

        public override IDrawable GetDrawable(string filename, MeshLoadMode loadMode = MeshLoadMode.GPU)
        {
            IDrawable drawable;
            if (!drawables.TryGetValue(filename, out drawable))
            {
                drawable = Utf.UtfLoader.LoadDrawable(filename, this);
                drawable?.ClearResources();
                drawables.Add(filename, drawable);
            }
            return drawable;
        }

        public override void LoadResourceFile(string filename, MeshLoadMode loadMode = MeshLoadMode.GPU) { }
    }
    public class GameResourceManager : ResourceManager, IDisposable
	{
		public IGLWindow GLWindow;
        public long EstimatedTextureMemory { get; private set; }

		Dictionary<uint, VMeshResource> meshes = new();
        Dictionary<uint, VMeshData> meshDatas = new();
        Dictionary<uint, string> meshFiles = new();
		Dictionary<uint, Material> materials = new();
		Dictionary<uint, string> materialfiles = new();
		Dictionary<string, Texture> textures = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> texturefiles = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, IDrawable> drawables = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, TextureShape> shapes = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, TexFrameAnimation> frameanims = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ParticleLibrary> particlelibs = new(StringComparer.OrdinalIgnoreCase);

		List<string> loadedResFiles = new List<string>();
		List<string> preloadFiles = new List<string>();

        Dictionary<int, QuadSphere> quadSpheres = new Dictionary<int, QuadSphere>();
        Dictionary<int, OpenCylinder> cylinders = new Dictionary<int, OpenCylinder>();

        private VertexResourceAllocator vertexResourceAllocator = new VertexResourceAllocator();

        public override VertexResource AllocateVertices<T>(T[] vertices, ushort[] indices)
        {
            if (!GLWindow.IsUiThread()) throw new InvalidOperationException();
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            return vertexResourceAllocator.Allocate(vertices, indices);
        }

        public override QuadSphere GetQuadSphere(int slices) {
            QuadSphere sph;
            if(!quadSpheres.TryGetValue(slices, out sph)) {
                sph = new QuadSphere(slices);
                quadSpheres.Add(slices, sph);
            }
            return sph;
        }

        public override OpenCylinder GetOpenCylinder(int slices)
        {
            OpenCylinder cyl;
            if (!cylinders.TryGetValue(slices, out cyl))
            {
                cyl = new OpenCylinder(slices);
                cylinders.Add(slices, cyl);
            }
            return cyl;
        }
        public override Dictionary<string, Texture> TextureDictionary
		{
			get
			{
				return textures;
			}
		}
		public override Dictionary<uint, Material> MaterialDictionary
		{
			get
			{
				return materials;
			}
		}
        public override Dictionary<string, TexFrameAnimation> AnimationDictionary => frameanims;

        public GameResourceManager(IGLWindow g) : this()
		{
			GLWindow = g;
			DefaultMaterial = new Material(this);
			DefaultMaterial.Name = "$LL_DefaultMaterialName";
            DefaultMaterial.Initialize(this);
		}

        public GameResourceManager(GameResourceManager src) : this(src.GLWindow)
        {
            texturefiles = new Dictionary<string, string>(src.texturefiles, StringComparer.OrdinalIgnoreCase);
            shapes = new Dictionary<string, TextureShape>(src.shapes, StringComparer.OrdinalIgnoreCase);
            materialfiles = new Dictionary<uint, string>(src.materialfiles);
            foreach (var mat in src.materials.Keys)
                materials[mat] = null;
            foreach (var tex in src.textures.Keys)
                textures[tex] = null;
        }

        public GameResourceManager()
		{
			NullTexture = new Texture2D(1, 1, false, SurfaceFormat.Color);
			NullTexture.SetData(new byte[] { 0xFF, 0xFF, 0xFF, 0x0 });

			WhiteTexture = new Texture2D(1, 1, false, SurfaceFormat.Color);
			WhiteTexture.SetData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

            GreyTexture = new Texture2D(1,1, false, SurfaceFormat.Color);
            GreyTexture.SetData(new byte[] { 128, 128, 128, 0xFF});
		}

		public void Preload()
		{
			foreach (var file in preloadFiles)
			{
				LoadResourceFile(file);
			}
			preloadFiles = null;
		}

		public Cursor GetCursor(string name)
		{
			return cursors[name];
		}

		public void AddCursor(Cursor c, string name)
		{
			c.Resources = this;
			cursors.Add(name, c);
		}

		public void AddShape(string name, TextureShape shape)
		{
			shapes.Add(name, shape);
		}
		public override bool TryGetShape(string name, out TextureShape shape)
		{
			return shapes.TryGetValue(name, out shape);
		}

		public override bool TryGetFrameAnimation(string name, out TexFrameAnimation anim)
		{
			return frameanims.TryGetValue(name, out anim);
		}

		public void AddPreload(IEnumerable<string> files)
		{
			preloadFiles.AddRange(files);
		}

		public bool TextureExists(string name)
		{
			return texturefiles.ContainsKey(name) || name == NullTextureName || name == WhiteTextureName;
		}

		public void AddTexture(string name,string filename)
		{
			var dat = ImageLib.Generic.FromFile(filename);
			textures.Add(name, dat);
			texturefiles.Add(name, filename);
            EstimatedTextureMemory += dat.EstimatedTextureMemory;
        }

		public void ClearTextures()
		{
            loadedResFiles = new List<string>();
			var keys = new string[textures.Count];
			textures.Keys.CopyTo(keys, 0);
			foreach (var k in keys)
			{
				if (textures[k] != null)
				{
					textures[k].Dispose();
					textures[k] = null;
				}
			}

            EstimatedTextureMemory = 0;
        }

        public void ClearMeshes()
        {
            loadedResFiles = new List<string>();
            var keys = new uint[meshes.Count];
            meshes.Keys.CopyTo(keys, 0);
            foreach (var k in keys)
            {
                if (meshes[k] != null)
                {
                    meshes[k].Dispose();
                    meshes[k] = null;
                }
                meshDatas[k] = null;
            }
        }

		public override Texture FindTexture (string name)
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            if (name == null) return null;
			if (name == NullTextureName)
				return NullTexture;
			if (name == WhiteTextureName)
				return WhiteTexture;
            if (name == GreyTextureName)
                return GreyTexture;
            Texture outtex;
			if (!textures.TryGetValue(name, out outtex))
				return null;
			if (outtex == null)
			{
				var file = texturefiles[name];
				FLLog.Debug("Resources", string.Format("Reloading {0} from {1}", name, file));
				LoadResourceFile(file);
                outtex = textures[name];
			}
            return outtex;
		}

		public override Material FindMaterial (uint materialId)
		{
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            Material m = null;
			materials.TryGetValue (materialId, out m);
			return m;
		}

        public override VMeshResource FindMesh (uint vMeshLibId)
		{
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            if (!meshes.TryGetValue(vMeshLibId, out var vms)){
                return null;
            }
            if (vms != null) return vms;
            if (meshDatas.TryGetValue(vMeshLibId, out var d) && d != null){
                d.Initialize(this);
                meshes[vMeshLibId] = d.Resource;
                vms = d.Resource;
            }
            else
            {
                FLLog.Debug("Resources", $"Reloading meshes from {meshFiles[vMeshLibId]}");
                LoadResourceFile(meshFiles[vMeshLibId], MeshLoadMode.GPU);
                vms = meshes[vMeshLibId];
            }
            return vms;
		}

        public override VMeshData FindMeshData(uint vMeshLibId)
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            if (!meshDatas.TryGetValue(vMeshLibId, out var vms)){
                return null;
            }
            if (vms != null) return vms;
            LoadResourceFile(meshFiles[vMeshLibId], MeshLoadMode.CPU);
            return meshDatas[vMeshLibId];
        }

        public void AddResources(Utf.IntermediateNode node, string id)
		{
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            MatFile mat;
			TxmFile txm;
			VmsFile vms;
			Utf.UtfLoader.LoadResourceNode(node, this, out mat, out txm, out vms);
			if (mat != null) AddMaterials(mat, id);
			if (txm != null) AddTextures(txm, id);
			if (vms != null) AddMeshes(vms, MeshLoadMode.All, id);
		}

		public void RemoveResourcesForId(string id)
		{
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            List<string> removeTex = new List<string>();
			foreach (var tex in textures)
			{
				if (texturefiles[tex.Key] == id)
				{
					texturefiles.Remove(tex.Key);
					tex.Value.Dispose();
					removeTex.Add(tex.Key);
				}
			}
			foreach (var key in removeTex) textures.Remove(key);
			List<uint> removeMats = new List<uint>();
			foreach (var mat in materials)
			{
				if (materialfiles[mat.Key] == id)
				{
					materialfiles.Remove(mat.Key);
					mat.Value.Loaded = false;
					removeMats.Add(mat.Key);
				}
			}
			foreach (var key in removeMats) materials.Remove(key);

            var removeMeshes = meshes.Where(x => meshFiles[x.Key] == id).ToArray();
            var removeMeshDatas = meshDatas.Where(x => meshFiles[x.Key] == id).ToArray();

            foreach (var m in removeMeshes)
            {
                m.Value.Dispose();
                meshFiles.Remove(m.Key);
                meshes.Remove(m.Key);
            }
            foreach (var m in removeMeshDatas)
            {
                meshFiles.Remove(m.Key);
                meshDatas.Remove(m.Key);
            }

        }

        public override Fx.ParticleLibrary GetParticleLibrary(string filename)
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            Fx.ParticleLibrary lib;
            if (!particlelibs.TryGetValue(filename, out lib))
            {
                var ale = new Utf.Ale.AleFile(filename);
                lib = new Fx.ParticleLibrary(this, ale);
                particlelibs.Add(filename, lib);
            }
            return lib;
        }

        public override void LoadResourceFile(string filename, MeshLoadMode meshMode = MeshLoadMode.GPU)
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
            var fn = filename.ToLowerInvariant();
            if (loadedResFiles.Contains(fn)) return;

            MatFile mat;
            TxmFile txm;
            VmsFile vms;
            Utf.UtfLoader.LoadResourceFile(filename, this, out mat, out txm, out vms);
            if (mat != null) AddMaterials(mat, filename);
            if (txm != null) AddTextures(txm, filename);
            if (vms != null) AddMeshes(vms, meshMode, filename);
            if (vms == null && mat == null && txm == null)
                FLLog.Warning("Resources", $"Could not load resources from file '{filename}'");
            loadedResFiles.Add(fn);
        }

		void AddTextures(TxmFile t, string filename)
		{
			foreach (var tex in t.Textures) {
				if (!textures.TryGetValue(tex.Key, out var existing) || existing == null)
				{
					var v = tex.Value;
					v.Initialize();
                    if (v.Texture != null)
                    {
                        EstimatedTextureMemory += v.Texture.EstimatedTextureMemory;
                        textures[tex.Key] = v.Texture;
                        texturefiles.TryAdd(tex.Key, filename);
                    }
				}
			}
			foreach (var anim in t.Animations)
            {
                frameanims.TryAdd(anim.Key, anim.Value);
            }
		}

		void AddMaterials(MatFile m, string filename)
		{
			if (m.TextureLibrary != null) {
				AddTextures(m.TextureLibrary, filename);
			}
			foreach (var kv in m.Materials) {
				if (!materials.ContainsKey(kv.Key))
				{
                    kv.Value.Initialize(this);
					materials.Add(kv.Key, kv.Value);
                    materialfiles[kv.Key] = filename;
                }
			}
		}
		void AddMeshes(VmsFile vms, MeshLoadMode mode, string filename)
        {
            bool isGpu = (mode & MeshLoadMode.GPU) == MeshLoadMode.GPU;
            bool isCpu = (mode & MeshLoadMode.CPU) == MeshLoadMode.CPU;
			foreach (var kv in vms.Meshes) {
                if (!meshes.TryGetValue(kv.Key, out var existingGpu) || (isGpu && existingGpu == null))
                {
                    if (isGpu)
                    {
                        kv.Value.Initialize(this);
                        meshes[kv.Key] = kv.Value.Resource;
                    }
                    else
                    {
                        kv.Value.Resource = null;
                    }
                    meshFiles.TryAdd(kv.Key, filename);
                }
                if (!meshDatas.TryGetValue(kv.Key, out var existingCpu) || (isCpu && existingCpu == null))
                {
                    meshDatas[kv.Key] = isCpu ? kv.Value : null;
                    meshFiles.TryAdd(kv.Key, filename);
                }
            }
		}
		public override IDrawable GetDrawable(string filename, MeshLoadMode loadMode = MeshLoadMode.GPU)
        {
            if (isDisposed) throw new ObjectDisposedException(nameof(GameResourceManager));
			IDrawable drawable;
			if (!drawables.TryGetValue(filename, out drawable))
			{
				drawable = Utf.UtfLoader.LoadDrawable(filename, this);
                if(drawable == null) {
                    drawables.Add(filename, null);
                    return null;
                }
				if (drawable is CmpFile) /* Get Resources */
				{
					var cmp = (CmpFile)drawable;
					if (cmp.MaterialLibrary != null) AddMaterials(cmp.MaterialLibrary, filename);
					if (cmp.TextureLibrary != null) AddTextures(cmp.TextureLibrary, filename);
					if (cmp.VMeshLibrary != null) AddMeshes(cmp.VMeshLibrary, loadMode, filename);
                    foreach (var mdl in cmp.Models.Values) {
                        if (mdl.MaterialLibrary != null) AddMaterials(mdl.MaterialLibrary, filename);
                        if (mdl.TextureLibrary != null) AddTextures(mdl.TextureLibrary, filename);
                        if (mdl.VMeshLibrary != null) AddMeshes(mdl.VMeshLibrary, loadMode, filename);
                    }
				}
				if (drawable is ModelFile)
				{
					var mdl = (ModelFile)drawable;
					if (mdl.MaterialLibrary != null) AddMaterials(mdl.MaterialLibrary, filename);
					if (mdl.TextureLibrary != null) AddTextures(mdl.TextureLibrary, filename);
					if (mdl.VMeshLibrary != null) AddMeshes(mdl.VMeshLibrary, loadMode, filename);
                }
				if (drawable is DfmFile)
				{
					var dfm = (DfmFile)drawable;
                    dfm.Initialize(this);
					if (dfm.MaterialLibrary != null) AddMaterials(dfm.MaterialLibrary, filename);
					if (dfm.TextureLibrary != null) AddTextures(dfm.TextureLibrary, filename);
                }
				if (drawable is SphFile)
				{
					var sph = (SphFile)drawable;
					if (sph.MaterialLibrary != null) AddMaterials(sph.MaterialLibrary, filename);
					if (sph.TextureLibrary != null) AddTextures(sph.TextureLibrary, filename);
					if (sph.VMeshLibrary != null) AddMeshes(sph.VMeshLibrary, loadMode, filename);
                }
                drawable.ClearResources();
				drawables.Add(filename, drawable);
			}
			return drawable;
		}

        private bool isDisposed = false;

        public void Dispose()
        {
            if (isDisposed) return;
            isDisposed = true;
            //Textures
            foreach (var v in textures.Values) {
                if(v != null)
                    v.Dispose();
            }
            NullTexture.Dispose();
            WhiteTexture.Dispose();
            GreyTexture.Dispose();
            //Vertex buffers
            vertexResourceAllocator.Dispose();
        }
	}
}
