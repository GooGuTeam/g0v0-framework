// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Veldrid;

namespace osu.Framework.Graphics.Veldrid.Textures
{
    /// <summary>
    /// Something attached to a texture unit of the pipeline, which provides the resource set bound for that unit's layout.
    /// </summary>
    internal interface IVeldridResourceSetSource
    {
        ResourceSet GetResourceSet(ResourceFactory factory, ResourceLayout layout);
    }
}
