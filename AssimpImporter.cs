using System;
using System.IO;
using System.Collections.Generic;
using Assimp;
using NbCore;
using NbCore.Math;
using NbCore.Systems;

namespace NibbleAssimpPlugin
{

    public static class AssimpImporter
    {
        public static AssimpContext _ctx;
        public static Plugin PluginRef;
        public static string WorkingDirectory = "";

        public static Dictionary<string, NbTexture> ImportedTextures;


        public static void InitState(string dirpath)
        {
            WorkingDirectory = dirpath;
            ImportedTextures = new();
        }

        public static SceneGraphNode ImportNode(Node node, Scene scn)
        {

            SceneNodeType nodeType = SceneNodeType.LOCATOR;
            if (node.HasMeshes)
            {
                nodeType = SceneNodeType.MESH;
                //TODO : CREATE MESH NODE
                //_n = PluginRef.EngineRef.CreateMeshNode();
            }

            SceneGraphNode _n = new SceneGraphNode(nodeType)
            {
                Name = node.Name,
            };

            NbMatrix4 transform = new();
            transform.M11 = node.Transform.A1;
            transform.M12 = node.Transform.A2;
            transform.M13 = node.Transform.A3;
            transform.M14 = node.Transform.A4;
            transform.M21 = node.Transform.B1;
            transform.M22 = node.Transform.B2;
            transform.M23 = node.Transform.B3;
            transform.M24 = node.Transform.B4;
            transform.M31 = node.Transform.C1;
            transform.M32 = node.Transform.C2;
            transform.M33 = node.Transform.C3;
            transform.M34 = node.Transform.C4;
            transform.M41 = node.Transform.D1;
            transform.M42 = node.Transform.D2;
            transform.M43 = node.Transform.D3;
            transform.M44 = node.Transform.D4;

            //Add Transform Component
            TransformData td = TransformData.CreateFromMatrix(transform);
            
            TransformComponent tc = new(td);
            _n.AddComponent<TransformComponent>(tc);

            if (node.HasMeshes)
            {
                Mesh assimp_mesh = scn.Meshes[node.MeshIndices[0]];
                Material assimp_mat = scn.Materials[assimp_mesh.MaterialIndex];
                
                NbMeshData nibble_mesh_data = GenerateMeshData(assimp_mesh);
                NbMeshMetaData nibble_mesh_metadata = GenerateGeometryMetaData(nibble_mesh_data.VertexBuffer.Length / (int) nibble_mesh_data.VertexBufferStride,
                                                                               nibble_mesh_data.IndexBuffer.Length / 3);
                MeshMaterial nibble_mat = GenerateMaterial(assimp_mat);

                //Generate NbMesh
                NbMesh nibble_mesh = new()
                {
                    Hash = NbHasher.CombineHash(nibble_mesh_data.Hash, nibble_mesh_metadata.GetHash()),
                    Data = nibble_mesh_data,
                    MetaData = nibble_mesh_metadata,
                    Material = nibble_mat
                };

                MeshComponent mc = new()
                {
                    Mesh = nibble_mesh
                };

                //TODO Process the corresponding mesh if needed
                _n.AddComponent<MeshComponent>(mc);
            }

            //Parse Textures
            foreach (EmbeddedTexture texture in scn.Textures)
            {
                PluginRef.Log("Do something with the texture!", LogVerbosityLevel.INFO);
            }

            foreach (Node child in node.Children)
            {
                SceneGraphNode _c = ImportNode(child, scn);
                _c.SetParent(_n);
            }
            
            return _n;
        }

        public static SceneGraphNode Import(string filepath)
        {
            Scene scn = _ctx.ImportFile(filepath, PostProcessSteps.CalculateTangentSpace | PostProcessSteps.FlipUVs | PostProcessSteps.Triangulate);
            return ImportNode(scn.RootNode, scn);
        }

        private static void LoadTexture(string path)
        {
            //Load texture here
            if (!ImportedTextures.ContainsKey(path))
            {
                NbTexture tex = PluginRef.EngineRef.CreateTexture(Path.Join(WorkingDirectory, path),
                    NbTextureWrapMode.Repeat, NbTextureFilter.Linear, NbTextureFilter.Linear);
                ImportedTextures[path] = tex;
            }
        }

