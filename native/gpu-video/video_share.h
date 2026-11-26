// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.
#pragma once

// The renderer's side of shared video output: each slot texture from g0_vdec_read_shared is opened once on
// the renderer's device, then every frame copies its Y, U and V planes into the renderer's own video textures on the GPU.
// The keyed mutex orders this against the decoder: the copy acquires key 1 and releases key 0.

static HRESULT plane_boxes(int width, int height, D3D11_BOX boxes[3]) {
    if (width <= 0 || height <= 0) return E_INVALIDARG;
    const UINT w = (UINT)width, h = (UINT)height, cw = (w + 1) / 2, ch = (h + 1) / 2;
    boxes[0] = { 0, 0, 0, w, h, 1 };
    boxes[1] = { 0, h, 0, cw, h + ch, 1 };
    boxes[2] = { cw, h, 0, cw * 2, h + ch, 1 };
    return S_OK;
}

// --- Direct3D 11 renderer ---

struct D3D11VideoShare {
    ID3D11DeviceContext *context = nullptr;
    ID3D11Texture2D *texture = nullptr;
    IDXGIKeyedMutex *mutex = nullptr;
};

API void g0_vshare_d3d11_close(D3D11VideoShare *s) {
    if (!s) return;
    release(s->mutex); release(s->texture); release(s->context);
    delete s;
}

API HRESULT g0_vshare_d3d11_luid(ID3D11Device *device, LUID *luid) {
    if (!device || !luid) return E_INVALIDARG;
    IDXGIDevice *dxgi = nullptr;
    IDXGIAdapter *adapter = nullptr;
    HRESULT hr = device->QueryInterface(__uuidof(IDXGIDevice), (void **)&dxgi);
    CHECK(dxgi->GetAdapter(&adapter));
    DXGI_ADAPTER_DESC desc;
    CHECK(adapter->GetDesc(&desc));
    if (SUCCEEDED(hr)) *luid = desc.AdapterLuid;
    release(adapter); release(dxgi);
    return hr;
}

API HRESULT g0_vshare_d3d11_open(ID3D11Device *device, HANDLE handle, D3D11VideoShare **output) {
    if (!device || !handle || !output) return E_INVALIDARG;
    *output = nullptr;
    auto s = new(std::nothrow) D3D11VideoShare();
    if (!s) return E_OUTOFMEMORY;
    ID3D11Device1 *device1 = nullptr;
    HRESULT hr = device->QueryInterface(__uuidof(ID3D11Device1), (void **)&device1);
    CHECK(device1->OpenSharedResource1(handle, __uuidof(ID3D11Texture2D), (void **)&s->texture));
    CHECK(s->texture->QueryInterface(__uuidof(IDXGIKeyedMutex), (void **)&s->mutex));
    if (SUCCEEDED(hr)) device->GetImmediateContext(&s->context);
    release(device1);
    if (FAILED(hr)) { g0_vshare_d3d11_close(s); return hr; }
    *output = s;
    return S_OK;
}

// Copies a frame of `width` by `height` into the renderer's R8 textures. Call on the thread that owns the immediate context:
// the copy is ordered after everything already submitted that reads those textures, and before what is submitted later.
API HRESULT g0_vshare_d3d11_copy(D3D11VideoShare *s, ID3D11Texture2D *y, ID3D11Texture2D *u, ID3D11Texture2D *v, int width, int height) {
    if (!s || !y || !u || !v) return E_INVALIDARG;
    D3D11_BOX boxes[3];
    HRESULT hr = plane_boxes(width, height, boxes);
    if (FAILED(hr)) return hr;
    hr = s->mutex->AcquireSync(1, 1000);
    if (hr != S_OK) return FAILED(hr) ? hr : HRESULT_FROM_WIN32(WAIT_TIMEOUT);
    ID3D11Texture2D *targets[3] = { y, u, v };
    for (int i = 0; i < 3; i++)
        s->context->CopySubresourceRegion(targets[i], 0, 0, 0, 0, s->texture, 0, &boxes[i]);
    return s->mutex->ReleaseSync(0);
}

// --- Vulkan renderer ---

struct VulkanVideoShare {
    HMODULE loader = nullptr;
    VkDevice device = VK_NULL_HANDLE;
    VkImage image = VK_NULL_HANDLE;
    VkDeviceMemory memory = VK_NULL_HANDLE;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkCommandBuffer commands = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    uint32_t family = 0;
    bool submitted = false;
    VkWin32KeyedMutexAcquireReleaseInfoKHR keys = {};
    uint64_t acquireKey = 1, releaseKey = 0;
    uint32_t timeout = 1000;
#define PROC(name) PFN_##name name = nullptr
    PROC(vkDestroyImage); PROC(vkFreeMemory); PROC(vkDestroyCommandPool); PROC(vkDestroyFence);
    PROC(vkResetCommandBuffer); PROC(vkBeginCommandBuffer); PROC(vkEndCommandBuffer);
    PROC(vkCmdPipelineBarrier); PROC(vkCmdCopyImage); PROC(vkWaitForFences); PROC(vkResetFences);
#undef PROC
};

