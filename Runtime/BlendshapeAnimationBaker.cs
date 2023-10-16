using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace nadena.dev.modular_avatar.incubator {
    public class BlendshapeAnimationBaker : MonoBehaviour, IEditorOnly
    {
        public List<Motion> motions = new List<Motion>();
    }
}