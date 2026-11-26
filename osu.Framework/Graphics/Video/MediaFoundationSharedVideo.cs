// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Veldrid;
using osu.Framework.Graphics.Veldrid.Textures;
using osu.Framework.Imaging;
using Veldrid;
using Texture = Veldrid.Texture;

namespace osu.Framework.Graphics.Video
{
    /// <summary>
    /// The renderer's side of zero-copy video decoding (<see cref="Configuration.FrameworkSetting.ZeroCopyVideoDecoding"/>):
    /// opens the decoder's shared slot textures on the Direct3D 11 or Vulkan device and copies each frame into a video texture on the GPU.
    /// </summary>
    internal sealed class MediaFoundationSharedVideo : IDisposable
    {
        // VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, the layout g0_vshare_vk_prepare leaves the video planes in.
        private const uint shader_read_only_optimal = 5;

        private readonly IVeldridRenderer renderer;
        private readonly bool vulkan;
        private readonly Dictionary<(int generation, int slot), IntPtr> consumers = new Dictionary<(int, int), IntPtr>();
        private readonly Lock sync = new Lock();
        private int latestGeneration;
        private bool disposed;

        private MediaFoundationSharedVideo(IVeldridRenderer renderer, bool vulkan)
        {
            this.renderer = renderer;
            this.vulkan = vulkan;
        }

