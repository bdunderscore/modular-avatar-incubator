using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using nadena.dev.ndmf;
using nadena.dev.ndmf.runtime;
using nadena.dev.ndmf.util;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace nadena.dev.modular_avatar.incubator.editor
{
    public class BlendshapeAnimationBakerPass : Pass<BlendshapeAnimationBakerPass>
    {
        private const string BLENDSHAPE_PREFIX = "blendShape.";

        private class MeshState
        {
            public readonly SkinnedMeshRenderer smr;
            public readonly Mesh mesh, newMesh;
            public readonly Vector3[] baseDeltas;
            public readonly Vector3[] outputShapeVertices;
            public readonly Vector3[] zero;
            public readonly Vector3[] shapeVertices, shapeNormals, shapeTangents;

            public float[] weights, originalWeight;

            public MeshState(SkinnedMeshRenderer smr)
            {
                this.smr = smr;
                var m = smr.sharedMesh;
                mesh = m;

                newMesh = Mesh.Instantiate(mesh);
                newMesh.name = mesh.name + " (baked shapes)";
                
                baseDeltas = new Vector3[m.vertexCount];
                outputShapeVertices = new Vector3[m.vertexCount];
                zero = new Vector3[m.vertexCount];
                shapeVertices = new Vector3[m.vertexCount];
                shapeNormals = new Vector3[m.vertexCount];
                shapeTangents = new Vector3[m.vertexCount];

                var shapeCount = m.blendShapeCount;
                weights = new float[shapeCount];
                originalWeight = new float[shapeCount];
                for (int i = 0; i < shapeCount; i++)
                {
                    originalWeight[i] = weights[i] = smr.GetBlendShapeWeight(i);
                }
            }
            
            public void ResetForNewClip()
            {
                Array.Copy(baseDeltas, outputShapeVertices, baseDeltas.Length);
            }

            public void Apply()
            {
                Debug.Log($"=== Applying to SMR: {RuntimeUtil.AvatarRootPath(smr.gameObject)} in {RuntimeUtil.FindAvatarInParents(smr.transform).gameObject.name}");
                smr.sharedMesh = newMesh;

                for (int i = 0; i < Math.Min(weights.Length, mesh.blendShapeCount); i++)
                {
                    smr.SetBlendShapeWeight(i, weights[i]);
                }
            }
        }
        
        protected override void Execute(BuildContext context)
        {
            var rootAnimator = context.AvatarRootTransform.GetComponent<Animator>();
            rootAnimator.runtimeAnimatorController = null;
            
            List<AnimatorOverrideController> animatorControllers = GatherAnimators(context);
            List<AnimationClip> clips = GatherClips(context);

            // Identify the blendshapes that will be in scope. For them to be in scope, the following needs to be true:
            // 1. The blendshape is only animated by clips that are in scope.
            // 2. The only animations used have either a single keyframe animating the target blendshape, or all
            //    keyframes have the same value (up to epsilon).

            Dictionary<Mesh, HashSet<string>> inScopeBlendshapes = GatherInScopeShapes(context, animatorControllers, clips);
            
            // Identify mesh renderers using each mesh of interest

            Dictionary<Mesh, HashSet<SkinnedMeshRenderer>> rendererInfo = GatherRenderers(context, inScopeBlendshapes.Keys);
            
            // Remove any meshes which are referenced by multiple renderers
            foreach (var kvp in rendererInfo)
            {
                if (kvp.Value.Count > 1)
                {
                    inScopeBlendshapes.Remove(kvp.Key);
                }
            }

            Dictionary<Mesh, MeshState> meshStates = new Dictionary<Mesh, MeshState>();

            foreach (var kvp in inScopeBlendshapes)
            {
                var mesh = kvp.Key;
                var state = new MeshState(rendererInfo[kvp.Key].First());
                meshStates[mesh] = state;

                var blendshapes = kvp.Value;
                var smr = rendererInfo[kvp.Key].FirstOrDefault();

                Vector3[] shapeVertices = state.shapeVertices;
                Vector3[] shapeNormals = state.shapeNormals;
                Vector3[] shapeTangents = state.shapeTangents;
                Vector3[] baseDeltas = state.baseDeltas;

                var vertexPositions = mesh.vertices;
                foreach (var shape in inScopeBlendshapes[mesh])
                {
                    var index = mesh.GetBlendShapeIndex(shape);
                    float weight = state.weights[index];
                    mesh.GetBlendShapeFrameVertices(index, 0, shapeVertices, shapeNormals, shapeTangents);

                    for (var i = 0; i < vertexPositions.Length; i++)
                    {
                        var delta = shapeVertices[i] * weight / 100.0f;
                        baseDeltas[i] -= delta;
                        vertexPositions[i] += delta;
                    }

                    state.weights[index] = 0;
                }

                state.newMesh.vertices = vertexPositions;
                
                EditorUtility.SetDirty(smr);
            }
            
            List<KeyValuePair<AnimationClip, AnimationClip>> overrides =
                    new List<KeyValuePair<AnimationClip, AnimationClip>>();
                
            // Construct a new blendshape for each input animation
            foreach (var clip in clips)
            {
                var shapeName = "Baked " + clip.name;
                var newClip = new AnimationClip();
                EditorUtility.CopySerialized(clip, newClip);
                newClip.name = shapeName;

                foreach (var state in meshStates.Values)
                {
                    state.ResetForNewClip();
                }

                newClip.ClearCurves();

                foreach (var kvp in meshStates)
                {
                    var mesh = kvp.Key;
                    var state = kvp.Value;
                    var shapes = inScopeBlendshapes[mesh];

                    var smr = rendererInfo[mesh].First();
                    var smrPath = RuntimeUtil.AvatarRootPath(smr.gameObject);
                    
                    foreach (var shape in shapes)
                    {
                        var binding = new EditorCurveBinding()
                        {
                            path = smrPath,
                            propertyName = BLENDSHAPE_PREFIX + shape,
                            type = typeof(SkinnedMeshRenderer)
                        };
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        var index = mesh.GetBlendShapeIndex(shape);
                        float weight;
                        if (curve != null)
                        {
                            weight = curve.keys[0].value;
                        }
                        else
                        {
                            weight = state.originalWeight[index];
                        }
                        
                        if (weight == 0) continue;

                        var outputShapeVertices = state.outputShapeVertices;
                        Vector3[] shapeVertices = state.shapeVertices;
                        Vector3[] shapeNormals = state.shapeNormals;
                        Vector3[] shapeTangents = state.shapeTangents;
                    
                        mesh.GetBlendShapeFrameVertices(index, 0, shapeVertices, shapeNormals, shapeTangents);
                    
                        for (int i = 0; i < shapeVertices.Length; i++)
                        {
                            outputShapeVertices[i] += shapeVertices[i] * weight / 100.0f;
                        }
                    }
                }
                
                var bindings = AnimationUtility.GetCurveBindings(clip);
                foreach (var binding in bindings)
                {
                    var blendshapeName = binding.propertyName.Substring(BLENDSHAPE_PREFIX.Length);
                    var curve = AnimationUtility.GetEditorCurve(clip, binding);
                    
                    if (binding.type != typeof(SkinnedMeshRenderer)
                        || !binding.propertyName.StartsWith(BLENDSHAPE_PREFIX)
                    )
                    {
                        AnimationUtility.SetEditorCurve(newClip, binding, curve);
                        continue;
                    }

                    var smr = context.AvatarRootTransform.Find(binding.path)
                        ?.GetComponentInChildren<SkinnedMeshRenderer>();
                    // ReSharper disable once Unity.NoNullPropagation
                    var mesh = smr?.sharedMesh;

                    if (mesh == null || !inScopeBlendshapes.TryGetValue(mesh, out var shapes) || !shapes.Contains(blendshapeName))
                    {
                        AnimationUtility.SetEditorCurve(newClip, binding, curve);
                    }
                }

                foreach (var objBinding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    AnimationUtility.SetObjectReferenceCurve(newClip, objBinding, 
                        AnimationUtility.GetObjectReferenceCurve(clip, objBinding));
                }

                foreach (var state in meshStates.Values)
                {
                    var shapeVertices = state.outputShapeVertices;
                    var zero = state.zero;
                    
                    state.newMesh.AddBlendShapeFrame(shapeName, 100.0f, shapeVertices, zero, zero);
                    
                    AnimationUtility.SetEditorCurve(newClip, new EditorCurveBinding()
                    {
                        // ReSharper disable once PossibleNullReferenceException
                        path = RuntimeUtil.AvatarRootPath(rendererInfo[state.mesh].FirstOrDefault().gameObject),
                        type = typeof(SkinnedMeshRenderer),
                        propertyName = BLENDSHAPE_PREFIX + shapeName
                    }, new AnimationCurve(new Keyframe(0, 100), new Keyframe(1, 100)));
                }
                
                overrides.Add(new KeyValuePair<AnimationClip, AnimationClip>(clip, newClip));
            }

            foreach (var aoc in animatorControllers)
            {
                aoc.ApplyOverrides(overrides);
            }

            foreach (var state in meshStates.Values)
            {
                state.Apply();
                
                Debug.Log("=== Dumping blendshape states ===");
                var smr = state.smr;
                var mesh = smr.sharedMesh;
                var shapes = mesh.blendShapeCount;

                for (int i = 0; i < shapes; i++)
                {
                    Debug.Log($"{mesh.GetBlendShapeName(i)}: {smr.GetBlendShapeWeight(i)}");
                }
            }
        }

        private Dictionary<Mesh, HashSet<SkinnedMeshRenderer>> GatherRenderers(BuildContext context, ICollection<Mesh> inScopeMeshes)
        {
            Dictionary<Mesh, HashSet<SkinnedMeshRenderer>> renderers =
                new Dictionary<Mesh, HashSet<SkinnedMeshRenderer>>();

            foreach (var smr in context.AvatarRootTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh;
                if (mesh == null || !inScopeMeshes.Contains(mesh)) continue;
                
                if (!renderers.TryGetValue(mesh, out var set))
                {
                    set = new HashSet<SkinnedMeshRenderer>();
                    renderers[mesh] = set;
                }

                set.Add(smr);
            }

            return renderers;
        }

        private Dictionary<Mesh, HashSet<string>> GatherInScopeShapes(
            BuildContext context, 
            List<AnimatorOverrideController> animatorControllers, 
            List<AnimationClip> inScopeClipsList
        )
        {
            var inScopeBlendshapes = new Dictionary<Mesh, HashSet<string>>();
            var inScopeClips = new HashSet<AnimationClip>(inScopeClipsList);

            // Register any meshes used by our in-scope clips
            foreach (var clip in inScopeClipsList)
            {
                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    if (binding.type != typeof(SkinnedMeshRenderer)
                        || !binding.propertyName.StartsWith(BLENDSHAPE_PREFIX)
                       )
                    {
                        continue;
                    }

                    var smr = context.AvatarRootTransform.Find(binding.path)
                        ?.GetComponentInChildren<SkinnedMeshRenderer>();
                    if (smr == null) continue;

                    HashSet<string> shapes = new HashSet<string>();
                    var mesh = smr.sharedMesh;
                    int n_shapes = mesh.blendShapeCount;

                    for (int i = 0; i < n_shapes; i++)
                    {
                        var blendShapeName = mesh.GetBlendShapeName(i);
                        if (blendShapeName.StartsWith("vrc.")) continue;
                        
                        shapes.Add(blendShapeName);
                    }

                    inScopeBlendshapes[mesh] = shapes;
                }
            }
            
            foreach (var aoc in animatorControllers)
            {
                foreach (var obj in
                         aoc.runtimeAnimatorController.ReferencedAssets())
                {
                    var clip = obj as AnimationClip;
                    if (clip == null) continue;
                    
                    // Remove any blendshapes referenced by foreign clips
                    if (inScopeClips.Contains(clip)) continue;

                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (binding.type != typeof(SkinnedMeshRenderer)
                            || !binding.propertyName.StartsWith(BLENDSHAPE_PREFIX)
                           )
                        {
                            continue;
                        }

                        var smr = context.AvatarRootTransform.Find(binding.path)
                            ?.GetComponentInChildren<SkinnedMeshRenderer>();
                        var mesh = smr?.sharedMesh;
                        if (mesh == null || !inScopeBlendshapes.TryGetValue(mesh, out var shapes)) continue;

                        var blendshapeName = binding.propertyName.Substring(BLENDSHAPE_PREFIX.Length);
                        shapes.Remove(blendshapeName);
                    }
                }
            }

            return inScopeBlendshapes;
        }

        private List<AnimationClip> GatherClips(BuildContext context)
        {
            return context.AvatarRootTransform.GetComponentsInChildren<BlendshapeAnimationBaker>(true)
                .SelectMany(c => c.motions)
                .Select(m => m as AnimationClip)
                .Where(m => m != null)
                .ToList();
        }

        private List<AnimatorOverrideController> GatherAnimators(BuildContext context)
        {
            var desc = context.AvatarDescriptor;
            var layers = desc.baseAnimationLayers;

            for (int i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer.type == VRCAvatarDescriptor.AnimLayerType.FX && !layer.isDefault &&
                    layer.animatorController != null)
                {
                    AnimatorOverrideController aoc = new AnimatorOverrideController();
                    aoc.name = "FX Overrides";
                    aoc.runtimeAnimatorController = layer.animatorController;
                    layer.animatorController = aoc;
                    layers[i] = layer;
                    desc.baseAnimationLayers = layers;

                    return new List<AnimatorOverrideController>() {aoc};
                }
            }
            
            return new List<AnimatorOverrideController>();
        }
    }
}