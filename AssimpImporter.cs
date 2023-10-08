using System;
using System.IO;
using System.Collections.Generic;
using Assimp;
using NbCore;
using NbCore.Systems;
using NbCore.Common;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks.Dataflow;

namespace NibbleAssimpPlugin
{

    public static class AssimpImporter
    {
        public static Plugin PluginRef;
        private static AssimpContext _ctx;
        public static string WorkingDirectory = "";

        public static Dictionary<string, NbTexture> ImportedTextures;
        private static List<string> _joints = new();
        private static Dictionary<string, int> _jointIndexMap = new();
        private static Dictionary<string, NbMatrix4> _jointBindMatrices;
        private static Dictionary<string, SceneGraphNode> _jointNodes;
        private static int _meshGroupCounter = 0;
        private static NbMeshGroup _MeshGroup;


        public static void InitState(string dirpath)
        {
            WorkingDirectory = dirpath;
            ImportedTextures = new();
            _joints = new();
            _jointIndexMap = new();
            _jointBindMatrices = new();
            _jointNodes = new();
            _MeshGroup = new()
            {
                ID = _meshGroupCounter++,
            };
            _meshGroupCounter = 0;
            InitAssimpContext();
        }

        private static void InitAssimpContext()
        {
            _ctx = new AssimpContext();
        }

        public static void ClearState()
        {
            _ctx.Dispose();
        }

        private static void SetNodeTransform(SceneGraphNode _n, Node node)
        {
            TransformComponent tc = _n.GetComponent<TransformComponent>() as TransformComponent;

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

            transform.Transpose();
            tc.Data.SetFromMatrix(transform);
        }

        private static SceneGraphNode GenerateLocatorNode(Node node, Scene scn)
        {
            SceneGraphNode _n = PluginRef.EngineRef.CreateLocatorNode(node.Name);
            SetNodeTransform(_n, node);
            return _n;
        }

        private static SceneGraphNode GenerateJointNode(Node node, Scene scn)
        {
            SceneGraphNode _n = new SceneGraphNode(SceneNodeType.JOINT)
            {
                Name = node.Name,
            };

            //Add Transform Component
            TransformData td = new();

            TransformComponent tc = new(td);
            _n.AddComponent<TransformComponent>(tc);

            SetNodeTransform(_n, node);

            //Create Joint Component
            JointComponent jc = new()
            {
                JointIndex = -1
            };

            _n.AddComponent<JointComponent>(jc);

            //Create MeshComponent
            MeshComponent mc = new()
            {
                Mesh = PluginRef.EngineRef.GetMesh(NbHasher.Hash("default_cross"))
            };

            _n.AddComponent<MeshComponent>(mc);
            _jointNodes[_n.Name] = _n;
            return _n;
        }

        private static SceneGraphNode GenerateMeshNode(Node node, Scene scn)
        {
            //The engine does not support multiple mesh components per node
            //For now we will create separate nodes per included assimp mesh

            SceneGraphNode _n = new SceneGraphNode(SceneNodeType.MESH)
            {
                Name = node.Name,
            };

            //Add Transform Component
            TransformData td = new();

            TransformComponent tc = new(td);
            _n.AddComponent<TransformComponent>(tc);

            SetNodeTransform(_n, node);

            for (int i = 0; i < node.MeshIndices.Count; i++)
            {
                Mesh assimp_mesh = scn.Meshes[node.MeshIndices[i]];
                Material assimp_mat = scn.Materials[assimp_mesh.MaterialIndex];

                NbMeshData nibble_mesh_data = GenerateMeshData(assimp_mesh);
                NbMeshMetaData nibble_mesh_metadata = GenerateGeometryMetaData(assimp_mesh, nibble_mesh_data.VertexBuffer.Length / (int)nibble_mesh_data.VertexBufferStride,
                                                                               nibble_mesh_data.IndexBuffer.Length / (3 * (nibble_mesh_data.IndexFormat == NbPrimitiveDataType.UnsignedShort ? 2 : 4)), assimp_mesh.BoneCount);
                NbMaterial nibble_mat = GenerateMaterial(assimp_mat, assimp_mesh);
                 
                //Generate NbMesh
                NbMesh nibble_mesh = new()
                {
                    Hash = NbHasher.CombineHash(nibble_mesh_data.Hash, nibble_mesh_metadata.GetHash()),
                    Data = nibble_mesh_data,
                    MetaData = nibble_mesh_metadata,
                    Material = nibble_mat
                };

                //TODO: Add support for transparent meshes as well
                _MeshGroup.AddOpaqueMesh(nibble_mesh);

                MeshComponent mc = new()
                {
                    Mesh = nibble_mesh
                };

                _n.AddComponent<MeshComponent>(mc);
            }

            return _n;
        }