        private static MeshMaterial GenerateMaterial(Material mat)
        {
            MeshMaterial material = new();
            material.Name = mat.Name;

            //Find out material flags
            if (mat.HasTextureDiffuse)
            {
                LoadTexture(mat.TextureDiffuse.FilePath);
                material.AddFlag(MaterialFlagEnum._NB_DIFFUSE_MAP);
            }
                    
            if (mat.HasTextureNormal)
            {
                LoadTexture(mat.TextureNormal.FilePath);
                material.AddFlag(MaterialFlagEnum._NB_NORMAL_MAP);
            }
                
            if (mat.HasTextureEmissive)
            {
                LoadTexture(mat.TextureEmissive.FilePath);
                material.AddFlag(MaterialFlagEnum._NB_EMISSIVE_MAP);
            }
                
            //Compile Material Shader
            GLSLShaderConfig conf = PluginRef.EngineRef.GetShaderConfigByName("UberShader_Deferred");
            ulong shader_hash = PluginRef.EngineRef.CalculateShaderHash(conf, PluginRef.EngineRef.GetMaterialShaderDirectives(material));

            NbShader shader = PluginRef.EngineRef.GetShaderByHash(shader_hash);
            if (shader == null)
            {
                shader = new()
                {
                    directives = PluginRef.EngineRef.GetMaterialShaderDirectives(material)
                };

                shader.SetShaderConfig(conf);
                PluginRef.EngineRef.CompileShader(shader);
            }

            //Load Samplers
            int sampler_id = 0;
            
            //Attach DiffuseMap
            if (mat.HasTextureDiffuse)
            {
                NbSampler sampler = new()
                {
                    SamplerID = sampler_id,
                    ShaderBinding = "mpCustomPerMaterial.gDiffuseMap",
                    Texture = ImportedTextures[mat.TextureDiffuse.FilePath]
                };
                sampler_id++;
                material.Samplers.Add(sampler);
            }

            //Attach NormalMap
            if (mat.HasTextureNormal)
            {
                NbSampler sampler = new()
                {
                    SamplerID = sampler_id,
                    ShaderBinding = "mpCustomPerMaterial.gNormalMap",
                    Texture = ImportedTextures[mat.TextureNormal.FilePath]
                };
                sampler_id++;
                material.Samplers.Add(sampler);
            }


            //Attach EmissiveMap
            if (mat.HasTextureEmissive)
            {
                NbSampler sampler = new()
                {
                    SamplerID = sampler_id,
                    ShaderBinding = "mpCustomPerMaterial.gEmissiveMap",
                    Texture = ImportedTextures[mat.TextureEmissive.FilePath]
                };

                sampler_id++;
                material.Samplers.Add(sampler);
            }

            if (mat.HasColorDiffuse)
            {
                //Add material uniform
                NbUniform uf = new()
                {
                    Name = "mainColor",
                    Values = new NbVector4(mat.ColorDiffuse.R, mat.ColorDiffuse.G, mat.ColorDiffuse.B, mat.ColorDiffuse.A),
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uDiffuseFactor",
                        Type = NbUniformType.Vector4
                    }
                };

                material.Uniforms.Add(uf);
            }

            material.AttachShader(shader);
            
            return material;
        }

        private static NbMeshMetaData GenerateGeometryMetaData(int vx_count, int tris_count)
        {
            NbMeshMetaData metadata = new()
            {
                BatchCount = tris_count * 3,
                FirstSkinMat = 0,
                LastSkinMat = 0,
                VertrEndGraphics = vx_count - 1,
                VertrEndPhysics = vx_count
            };

            return metadata;
        }

        public static Vector3D CalcTangent(Vector3D p0, Vector3D p1, Vector3D p2, 
                                           Vector3D uv0, Vector3D uv1, Vector3D uv2)
        {
            Vector3D tangent = new();

            Vector3D e1 = p1 - p0;
            Vector3D e2 = p2 - p0;

            Vector3D duv1 = uv1 - uv0;
            Vector3D duv2 = uv2 - uv0;

            float f = 1.0f / (duv1.X * duv2.Y - duv2.X * duv1.Y);

            tangent.X = f * (duv2.Y * e1.X - duv1.Y * e2.X);
            tangent.Y = f * (duv2.Y * e1.Y - duv1.Y * e2.Y);
            tangent.Z = f * (duv2.Y * e1.Z - duv1.Y * e2.Z);

            return tangent;
        }

