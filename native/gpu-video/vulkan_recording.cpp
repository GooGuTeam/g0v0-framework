// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#define VK_USE_PLATFORM_WIN32_KHR
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <dxgi1_2.h>
#include "recorder.cpp"
#include "video_decoder.h"
#include <stdexcept>

// One worker owns this bridge; queue submission is marshalled to Veldrid's draw thread.
// No source image is touched until its framework copy fence is signalled.
struct VulkanRecording {
    HMODULE loader = nullptr;
    VkInstance instance = VK_NULL_HANDLE;
    VkDevice device = VK_NULL_HANDLE;
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkCommandBuffer commands = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    ID3D11Device *d3d = nullptr;
    ID3D11Texture2D *texture = nullptr;
    IDXGIKeyedMutex *mutex = nullptr;
    Recording *recording = nullptr;
    uint32_t family = 0, width = 0, height = 0;
    bool initial = true, submitted = false;
    VkWin32KeyedMutexAcquireReleaseInfoKHR keys = {};
    uint64_t acquireKey = 0, releaseKey = 1;
    uint32_t timeout = 1000;
#define PROC(name) PFN_##name name = nullptr
    PROC(vkCreateImage); PROC(vkDestroyImage); PROC(vkGetImageMemoryRequirements);
    PROC(vkGetMemoryWin32HandlePropertiesKHR); PROC(vkAllocateMemory); PROC(vkFreeMemory); PROC(vkBindImageMemory);
    PROC(vkCreateCommandPool); PROC(vkDestroyCommandPool); PROC(vkAllocateCommandBuffers);
    PROC(vkResetCommandBuffer); PROC(vkBeginCommandBuffer); PROC(vkEndCommandBuffer);
    PROC(vkCmdPipelineBarrier); PROC(vkCmdCopyImage); PROC(vkCreateFence); PROC(vkDestroyFence);
    PROC(vkWaitForFences); PROC(vkResetFences);
#undef PROC
};
static void vkcheck(VkResult result) { if (result != VK_SUCCESS) throw std::runtime_error("Vulkan recording operation failed"); }

API HRESULT g0_vk_close(VulkanRecording *r) {
    if (!r) return E_INVALIDARG;
    // Only submissions which actually reached the queue are waited for. Never free
    // pending command resources after a timeout; retain them until device/process teardown.
    if (r->submitted && r->vkWaitForFences(r->device, 1, &r->fence, VK_TRUE, 5000000000ULL) != VK_SUCCESS)
        return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    HRESULT hr = r->recording ? g0_record_close(r->recording) : S_OK;
    if (r->fence) r->vkDestroyFence(r->device,r->fence,nullptr);
    if (r->pool) r->vkDestroyCommandPool(r->device,r->pool,nullptr);
    if (r->image) r->vkDestroyImage(r->device,r->image,nullptr);
    if (r->memory) r->vkFreeMemory(r->device,r->memory,nullptr);
    release(r->mutex); release(r->texture); release(r->d3d);
    if (r->loader) FreeLibrary(r->loader);
    delete r; return hr;
}

