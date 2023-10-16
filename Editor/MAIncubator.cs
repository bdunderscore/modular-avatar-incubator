using nadena.dev.modular_avatar.incubator.editor;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(MAIncubator))]

namespace nadena.dev.modular_avatar.incubator.editor
{
    internal class MAIncubator : Plugin<MAIncubator>
    {
        protected override void Configure()
        {
            InPhase(BuildPhase.Resolving)
                // XXX: Compatibility issues due to AAO caching meshes very early
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .BeforePlugin("nadena.dev.modular-avatar")
                .Run(BlendshapeAnimationBakerPass.Instance);
        }
    }
}