        public static SceneGraphNode ImportNode(Node node, Scene scn, SceneGraphNode sceneRef)
        {
            SceneGraphNode _n;
            
            if (node.HasMeshes)
            {
                //Create Mesh Node
                _n = GenerateMeshNode(node, scn);
            
            } else if (_joints.Contains(node.Name))
            {
                _n = GenerateJointNode(node, scn);
            }
            else
            {
                _n = GenerateLocatorNode(node, scn);
            }

            _n.Root = sceneRef;
            //Once all components are in place, properly populate
            if (sceneRef != null)
            {
                SceneComponent sc = sceneRef.GetComponent<SceneComponent>();
                sc.AddNode(_n);
            } else
            {
                //Create SceneComponent
                SceneComponent sc = new();
                _n.AddComponent<SceneComponent>(sc);
                sceneRef = _n;
            }

            foreach (Node child in node.Children)
            {
                SceneGraphNode _c = ImportNode(child, scn, sceneRef);
                _c.SetParent(_n);
            }

            return _n;
        }

        public static SceneGraphNode Import(string filepath)
        {
            InitState(Path.GetDirectoryName(filepath));
            Scene scn = _ctx.ImportFile(filepath, PostProcessSteps.Triangulate | PostProcessSteps.CalculateTangentSpace |
            PostProcessSteps.FlipUVs);
            
            ClearState();

            SceneGraphNode root = ImportNode(scn.RootNode, scn, null);

            //Check for animations and joints
            if (scn.AnimationCount > 0)
            {
                //Identify Joints
                AnimComponent ac = GetAnimationComponent(scn);
                
                //Fix joint info
                foreach (string name in _joints)
                {
                    JointComponent jc = _jointNodes[name].GetComponent<JointComponent>() as JointComponent;
                    jc.JointIndex = _jointIndexMap[name];
                }

                _MeshGroup.JointCount = _joints.Count;
                foreach (KeyValuePair<string, int> pair in _jointIndexMap)
                {
                    _MeshGroup.JointBindingDataList[pair.Value].invBindMatrix = _jointBindMatrices[pair.Key];
                    _MeshGroup.JointBindingDataList[pair.Value].BindMatrix = _jointBindMatrices[pair.Key].Inverted();

                    //_MeshGroup.JointBindingDataList[pair.Value].BindMatrix.Transpose();
                    //_MeshGroup.JointBindingDataList[pair.Value].invBindMatrix.Transpose();
                }

                ac.AnimGroup.RefMeshGroup = _MeshGroup;
                ac.AnimGroup.AnimationRoot = root;

                root.AddComponent<AnimComponent>(ac);
            }

            return root;
        }