static HRESULT open_vulkan(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
    uint32_t width, uint32_t height, const wchar_t *path, UINT audioRate, UINT channels, VulkanRecording **output,
    PacketCallback callback = nullptr, void *callbackContext = nullptr, UINT fps = 60, UINT bitrate = 12000000, UINT profile = 0, UINT outputWidth = 0, UINT outputHeight = 0) {
    if (!output) return E_POINTER;
    *output = nullptr;
    auto r = new(std::nothrow) VulkanRecording();
    if (!r) return E_OUTOFMEMORY;
    IDXGIFactory1 *factory = nullptr; IDXGIAdapter1 *adapter = nullptr; IDXGIResource1 *resource = nullptr;
    HANDLE shared = nullptr;
    HRESULT hr = E_FAIL;
    try {
        r->instance = instance; r->device = device; r->family = family; r->width = width; r->height = height;
        r->loader = LoadLibraryW(L"vulkan-1.dll");
        if (!r->loader) throw std::runtime_error("Vulkan loader unavailable");
        FARPROC address = GetProcAddress(r->loader,"vkGetInstanceProcAddr");
        PFN_vkGetInstanceProcAddr get; memcpy(&get,&address,sizeof(get));
        if (!get) throw std::runtime_error("Vulkan loader entry missing");
#define INST(name, entry) auto name = reinterpret_cast<PFN_##name>(get(instance,entry)); if (!name) throw std::runtime_error(entry)
        INST(vkGetDeviceProcAddr,"vkGetDeviceProcAddr");
        INST(vkGetPhysicalDeviceProperties2,"vkGetPhysicalDeviceProperties2KHR");
        INST(vkGetPhysicalDeviceImageFormatProperties2,"vkGetPhysicalDeviceImageFormatProperties2KHR");
        INST(vkGetPhysicalDeviceMemoryProperties,"vkGetPhysicalDeviceMemoryProperties");
#undef INST
#define DEV(name) r->name = reinterpret_cast<PFN_##name>(vkGetDeviceProcAddr(device,#name)); if (!r->name) throw std::runtime_error(#name)
        DEV(vkCreateImage); DEV(vkDestroyImage); DEV(vkGetImageMemoryRequirements); DEV(vkGetMemoryWin32HandlePropertiesKHR);
        DEV(vkAllocateMemory); DEV(vkFreeMemory); DEV(vkBindImageMemory); DEV(vkCreateCommandPool); DEV(vkDestroyCommandPool);
        DEV(vkAllocateCommandBuffers); DEV(vkResetCommandBuffer); DEV(vkBeginCommandBuffer); DEV(vkEndCommandBuffer);
        DEV(vkCmdPipelineBarrier); DEV(vkCmdCopyImage); DEV(vkCreateFence); DEV(vkDestroyFence); DEV(vkWaitForFences); DEV(vkResetFences);
#undef DEV
        VkPhysicalDeviceIDProperties id = {}; id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
        VkPhysicalDeviceProperties2 props = {}; props.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2; props.pNext = &id;
        vkGetPhysicalDeviceProperties2(physical,&props);
        if (!id.deviceLUIDValid) throw std::runtime_error("No valid adapter LUID");
        hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1),(void **)&factory);
        if (FAILED(hr)) throw std::runtime_error("DXGI factory");
        for (UINT i = 0; factory->EnumAdapters1(i,&adapter) != DXGI_ERROR_NOT_FOUND; i++) {
            DXGI_ADAPTER_DESC1 desc;
            hr = adapter->GetDesc1(&desc); if (FAILED(hr)) throw std::runtime_error("DXGI description");
            if (!memcmp(&desc.AdapterLuid,id.deviceLUID,VK_LUID_SIZE)) break;
            release(adapter);
        }
        if (!adapter) { hr = DXGI_ERROR_NOT_FOUND; throw std::runtime_error("No same-LUID adapter"); }
        hr = D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            nullptr,0,D3D11_SDK_VERSION,&r->d3d,nullptr,nullptr);
        if (FAILED(hr)) throw std::runtime_error("D3D device");
        hr = g0_record_prepare(r->d3d); if (FAILED(hr)) throw std::runtime_error("D3D threading");
        VkPhysicalDeviceExternalImageFormatInfo external = {}; external.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO;
        external.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkPhysicalDeviceImageFormatInfo2 format = {}; format.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2;
        format.pNext = &external; format.format = VK_FORMAT_B8G8R8A8_UNORM; format.type = VK_IMAGE_TYPE_2D;
        format.tiling = VK_IMAGE_TILING_OPTIMAL; format.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        VkExternalImageFormatProperties externalProps = {}; externalProps.sType = VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES;
        VkImageFormatProperties2 formatProps = {}; formatProps.sType = VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2; formatProps.pNext = &externalProps;
        vkcheck(vkGetPhysicalDeviceImageFormatProperties2(physical,&format,&formatProps));
        if (!(externalProps.externalMemoryProperties.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT))
            throw std::runtime_error("BGRA shared memory unsupported");
        D3D11_TEXTURE2D_DESC desc = {}; desc.Width = width; desc.Height = height; desc.MipLevels = desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        hr = r->d3d->CreateTexture2D(&desc,nullptr,&r->texture); if (FAILED(hr)) throw std::runtime_error("shared texture");
        hr = r->texture->QueryInterface(__uuidof(IDXGIResource1),(void **)&resource); if (FAILED(hr)) throw std::runtime_error("shared resource");
        hr = r->texture->QueryInterface(__uuidof(IDXGIKeyedMutex),(void **)&r->mutex); if (FAILED(hr)) throw std::runtime_error("keyed mutex");
        hr = resource->CreateSharedHandle(nullptr,DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,nullptr,&shared);
        if (FAILED(hr)) throw std::runtime_error("shared handle");
        VkExternalMemoryImageCreateInfo imageExternal = {}; imageExternal.sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO; imageExternal.handleTypes = external.handleType;
        VkImageCreateInfo imageCi = {}; imageCi.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO; imageCi.pNext = &imageExternal;
        imageCi.imageType = VK_IMAGE_TYPE_2D; imageCi.format = format.format; imageCi.extent = {width,height,1}; imageCi.mipLevels = imageCi.arrayLayers = 1;
        imageCi.samples = VK_SAMPLE_COUNT_1_BIT; imageCi.tiling = VK_IMAGE_TILING_OPTIMAL; imageCi.usage = format.usage;
        vkcheck(r->vkCreateImage(device,&imageCi,nullptr,&r->image));
        VkMemoryRequirements req; r->vkGetImageMemoryRequirements(device,r->image,&req);
        VkMemoryWin32HandlePropertiesKHR handleProps = {}; handleProps.sType = VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR;
        vkcheck(r->vkGetMemoryWin32HandlePropertiesKHR(device,external.handleType,shared,&handleProps));
        VkPhysicalDeviceMemoryProperties memoryProps; vkGetPhysicalDeviceMemoryProperties(physical,&memoryProps);
        uint32_t type = 0; while (type < memoryProps.memoryTypeCount && !((req.memoryTypeBits & handleProps.memoryTypeBits) & (1u << type))) type++;
        if (type == memoryProps.memoryTypeCount) throw std::runtime_error("memory type mismatch");
        VkMemoryDedicatedAllocateInfo dedicated = {}; dedicated.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO; dedicated.image = r->image;
        VkImportMemoryWin32HandleInfoKHR import = {}; import.sType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR;
        import.pNext = &dedicated; import.handleType = external.handleType; import.handle = shared;
        VkMemoryAllocateInfo allocation = {}; allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO; allocation.pNext = &import; allocation.allocationSize = req.size; allocation.memoryTypeIndex = type;
        vkcheck(r->vkAllocateMemory(device,&allocation,nullptr,&r->memory));
        vkcheck(r->vkBindImageMemory(device,r->image,r->memory,0));
        VkCommandPoolCreateInfo pool = {}; pool.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO; pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT; pool.queueFamilyIndex = family;
        vkcheck(r->vkCreateCommandPool(device,&pool,nullptr,&r->pool));
        VkCommandBufferAllocateInfo commands = {}; commands.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        commands.commandPool = r->pool; commands.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; commands.commandBufferCount = 1;
        vkcheck(r->vkAllocateCommandBuffers(device,&commands,&r->commands));
        VkFenceCreateInfo fence = {}; fence.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO; vkcheck(r->vkCreateFence(device,&fence,nullptr,&r->fence));
        r->keys.sType = VK_STRUCTURE_TYPE_WIN32_KEYED_MUTEX_ACQUIRE_RELEASE_INFO_KHR;
        r->keys.acquireCount = r->keys.releaseCount = 1; r->keys.pAcquireSyncs = r->keys.pReleaseSyncs = &r->memory;
        r->keys.pAcquireKeys = &r->acquireKey; r->keys.pReleaseKeys = &r->releaseKey; r->keys.pAcquireTimeouts = &r->timeout;
        hr = open_recording(r->d3d,path,outputWidth ? outputWidth : width,outputHeight ? outputHeight : height,audioRate,channels,&r->recording,callback,callbackContext,fps,bitrate,profile); if (FAILED(hr)) throw std::runtime_error("encoder open");
        *output = r;
    } catch (...) { if (SUCCEEDED(hr)) hr = E_FAIL; g0_vk_close(r); }
    if (shared) CloseHandle(shared);
    release(resource); release(adapter); release(factory);
    return *output ? S_OK : hr;
}

