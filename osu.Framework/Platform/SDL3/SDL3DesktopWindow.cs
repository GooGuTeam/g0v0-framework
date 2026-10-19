// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE-OSU file in the repository root for full licence text.

using static SDL.SDL3;

namespace osu.Framework.Platform.SDL3
{
    internal class SDL3DesktopWindow : SDL3Window
    {
        public SDL3DesktopWindow(GraphicsSurfaceType surfaceType, string appName)
            : base(surfaceType, appName)
        {
        }

        protected override unsafe void UpdateWindowStateAndSize(WindowState state, Display display, DisplayMode displayMode)
        {
            if (state is WindowState.Normal or WindowState.Maximised)
            {
                SDL_SetWindowFullscreen(SDLWindowHandle, false).LogErrorIfFailed();
                SDL_SetWindowBordered(SDLWindowHandle, true).LogErrorIfFailed();
                SDL_RestoreWindow(SDLWindowHandle).LogErrorIfFailed();
            }

            base.UpdateWindowStateAndSize(state, display, displayMode);
        }
    }
}