        private static AnimComponent GetAnimationComponent(Scene scn)
        {
            AnimComponent ac = new AnimComponent();
            int _animCounter = 0;
            foreach (Assimp.Animation anim in scn.Animations)
            {
                NbCore.Animation new_anim = new NbCore.Animation();
                AnimationData data = new();
                data.MetaData.Name = anim.Name == "" ? "Anim" + _animCounter++ : anim.Name;
                data.MetaData.FrameStart = 0;
                data.MetaData.Speed = 1.0f;

                data.FrameCount = (int)(anim.DurationInTicks * 60 / (float) anim.TicksPerSecond);
                data.MetaData.FrameEnd = data.FrameCount - 1;
                float ticks_per_frame = (float) anim.TicksPerSecond / 60.0f;


                foreach (NodeAnimationChannel channel in anim.NodeAnimationChannels)
                {

                    if (!_joints.Contains(channel.NodeName))
                    {
                        _joints.Add(channel.NodeName);
                    }

                    data.AddNode(channel.NodeName);

                    Callbacks.Assert(channel.PositionKeyCount >= 2, "test");
                    Callbacks.Assert(channel.RotationKeyCount >= 2, "test");
                    Callbacks.Assert(channel.ScalingKeyCount >= 2, "test");
                     
                    //Translations
                    int key = 0;

                    NbVector3 prev_pos = new NbVector3(channel.PositionKeys[0].Value.X,
                                                  channel.PositionKeys[0].Value.Y,
                                                  channel.PositionKeys[0].Value.Z);
                    float prev_time = (float) channel.PositionKeys[0].Time;
                    
                    NbVector3 next_pos = new NbVector3(channel.PositionKeys[1].Value.X,
                                                  channel.PositionKeys[1].Value.Y,
                                                  channel.PositionKeys[1].Value.Z);
                    float next_time = (float) channel.PositionKeys[1].Time;
                    data.SetNodeTranslation(channel.NodeName, 0, prev_pos);

                    for (int i = 1; i < data.FrameCount; i++)
                    {
                        float frame_time = i * ticks_per_frame;

                        if (frame_time >= next_time)
                        {
                            if (key + 1 < channel.PositionKeyCount)
                            {
                                prev_pos = next_pos;
                                prev_time = next_time;

                                key = key + 1;
                                next_pos = new NbVector3(channel.PositionKeys[key].Value.X,
                                                  channel.PositionKeys[key].Value.Y,
                                                  channel.PositionKeys[key].Value.Z);
                                next_time = (float)channel.PositionKeys[key].Time;
                            }

                        }
                        
                        //Interpolate
                        float lerp = (frame_time - prev_time) / (next_time - prev_time);
                        data.SetNodeTranslation(channel.NodeName, i, NbVector3.Lerp(prev_pos, next_pos, lerp));
                        //Console.WriteLine($"{channel.NodeName} {i} {data.GetNodeTranslation(channel.NodeName, i)}");
                    }

                    //Rotations
                    
                    key = 0;

                    NbQuaternion prev_rot = new NbQuaternion( channel.RotationKeys[0].Value.X,
                                                              channel.RotationKeys[0].Value.Y,
                                                              channel.RotationKeys[0].Value.Z,
                                                              channel.RotationKeys[0].Value.W);
                    prev_time = (float)channel.RotationKeys[0].Time;

                    NbQuaternion next_rot = new NbQuaternion(channel.RotationKeys[1].Value.X,
                                                              channel.RotationKeys[1].Value.Y,
                                                              channel.RotationKeys[1].Value.Z,
                                                              channel.RotationKeys[1].Value.W);
                    next_time = (float)channel.RotationKeys[1].Time;
                    data.SetNodeRotation(channel.NodeName, 0, prev_rot);

                    for (int i = 1; i < data.FrameCount; i++)
                    {
                        float frame_time = i * ticks_per_frame;

                        if (frame_time >= next_time)
                        {
                            if (key + 1 < channel.PositionKeyCount)
                            {
                                prev_rot = next_rot;
                                prev_time = next_time;

                                key += 1;
                                next_rot = new NbQuaternion(channel.RotationKeys[key].Value.X,
                                                              channel.RotationKeys[key].Value.Y,
                                                              channel.RotationKeys[key].Value.Z,
                                                              channel.RotationKeys[key].Value.W);
                                next_time = (float)channel.RotationKeys[key].Time;
                            }
                        }

                        //Interpolate
                        float lerp = (frame_time - prev_time) / (next_time - prev_time);
                        data.SetNodeRotation(channel.NodeName, i, NbQuaternion.Slerp(prev_rot, next_rot, lerp));
                        //Console.WriteLine($"{channel.NodeName} {i} {data.GetNodeRotation(channel.NodeName, i)}");
                    }


                    //Scale
                    key = 0;

                    prev_pos = new NbVector3(channel.ScalingKeys[0].Value.X,
                                                  channel.ScalingKeys[0].Value.Y,
                                                  channel.ScalingKeys[0].Value.Z);
                    prev_time = (float)channel.ScalingKeys[0].Time;

                    next_pos = new NbVector3(channel.ScalingKeys[1].Value.X,
                                                  channel.ScalingKeys[1].Value.Y,
                                                  channel.ScalingKeys[1].Value.Z);
                    next_time = (float)channel.ScalingKeys[1].Time;
                    data.SetNodeScale(channel.NodeName, 0, prev_pos);

                    for (int i = 1; i < data.FrameCount; i++)
                    {
                        float frame_time = i * ticks_per_frame;

                        if (frame_time >= next_time)
                        {
                            if (key + 1 < channel.PositionKeyCount)
                            {
                                prev_pos = next_pos;
                                prev_time = next_time;

                                key += 1;
                                next_pos = new NbVector3(channel.ScalingKeys[key].Value.X,
                                                  channel.ScalingKeys[key].Value.Y,
                                                  channel.ScalingKeys[key].Value.Z);
                                next_time = (float)channel.ScalingKeys[key].Time;
                            }

                        }

                        //Interpolate
                        float lerp = (frame_time - prev_time) / (next_time - prev_time);
                        data.SetNodeScale(channel.NodeName, i, NbVector3.Lerp(prev_pos, next_pos, lerp));
                        //Console.WriteLine($"{channel.NodeName} {i} {data.GetNodeScale(channel.NodeName, i)}");

                    }

                }
                new_anim.animData = data;
                ac.AnimGroup.Animations.Add(new_anim);
                ac.AnimationDict[new_anim.animData.MetaData.Name] = new_anim; //Save animation
            
            }


            return ac;
        }