API HRESULT g0_vk_open(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
    uint32_t width, uint32_t height, const wchar_t *path, UINT audioRate, UINT channels, VulkanRecording **output) {
    return open_vulkan(instance,physical,device,family,width,height,path,audioRate,channels,output);
}
API HRESULT g0_vk_stream_open(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
    uint32_t width, uint32_t height, UINT audioRate, UINT channels, PacketCallback callback, void *context, VulkanRecording **output) {
    if (!callback) return E_INVALIDARG;
    return open_vulkan(instance,physical,device,family,width,height,nullptr,audioRate,channels,output,callback,context);
}
API HRESULT g0_vk_open_configured(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
    uint32_t width, uint32_t height, const wchar_t *path, UINT audioRate, UINT channels,
    UINT fps, UINT bitrate, UINT profile, UINT outputWidth, UINT outputHeight, VulkanRecording **output) {
    return open_vulkan(instance,physical,device,family,width,height,path,audioRate,channels,output,nullptr,nullptr,fps,bitrate,profile,outputWidth,outputHeight);
}
API HRESULT g0_vk_stream_open_configured(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
    uint32_t width, uint32_t height, UINT audioRate, UINT channels, UINT fps, UINT bitrate, UINT profile,
    UINT outputWidth, UINT outputHeight, PacketCallback callback, void *context, VulkanRecording **output) {
    if (!callback) return E_INVALIDARG;
    return open_vulkan(instance,physical,device,family,width,height,nullptr,audioRate,channels,output,callback,context,fps,bitrate,profile,outputWidth,outputHeight);
}
API HRESULT g0_vk_stream_keyframe(VulkanRecording *r) { return r ? g0_stream_keyframe(r->recording) : E_INVALIDARG; }

