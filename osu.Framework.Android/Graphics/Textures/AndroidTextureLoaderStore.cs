// Copyright (c) ppy Pty Ltd <contact@ppy.sh> & GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE & LICENCE-OSU file in the repository root for full licence text.

using osu.Framework.Graphics.Textures;
using osu.Framework.IO.Stores;

namespace osu.Framework.Android.Graphics.Textures
{
    internal class AndroidTextureLoaderStore : TextureLoaderStore
    {
        public AndroidTextureLoaderStore(IResourceStore<byte[]> store)
            : base(store)
        {
        }
    }
}