        private static void LoadTexture(string path, NbTextureFilter magFilter, NbTextureFilter minFilter, bool gamma_correct)
        {
            //Load texture here
            if (!ImportedTextures.ContainsKey(path))
            {
                NbTexture tex = PluginRef.EngineRef.CreateTexture(Path.Join(WorkingDirectory, path),
                    NbTextureWrapMode.Repeat, minFilter, magFilter, gamma_correct);
                ImportedTextures[path] = tex;
            }
        }

        private static void AddSampler(string name, NbMaterial mat, int sampler_id, string binding, string tex_path)
        {
            NbSampler sampler = new()
            {
                Name = name,
                SamplerID = sampler_id,
                ShaderBinding = binding,
                Texture = ImportedTextures[tex_path]
            };

            mat.Samplers.Add(sampler);
        }

        private static NbMaterial GenerateMaterial(Material mat, Mesh mesh)
        {
            NbMaterial material = new();
            material.Name = mat.Name;

            //Find out material flags
            
            if (mat.HasTextureNormal)
            {
                LoadTexture(mat.TextureNormal.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, false);
                material.AddFlag(NbMaterialFlagEnum._NB_NORMAL_MAP);
            }
                
            if (mat.HasTextureEmissive)
            {
                LoadTexture(mat.TextureEmissive.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, true);
                material.AddFlag(NbMaterialFlagEnum._NB_EMISSIVE_MAP);
            }

            if (mat.HasTextureLightMap)
            {
                LoadTexture(mat.TextureLightMap.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, false);
            }

            if (mat.IsPBRMaterial)
            {
                if (mat.PBR.HasTextureBaseColor)
                {
                    LoadTexture(mat.PBR.TextureBaseColor.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, true);
                    material.AddFlag(NbMaterialFlagEnum._NB_DIFFUSE_MAP);
                }

                if (mat.PBR.HasTextureMetalness)
                    LoadTexture(mat.PBR.TextureMetalness.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, false);
                
                if (mat.PBR.HasTextureRoughness)
                    LoadTexture(mat.PBR.TextureMetalness.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, false);
                
                //Figure out flag combo
                if (mat.PBR.HasTextureMetalness && mat.PBR.HasTextureRoughness)
                {
                    if (mat.PBR.TextureMetalness.FilePath == mat.PBR.TextureRoughness.FilePath)
                    {
                        if (mat.HasTextureLightMap)
                        {
                            if (mat.TextureLightMap.FilePath == mat.PBR.TextureRoughness.FilePath)
                            {
                                material.AddFlag(NbMaterialFlagEnum._NB_AO_METALLIC_ROUGHNESS_MAP);
                            }
                            else
                            {
                                material.AddFlag(NbMaterialFlagEnum._NB_METALLIC_ROUGHNESS_MAP);
                                material.AddFlag(NbMaterialFlagEnum._NB_AO_MAP);
                            }
                        } else
                        {
                            material.AddFlag(NbMaterialFlagEnum._NB_METALLIC_ROUGHNESS_MAP);
                        } 
                    
                    } else
                    {
                        if (mat.HasTextureLightMap)
                            material.AddFlag(NbMaterialFlagEnum._NB_AO_MAP);
                        //TODO: Add support for separated metallic roughness maps
                    }
                } else
                {
                    //TODO: Add support for separated metallic roughness maps
                }
            } else
            {
                if (mat.HasTextureDiffuse)
                {
                    LoadTexture(mat.TextureDiffuse.FilePath, NbTextureFilter.Linear, NbTextureFilter.Linear, true);
                    material.AddFlag(NbMaterialFlagEnum._NB_DIFFUSE_MAP);
                }
            }

            //Load Samplers
            int sampler_id = 0;

            //Attach DiffuseMap
            if (mat.HasTextureDiffuse)
            {
                AddSampler("Diffuse Map", material, sampler_id, "mpCustomPerMaterial.gDiffuseMap", mat.TextureDiffuse.FilePath);
                sampler_id++;
            }

            //Attach NormalMap
            if (mat.HasTextureNormal)
            {
                AddSampler("Normal Map", material, sampler_id, "mpCustomPerMaterial.gNormalMap", mat.TextureNormal.FilePath);
                sampler_id++;
            }

            //Attach EmissiveMap
            if (mat.HasTextureEmissive)
            {
                AddSampler("Emissive Map", material, sampler_id, "mpCustomPerMaterial.gEmissiveMap", mat.TextureEmissive.FilePath);
                sampler_id++;
            }

            //Attach Roughness + Metalness Maps
            if (mat.IsPBRMaterial)
            {
                if (mat.PBR.HasTextureMetalness && mat.PBR.HasTextureRoughness)
                {
                    if (mat.PBR.TextureMetalness.FilePath == mat.PBR.TextureRoughness.FilePath)
                    {
                        AddSampler("MetallicRoughness Map", material, sampler_id, "mpCustomPerMaterial.gMasksMap", mat.PBR.TextureMetalness.FilePath);
                        sampler_id++;
                    }
                }

            } else if (mat.HasTextureLightMap)
            {
                AddSampler("Light Map", material, sampler_id, "mpCustomPerMaterial.gAoMap", mat.TextureLightMap.FilePath);
                sampler_id++;
            }
            
            
            //Set Diffuse Color
            if (mat.HasColorDiffuse)
            {
                //Add material uniform
                NbUniform uf = new()
                {
                    Name = "mainColor",
                    Values = new NbVector4(mat.ColorDiffuse.R, mat.ColorDiffuse.G, mat.ColorDiffuse.B, mat.ColorDiffuse.A),
                    ShaderBinding = "mpCustomPerMaterial.uDiffuseFactor",
                    Type = NbUniformType.Vector3,
                };
                
                material.Uniforms.Add(uf);
            }

            //Set Emissive Color
            if (mat.HasColorEmissive)
            {
                //Add material uniform
                NbUniform uf = new()
                {
                    Name = "EmissiveColor",
                    Values = new NbVector4(mat.ColorEmissive.R, mat.ColorEmissive.G, mat.ColorEmissive.B, mat.ColorEmissive.A),
                    ShaderBinding = "mpCustomPerMaterial.uEmissiveFactor",
                    Type = NbUniformType.Vector3
                };

                material.Uniforms.Add(uf);

                NbUniform uuf = new()
                {
                    Name = "Emissive Strength",
                    Values = new NbVector4(1.0f),
                    ShaderBinding = "mpCustomPerMaterial.uEmissiveStrength",
                    Type = NbUniformType.Float
                };

                material.Uniforms.Add(uuf);
            }

            //Get MetallicFactor
            if (mat.HasProperty("$mat.metallicFactor", TextureType.None, 0))
            {
                float value = mat.GetProperty("$mat.metallicFactor", TextureType.None, 0).GetFloatValue();
                //Add material uniform
                NbUniform uf = new(NbUniformType.Float, "Metallic", value);
                uf.ShaderBinding = "mpCustomPerMaterial.uMetallicFactor";
                material.Uniforms.Add(uf);
            }

            //Get RoughnessFactor
            if (mat.HasProperty("$mat.roughnessFactor", TextureType.None, 0))
            {
                float value = mat.GetProperty("$mat.roughnessFactor,0,0").GetFloatValue();
                //Add material uniform
                NbUniform uf = new(NbUniformType.Float, "Roughness", value);
                uf.ShaderBinding = "mpCustomPerMaterial.uRoughnessFactor";
                material.Uniforms.Add(uf);
            }

            //Get Roughness Metallic Texture
            //if (mat.HasProperty("$mat.gltf.pbrMetalli"))


            //Get correct shader config
            NbShaderSource conf_vs = RenderState.engineRef.GetShaderSourceByFilePath("./Assets/Shaders/Source/Simple_VS.glsl");
            NbShaderSource conf_fs = RenderState.engineRef.GetShaderSourceByFilePath("./Assets/Shaders/Source/ubershader_fs.glsl");
            NbShaderMode conf_mode = NbShaderMode.DEFFERED;

            if (mesh.HasBones)
                conf_mode |= NbShaderMode.SKINNED;

            ulong conf_hash = NbShaderConfig.GetHash(conf_vs, conf_fs, null, null, null, conf_mode);

            NbShaderConfig conf = PluginRef.EngineRef.GetShaderConfigByHash(conf_hash);
            if (conf == null)
            {
                conf = new NbShaderConfig(conf_vs, conf_fs, null, null, null, conf_mode);
            }

            //Compile Material Shader
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

            material.AttachShader(shader);
            
            return material;
        }