// Returns command handles only. Managed code submits on the render thread using
// BackendInfoVulkan.SubmitExternalCapture; the worker never calls vkQueueSubmit.
API HRESULT g0_vk_prepare(VulkanRecording *r, VkImage source, VkCommandBuffer *cmd, void **next, VkFence *fence) {
    if (!r || r->submitted) return E_INVALIDARG;
    try {
        vkcheck(r->vkResetCommandBuffer(r->commands,0));
        VkCommandBufferBeginInfo begin = {}; begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        vkcheck(r->vkBeginCommandBuffer(r->commands,&begin));
        VkImageMemoryBarrier barriers[2] = {};
        for (auto &b : barriers) { b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER; b.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT,0,1,0,1}; }
        barriers[0].image = source; barriers[0].oldLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL; barriers[0].newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        barriers[0].srcQueueFamilyIndex = barriers[0].dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        barriers[0].srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT; barriers[0].dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        barriers[1].image = r->image; barriers[1].oldLayout = r->initial ? VK_IMAGE_LAYOUT_UNDEFINED : VK_IMAGE_LAYOUT_GENERAL;
        barriers[1].newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; barriers[1].srcQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL; barriers[1].dstQueueFamilyIndex = r->family;
        barriers[1].dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        r->vkCmdPipelineBarrier(r->commands,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,VK_PIPELINE_STAGE_TRANSFER_BIT,0,0,nullptr,0,nullptr,2,barriers);
        VkImageCopy region = {}; region.srcSubresource = region.dstSubresource = {VK_IMAGE_ASPECT_COLOR_BIT,0,0,1}; region.extent = {r->width,r->height,1};
        r->vkCmdCopyImage(r->commands,source,VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,r->image,VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,1,&region);
        barriers[0].oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL; barriers[0].newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
        barriers[0].srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT; barriers[0].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        barriers[1].oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL; barriers[1].newLayout = VK_IMAGE_LAYOUT_GENERAL;
        barriers[1].srcQueueFamilyIndex = r->family; barriers[1].dstQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL;
        barriers[1].srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; barriers[1].dstAccessMask = 0;
        r->vkCmdPipelineBarrier(r->commands,VK_PIPELINE_STAGE_TRANSFER_BIT,VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,0,0,nullptr,0,nullptr,2,barriers);
        vkcheck(r->vkEndCommandBuffer(r->commands));
        *cmd = r->commands; *next = &r->keys; *fence = r->fence; return S_OK;
    } catch (...) { return E_FAIL; }
}
API HRESULT g0_vk_encode(VulkanRecording *r, LONGLONG time) {
    r->submitted = true;
    VkResult result = r->vkWaitForFences(r->device,1,&r->fence,VK_TRUE,5000000000ULL);
    if (result != VK_SUCCESS) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    r->submitted = false; r->initial = false;
    if (r->vkResetFences(r->device,1,&r->fence) != VK_SUCCESS) return E_FAIL;
    HRESULT hr = r->mutex->AcquireSync(1,1000);
    if (hr != S_OK) return E_FAIL;
    hr = g0_record_frame(r->recording,r->texture,time);
    HRESULT unlock = r->mutex->ReleaseSync(0);
    return FAILED(hr) ? hr : unlock;
}
API HRESULT g0_vk_audio(VulkanRecording *r, const float *pcm, UINT frames, LONGLONG position) { return g0_record_audio(r->recording,pcm,frames,position); }
API void g0_vk_stats(VulkanRecording *r, UINT64 *accepted, UINT64 *pool, UINT64 *cadence) { g0_record_stats(r->recording,accepted,pool,cadence); }
