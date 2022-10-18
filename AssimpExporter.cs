using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using Assimp;
using NbCore;
using NbCore.Math;
using NbCore.Systems;
using NbCore.Utils;

namespace NibbleAssimpPlugin
{
    public static class AssimpExporter
    {
        public static Matrix4x4 convertMatrix(NbMatrix4 localMat)
        {
            Matrix4x4 mat = new()
            {
                A1 = localMat.Column0.X,
                A2 = localMat.Column0.Y,
                A3 = localMat.Column0.Z,
                A4 = localMat.Column0.W,
                B1 = localMat.Column1.X,
                B2 = localMat.Column1.Y,
                B3 = localMat.Column1.Z,
                B4 = localMat.Column1.W,
                C1 = localMat.Column2.X,
                C2 = localMat.Column2.Y,
                C3 = localMat.Column2.Z,
                C4 = localMat.Column2.W,
                D1 = localMat.Column3.X,
                D2 = localMat.Column3.Y,
                D3 = localMat.Column3.Z,
                D4 = localMat.Column3.W
            };

            return mat;
        }

        public static Vector3D convertVector(NbVector3 localVec)
        {
            Vector3D vec = new();
            vec.X = localVec.X;
            vec.Y = localVec.Y;
            vec.Z = localVec.Z;
            return vec;
        }

        public static Quaternion convertQuaternion(NbQuaternion localQuat)
        {
            Quaternion q = new();
            q.X = localQuat.X;
            q.Y = localQuat.Y;
            q.Z = localQuat.Z;
            q.W = localQuat.W;
            return q;
        }

        public static Plugin PluginRef;
        private static AssimpContext _ctx;
        private static Dictionary<ulong, int> _exportedMeshMap;
        private static Dictionary<string, int> _exportedMaterialMap;

        private static void Init()
        {
            _ctx = new AssimpContext();
            _exportedMeshMap = new();
            _exportedMaterialMap = new();
        }

        private static void Finalize()
        {
            _ctx.Dispose();
        }

        public static Scene ExportScene(SceneGraph g, string filepath, string format)
        {
            Init();
            Scene scn = new Scene();
            Node n = ExportNode(g.Root, ref scn);
            scn.RootNode = n;
            _ctx.ExportFile(scn, filepath, format);
            Finalize();
            return scn;
        }

        private static Vector2D GetVector2(BinaryReader br, bufInfo buf)
        {
            return new Vector2D(GetFloat(br, buf.type),
                                GetFloat(br, buf.type));
        }

        private static Vector3D GetVector3(BinaryReader br, bufInfo buf)
        {
            switch (buf.type)
            {
                case NbPrimitiveDataType.Int2101010Rev:
                    {
                        int i1, i2, i3;
                        uint value;
                        byte[] a32 = new byte[4];
                        a32 = br.ReadBytes(4);
                        value = BitConverter.ToUInt32(a32, 0);
                        //Convert Values
                        i1 = TwosComplement.toInt((value >> 00) & 0x3FF, 10);
                        i2 = TwosComplement.toInt((value >> 10) & 0x3FF, 10);
                        i3 = TwosComplement.toInt((value >> 20) & 0x3FF, 10);
                        //int i4 = _2sComplement.toInt((value >> 30) & 0x003, 10);
                        float norm = (float)Math.Sqrt(i1 * i1 + i2 * i2 + i3 * i3);
                        return new Vector3D(Convert.ToSingle(i1) / norm,
                                            Convert.ToSingle(i2) / norm,
                                            Convert.ToSingle(i3) / norm);
                    }
                default:
                    return new Vector3D(GetFloat(br, buf.type),
                                        GetFloat(br, buf.type),
                                        GetFloat(br, buf.type));
            }
        }

        private static float GetFloat(BinaryReader br, NbPrimitiveDataType type)
        {
            switch (type)
            {
                case NbPrimitiveDataType.Float:
                    return br.ReadSingle();
                case NbPrimitiveDataType.HalfFloat:
                    {
                        uint data = br.ReadUInt16();
                        return NbCore.Math.Half.decompress(data);
                    }
                default:
                    PluginRef.Log($"Unsupported Float Type {type}", LogVerbosityLevel.WARNING);
                    break;
            }
            return -1.0f;
        }

        private static Mesh ExportMesh(NbMesh mesh, ref Scene scn)
        {
            Mesh m = new Mesh();
            //Convert byte buffer to assimp

            MemoryStream ms = new MemoryStream(mesh.Data.VertexBuffer);
            BinaryReader br = new BinaryReader(ms);

            int vertices_count = mesh.MetaData.VertrEndGraphics - mesh.MetaData.VertrStartGraphics + 1;

            for (int j = 0; j < mesh.Data.buffers.Length; j++)
            {
                bufInfo buf = mesh.Data.buffers[j];
                br.BaseStream.Seek(buf.offset, SeekOrigin.Begin);
                
                if (buf.semantic == 0) //Vertices
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        Vector3D vec = new Vector3D();

                        for (int k = 0; k < buf.count; k++)
                            vec[k] = GetFloat(br, buf.type);
                        
                        br.BaseStream.Seek(buf.stride, SeekOrigin.Current);
                        m.Vertices.Add(vec);
                    }
                }
                else if (buf.semantic == 1) //UVs
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        Vector3D vec1 = new Vector3D();
                        Vector3D vec2 = new Vector3D();
                        
