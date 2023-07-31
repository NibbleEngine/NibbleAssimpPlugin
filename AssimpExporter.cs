using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Security;
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
            
            Node n = ExportNode(g.Root, ref scn, ref g);
            scn.RootNode = n;
            _ctx.ExportFile(scn, filepath, format);
            Finalize();
            return scn;
        }

        private static Vector2D GetVector2(BinaryReader br, NbMeshBufferInfo buf)
        {
            return new Vector2D(GetFloat(br, buf.type),
                                GetFloat(br, buf.type));
        }

        private static Vector3D GetVector3(BinaryReader br, NbMeshBufferInfo buf)
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

        private static uint GetUInt(BinaryReader br, NbPrimitiveDataType type)
        {
            switch (type)
            {
                case NbPrimitiveDataType.UnsignedByte:
                    return br.ReadByte();
                case NbPrimitiveDataType.UnsignedInt:
                    return br.ReadUInt32();
                case NbPrimitiveDataType.UnsignedShort:
                    return br.ReadUInt16();
                case NbPrimitiveDataType.Int:
                    return (uint) br.ReadInt32();
                default:
                    PluginRef.Log($"Unsupported Float Type {type}", LogVerbosityLevel.WARNING);
                    break;
            }

            throw new NotImplementedException();
        }

        private static int GetInt(BinaryReader br, NbPrimitiveDataType type)
        {
            switch (type)
            {
                case NbPrimitiveDataType.UnsignedByte:
                    return br.ReadByte();
                case NbPrimitiveDataType.UnsignedInt:
                    return (int) br.ReadUInt32();
                case NbPrimitiveDataType.UnsignedShort:
                    return br.ReadUInt16();
                case NbPrimitiveDataType.Int:
                    return br.ReadInt32();
                default:
                    PluginRef.Log($"Unsupported Float Type {type}", LogVerbosityLevel.WARNING);
                    break;
            }

            throw new NotImplementedException();
        }

        private static Mesh ExportMesh(NbMesh mesh, ref Scene scn, ref SceneGraph scn_graph)
        {
            Mesh m = new Mesh();
            m.PrimitiveType = PrimitiveType.Triangle;
            //Convert byte buffer to assimp

            MemoryStream ms = new MemoryStream(mesh.Data.VertexBuffer);
            BinaryReader br = new BinaryReader(ms);
            Dictionary<string, Bone> mesh_bone_string_map = new();
            Dictionary<int, Bone> mesh_bone_id_map = new();
            Dictionary<int, int[]> mesh_boneIndices_perVertex = new();
            
            int vertices_count = mesh.MetaData.VertrEndGraphics - mesh.MetaData.VertrStartGraphics + 1;

            for (int j = 0; j < mesh.Data.buffers.Length; j++)
            {
                NbMeshBufferInfo buf = mesh.Data.buffers[j];
                br.BaseStream.Seek(buf.offset, SeekOrigin.Begin);
                
                if (buf.semantic == NbBufferSemantic.VERTEX) //Vertices
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        br.BaseStream.Seek(mesh.Data.VertexBufferStride * i + buf.offset, SeekOrigin.Begin);
                        Vector3D vec = new Vector3D();

                        for (int k = 0; k < buf.count; k++)
                            vec[k] = GetFloat(br, buf.type);
                        
                        m.Vertices.Add(vec);
                    }
                }
                else if (buf.semantic == NbBufferSemantic.UV) //UVs
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        br.BaseStream.Seek(mesh.Data.VertexBufferStride * i + buf.offset, SeekOrigin.Begin);
                        Vector3D vec1 = new Vector3D();
                        Vector3D vec2 = new Vector3D();
                        
                        vec1.X = GetFloat(br, buf.type);
                        vec1.Y = 1.0f - GetFloat(br, buf.type);
                        vec2.X = GetFloat(br, buf.type);
                        vec2.Y = 1.0f - GetFloat(br, buf.type);

                        m.TextureCoordinateChannels[0].Add(vec1);
                        m.TextureCoordinateChannels[1].Add(vec2);
                    }
                    m.UVComponentCount[0] = 2;
                }
                else if (buf.semantic == NbBufferSemantic.NORMAL) //Normals
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        br.BaseStream.Seek(mesh.Data.VertexBufferStride * i + buf.offset, SeekOrigin.Begin);
                        Vector3D vec = GetVector3(br, buf);
                        m.Normals.Add(vec);
                    }
                }
                else if (buf.semantic == NbBufferSemantic.TANGENT) //Tangents
                {

                    for (int i = 0; i < vertices_count; i++)
                    {
                        br.BaseStream.Seek(mesh.Data.VertexBufferStride * i + buf.offset, SeekOrigin.Begin);
                        Vector3D vec = new Vector3D();

                        for (int k = 0; k < buf.count; k++)
                            vec[k] = GetFloat(br, buf.type);

                        m.Tangents.Add(vec);
                    }

                }
                else if (buf.semantic == NbBufferSemantic.BLENDINDICES) //BlendIndices
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        mesh_boneIndices_perVertex[i] = new int[buf.count];
                        for (int k = 0; k < buf.count; k++)
                        {
                            //Get local bone index
                            int bone_id = GetInt(br, buf.type);
                            int actual_joint_index = mesh.MetaData.BoneRemapIndices[bone_id];
                            mesh_boneIndices_perVertex[i][k] = actual_joint_index;
                            SceneGraphNode joint = scn_graph.GetJointNodeByJointID(actual_joint_index);
                            
                            //Create Bone if needed
                            if (!mesh_bone_string_map.ContainsKey(joint.Name))
                            {
                                TransformComponent tc = (TransformComponent)joint.GetComponent<TransformComponent>();
                                Bone bone = new Bone();
                                bone.Name = joint.Name;
                                bone.OffsetMatrix = convertMatrix(tc.Data.LocalTransformMat);
                                
                                m.Bones.Add(bone);
                                mesh_bone_string_map.Add(joint.Name, bone);
                                mesh_bone_id_map.Add(actual_joint_index, bone);
                            }
                        }

                        br.BaseStream.Seek(buf.stride - buf.offset, SeekOrigin.Current);
                    }
                }
                else if (buf.semantic == NbBufferSemantic.BLENDWEIGHTS) //BlendWeights
                {
                    for (int i = 0; i < vertices_count; i++)
                    {
                        for (int k = 0; k < buf.count; k++)
                        {
                            //Get bone_weight
                            float bone_weight = GetFloat(br, buf.type);

                            VertexWeight vw;
                            vw.VertexID = i;
                            vw.Weight = bone_weight;
                            mesh_bone_id_map[mesh_boneIndices_perVertex[i][k]].VertexWeights.Add(vw);
                        }

                        br.BaseStream.Seek(buf.stride - buf.offset, SeekOrigin.Current);
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
            m.Name = mat.Name;

            //Material Parameters
            m.ColorDiffuse = new(mat.DiffuseColor.Values.X,
                                 mat.DiffuseColor.Values.Y,
                                 mat.DiffuseColor.Values.Z,
                                 mat.DiffuseColor.Values.W);

            m.ColorAmbient = new(mat.AmbientColor.Values.X,
                                 mat.AmbientColor.Values.Y,
                                 mat.AmbientColor.Values.Z,
                                 mat.AmbientColor.Values.W);

            m.ColorSpecular = new(mat.SpecularColor.Values.X,
                                  mat.SpecularColor.Values.Y,
                                  mat.SpecularColor.Values.Z,
                                  mat.SpecularColor.Values.W);


            //Export Samplers
            foreach (NbSampler sampler in mat.ActiveSamplers)
            {
                TextureSlot texSlot = new();
                TextureSlot texSlot1 = new();
                texSlot.FilePath = sampler.Texture.Path;
                var t = new MaterialProperty();
                t.Name = "pbrMetallicRoughness.metallicRoughnessTexture";
                t.SetStringValue("1");
                
                switch (sampler.ShaderBinding)
                {
                    case "mpCustomPerMaterial.gDiffuseMap":
                        texSlot.TextureType = TextureType.Diffuse;
                        m.AddMaterialTexture(texSlot);
                        //Also add as PBR Base Color
                        texSlot1.FilePath = sampler.Texture.Path;
                        texSlot1.TextureType = TextureType.BaseColor;
                        m.AddMaterialTexture(texSlot1);
                        break;
                    case "mpCustomPerMaterial.gNormalMap":
                        texSlot.TextureType = TextureType.Normals;
                        m.AddMaterialTexture(texSlot);
                        break;
                    case "mpCustomPerMaterial.gEmissiveMap":
                        texSlot.TextureType = TextureType.Emissive;
                        m.AddMaterialTexture(texSlot);
                        break;
                    case "mpCustomPerMaterial.gMasksMap":
                        texSlot.TextureType = TextureType.Roughness;
                        //Add a second slot for the metallic
                        texSlot1.FilePath = sampler.Texture.Path;
                        texSlot1.TextureType = TextureType.Metalness;
                        m.AddMaterialTexture(texSlot);
                        m.AddMaterialTexture(texSlot1);
                        break;
                }
                
            }
            
            return m;
        }

        public static Node ExportNode(SceneGraphNode m, ref Scene scn, ref SceneGraph scn_graph)
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
                        Mesh mesh = ExportMesh(mc.Mesh, ref scn, ref scn_graph);
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
                Node c = ExportNode(child, ref scn, ref scn_graph);
                node.Children.Add(c);
            }

            return node;
        }


    }
}