static PFN_vkGetInstanceProcAddr load_vulkan(HMODULE *loader) {
    *loader = LoadLibraryW(L"vulkan-1.dll");
    if (!*loader) return nullptr;
    FARPROC address = GetProcAddress(*loader, "vkGetInstanceProcAddr");
    PFN_vkGetInstanceProcAddr get;
    memcpy(&get, &address, sizeof(get));
    return get;
}

API HRESULT g0_vshare_vk_luid(VkInstance instance, VkPhysicalDevice physical, LUID *luid) {
    if (!instance || !physical || !luid) return E_INVALIDARG;
    HMODULE loader = nullptr;
    PFN_vkGetInstanceProcAddr get = load_vulkan(&loader);
    auto properties2 = get ? reinterpret_cast<PFN_vkGetPhysicalDeviceProperties2>(get(instance, "vkGetPhysicalDeviceProperties2KHR")) : nullptr;
    HRESULT hr = E_NOINTERFACE;
    if (properties2) {
        VkPhysicalDeviceIDProperties id = {};
        id.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ID_PROPERTIES;
        VkPhysicalDeviceProperties2 properties = {};
        properties.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PROPERTIES_2;
        properties.pNext = &id;
        properties2(physical, &properties);
        if (id.deviceLUIDValid) { memcpy(luid, id.deviceLUID, sizeof(*luid)); hr = S_OK; }
        else hr = DXGI_ERROR_NOT_FOUND;
    }
    if (loader) FreeLibrary(loader);
    return hr;
}

API void g0_vshare_vk_close(VulkanVideoShare *s) {
    if (!s) return;
    // Never free resources a submission may still use; on a timeout they are leaked until the device goes away.
    if (s->submitted && s->vkWaitForFences(s->device, 1, &s->fence, VK_TRUE, 5000000000ULL) != VK_SUCCESS) return;
    if (s->fence) s->vkDestroyFence(s->device, s->fence, nullptr);
    if (s->pool) s->vkDestroyCommandPool(s->device, s->pool, nullptr);
    if (s->image) s->vkDestroyImage(s->device, s->image, nullptr);
    if (s->memory) s->vkFreeMemory(s->device, s->memory, nullptr);
    if (s->loader) FreeLibrary(s->loader);
    delete s;
}