        private static NbMeshMetaData GenerateGeometryMetaData(Mesh mesh, int vx_count, int tris_count, int bone_count)
        {
            NbMeshMetaData metadata = new()
            {
                BatchCount = tris_count * 3,
                FirstSkinMat = 0,
                LastSkinMat = bone_count - 1,
                VertrEndGraphics = vx_count - 1,
                VertrEndPhysics = vx_count,
                AABBMAX = new NbVector3(-1000000.0f),
                AABBMIN = new NbVector3(1000000.0f),
            };


            metadata.BoneRemapIndices = new int[bone_count];
            for (int i = 0; i < bone_count; i++)
                metadata.BoneRemapIndices[i] = i;

            //Calculated Bounding Box
            for (int i = 0; i < mesh.VertexCount; i++)
            {
                Vector3D point = mesh.Vertices[i];
                metadata.AABBMIN.X = System.Math.Min(metadata.AABBMIN.X, point.X);
                metadata.AABBMIN.Y = System.Math.Min(metadata.AABBMIN.Y, point.Y);
                metadata.AABBMIN.Z = System.Math.Min(metadata.AABBMIN.Z, point.Z);
                metadata.AABBMAX.X = System.Math.Max(metadata.AABBMAX.X, point.X);
                metadata.AABBMAX.Y = System.Math.Max(metadata.AABBMAX.Y, point.Y);
                metadata.AABBMAX.Z = System.Math.Max(metadata.AABBMAX.Z, point.Z);
            }

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
            //Normals
            bufferCount += 1; 
            vx_stride += 12; 

            //Tangents
            bufferCount += mesh.HasTangentBasis ? 1 : 0;
            vx_stride += mesh.HasTangentBasis ? 12 : 0;
            
            //Uvs
            bufferCount += mesh.TextureCoordinateChannelCount; //Uvs
            vx_stride += System.Math.Min(1, mesh.TextureCoordinateChannelCount) * 16; //Uvs
            

            //Preprocess Bones
            List<float>[] vx_blend_weights = new List<float>[mesh.VertexCount];
            List<int>[] vx_blend_indices = new List<int>[mesh.VertexCount];

            for (int i = 0; i < mesh.VertexCount; i++)
            {
                vx_blend_indices[i] = new();
                vx_blend_weights[i] = new();
            }

            for (int i = 0; i < mesh.BoneCount; i++)
            {
                if (!_jointIndexMap.ContainsKey(mesh.Bones[i].Name))
                {
                    _jointIndexMap[mesh.Bones[i].Name] = i;

                    NbMatrix4 transform = new();
                    transform.M11 = mesh.Bones[i].OffsetMatrix.A1;
                    transform.M12 = mesh.Bones[i].OffsetMatrix.A2;
                    transform.M13 = mesh.Bones[i].OffsetMatrix.A3;
                    transform.M14 = mesh.Bones[i].OffsetMatrix.A4;
                    transform.M21 = mesh.Bones[i].OffsetMatrix.B1;
                    transform.M22 = mesh.Bones[i].OffsetMatrix.B2;
                    transform.M23 = mesh.Bones[i].OffsetMatrix.B3;
                    transform.M24 = mesh.Bones[i].OffsetMatrix.B4;
                    transform.M31 = mesh.Bones[i].OffsetMatrix.C1;
                    transform.M32 = mesh.Bones[i].OffsetMatrix.C2;
                    transform.M33 = mesh.Bones[i].OffsetMatrix.C3;
                    transform.M34 = mesh.Bones[i].OffsetMatrix.C4;
                    transform.M41 = mesh.Bones[i].OffsetMatrix.D1;
                    transform.M42 = mesh.Bones[i].OffsetMatrix.D2;
                    transform.M43 = mesh.Bones[i].OffsetMatrix.D3;
                    transform.M44 = mesh.Bones[i].OffsetMatrix.D4;

                    transform.Transpose();
                    _jointBindMatrices[mesh.Bones[i].Name] = transform;
                } else
                {
                    //TODO: INTEGRITY CHECKS
                    PluginRef.Log("BONE EXISTS", LogVerbosityLevel.WARNING);
                }
                
                for (int j = 0; j < mesh.Bones[i].VertexWeightCount; j++)
                {
                    VertexWeight vw = mesh.Bones[i].VertexWeights[j];
                    vx_blend_indices[vw.VertexID].Add(i);
                    vx_blend_weights[vw.VertexID].Add(vw.Weight);
                }
            }

            int bones_per_vertex = 4;
            for (int i = 0; i < mesh.VertexCount; i++)
                bones_per_vertex = System.Math.Max(vx_blend_indices[i].Count, bones_per_vertex);

            bones_per_vertex += (bones_per_vertex % 4); //multiples of 4 bones per vertex

            //Bone Indices + Blend Weights
            bufferCount += mesh.HasBones ? 2 : 0;
            NbPrimitiveDataType bone_index_type = NbPrimitiveDataType.UnsignedByte;
            if (mesh.BoneCount > 256)
                bone_index_type = NbPrimitiveDataType.UnsignedShort;

            if (bone_index_type == NbPrimitiveDataType.UnsignedShort)
                vx_stride += mesh.HasBones ? (bones_per_vertex * (2 + 4)) : 0; //Short indices and floats for the weights
            else
                vx_stride += mesh.HasBones ? (bones_per_vertex * (1 + 4)) : 0; //byte indices and floats for the weights

            data.buffers = new NbMeshBufferInfo[bufferCount];
            data.VertexBufferStride = (uint) vx_stride;
            

            //Prepare vx Buffers
            int offset = 0;
            int buf_index = 0;
            data.buffers[buf_index] = new()
            {
                count = 3,
                normalize = false,
                offset = offset,
                semantic = NbBufferSemantic.VERTEX,
                sem_text = "vPosition",
                stride = vx_stride,
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
                semantic = NbBufferSemantic.NORMAL,
                sem_text = "nPosition",
                stride = vx_stride,
                type = NbPrimitiveDataType.Float
            };
            offset += 12;
            buf_index++;

            if (mesh.HasTangentBasis)
            {
                //Buffer for tangents
                data.buffers[buf_index] = new()
                {
                    count = 3,
                    normalize = true,
                    offset = offset,
                    semantic = NbBufferSemantic.TANGENT,
                    sem_text = "tPosition",
                    stride = vx_stride,
                    type = NbPrimitiveDataType.Float
                };  

                offset += 12;
                buf_index++;
            }
            
            if (mesh.TextureCoordinateChannelCount > 0)
            {
                //2 UV channels are supported for now
                data.buffers[buf_index] = new()
                {
                    count = 4,
                    normalize = false,
                    offset = offset,
                    semantic = NbBufferSemantic.UV,
                    sem_text = "uvPosition",
                    stride = vx_stride,
                    type = NbPrimitiveDataType.Float
                };

                offset += 16;
                buf_index++;
            }

            
            if (mesh.HasBones)
            {
                //Blend Indices
                data.buffers[buf_index] = new()
                {
                    count = bones_per_vertex,
                    normalize = false,
                    offset = offset,
                    semantic = NbBufferSemantic.BLENDINDICES,
                    sem_text = "blendIndices",
                    stride = vx_stride,
                    type = bone_index_type
                };

                offset += bones_per_vertex * (bone_index_type == NbPrimitiveDataType.UnsignedShort ? 2 : 1);
                buf_index++;

                //Blend Weights
                data.buffers[buf_index] = new()
                {
                    count = bones_per_vertex,
                    normalize = false,
                    offset = offset,
                    semantic = NbBufferSemantic.BLENDWEIGHTS,
                    sem_text = "blendWeights",
                    stride = vx_stride,
                    type = NbPrimitiveDataType.Float
                };
            }


            //Convert Geometry
            List<Vector3D> verts = new();
            List<Vector3D> normals = new();
            List<Vector3D> tangents = new();
            List<List<int>> blendIndices = new();
            List<List<float>> blendWeights = new();
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
                
                if (mesh.HasTangentBasis)
                {
                    tangents.Add(mesh.Tangents[i1]);
                    tangents.Add(mesh.Tangents[i2]);
                    tangents.Add(mesh.Tangents[i3]);
                }
                
                //Write UVs
                for (int k = 0; k < System.Math.Min(2, mesh.TextureCoordinateChannelCount); k++)
                {
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i1]);
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i2]);
                    uvs[k].Add(mesh.TextureCoordinateChannels[k][i3]);
                }

                //Write BlendIndices and Weights
                if (mesh.HasBones)
                {
                    List<int> bI = new List<int>();
                    List<float> bW = new List<float>();

                    for (int k = 0; k < vx_blend_indices[i1].Count; k++)
                    {
                        bI.Add(vx_blend_indices[i1][k]);
                        bW.Add(vx_blend_weights[i1][k]);
                    }

                    blendIndices.Add(bI);
                    blendWeights.Add(bW);


                    bI = new List<int>();
                    bW = new List<float>();
                    for (int k = 0; k < vx_blend_indices[i2].Count; k++)
                    {
                        bI.Add(vx_blend_indices[i2][k]);
                        bW.Add(vx_blend_weights[i2][k]);
                    }

                    blendIndices.Add(bI);
                    blendWeights.Add(bW);


                    bI = new List<int>();
                    bW = new List<float>();
                    for (int k = 0; k < vx_blend_indices[i3].Count; k++)
                    {
                        bI.Add(vx_blend_indices[i3][k]);
                        bW.Add(vx_blend_weights[i3][k]);
                    }

                    blendIndices.Add(bI);
                    blendWeights.Add(bW);
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

                if (mesh.HasTangentBasis)
                {
                    Vector3D tangent = tangents[i];
                    bw.Write(tangent.X);
                    bw.Write(tangent.Y);
                    bw.Write(tangent.Z);
                }

                //Write UVs
                if (mesh.TextureCoordinateChannelCount > 0)
                {
                    for (int j = 0; j < uvs.Count; j++)
                    {
                        bw.Write(uvs[j][i].X);
                        bw.Write(uvs[j][i].Y);
                    }

                    for (int j= uvs.Count; j < 2; j++)
                    {
                        bw.Write(0.0f);
                        bw.Write(0.0f);
                    }
                }
                
                    
                //Write BlendIndices
                if (mesh.HasBones)
                {
                    for (int j = 0; j < blendIndices[i].Count; j++)
                    {
                        if (bone_index_type == NbPrimitiveDataType.UnsignedShort)
                            bw.Write((short)blendIndices[i][j]);
                        else
                            bw.Write((byte)blendIndices[i][j]);
                    }

                    for (int j = blendIndices[i].Count; j < bones_per_vertex; j++)
                    {
                        if (bone_index_type == NbPrimitiveDataType.UnsignedShort)
                            bw.Write((short)0);
                        else
                            bw.Write((byte)0);
                    }

                    //Write BlendWeights
                    for (int j = 0; j < blendWeights[i].Count; j++)
                        bw.Write(blendWeights[i][j]);

                    for (int j = blendWeights[i].Count; j < bones_per_vertex; j++)
                    {
                        bw.Write(0.0f);
                    }
                }
                
            }
            ms.Close();

            //Write Indices
            data.IndicesType = NbRenderPrimitive.Triangles;
            int indicesCount = verts.Count;
            if (indicesCount < 0xFFFF)
            {
                data.IndexFormat = NbPrimitiveDataType.UnsignedShort;
                data.IndexBuffer = new byte[indicesCount * sizeof(ushort)];

                ms = new MemoryStream(data.IndexBuffer);
                bw = new BinaryWriter(ms);

                for (int i = 0; i < indicesCount; i++)
                    bw.Write((ushort)i);
                bw.Close();
            } else
            {
                data.IndexFormat = NbPrimitiveDataType.UnsignedInt;
                data.IndexBuffer = new byte[indicesCount * sizeof(uint)];
                ms = new MemoryStream(data.IndexBuffer);
                bw = new BinaryWriter(ms);
                for (int i = 0; i < indicesCount; i++)
                    bw.Write((uint) i);
                bw.Close();
            }


            //FileStream fs = new FileStream("dump", FileMode.CreateNew);
            //fs.Write(data.VertexBuffer, 0, data.VertexBuffer.Length);
            //fs.Close();

            data.Hash = NbHasher.CombineHash(NbHasher.Hash(data.VertexBuffer),
                                             NbHasher.Hash(data.IndexBuffer));
            return data;
        }
    }
    
    
}

