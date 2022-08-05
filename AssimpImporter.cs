using System;
using System.IO;
using System.Collections.Generic;
using Assimp;
using NbCore;
using NbCore.Math;
using NbCore.Systems;
using NbCore.Common;
using System.Xml.Linq;

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
            SceneGraphNode _n = new SceneGraphNode(SceneNodeType.MESH)
            {
                Name = node.Name,
            };

            //Add Transform Component
            TransformData td = new();
            
            TransformComponent tc = new(td);
            _n.AddComponent<TransformComponent>(tc);

            SetNodeTransform(_n, node);

            Mesh assimp_mesh = scn.Meshes[node.MeshIndices[0]];
            Material assimp_mat = scn.Materials[assimp_mesh.MaterialIndex];

            NbMeshData nibble_mesh_data = GenerateMeshData(assimp_mesh);
            NbMeshMetaData nibble_mesh_metadata = GenerateGeometryMetaData(nibble_mesh_data.VertexBuffer.Length / (int)nibble_mesh_data.VertexBufferStride,
                                                                           nibble_mesh_data.IndexBuffer.Length / 3, assimp_mesh.BoneCount);
            NbMaterial nibble_mat = GenerateMaterial(assimp_mat, assimp_mesh);

            //Generate NbMesh
            NbMesh nibble_mesh = new()
            {
                Hash = NbHasher.CombineHash(nibble_mesh_data.Hash, nibble_mesh_metadata.GetHash()),
                Data = nibble_mesh_data,
                MetaData = nibble_mesh_metadata,
                Material = nibble_mat
            };

            _MeshGroup.AddMesh(nibble_mesh);

            MeshComponent mc = new()
            {
                Mesh = nibble_mesh
            };

            //TODO Process the corresponding mesh if needed
            _n.AddComponent<MeshComponent>(mc);

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
                SceneComponent sc = sceneRef.GetComponent<SceneComponent>() as SceneComponent;
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
            //Scene scn = _ctx.ImportFile(filepath, PostProcessPreset.TargetRealTimeQuality |
            //                                      PostProcessSteps.FlipUVs);
            
            Scene scn = _ctx.ImportFile(filepath, PostProcessSteps.Triangulate | PostProcessSteps.GenerateNormals | PostProcessSteps.CalculateTangentSpace |
            PostProcessSteps.FlipUVs);
            
            //Identify Joints
            AnimComponent ac = GetAnimationComponent(scn);

            ClearState();

            SceneGraphNode root = ImportNode(scn.RootNode, scn, null);

            //Fix joint info
            foreach (string name in _joints)
            {
                JointComponent jc = _jointNodes[name].GetComponent<JointComponent>() as JointComponent;
                jc.JointIndex = _jointIndexMap[name];
            }


            //Attach components
            if (ac.AnimGroup.Animations.Count > 0)
            {
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
                LoadTexture(mat.TextureNormal.FilePath);
                material.AddFlag(MaterialFlagEnum._NB_NORMAL_MAP);
            }
                
            if (mat.HasTextureEmissive)
            {
                LoadTexture(mat.TextureEmissive.FilePath);
                material.AddFlag(MaterialFlagEnum._NB_EMISSIVE_MAP);
            }

            if (mat.HasTextureLightMap)
            {
                LoadTexture(mat.TextureLightMap.FilePath);
            }

            if (mat.IsPBRMaterial)
            {
                if (mat.PBR.HasTextureBaseColor)
                {
                    LoadTexture(mat.PBR.TextureBaseColor.FilePath);
                    material.AddFlag(MaterialFlagEnum._NB_DIFFUSE_MAP);
                }

                if (mat.PBR.HasTextureMetalness)
                    LoadTexture(mat.PBR.TextureMetalness.FilePath);
                
                if (mat.PBR.HasTextureRoughness)
                    LoadTexture(mat.PBR.TextureMetalness.FilePath);
                
                //Figure out flag combo
                if (mat.PBR.HasTextureMetalness && mat.PBR.HasTextureRoughness)
                {
                    if (mat.PBR.TextureMetalness.FilePath == mat.PBR.TextureRoughness.FilePath)
                    {
                        if (mat.HasTextureLightMap)
                        {
                            if (mat.TextureLightMap.FilePath == mat.PBR.TextureRoughness.FilePath)
                            {
                                material.AddFlag(MaterialFlagEnum._NB_AO_METALLIC_ROUGHNESS_MAP);
                            }
                            else
                            {
                                material.AddFlag(MaterialFlagEnum._NB_METALLIC_ROUGHNESS_MAP);
                                material.AddFlag(MaterialFlagEnum._NB_AO_MAP);
                            }
                        } else
                        {
                            material.AddFlag(MaterialFlagEnum._NB_METALLIC_ROUGHNESS_MAP);
                        } 
                    
                    } else
                    {
                        if (mat.HasTextureLightMap)
                            material.AddFlag(MaterialFlagEnum._NB_AO_MAP);
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
                    LoadTexture(mat.TextureDiffuse.FilePath);
                    material.AddFlag(MaterialFlagEnum._NB_DIFFUSE_MAP);
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
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uDiffuseFactor",
                        Type = NbUniformType.Vector3
                    }
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
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uEmissiveFactor",
                        Type = NbUniformType.Vector3
                    }
                };

                material.Uniforms.Add(uf);

                NbUniform uuf = new()
                {
                    Name = "Emissive Strength",
                    Values = new NbVector4(1.0f),
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uEmissiveStrength",
                        Type = NbUniformType.Float
                    }
                };

                material.Uniforms.Add(uuf);
            }

            //Get MetallicFactor
            if (mat.HasProperty("$mat.metallicFactor", TextureType.None, 0))
            {
                float value = mat.GetProperty("$mat.metallicFactor", TextureType.None, 0).GetFloatValue();
                //Add material uniform
                NbUniform uf = new()
                {
                    Name = "Metallic",
                    Values = new NbVector4(value, value, value, value),
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uMetallicFactor",
                        Type = NbUniformType.Float
                    }
                };

                material.Uniforms.Add(uf);
            }

            //Get RoughnessFactor
            if (mat.HasProperty("$mat.roughnessFactor", TextureType.None, 0))
            {
                float value = mat.GetProperty("$mat.roughnessFactor,0,0").GetFloatValue();
                //Add material uniform
                NbUniform uf = new()
                {
                    Name = "Roughness",
                    Values = new NbVector4(value, value, value, value),
                    State = new NbUniformState()
                    {
                        ShaderBinding = "mpCustomPerMaterial.uRoughnessFactor",
                        Type = NbUniformType.Float
                    }
                };

                material.Uniforms.Add(uf);
            }

            //Get Roughness Metallic Texture
            //if (mat.HasProperty("$mat.gltf.pbrMetalli"))



            //Get correct shader config
            GLSLShaderSource conf_vs = RenderState.engineRef.GetShaderSourceByFilePath("Shaders/Simple_VS.glsl");
            GLSLShaderSource conf_fs = RenderState.engineRef.GetShaderSourceByFilePath("Shaders/ubershader_fs.glsl");
            NbShaderMode conf_mode = NbShaderMode.DEFFERED;

            if (mesh.HasBones)
                conf_mode = NbShaderMode.SKINNED | NbShaderMode.DEFFERED;

            ulong conf_hash = GLSLShaderConfig.GetHash(conf_vs, conf_fs, null, null, null, conf_mode);

            GLSLShaderConfig conf = PluginRef.EngineRef.GetShaderConfigByHash(conf_hash);
            if (conf == null)
            {
                conf = new GLSLShaderConfig(conf_vs, conf_fs, null, null, null, conf_mode);
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

        private static NbMeshMetaData GenerateGeometryMetaData(int vx_count, int tris_count, int bone_count)
        {
            NbMeshMetaData metadata = new()
            {
                BatchCount = tris_count * 3,
                FirstSkinMat = 0,
                LastSkinMat = bone_count - 1,
                VertrEndGraphics = vx_count - 1,
                VertrEndPhysics = vx_count
            };

            metadata.BoneRemapIndices = new int[bone_count];
            for (int i = 0; i < bone_count; i++)
                metadata.BoneRemapIndices[i] = i;

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
            vx_stride += Math.Min(1, mesh.TextureCoordinateChannelCount) * 16; //Uvs
            

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
                bones_per_vertex = Math.Max(vx_blend_indices[i].Count, bones_per_vertex);

            bones_per_vertex += (bones_per_vertex % 4); //multiples of 4 bones per vertex

            //Bone Indices + Blend Weights
            bufferCount += mesh.HasBones ? 2 : 0;
            NbPrimitiveDataType bone_index_type = NbPrimitiveDataType.UnsignedByte;
            if (mesh.BoneCount > 256)
                bone_index_type = NbPrimitiveDataType.UnsignedShort;

            if (bone_index_type == NbPrimitiveDataType.UnsignedShort)
                vx_stride += bones_per_vertex * (2 + 4); //Short indices and floats for the weights
            else
                vx_stride += bones_per_vertex * (1 + 4); //byte indices and floats for the weights

                
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

            if (mesh.HasTangentBasis)
            {
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
            }
            
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
                    semantic = 5,
                    sem_text = "blendIndices",
                    stride = (uint) vx_stride,
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
                    semantic = 6,
                    sem_text = "blendWeights",
                    stride = (uint) vx_stride,
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
                for (int k = 0; k < Math.Min(2, mesh.TextureCoordinateChannelCount); k++)
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

                    for (int j=uvs.Count - 1; j < 2; j++)
                    {
                        bw.Write(0.0f);
                        bw.Write(0.0f);
                    }
                }
                
                    
                //Write BlendIndices
                
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
                        bw.Write((short) 0);
                    else
                        bw.Write((byte) 0);
                }
                
                //Write BlendWeights
                for (int j = 0; j < blendWeights[i].Count; j++)
                    bw.Write(blendWeights[i][j]);

                for (int j = blendWeights[i].Count; j < bones_per_vertex; j++)
                {
                    bw.Write(0.0f);
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


            //FileStream fs = new FileStream("dump", FileMode.CreateNew);
            //fs.Write(data.VertexBuffer, 0, data.VertexBuffer.Length);
            //fs.Close();

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