// Imports a slot texture (R8, `width` by `height`) into the renderer's device. Needs VK_KHR_external_memory_win32 and
// VK_KHR_win32_keyed_mutex, which the framework enables with its capture interop.
API HRESULT g0_vshare_vk_open(VkInstance instance, VkPhysicalDevice physical, VkDevice device, uint32_t family,
                              HANDLE handle, uint32_t width, uint32_t height, VulkanVideoShare **output) {
    if (!instance || !physical || !device || !handle || !width || !height || !output) return E_INVALIDARG;
    *output = nullptr;
    auto s = new(std::nothrow) VulkanVideoShare();
    if (!s) return E_OUTOFMEMORY;
    s->device = device;
    s->family = family;
    try {
        PFN_vkGetInstanceProcAddr get = load_vulkan(&s->loader);
        if (!get) throw std::runtime_error("Vulkan loader unavailable");
#define INST(name, entry) auto name = reinterpret_cast<PFN_##name>(get(instance, entry)); if (!name) throw std::runtime_error(entry)
        INST(vkGetDeviceProcAddr, "vkGetDeviceProcAddr");
        INST(vkGetPhysicalDeviceImageFormatProperties2, "vkGetPhysicalDeviceImageFormatProperties2KHR");
        INST(vkGetPhysicalDeviceMemoryProperties, "vkGetPhysicalDeviceMemoryProperties");
#undef INST
#define DEV(name) auto name = reinterpret_cast<PFN_##name>(vkGetDeviceProcAddr(device, #name)); if (!name) throw std::runtime_error(#name)
        DEV(vkCreateImage); DEV(vkGetImageMemoryRequirements); DEV(vkGetMemoryWin32HandlePropertiesKHR); DEV(vkAllocateMemory);
        DEV(vkBindImageMemory); DEV(vkCreateCommandPool); DEV(vkAllocateCommandBuffers); DEV(vkCreateFence);
#undef DEV
#define KEEP(name) s->name = reinterpret_cast<PFN_##name>(vkGetDeviceProcAddr(device, #name)); if (!s->name) throw std::runtime_error(#name)
        KEEP(vkDestroyImage); KEEP(vkFreeMemory); KEEP(vkDestroyCommandPool); KEEP(vkDestroyFence);
        KEEP(vkResetCommandBuffer); KEEP(vkBeginCommandBuffer); KEEP(vkEndCommandBuffer);
        KEEP(vkCmdPipelineBarrier); KEEP(vkCmdCopyImage); KEEP(vkWaitForFences); KEEP(vkResetFences);
#undef KEEP
        const VkExternalMemoryHandleTypeFlagBits handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkPhysicalDeviceExternalImageFormatInfo external = {};
        external.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO;
        external.handleType = handleType;
        VkPhysicalDeviceImageFormatInfo2 format = {};
        format.sType = VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2;
        format.pNext = &external;
        format.format = VK_FORMAT_R8_UNORM;
        format.type = VK_IMAGE_TYPE_2D;
        format.tiling = VK_IMAGE_TILING_OPTIMAL;
        format.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT;
        VkExternalImageFormatProperties externalProperties = {};
        externalProperties.sType = VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES;
        VkImageFormatProperties2 formatProperties = {};
        formatProperties.sType = VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2;
        formatProperties.pNext = &externalProperties;
        vkcheck(vkGetPhysicalDeviceImageFormatProperties2(physical, &format, &formatProperties));
        if (!(externalProperties.externalMemoryProperties.externalMemoryFeatures & VK_EXTERNAL_MEMORY_FEATURE_IMPORTABLE_BIT))
            throw std::runtime_error("R8 shared memory unsupported");

        VkExternalMemoryImageCreateInfo imageExternal = {};
        imageExternal.sType = VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO;
        imageExternal.handleTypes = handleType;
        VkImageCreateInfo image = {};
        image.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
        image.pNext = &imageExternal;
        image.imageType = VK_IMAGE_TYPE_2D;
        image.format = format.format;
        image.extent = { width, height, 1 };
        image.mipLevels = image.arrayLayers = 1;
        image.samples = VK_SAMPLE_COUNT_1_BIT;
        image.tiling = VK_IMAGE_TILING_OPTIMAL;
        image.usage = format.usage;
        vkcheck(vkCreateImage(device, &image, nullptr, &s->image));
        VkMemoryRequirements requirements;
        vkGetImageMemoryRequirements(device, s->image, &requirements);
        VkMemoryWin32HandlePropertiesKHR handleProperties = {};
        handleProperties.sType = VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR;
        vkcheck(vkGetMemoryWin32HandlePropertiesKHR(device, handleType, handle, &handleProperties));
        VkPhysicalDeviceMemoryProperties memoryProperties;
        vkGetPhysicalDeviceMemoryProperties(physical, &memoryProperties);
        uint32_t type = 0;
        while (type < memoryProperties.memoryTypeCount && !((requirements.memoryTypeBits & handleProperties.memoryTypeBits) & (1u << type))) type++;
        if (type == memoryProperties.memoryTypeCount) throw std::runtime_error("memory type mismatch");
        VkMemoryDedicatedAllocateInfo dedicated = {};
        dedicated.sType = VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO;
        dedicated.image = s->image;
        // Importing a D3D11 texture handle does not transfer its ownership; the decoder keeps and closes it.
        VkImportMemoryWin32HandleInfoKHR import = {};
        import.sType = VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR;
        import.pNext = &dedicated;
        import.handleType = handleType;
        import.handle = handle;
        VkMemoryAllocateInfo allocation = {};
        allocation.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
        allocation.pNext = &import;
        allocation.allocationSize = requirements.size;
        allocation.memoryTypeIndex = type;
        vkcheck(vkAllocateMemory(device, &allocation, nullptr, &s->memory));
        vkcheck(vkBindImageMemory(device, s->image, s->memory, 0));

        VkCommandPoolCreateInfo pool = {};
        pool.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
        pool.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        pool.queueFamilyIndex = family;
        vkcheck(vkCreateCommandPool(device, &pool, nullptr, &s->pool));
        VkCommandBufferAllocateInfo commands = {};
        commands.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
        commands.commandPool = s->pool;
        commands.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY;
        commands.commandBufferCount = 1;
        vkcheck(vkAllocateCommandBuffers(device, &commands, &s->commands));
        VkFenceCreateInfo fence = {};
        fence.sType = VK_STRUCTURE_TYPE_FENCE_CREATE_INFO;
        vkcheck(vkCreateFence(device, &fence, nullptr, &s->fence));
        s->keys.sType = VK_STRUCTURE_TYPE_WIN32_KEYED_MUTEX_ACQUIRE_RELEASE_INFO_KHR;
        s->keys.acquireCount = s->keys.releaseCount = 1;
        s->keys.pAcquireSyncs = s->keys.pReleaseSyncs = &s->memory;
        s->keys.pAcquireKeys = &s->acquireKey;
        s->keys.pReleaseKeys = &s->releaseKey;
        s->keys.pAcquireTimeouts = &s->timeout;
    } catch (...) {
        g0_vshare_vk_close(s);
        return E_FAIL;
    }
    *output = s;
    return S_OK;
}

