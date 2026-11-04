// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Input.Handlers;
using osu.Framework.Logging;

namespace osu.Framework.Platform
{
    /// <summary>
    /// A <see cref="GameHost"/> that is never run, standing in for the host of a private scene drawn by an <see cref="OfflineRenderContext"/>.
    /// </summary>
    /// <remarks>
    /// Components that resolve a host for its renderer, storage, texture loaders or screen suspension work unchanged.
    /// It has no window, input handlers or threads: <see cref="GameHost.Window"/> and <see cref="GameHost.UpdateThread"/> are <c>null</c>,
    /// so components cannot pick up a live, wall-clock driven clock and keep the scene's deterministic one.
    /// Create it with <see cref="OfflineRenderContext.CreateSceneHost"/>; it is valid for the context's lifetime and holds nothing to dispose.
    /// </remarks>
    public sealed class OfflineSceneHost : GameHost
    {
        internal OfflineSceneHost(IRenderer renderer, Storage storage)
            : base(@"offline-scene")
        {
            Renderer = renderer;
            Storage = storage;
            CacheStorage = storage;
            AvailableInputHandlers = ImmutableArray<InputHandler>.Empty;
        }

        public override Storage GetStorage(string path) => new NativeStorage(path, this);

        public override bool OpenFileExternally(string filename)
        {
            Logger.Log($"An offline scene requested file \"{filename}\" to be opened.");
            return false;
        }

        public override bool PresentFileExternally(string filename)
        {
            Logger.Log($"An offline scene requested file \"{filename}\" to be shown.");
            return false;
        }

        public override void OpenUrlExternally(string url) => Logger.Log($"An offline scene requested URL \"{url}\" to be opened.");

        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => throw new NotSupportedException(@"Offline scenes have no window.");

        protected override Clipboard CreateClipboard() => new HeadlessClipboard();

        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers() => Array.Empty<InputHandler>();

        protected override void Dispose(bool disposing)
        {
            // Never run, so there is nothing to stop. The base implementation would flush the process-wide logger for shutdown.
        }
    }
}