                        vec1.X = GetFloat(br, buf.type);
                        vec1.Y = GetFloat(br, buf.type);
                        vec2.X = GetFloat(br, buf.type);
                        vec2.Y = GetFloat(br, buf.type);

                        br.BaseStream.Seek(buf.stride, SeekOrigin.Current);
                        m.TextureCoordinateChannels[0].Add(vec1);
                        m.TextureCoordinateChannels[1].Add(vec2);
                    }
                }
                else if (buf.semantic == 2) //Normals
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        Vector3D vec = GetVector3(br, buf);
                        
                        br.BaseStream.Seek(buf.stride, SeekOrigin.Current);
                        m.Normals.Add(vec);
                    }
                }
                else if (buf.semantic == 3) //Tangents
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        Vector3D vec = new Vector3D();

                        for (int k = 0; k < buf.count; k++)
                            vec[k] = GetFloat(br, buf.type);

                        br.BaseStream.Seek(buf.stride, SeekOrigin.Current);
                        m.Tangents.Add(vec);
                    }
                }
                else
                {
                    PluginRef.Log($"Unsupported buffer {buf.semantic} {buf.sem_text}", LogVerbosityLevel.WARNING);
                }
            }

            //Export Faces
            //Get indices
            ms = new MemoryStream(mesh.Data.IndexBuffer);
            BinaryReader ibr = new BinaryReader(ms);
            ibr.BaseStream.Seek(0, SeekOrigin.Begin);
            for (int i = 0; i < mesh.MetaData.BatchCount / 3; i++)
            {
                uint f1 = 0, f2 = 0, f3 = 0;
                
                if (mesh.Data.IndicesLength == NbPrimitiveDataType.UnsignedShort)
                {
                    f1 = ibr.ReadUInt16();
                    f2 = ibr.ReadUInt16();
                    f3 = ibr.ReadUInt16();
                } else if (mesh.Data.IndicesLength == NbPrimitiveDataType.UnsignedInt)
                {
                    f1 = ibr.ReadUInt32();
                    f2 = ibr.ReadUInt32();
                    f3 = ibr.ReadUInt32();
                }
                
                Face face = new Face();
                face.Indices.Add((int) f1);
                face.Indices.Add((int) f2);
                face.Indices.Add((int) f3);
                m.Faces.Add(face);
            }

            return m;
        }

        public static Material ExportMaterial(NbMaterial mat, ref Scene scn)
        {
            Material m = new Material();
            m.ColorDiffuse = new Color4D(1.0f);
            return m;
        }

        public static Node ExportNode(SceneGraphNode m, ref Scene scn)
        {
            if (_ctx is null) 
                Init();
            
            if (m is null)
                return null;
            
            //Default shit
            //Create assimp node
            Node node = new(m.Name);
            node.Transform = convertMatrix(TransformationSystem.GetEntityLocalMat(m));
            
            if (m.Type == SceneNodeType.MESH)
            {
                //Get MeshComponent
                MeshComponent mc = m.GetComponent<MeshComponent>() as MeshComponent;
                
                if (mc.Mesh != null)
                {
                    if (!_exportedMeshMap.ContainsKey(mc.Mesh.Hash))
                    {
                        //Convert Mesh and Add to scene
                        Mesh mesh = ExportMesh(mc.Mesh, ref scn);
                        _exportedMeshMap[mc.Mesh.Hash] = scn.MeshCount;
                        scn.Meshes.Add(mesh);

                        if (!_exportedMaterialMap.ContainsKey(mc.Mesh.Material.Name))
                        {
                            Material mat = ExportMaterial(mc.Mesh.Material, ref scn);
                            _exportedMaterialMap[mc.Mesh.Material.Name] = scn.MaterialCount;
                            mesh.MaterialIndex = scn.MaterialCount;
                            scn.Materials.Add(mat);
                        }
                    }

                    //Add Mesh to node
                    node.MeshIndices.Add(_exportedMeshMap[mc.Mesh.Hash]);
                }
            }

            if (m.Type == SceneNodeType.LIGHT)
            {
                LightComponent lc = m.GetComponent<LightComponent>() as LightComponent;
                //Create Light
                Light l = new Light();
                NbVector4 worldPos = TransformationSystem.GetEntityWorldPosition(m);
                l.Position = new Vector3D(worldPos.X, worldPos.Y, worldPos.Z);
                l.ColorDiffuse = new Color3D(lc.Data.Color.X, lc.Data.Color.Y, lc.Data.Color.Z);
                switch (lc.Data.LightType)
                {
                    case LIGHT_TYPE.POINT:
                        l.LightType = LightSourceType.Point; break;
                    case LIGHT_TYPE.SPOT:
                        l.LightType = LightSourceType.Spot; break;
                }
                scn.Lights.Add(l);
            
            }

            foreach (SceneGraphNode child in m.Children)
            {
                Node c = ExportNode(child, ref scn);
                node.Children.Add(c);
            }

            return node;
        }


    }
}
