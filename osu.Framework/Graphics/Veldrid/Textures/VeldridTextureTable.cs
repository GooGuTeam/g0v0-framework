// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Rendering;
using Veldrid;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    /// <summary>
    /// The textures in a bindless table (see <see cref="VeldridBindlessTextures"/>), as one resource set.
    /// </summary>
    /// <remarks>
    /// Descriptor sets are immutable here, so a new set is created when an entry or a texture's view changes. The renderer only replaces
    /// entries no pending vertex samples, so the set current at a draw is right for the whole batch.
    /// </remarks>
    internal sealed class VeldridTextureTable : IVeldridResourceSetSource, IDisposable
    {
        private readonly IVeldridRenderer renderer;
        private readonly VeldridTexture?[] textures = new VeldridTexture?[VeldridBindlessTextures.TABLE_SIZE];
        private readonly TextureView[] views = new TextureView[VeldridBindlessTextures.TABLE_SIZE];
        private readonly TextureView?[] setViews = new TextureView?[VeldridBindlessTextures.TABLE_SIZE];
        private readonly Sampler linearSampler;
        private readonly Sampler nearestSampler;

        private ResourceSet? set;
        private ResourceLayout? setLayout;

        public VeldridTextureTable(IVeldridRenderer renderer)
        {
            this.renderer = renderer;
            linearSampler = createSampler(SamplerFilter.MinLinearMagLinearMipLinear);
            nearestSampler = createSampler(SamplerFilter.MinPointMagPointMipPoint);
        }

        public void Set(int slot, VeldridTexture texture) => textures[slot] = texture;

        public ResourceSet GetResourceSet(ResourceFactory factory, ResourceLayout layout)
        {
            bool changed = set == null || layout != setLayout;

            for (int i = 0; i < views.Length; i++)
            {
                views[i] = textures[i]?.GetBindlessView() ?? throw new InvalidOperationException($"Bindless texture slot {i} has no texture.");
                changed |= views[i] != setViews[i];
            }

            if (!changed)
                return set!;

            if (set != null)
                renderer.ScheduleDisposal(static s => s.Dispose(), set);

            set = factory.CreateResourceSet(new ResourceSetDescription(layout, new TextureViewArray((TextureView[])views.Clone()), linearSampler, nearestSampler));
            setLayout = layout;
            Array.Copy(views, setViews, views.Length);
            return set;
        }

        private Sampler createSampler(SamplerFilter filter)
        {
            var description = new SamplerDescription
            {
                AddressModeU = SamplerAddressMode.Clamp,
                AddressModeV = SamplerAddressMode.Clamp,
                AddressModeW = SamplerAddressMode.Clamp,
                Filter = filter,
                MinimumLod = 0,
                MaximumLod = IRenderer.MAX_MIPMAP_LEVELS,
                MaximumAnisotropy = 0,
            };

            return renderer.Factory.CreateSampler(ref description);
        }

        public void Dispose()
        {
            set?.Dispose();
            linearSampler.Dispose();
            nearestSampler.Dispose();
        }
    }
}
