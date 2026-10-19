// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#define VK_USE_PLATFORM_WIN32_KHR
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <stdio.h>
#include <vector>
#include <stdexcept>
#include <string.h>

static void check(HRESULT hr) { if (FAILED(hr)) { printf("HRESULT=%08lx\n", (unsigned long)hr); throw std::runtime_error("D3D failure"); } }
static void checkVk(VkResult r) { if (r != VK_SUCCESS) { printf("VkResult=%d\n", r); throw std::runtime_error("Vulkan failure"); } }
template<class T> static void release(T *&p) { if (p) p->Release(); p = nullptr; }
#define LOAD(name) auto name = reinterpret_cast<PFN_##name>(get(instance, #name)); if (!name) throw std::runtime_error(#name)

// Isolated capability + round-trip probe. CPU mapping is test verification only.
// No game device or global settings are modified. Objects live until process exit on failure.
int main() {
    try {
        HMODULE loader = LoadLibraryW(L"vulkan-1.dll");
        if (!loader) throw std::runtime_error("Vulkan loader unavailable");
        FARPROC address = GetProcAddress(loader, "vkGetInstanceProcAddr");
        PFN_vkGetInstanceProcAddr get;
        static_assert(sizeof(get) == sizeof(address));
        memcpy(&get, &address, sizeof(get));
        if (!get) throw std::runtime_error("Vulkan entry point unavailable");
        VkInstance instance = VK_NULL_HANDLE;
        LOAD(vkCreateInstance);
        VkApplicationInfo app = {}; app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO; app.apiVersion = VK_API_VERSION_1_1;
        VkInstanceCreateInfo ci = {}; ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO; ci.pApplicationInfo = &app;
        checkVk(vkCreateInstance(&ci, nullptr, &instance));
        LOAD(vkEnumeratePhysicalDevices); LOAD(vkGetPhysicalDeviceProperties2); LOAD(vkGetPhysicalDeviceImageFormatProperties2);
        LOAD(vkGetPhysicalDeviceQueueFamilyProperties); LOAD(vkCreateDevice); LOAD(vkGetDeviceProcAddr);
        LOAD(vkGetPhysicalDeviceMemoryProperties);
        uint32_t count = 0; checkVk(vkEnumeratePhysicalDevices(instance, &count, nullptr));
        std::vector<VkPhysicalDevice> devices(count); checkVk(vkEnumeratePhysicalDevices(instance, &count, devices.data()));
        IDXGIFactory1 *factory = nullptr; check(CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void **)&factory));
        unsigned passed = 0;
        for (auto physical : devices) {
            VkPhysicalDeviceIDProperties id = {}; id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
            VkPhysicalDeviceProperties2 props = {}; props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2; props.pNext = &id;
            vkGetPhysicalDeviceProperties2(physical, &props);
            printf("Testing %s\n", props.properties.deviceName); fflush(stdout);
            if (!id.deviceLUIDValid) { puts("SKIP: no valid LUID"); continue; }
            IDXGIAdapter1 *adapter = nullptr;
            for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; i++) {
                DXGI_ADAPTER_DESC1 desc; check(adapter->GetDesc1(&desc));
                if (memcmp(&desc.AdapterLuid, id.deviceLUID, VK_LUID_SIZE) == 0) break;
                release(adapter);
            }
            if (!adapter) { puts("SKIP: no DXGI LUID match"); continue; }
            VkPhysicalDeviceExternalImageFormatInfo external = {}; external.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO;
            external.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
            VkPhysicalDeviceImageFormatInfo2 format = {}; format.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2;
            format.pNext = &external; format.format = VK_FORMAT_B8G8R8A8_UNORM; format.type = VK_IMAGE_TYPE_2D;
            format.tiling = VK_IMAGE_TILING_OPTIMAL; format.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT;
            VkExternalImageFormatProperties externalProps = {}; externalProps.sType = VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES;
            VkImageFormatProperties2 formatProps = {}; formatProps.sType = VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2; formatProps.pNext = &externalProps;
            VkResult supported = vkGetPhysicalDeviceImageFormatProperties2(physical, &format, &formatProps);
            if (supported != VK_SUCCESS || !(externalProps.externalMemoryProperties.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT)) {
                puts("SKIP: D3D11_TEXTURE BGRA8 not importable"); release(adapter); continue;
            }
            ID3D11Device *d3d = nullptr; ID3D11DeviceContext *context = nullptr;
            check(D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                D3D11_SDK_VERSION, &d3d, nullptr, &context)); release(adapter);
            D3D11_TEXTURE2D_DESC desc = {}; desc.Width = desc.Height = 64; desc.MipLevels = desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
            ID3D11Texture2D *texture = nullptr; check(d3d->CreateTexture2D(&desc, nullptr, &texture));
            IDXGIResource1 *resource = nullptr; IDXGIKeyedMutex *mutex = nullptr;
            check(texture->QueryInterface(__uuidof(IDXGIResource1), (void **)&resource));
            check(texture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void **)&mutex));
            HANDLE shared = nullptr; check(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &shared));
            uint32_t families = 0; vkGetPhysicalDeviceQueueFamilyProperties(physical, &families, nullptr);
            std::vector<VkQueueFamilyProperties> queues(families); vkGetPhysicalDeviceQueueFamilyProperties(physical, &families, queues.data());
            uint32_t family = 0; while (family < families && !(queues[family].queueFlags & VK_QUEUE_GRAPHICS_BIT)) family++;
            if (family == families) throw std::runtime_error("no graphics queue");
            float priority = 1;
            VkDeviceQueueCreateInfo queueCi = {}; queueCi.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO; queueCi.queueFamilyIndex = family; queueCi.queueCount = 1; queueCi.pQueuePriorities = &priority;
            const char *extensions[] = {"VK_KHR_external_memory_win32", "VK_KHR_win32_keyed_mutex"};
            VkDeviceCreateInfo deviceCi = {}; deviceCi.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
            deviceCi.queueCreateInfoCount = 1; deviceCi.pQueueCreateInfos = &queueCi; deviceCi.enabledExtensionCount = 2; deviceCi.ppEnabledExtensionNames = extensions;
            VkDevice device; checkVk(vkCreateDevice(physical, &deviceCi, nullptr, &device));