        /// <summary>
        /// Creates the renderer's side if it can take shared frames: a Direct3D 11 renderer, or a Vulkan renderer with capture interop.
        /// </summary>
        /// <param name="renderer">The renderer that draws the video.</param>
        /// <param name="adapter">The LUID of the renderer's adapter, on which the decoder must run.</param>
        public static MediaFoundationSharedVideo? TryCreate(IRenderer renderer, out long adapter)
        {
            adapter = 0;

            if (renderer is not IVeldridRenderer veldrid || !veldrid.IsInitialised)
                return null;

            var device = veldrid.Device;

            switch (device.BackendType)
            {
                case GraphicsBackend.Direct3D11:
                    return g0_vshare_d3d11_luid(device.GetD3D11Info().Device, out adapter) >= 0 ? new MediaFoundationSharedVideo(veldrid, false) : null;

                case GraphicsBackend.Vulkan:
                    var info = device.GetVulkanInfo();
                    if (!info.Win32CaptureInteropEnabled)
                        return null;

                    return g0_vshare_vk_luid(info.Instance, info.PhysicalDevice, out adapter) >= 0 ? new MediaFoundationSharedVideo(veldrid, true) : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Copies a decoded frame into the video texture planes. Must run on the draw thread.
        /// </summary>
        /// <returns>Whether the frame was copied; if not, the decoder must take its slot back.</returns>
        public bool Copy(SharedFrame frame, int width, int height, Texture y, Texture u, Texture v)
        {
            lock (sync)
            {
                if (disposed)
                    return false;

                // Slots of an earlier frame size: a frame left over from before the size changed is not worth showing,
                // and the consumers of those slots are closed once the first frame of the new size arrives.
                if (frame.Generation < latestGeneration)
                    return false;

                if (frame.Generation > latestGeneration)
                {
                    closeConsumers(frame.Generation);
                    latestGeneration = frame.Generation;
                }

                if (!consumers.TryGetValue((frame.Generation, frame.Slot), out IntPtr consumer))
                {
                    var device = renderer.Device;
                    int result;

                    if (vulkan)
                    {
                        var info = device.GetVulkanInfo();
                        result = g0_vshare_vk_open(info.Instance, info.PhysicalDevice, info.Device, info.GraphicsQueueFamilyIndex, frame.Handle,
                            (uint)frame.TextureWidth, (uint)frame.TextureHeight, out consumer);
                    }
                    else
                        result = g0_vshare_d3d11_open(device.GetD3D11Info().Device, frame.Handle, out consumer);

                    Marshal.ThrowExceptionForHR(result);
                    consumers.Add((frame.Generation, frame.Slot), consumer);
                }

                if (vulkan)
                {
                    var info = renderer.Device.GetVulkanInfo();
                    Marshal.ThrowExceptionForHR(g0_vshare_vk_prepare(consumer, info.GetVkImage(y), info.GetVkImage(u), info.GetVkImage(v), width, height,
                        out IntPtr command, out IntPtr next, out ulong fence));
                    info.SubmitExternalCapture(command, next, fence);
                    g0_vshare_vk_submitted(consumer);

                    info.OverrideImageLayout(y, shader_read_only_optimal);
                    info.OverrideImageLayout(u, shader_read_only_optimal);
                    info.OverrideImageLayout(v, shader_read_only_optimal);
                }
                else
                {
                    var info = renderer.Device.GetD3D11Info();
                    Marshal.ThrowExceptionForHR(g0_vshare_d3d11_copy(consumer, info.GetTexturePointer(y), info.GetTexturePointer(u), info.GetTexturePointer(v), width, height));
                }

                return true;
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                    return;

                disposed = true;
                closeConsumers(int.MaxValue);
            }
        }

        /// <summary>
        /// Closes the consumers of slots older than <paramref name="generation"/>.
        /// </summary>
        private void closeConsumers(int generation)
        {
            foreach (var (key, consumer) in consumers.Where(c => c.Key.generation < generation).ToArray())
            {
                if (vulkan)
                    g0_vshare_vk_close(consumer);
                else
                    g0_vshare_d3d11_close(consumer);

                consumers.Remove(key);
            }
        }

        /// <summary>
        /// A frame in one of the decoder's shared slot textures, laid out as described in native/gpu-video/video_decoder.h.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SharedFrame
        {
            public IntPtr Handle;
            public int Slot;
            public int Generation;
            public int TextureWidth;
            public int TextureHeight;
        }

        /// <summary>
        /// Copies a shared frame into a video texture when uploaded, and returns its slot to the decoder when disposed.
        /// </summary>
        internal sealed class Upload : IVeldridSharedVideoUpload
        {
            private readonly MediaFoundationVideoDecoder decoder;
            private readonly SharedFrame frame;
            private bool copied;
            private int released;

            public Upload(MediaFoundationVideoDecoder decoder, SharedFrame frame, int width, int height)
            {
                this.decoder = decoder;
                this.frame = frame;
                Bounds = new RectangleI(0, 0, width, height);
            }

            public ReadOnlySpan<Rgba32> Data => ReadOnlySpan<Rgba32>.Empty;

            public int Level => 0;

            public RectangleI Bounds { get; set; }

            public void CopyTo(IVeldridRenderer renderer, Texture y, Texture u, Texture v) => copied = decoder.CopySharedFrame(frame, Bounds.Width, Bounds.Height, y, u, v);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref released, 1) == 0)
                    decoder.ReleaseSharedFrame(frame, copied);
            }
        }

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_d3d11_luid(IntPtr device, out long luid);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_d3d11_open(IntPtr device, IntPtr handle, out IntPtr share);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_d3d11_copy(IntPtr share, IntPtr y, IntPtr u, IntPtr v, int width, int height);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vshare_d3d11_close(IntPtr share);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_vk_luid(IntPtr instance, IntPtr physicalDevice, out long luid);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_vk_open(IntPtr instance, IntPtr physicalDevice, IntPtr device, uint family, IntPtr handle, uint width, uint height,
                                                    out IntPtr share);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int g0_vshare_vk_prepare(IntPtr share, ulong y, ulong u, ulong v, int width, int height, out IntPtr command, out IntPtr next,
                                                       out ulong fence);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vshare_vk_submitted(IntPtr share);

        [DllImport(MediaFoundationLibrary.NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void g0_vshare_vk_close(IntPtr share);
    }
}