// Records the copy of a frame of `width` by `height` into the renderer's R8 images and returns what to submit with
// BackendInfoVulkan.SubmitExternalCapture on the queue's owning thread. The images end in SHADER_READ_ONLY_OPTIMAL.
// The submission's pipeline barriers wait for earlier submissions on the queue that read the images.
API HRESULT g0_vshare_vk_prepare(VulkanVideoShare *s, VkImage y, VkImage u, VkImage v, int width, int height,
                                 VkCommandBuffer *cmd, void **next, VkFence *fence) {
    if (!s || !y || !u || !v || !cmd || !next || !fence) return E_INVALIDARG;
    D3D11_BOX boxes[3];
    HRESULT hr = plane_boxes(width, height, boxes);
    if (FAILED(hr)) return hr;
    try {
        // Each slot's last copy finished several frames ago; this wait only guards against reusing its command buffer early.
        if (s->submitted) {
            if (s->vkWaitForFences(s->device, 1, &s->fence, VK_TRUE, 1000000000ULL) != VK_SUCCESS) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
            vkcheck(s->vkResetFences(s->device, 1, &s->fence));
            s->submitted = false;
        }
        vkcheck(s->vkResetCommandBuffer(s->commands, 0));
        VkCommandBufferBeginInfo begin = {};
        begin.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
        begin.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        vkcheck(s->vkBeginCommandBuffer(s->commands, &begin));

        VkImage targets[3] = { y, u, v };
        VkImageMemoryBarrier barriers[4] = {};
        for (auto &b : barriers) {
            b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
            b.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            b.srcQueueFamilyIndex = b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        }
        // The shared image comes from Direct3D (the external queue family), which has no image layouts: GENERAL.
        barriers[0].image = s->image;
        barriers[0].oldLayout = VK_IMAGE_LAYOUT_GENERAL;
        barriers[0].newLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        barriers[0].srcQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL;
        barriers[0].dstQueueFamilyIndex = s->family;
        barriers[0].dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        // The renderer's images are overwritten entirely, so their previous contents and layout do not matter.
        for (int i = 0; i < 3; i++) {
            barriers[i + 1].image = targets[i];
            barriers[i + 1].oldLayout = VK_IMAGE_LAYOUT_UNDEFINED;
            barriers[i + 1].newLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
            barriers[i + 1].dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
        }
        s->vkCmdPipelineBarrier(s->commands, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 4, barriers);

        for (int i = 0; i < 3; i++) {
            VkImageCopy region = {};
            region.srcSubresource = region.dstSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
            region.srcOffset = { (int32_t)boxes[i].left, (int32_t)boxes[i].top, 0 };
            region.extent = { boxes[i].right - boxes[i].left, boxes[i].bottom - boxes[i].top, 1 };
            s->vkCmdCopyImage(s->commands, s->image, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, targets[i], VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &region);
        }

        barriers[0].oldLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
        barriers[0].newLayout = VK_IMAGE_LAYOUT_GENERAL;
        barriers[0].srcQueueFamilyIndex = s->family;
        barriers[0].dstQueueFamilyIndex = VK_QUEUE_FAMILY_EXTERNAL;
        barriers[0].srcAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
        barriers[0].dstAccessMask = 0;
        for (int i = 1; i < 4; i++) {
            barriers[i].oldLayout = VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
            barriers[i].newLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
            barriers[i].srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
            barriers[i].dstAccessMask = VK_ACCESS_SHADER_READ_BIT;
        }
        s->vkCmdPipelineBarrier(s->commands, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, nullptr, 0, nullptr, 4, barriers);
        vkcheck(s->vkEndCommandBuffer(s->commands));
    } catch (...) {
        return E_FAIL;
    }
    *cmd = s->commands;
    *next = &s->keys;
    *fence = s->fence;
    return S_OK;
}

// Marks the commands from g0_vshare_vk_prepare as submitted, so they are waited for before reuse or closing.
API void g0_vshare_vk_submitted(VulkanVideoShare *s) {
    if (s) s->submitted = true;
}