        public static NbMeshData GenerateMeshData(Mesh mesh)
        {
            NbMeshData data = new();

            //Populate buffers
            int bufferCount = 1; //Vertices
            int vx_stride = 12;
            bufferCount += mesh.HasNormals ? 2 : 0; //Normals
            vx_stride += mesh.HasNormals ? 24 : 0; //Normals
            bufferCount += mesh.TextureCoordinateChannelCount; //Uvs
            vx_stride += Math.Max(1, mesh.TextureCoordinateChannelCount) * 16; //Uvs
            
            data.buffers = new bufInfo[bufferCount];
            data.VertexBufferStride = (uint) vx_stride;
            
            //Prepare vx Buffers
            int offset = 0;
            int buf_index = 0;
            data.buffers[buf_index] = new()
            {
                count = 3,
                normalize = false,
                offset = offset,
                semantic = 0,
                sem_text = "vPosition",
                stride = (uint)vx_stride,
                type = NbPrimitiveDataType.Float
            };
            offset = 12;
            buf_index = 1;

            //Buffer for normals
            data.buffers[buf_index] = new()
            {
                count = 3,
                normalize = true,
                offset = offset,
                semantic = 2,
                sem_text = "nPosition",
                stride = (uint)vx_stride,
                type = NbPrimitiveDataType.Float
            };
            offset += 12;
            buf_index++;

            //Buffer for tangents
            data.buffers[buf_index] = new()
            {
                count = 3,
                normalize = true,
                offset = offset,
                semantic = 3,
                sem_text = "tPosition",
                stride = (uint)vx_stride,
                type = NbPrimitiveDataType.Float
            };

            offset += 12;
            buf_index++;

            
            if (mesh.TextureCoordinateChannelCount > 0)
            {
                //2 UV channels are supported for now
                data.buffers[buf_index] = new()
                {
                    count = 4,
                    normalize = false,
                    offset = offset,
                    semantic = 1,
                    sem_text = "uvPosition",
                    stride = (uint)vx_stride,
                    type = NbPrimitiveDataType.Float
                };
            }
            
            //Convert Geometry
            List<Vector3D> verts = new();
            List<Vector3D> normals = new();
            List<Vector3D> tangents = new();
            List<List<Vector3D>> uvs = new();
            for (int i = 0; i < mesh.TextureCoordinateChannelCount; i++)
                uvs.Add(new());
            
            for (int i = 0; i < mesh.FaceCount; i++)
            {
                int i1 = mesh.Faces[i].Indices[0];
                int i2 = mesh.Faces[i].Indices[1];
                int i3 = mesh.Faces[i].Indices[2];

                Vector3D p1 = mesh.Vertices[i1];
                Vector3D p2 = mesh.Vertices[i2];
                Vector3D p3 = mesh.Vertices[i3];

                //Add vertices
                verts.Add(p1);
                verts.Add(p2);
                verts.Add(p3);

                //Add Normals and tangents
                normals.Add(mesh.Normals[i1]);
                normals.Add(mesh.Normals[i2]);
                normals.Add(mesh.Normals[i3]);

                tangents.Add(mesh.Tangents[i1]);
                tangents.Add(mesh.Tangents[i2]);
                tangents.Add(mesh.Tangents[i3]);
                
                //Write UVs
                for (int k = 0; k < Math.Min(2, mesh.TextureCoordinateChannelCount); k++)
                {
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i1]);
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i2]);
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i3]);
                }
            }

            //Copy vertex data
            data.VertexBuffer = new byte[vx_stride * verts.Count];
            MemoryStream ms = new MemoryStream(data.VertexBuffer);
            BinaryWriter bw = new BinaryWriter(ms);
            for (int i = 0; i < verts.Count; i++)
            {
                //Verts
                bw.Write(verts[i].X);
                bw.Write(verts[i].Y);
                bw.Write(verts[i].Z);

                Vector3D normal = normals[i];
                bw.Write(normal.X);
                bw.Write(normal.Y);
                bw.Write(normal.Z);

                Vector3D tangent = tangents[i];
                bw.Write(tangent.X);
                bw.Write(tangent.Y);
                bw.Write(tangent.Z);

                //Write UVs
                for (int j=0; j< 2; j++)
                {
                    if (j >= uvs.Count)
                    {
                        bw.Write(0.0f);
                        bw.Write(0.0f);
                    } else
                    {
                        bw.Write(uvs[j][i].X);
                        bw.Write(uvs[j][i].Y);
                    }
                }
            }
            ms.Close();

            //Write Indices
            int indicesCount = verts.Count;
            if (indicesCount < 0xFFFF)
            {
                data.IndicesLength = NbPrimitiveDataType.UnsignedShort;
                data.IndexBuffer = new byte[indicesCount * sizeof(ushort)];

                ms = new MemoryStream(data.IndexBuffer);
                bw = new BinaryWriter(ms);

                for (int i = 0; i < indicesCount; i++)
                    bw.Write((ushort)i);
                bw.Close();
            } else
            {
                data.IndicesLength = NbPrimitiveDataType.UnsignedInt;
                data.IndexBuffer = new byte[indicesCount * sizeof(uint)];
                ms = new MemoryStream(data.IndexBuffer);
                bw = new BinaryWriter(ms);
                for (int i = 0; i < indicesCount; i++)
                    bw.Write((uint) i);
                bw.Close();
            }
            
            data.Hash = NbHasher.CombineHash(NbHasher.Hash(data.VertexBuffer),
                                             NbHasher.Hash(data.IndexBuffer));
            return data;
        }
    }
    
    /* Bring that shit back when we are done with the transition to the ECS system
     
    public override Assimp.Node assimpExport(ref Assimp.Scene scn, ref Dictionary<int, int> meshImportStatus)
        {
            Assimp.Mesh amesh = new Assimp.Mesh();
            Assimp.Node node;
            amesh.Name = Name;

            int meshHash = meshVao.GetHashCode();

            //TESTING
            if (scn.MeshCount > 20)
            {
                node = base.assimpExport(ref scn, ref meshImportStatus);
                return node;
            }

            if (!meshImportStatus.ContainsKey(meshHash))
            //if (false)
            {
                meshImportStatus[meshHash] = scn.MeshCount;

                int vertcount = metaData.vertrend_graphics - metaData.vertrstart_graphics + 1;
                MemoryStream vms = new MemoryStream(gobject.meshDataDict[metaData.Hash].vs_buffer);
                MemoryStream ims = new MemoryStream(gobject.meshDataDict[metaData.Hash].is_buffer);
                BinaryReader vbr = new BinaryReader(vms);
                BinaryReader ibr = new BinaryReader(ims);


                //Initialize Texture Component Channels
                if (gobject.bufInfo[1] != null)
                {
                    List<Assimp.Vector3D> textureChannel = new List<Assimp.Vector3D>();
                    amesh.TextureCoordinateChannels.Append(textureChannel);
                    amesh.UVComponentCount[0] = 2;
                }

                //Generate bones only for the joints related to the mesh
                Dictionary<int, Assimp.Bone> localJointDict = new Dictionary<int, Assimp.Bone>();

                //Export Bone Structure
                if (Skinned)
                //if (false)
                {
                    for (int i = 0; i < meshVao.BoneRemapIndicesCount; i++)
                    {
                        int joint_id = meshVao.BoneRemapIndices[i];
                        //Fetch name
                        Joint relJoint = null;

                        foreach (Joint jnt in parentScene.jointDict.Values)
                        {
                            if (jnt.jointIndex == joint_id)
                            {
                                relJoint = jnt;
                                break;
                            }

                        }

                        //Generate bone
                        Assimp.Bone b = new Assimp.Bone();
                        if (relJoint != null)
                        {
                            b.Name = relJoint.Name;
                            b.OffsetMatrix = MathUtils.convertMatrix(relJoint.invBMat);
                        }


                        localJointDict[i] = b;
                        amesh.Bones.Add(b);
                    }
                }



                //Write geometry info

                vbr.BaseStream.Seek(0, SeekOrigin.Begin);
                for (int i = 0; i < vertcount; i++)
                {
                    Assimp.Vector3D v, vN;

                    for (int j = 0; j < gobject.bufInfo.Count; j++)
                    {
                        bufInfo buf = gobject.bufInfo[j];
                        if (buf is null)
                            continue;

                        switch (buf.semantic)
                        {
                            case 0: //vPosition
                                {
                                    switch (buf.type)
                                    {
                                        case VertexAttribPointerType.HalfFloat:
                                            uint v1 = vbr.ReadUInt16();
                                            uint v2 = vbr.ReadUInt16();
                                            uint v3 = vbr.ReadUInt16();
                                            uint v4 = vbr.ReadUInt16();

                                            //Transform vector with worldMatrix
                                            v = new Assimp.Vector3D(Utils.Half.decompress(v1), Utils.Half.decompress(v2), Utils.Half.decompress(v3));
                                            break;
                                        case VertexAttribPointerType.Float: //This is used in my custom vbos
                                            float f1 = vbr.ReadSingle();
                                            float f2 = vbr.ReadSingle();
                                            float f3 = vbr.ReadSingle();
                                            //Transform vector with worldMatrix
                                            v = new Assimp.Vector3D(f1, f2, f3);
                                            break;
                                        default:
                                            throw new Exception("Unimplemented Vertex Type");
                                    }
                                    amesh.Vertices.Add(v);
                                    break;
                                }

                            case 1: //uvPosition
                                {
                                    Assimp.Vector3D uv;
                                    uint v1 = vbr.ReadUInt16();
                                    uint v2 = vbr.ReadUInt16();
                                    uint v3 = vbr.ReadUInt16();
                                    uint v4 = vbr.ReadUInt16();
                                    //uint v4 = Convert.ToUInt16(vbr.ReadUInt16());
                                    uv = new Assimp.Vector3D(Utils.Half.decompress(v1), Utils.Half.decompress(v2), 0.0f);

                                    amesh.TextureCoordinateChannels[0].Add(uv); //Add directly to the first channel
                                    break;
                                }
                            case 2: //nPosition
                            case 3: //tPosition
                                {
                                    switch (buf.type)
                                    {
                                        case (VertexAttribPointerType.Float):
                                            float f1, f2, f3;
                                            f1 = vbr.ReadSingle();
                                            f2 = vbr.ReadSingle();
                                            f3 = vbr.ReadSingle();
                                            vN = new Assimp.Vector3D(f1, f2, f3);
                                            break;
                                        case (VertexAttribPointerType.HalfFloat):
                                            uint v1, v2, v3;
                                            v1 = vbr.ReadUInt16();
                                            v2 = vbr.ReadUInt16();
                                            v3 = vbr.ReadUInt16();
                                            vN = new Assimp.Vector3D(Utils.Half.decompress(v1), Utils.Half.decompress(v2), Utils.Half.decompress(v3));
                                            break;
                                        case (VertexAttribPointerType.Int2101010Rev):
                                            int i1, i2, i3;
                                            uint value;
                                            byte[] a32 = new byte[4];
                                            a32 = vbr.ReadBytes(4);

                                            value = BitConverter.ToUInt32(a32, 0);
                                            //Convert Values
                                            i1 = _2sComplement.toInt((value >> 00) & 0x3FF, 10);
                                            i2 = _2sComplement.toInt((value >> 10) & 0x3FF, 10);
                                            i3 = _2sComplement.toInt((value >> 20) & 0x3FF, 10);
                                            //int i4 = _2sComplement.toInt((value >> 30) & 0x003, 10);
                                            float norm = (float)Math.Sqrt(i1 * i1 + i2 * i2 + i3 * i3);

                                            vN = new Assimp.Vector3D(Convert.ToSingle(i1) / norm,
                                                             Convert.ToSingle(i2) / norm,
                                                             Convert.ToSingle(i3) / norm);

                                            //Debug.WriteLine(vN);
                                            break;
                                        default:
                                            throw new Exception("UNIMPLEMENTED NORMAL TYPE. PLEASE REPORT");
                                    }

                                    if (j == 2)
                                        amesh.Normals.Add(vN);
                                    else if (j == 3)
                                    {
                                        amesh.Tangents.Add(vN);
                                        amesh.BiTangents.Add(new Assimp.Vector3D(0.0f, 0.0f, 1.0f));
                                    }
                                    break;
                                }
                            case 4: //bPosition
                                vbr.ReadBytes(4); // skip
                                break;
                            case 5: //BlendIndices + BlendWeights
                                {
                                    int[] joint_ids = new int[4];
                                    float[] weights = new float[4];

                                    for (int k = 0; k < 4; k++)
                                    {
                                        joint_ids[k] = vbr.ReadByte();
                                    }


                                    for (int k = 0; k < 4; k++)
                                        weights[k] = Utils.Half.decompress(vbr.ReadUInt16());

                                    if (Skinned)
                                    //if (false)
                                    {
                                        for (int k = 0; k < 4; k++)
                                        {
                                            int joint_id = joint_ids[k];

                                            Assimp.VertexWeight vw = new Assimp.VertexWeight();
                                            vw.VertexID = i;
                                            vw.Weight = weights[k];
                                            localJointDict[joint_id].VertexWeights.Add(vw);

                                        }


                                    }


                                    break;
                                }
                            case 6:
                                break; //Handled by 5
                            default:
                                {
                                    throw new Exception("UNIMPLEMENTED BUF Info. PLEASE REPORT");
                                    break;
                                }

                        }
                    }

                }

                //Export Faces
                //Get indices
                ibr.BaseStream.Seek(0, SeekOrigin.Begin);
                bool start = false;
                int fstart = 0;
                for (int i = 0; i < metaData.batchcount / 3; i++)
                {
                    int f1, f2, f3;
                    //NEXT models assume that all gstream meshes have uint16 indices
                    f1 = ibr.ReadUInt16();
                    f2 = ibr.ReadUInt16();
                    f3 = ibr.ReadUInt16();

                    if (!start && Type != TYPES.COLLISION)
                    { fstart = f1; start = true; }
                    else if (!start && Type == TYPES.COLLISION)
                    {
                        fstart = 0; start = true;
                    }

                    int f11, f22, f33;
                    f11 = f1 - fstart;
                    f22 = f2 - fstart;
                    f33 = f3 - fstart;


                    Assimp.Face face = new Assimp.Face();
                    face.Indices.Add(f11);
                    face.Indices.Add(f22);
                    face.Indices.Add(f33);


                    amesh.Faces.Add(face);
                }

                scn.Meshes.Add(amesh);

            }

            node = base.assimpExport(ref scn, ref meshImportStatus);
            node.MeshIndices.Add(meshImportStatus[meshHash]);

            return node;
        }
    
     * 
     */


    public class AssimpExporter
    {
        public static Assimp.Matrix4x4 convertMatrix(NbMatrix4 localMat)
        {
            Assimp.Matrix4x4 mat = new()
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

        public static Assimp.Vector3D convertVector(NbVector3 localVec)
        {
            Vector3D vec = new();
            vec.X = localVec.X;
            vec.Y = localVec.Y;
            vec.Z = localVec.Z;
            return vec;
        }

        public static Assimp.Quaternion convertQuaternion(NbQuaternion localQuat)
        {
            Quaternion q = new();
            q.X = localQuat.X;
            q.Y = localQuat.Y;
            q.Z = localQuat.Z;
            q.W = localQuat.W;
            return q;
        }

        
        //public static Animation AssimpExport(ref Assimp.Scene scn)
        //{
        //    Animation asAnim = new();
        //    asAnim.Name = Anim;


        //    //Make sure keyframe data is loaded from the files
        //    if (!loaded)
        //    {
        //        FetchAnimMetaData();
        //        loaded = true;
        //    }



        //    asAnim.TicksPerSecond = 60;
        //    asAnim.DurationInTicks = animMeta.FrameCount;
        //    float time_interval = 1.0f / (float)asAnim.TicksPerSecond;


        //    //Add Node-Bone Channels
        //    for (int i = 0; i < animMeta.NodeCount; i++)
        //    {
        //        string name = animMeta.NodeData[i].Node;
        //        Assimp.NodeAnimationChannel mChannel = new();
        //        mChannel.NodeName = name;

        //        //mChannel.PostState = Assimp.AnimationBehaviour.Linear;
        //        //mChannel.PreState = Assimp.AnimationBehaviour.Linear;


        //        //Export Keyframe Data
        //        for (int j = 0; j < animMeta.FrameCount; j++)
        //        {

        //            //Position
        //            Assimp.VectorKey vk = new(j * time_interval, convertVector(animMeta.anim_positions[name][j]));
        //            mChannel.PositionKeys.Add(vk);
        //            //Rotation
        //            Assimp.QuaternionKey qk = new(j * time_interval, convertQuaternion(animMeta.anim_rotations[name][j]));
        //            mChannel.RotationKeys.Add(qk);
        //            //Scale
        //            Assimp.VectorKey sk = new(j * time_interval, convertVector(animMeta.anim_scales[name][j]));
        //            mChannel.ScalingKeys.Add(sk);

        //        }

        //        asAnim.NodeAnimationChannels.Add(mChannel);

        //    }

        //    return asAnim;

        //}

        public static Node assimpExport(SceneGraphNode m, ref Assimp.Scene scn, ref Dictionary<int, int> meshImportStatus)
        {

            //Default shit
            //Create assimp node
            Node node = new(m.Name);
            node.Transform = convertMatrix(TransformationSystem.GetEntityLocalMat(m));

            //Handle animations maybe?
            if (m.HasComponent<AnimComponent>())
            {
                AnimComponent cmp = m.GetComponent<AnimComponent>() as AnimComponent;
                //TODO: Export Component to Assimp
                //cmp.AssimpExport(ref scn);
            }
            
            foreach (SceneGraphNode child in m.Children)
            {
                Node c = assimpExport(child, ref scn, ref meshImportStatus);
                node.Children.Add(c);
            }

            return node;
        }


    }
}