#define DEV(name) auto name = reinterpret_cast<PFN_##name>(vkGetDeviceProcAddr(device, #name)); if (!name) throw std::runtime_error(#name)
            DEV(vkCreateImage); DEV(vkGetImageMemoryRequirements); DEV(vkGetMemoryWin32HandlePropertiesKHR);
            DEV(vkAllocateMemory); DEV(vkBindImageMemory); DEV(vkGetDeviceQueue); DEV(vkCreateCommandPool);
            DEV(vkAllocateCommandBuffers); DEV(vkBeginCommandBuffer); DEV(vkEndCommandBuffer);
            DEV(vkCmdPipelineBarrier); DEV(vkCmdClearColorImage); DEV(vkQueueSubmit); DEV(vkCreateFence); DEV(vkWaitForFences); DEV(vkResetFences); DEV(vkResetCommandBuffer);
            DEV(vkDeviceWaitIdle); DEV(vkDestroyFence); DEV(vkDestroyCommandPool); DEV(vkDestroyImage); DEV(vkFreeMemory); DEV(vkDestroyDevice);
            VkExternalMemoryImageCreateInfo imageExternal = {}; imageExternal.sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO; imageExternal.handleTypes = external.handleType;
            VkImageCreateInfo imageCi = {}; imageCi.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO; imageCi.pNext = &imageExternal;
            imageCi.imageType = VK_IMAGE_TYPE_2D; imageCi.format = format.format; imageCi.extent = {64,64,1}; imageCi.mipLevels = imageCi.arrayLayers = 1;
            imageCi.samples = VK_SAMPLE_COUNT_1_BIT; imageCi.tiling = VK_IMAGE_TILING_OPTIMAL; imageCi.usage = format.usage;
            VkImage image; checkVk(vkCreateImage(device, &imageCi, nullptr, &image));
            VkMemoryRequirements req; vkGetImageMemoryRequirements(device, image, &req);
            VkMemoryWin32HandlePropertiesKHR handleProps = {}; handleProps.sType = VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR;
            checkVk(vkGetMemoryWin32HandlePropertiesKHR(device, external.handleType, shared, &handleProps));
            VkPhysicalDeviceMemoryProperties memoryProps; vkGetPhysicalDeviceMemoryProperties(physical, &memoryProps);
            uint32_t memoryType = 0;
            while (memoryType < memoryProps.memoryTypeCount && !((req.memoryTypeBits & handleProps.memoryTypeBits) & (1u << memoryType))) memoryType++;
            if (memoryType == memoryProps.memoryTypeCount) throw std::runtime_error("no compatible memory type");
            VkMemoryDedicatedAllocateInfo dedicated = {}; dedicated.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO; dedicated.image = image;
            VkImportMemoryWin32HandleInfoKHR import = {}; import.sType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR; import.pNext = &dedicated; import.handleType = external.handleType; import.handle = shared;
            VkMemoryAllocateInfo allocation = {}; allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO; allocation.pNext = &import; allocation.allocationSize = req.size; allocation.memoryTypeIndex = memoryType;
            VkDeviceMemory memory; checkVk(vkAllocateMemory(device, &allocation, nullptr, &memory));
            CloseHandle(shared); release(resource);
            checkVk(vkBindImageMemory(device, image, memory, 0));
            VkQueue queue; vkGetDeviceQueue(device, family, 0, &queue);
            VkCommandPoolCreateInfo poolCi = {}; poolCi.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO; poolCi.queueFamilyIndex = family; poolCi.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
            VkCommandPool pool; checkVk(vkCreateCommandPool(device, &poolCi, nullptr, &pool));
            VkCommandBufferAllocateInfo cmdCi = {}; cmdCi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO; cmdCi.commandPool = pool; cmdCi.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cmdCi.commandBufferCount = 1;
            VkCommandBuffer cmd; checkVk(vkAllocateCommandBuffers(device, &cmdCi, &cmd));
            VkFenceCreateInfo fenceCi = {}; fenceCi.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
            VkFence fence; checkVk(vkCreateFence(device, &fenceCi, nullptr, &fence));
            desc.BindFlags = desc.MiscFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            ID3D11Texture2D *readback = nullptr; check(d3d->CreateTexture2D(&desc, nullptr, &readback));
            for (int frame = 0; frame < 10000; frame++) {
                checkVk(vkResetCommandBuffer(cmd, 0));
                VkCommandBufferBeginInfo begin = {}; begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO; checkVk(vkBeginCommandBuffer(cmd, &begin));
                VkImageMemoryBarrier barrier = {}; barrier.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
                barrier.oldLayout = frame ? VK_IMAGE_LAYOUT_GENERAL : VK_IMAGE_LAYOUT_UNDEFINED; barrier.newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
                barrier.srcQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL; barrier.dstQueueFamilyIndex = family;
                barrier.image = image; barrier.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1}; barrier.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
                vkCmdPipelineBarrier(cmd, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0,nullptr,0,nullptr,1,&barrier);
                VkClearColorValue colour = {}; colour.float32[frame % 2 ? 1 : 0] = 1; colour.float32[3] = 1;
                vkCmdClearColorImage(cmd, image, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &colour, 1, &barrier.subresourceRange);
                barrier.oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; barrier.newLayout = VK_IMAGE_LAYOUT_GENERAL;
                barrier.srcQueueFamilyIndex = family; barrier.dstQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL;
                barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; barrier.dstAccessMask = 0;
                vkCmdPipelineBarrier(cmd,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT,0,0,nullptr,0,nullptr,1,&barrier);
                checkVk(vkEndCommandBuffer(cmd));
                uint64_t acquireKey = 0, releaseKey = 1; uint32_t timeout = 1000;
                VkWin32KeyedMutexAcquireReleaseInfoKHR keys = {}; keys.sType = VK_STRUCTURE_TYPE_WIN32_KEYED_MUTEX_ACQUIRE_RELEASE_INFO_KHR;
                keys.acquireCount = keys.releaseCount = 1; keys.pAcquireSyncs = keys.pReleaseSyncs = &memory; keys.pAcquireKeys = &acquireKey; keys.pReleaseKeys = &releaseKey; keys.pAcquireTimeouts = &timeout;
                VkSubmitInfo submit = {}; submit.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO; submit.pNext = &keys; submit.commandBufferCount = 1; submit.pCommandBuffers = &cmd;
                checkVk(vkQueueSubmit(queue,1,&submit,fence)); checkVk(vkWaitForFences(device,1,&fence,VK_TRUE,5000000000ULL));
                HRESULT acquired = mutex->AcquireSync(1,1000); if (acquired != S_OK) throw std::runtime_error("keyed mutex acquire timeout/abandoned");
                context->CopyResource(readback, texture);
                D3D11_MAPPED_SUBRESOURCE mapped; check(context->Map(readback,0,D3D11_MAP_READ,0,&mapped));
                auto pixel = static_cast<unsigned char *>(mapped.pData);
                bool valid = pixel[frame % 2 ? 1 : 2] == 255 && pixel[frame % 2 ? 2 : 1] == 0;
                context->Unmap(readback,0); check(mutex->ReleaseSync(0));
                if (!valid) throw std::runtime_error("shared pixel mismatch");
                checkVk(vkResetFences(device,1,&fence));
            }
            checkVk(vkDeviceWaitIdle(device)); vkDestroyFence(device,fence,nullptr); vkDestroyCommandPool(device,pool,nullptr);
            vkDestroyImage(device,image,nullptr); vkFreeMemory(device,memory,nullptr); vkDestroyDevice(device,nullptr);
            release(readback); release(mutex); release(texture); release(context); release(d3d);
            puts("PASS: same-LUID Vulkan -> D3D11 shared BGRA, 10000 alternating frames/keyed-mutex round trips"); passed++;
        }
        release(factory); LOAD(vkDestroyInstance); vkDestroyInstance(instance,nullptr); FreeLibrary(loader);
        return passed ? 0 : 2;
    } catch (const std::exception &error) { fprintf(stderr,"FAIL: %s\n",error.what()); return 1; }
}
