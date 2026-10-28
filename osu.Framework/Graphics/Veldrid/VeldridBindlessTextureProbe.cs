// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using osu.Framework.Graphics.Rendering;
using Veldrid;
using Vulkan;

namespace osu.Framework.Graphics.Veldrid
{
    internal static class VeldridBindlessTextureProbe
    {
        public static void Run(GraphicsDevice device, Renderer renderer)
        {
            try
            {
                run(device, renderer);
            }
            catch (Exception e)
            {
                // Optional diagnostics must not turn successful device initialisation into a failure.
                Logging.Logger.Log($"Bindless device query failed: {e.Message}");
            }
        }

        private static unsafe void run(GraphicsDevice device, Renderer renderer)
        {
            if (renderer.GraphicsProbeCache == null || !renderer.ProbeBindlessTextures)
                return;

            string identity = $"{device.BackendType}|{device.VendorName}|{device.DeviceName}";
            switch (device.BackendType)
            {
                case GraphicsBackend.Vulkan:
                {
                    var info = device.GetVulkanInfo();
                    VkPhysicalDeviceProperties properties;
                    VulkanNative.vkGetPhysicalDeviceProperties(info.PhysicalDevice, &properties);
                    identity += $"|{properties.vendorID}|{properties.deviceID}|{properties.apiVersion}|{properties.driverVersion}|{info.DriverName}|{info.DriverInfo}";
                    uint apiVersion = properties.apiVersion;
                    identity += $"|descriptor-arrays-v1|{info.TextureDescriptorIndexingEnabled}|{info.SampledTextureArrayLimit}";
                    renderer.ProbeBindless(identity, () => probeVulkan(info, apiVersion));
                    break;
                }

                case GraphicsBackend.OpenGL:
                case GraphicsBackend.OpenGLES:
                {
                    var info = device.GetOpenGLInfo();
                    identity += $"|{info.Version}|{info.ShadingLanguageVersion}|{string.Join(' ', info.Extensions)}";
                    renderer.ProbeBindless(identity, () => info.Extensions.Contains("GL_ARB_bindless_texture")
                        ? new BindlessTextureProbeResult(BindlessTextureProbeStatus.FeaturesReported,
                            "ARB_bindless_texture advertised. Veldrid GL handle residency and shader sampling are not validated; resource layouts still use bound textures.")
                        : new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unsupported, "ARB_bindless_texture is not advertised by this GL device."));
                    break;
                }

                case GraphicsBackend.Direct3D11:
                    renderer.ProbeBindless(identity, () => new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unsupported,
                        "The active D3D11 backend has no bindless descriptor path. D3D12 capabilities of this GPU do not apply to this device."));
                    break;

                default:
                    renderer.ProbeBindless(identity, () => new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unknown,
                        "This backend does not expose the required bindless capability query. Metal argument-buffer support must not be inferred from texture limits."));
                    break;
            }
        }

        private static unsafe BindlessTextureProbeResult probeVulkan(BackendInfoVulkan info, uint apiVersion)
        {
            bool core12 = apiVersion >= ((1u << 22) | (2u << 12));
            if (!core12 && !info.AvailableDeviceExtensions.Any(e => e.Name == "VK_EXT_descriptor_indexing"))
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unsupported, "Neither Vulkan 1.2 nor VK_EXT_descriptor_indexing is advertised.");

            IntPtr address;
            fixed (byte* name = "vkGetPhysicalDeviceFeatures2\0"u8)
                address = VulkanNative.vkGetInstanceProcAddr(info.Instance, name);
            if (address == IntPtr.Zero)
            {
                fixed (byte* name = "vkGetPhysicalDeviceFeatures2KHR\0"u8)
                    address = VulkanNative.vkGetInstanceProcAddr(info.Instance, name);
            }
            if (address == IntPtr.Zero)
                return new BindlessTextureProbeResult(BindlessTextureProbeStatus.Unknown, "Descriptor indexing advertised but Features2 query is unavailable on this instance.");

            DescriptorIndexingFeatures indexing = new DescriptorIndexingFeatures { SType = 1000161001 };
            PhysicalDeviceFeatures2 features = new PhysicalDeviceFeatures2 { SType = 1000059000, Next = (IntPtr)(&indexing) };
            Marshal.GetDelegateForFunctionPointer<GetFeatures2>(address)(info.PhysicalDevice, &features);

            // Field order is VkPhysicalDeviceDescriptorIndexingFeatures (20 VkBool32 values).
            bool nonUniform = indexing.Flags[4] != 0;
            bool updateAfterBind = indexing.Flags[11] != 0;
            bool partiallyBound = indexing.Flags[17] != 0;
            bool variableCount = indexing.Flags[18] != 0;
            bool runtimeArray = indexing.Flags[19] != 0;
            bool required = nonUniform && runtimeArray;
            return new BindlessTextureProbeResult(required ? BindlessTextureProbeStatus.FeaturesReported : BindlessTextureProbeStatus.Unsupported,
                $"Descriptor indexing: sampledImageNonUniform={nonUniform}, runtimeArray={runtimeArray}, updateAfterBind={updateAfterBind}, partiallyBound={partiallyBound}, variableCount={variableCount}. " +
                $"LogicalDeviceEnabled={info.TextureDescriptorIndexingEnabled}, sampledArrayLimit={info.SampledTextureArrayLimit}. " +
                "Fixed, fully populated texture arrays are implemented in Veldrid. Framework batching and indexed shaders are not connected; bindless rendering remains disabled.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PhysicalDeviceFeatures2
        {
            public uint SType;
            public IntPtr Next;
            public VkPhysicalDeviceFeatures Features;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct DescriptorIndexingFeatures
        {
            public uint SType;
            public IntPtr Next;
            public fixed uint Flags[20];
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private unsafe delegate void GetFeatures2(IntPtr physicalDevice, PhysicalDeviceFeatures2* features);
    }
}
