using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using static Emutastic.Services.VulkanInterop;

namespace Emutastic.Services
{
    /// <summary>
    /// Manages Vulkan context for libretro cores using the negotiation interface.
    /// The core creates the VkDevice; we create VkInstance and provide the GPU.
    /// After init, we expose retro_hw_render_interface_vulkan for the core to render,
    /// then read back each frame via staging buffers for WPF display.
    /// </summary>
    public unsafe class VulkanContext : IDisposable
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        // ── Deferred device/instance destruction ───────────────────────────────
        // After teardown, nvoglv64.dll driver threads may still reference VkDevice
        // memory.  Instead of destroying device+instance immediately (causing AVs),
        // stash them here and destroy at the start of the next session when all
        // driver threads have exited.
        private static VkDevice _deferredDevice;
        private static VkInstance _deferredInstance;
        private static retro_vulkan_destroy_device_t? _deferredCoreDestroyDevice;
        private static bool _deferredCoreOwned;

        /// <summary>
        /// Destroy any Vulkan device/instance left over from a previous session.
        /// Call this at the start of StartEmulator, before loading the core.
        /// </summary>
        public static void DestroyDeferredResources()
        {
            if (_deferredDevice.Handle == IntPtr.Zero && _deferredInstance.Handle == IntPtr.Zero)
                return;

            System.Diagnostics.Trace.WriteLine($"[Vulkan] Destroying deferred device=0x{_deferredDevice.Handle:X} instance=0x{_deferredInstance.Handle:X}");

            try
            {
                if (_deferredDevice.Handle != IntPtr.Zero)
                    vkDeviceWaitIdle(_deferredDevice);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Vulkan] Deferred vkDeviceWaitIdle: {ex.Message}"); }

            try
            {
                if (_deferredCoreOwned && _deferredCoreDestroyDevice != null)
                    _deferredCoreDestroyDevice.Invoke();
                else if (_deferredDevice.Handle != IntPtr.Zero)
                    vkDestroyDevice(_deferredDevice, IntPtr.Zero);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Vulkan] Deferred vkDestroyDevice: {ex.Message}"); }

            try
            {
                if (_deferredInstance.Handle != IntPtr.Zero)
                    vkDestroyInstance(_deferredInstance, IntPtr.Zero);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[Vulkan] Deferred vkDestroyInstance: {ex.Message}"); }

            _deferredDevice = default;
            _deferredInstance = default;
            _deferredCoreDestroyDevice = null;
            _deferredCoreOwned = false;
            System.Diagnostics.Trace.WriteLine("[Vulkan] Deferred resources destroyed.");
        }

        // ── Vulkan objects ──────────────────────────────────────────────────────
        private VkInstance _instance;
        private VkPhysicalDevice _physicalDevice;
        private VkDevice _device;
        private VkQueue _queue;
        private uint _queueFamilyIndex;

        // ── Negotiation ─────────────────────────────────────────────────────────
        private retro_hw_render_context_negotiation_interface_vulkan _negotiation;
        private retro_vulkan_create_device_t? _coreCreateDevice;
        private retro_vulkan_destroy_device_t? _coreDestroyDevice;
        private bool _coreOwnsDevice; // true when core created the device via create_device

        // ── HW render interface (returned to core) ──────────────────────────────
        private IntPtr _hwIfacePtr;  // pinned native allocation
        private GCHandle _handlePin; // prevent GC of 'this'

        // ── Managed callback delegates (prevent GC) ─────────────────────────────
        private retro_vulkan_set_image_t? _setImageDelegate;
        private retro_vulkan_get_sync_index_t? _getSyncIndexDelegate;
        private retro_vulkan_get_sync_index_mask_t? _getSyncIndexMaskDelegate;
        private retro_vulkan_set_command_buffers_t? _setCommandBuffersDelegate;
        private retro_vulkan_wait_sync_index_t? _waitSyncIndexDelegate;
        private retro_vulkan_lock_queue_t? _lockQueueDelegate;
        private retro_vulkan_unlock_queue_t? _unlockQueueDelegate;
        private retro_vulkan_set_signal_semaphore_t? _setSignalSemaphoreDelegate;

        // ── Image tracking ──────────────────────────────────────────────────────
        private retro_vulkan_image _currentImage;
        private IntPtr _currentVkImage;   // resolved VkImage for readback
        private bool _hasImage;
        private readonly object _imageLock = new();

        // ── Vulkan context struct (passed to create_device, read back device/queue) ──
        private IntPtr _vulkanContextPtr;

        // ── Readback resources ──────────────────────────────────────────────────
        private const int SyncCount = 3; // triple-buffered
        private uint _syncIndex;
        private VkCommandPool _cmdPool;
        private readonly VkCommandBuffer[] _cmdBufs = new VkCommandBuffer[SyncCount];
        private readonly VkFence[] _fences = new VkFence[SyncCount];
        private readonly VkBuffer[] _stagingBufs = new VkBuffer[SyncCount];
        private readonly VkDeviceMemory[] _stagingMem = new VkDeviceMemory[SyncCount];
        private readonly IntPtr[] _stagingMapped = new IntPtr[SyncCount];
        private uint _stagingWidth, _stagingHeight;
        private ulong _stagingSize;

        // Pre-allocated native memory for readback commands (avoid per-frame AllocHGlobal)
        private IntPtr _barrierPtr;
        private IntPtr _regionPtr;
        private IntPtr _cmdSubmitPtr;

        // ── Swapchain presentation (eliminates CPU readback) ────────────────────
        private VkSurfaceKHR _surface;
        private VkSwapchainKHR _swapchain;
        private VkImage[] _swapImages = Array.Empty<VkImage>();
        private VkSemaphore _imageAvailableSemaphore;
        private VkSemaphore _renderFinishedSemaphore;
        private VkFence _presentFence;
        private VkCommandBuffer _presentCmd;
        private uint _swapWidth, _swapHeight;
        private bool _swapchainActive;
        private IntPtr _blitRegionPtr; // pre-allocated VkImageBlit
        public bool HasSwapchain => _swapchainActive;

        // Surface extension function pointers (loaded at runtime)
        private PFN_vkCreateWin32SurfaceKHR? _vkCreateWin32SurfaceKHR;
        private PFN_vkDestroySurfaceKHR? _vkDestroySurfaceKHR;
        private PFN_vkGetPhysicalDeviceSurfaceSupportKHR? _vkGetPhysicalDeviceSurfaceSupportKHR;
        private PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR? _vkGetPhysicalDeviceSurfaceCapabilitiesKHR;
        private PFN_vkGetPhysicalDeviceSurfaceFormatsKHR? _vkGetPhysicalDeviceSurfaceFormatsKHR;

        // (VkImage is now read directly from retro_vulkan_image.ci_image — no interception needed)

        private bool _initialized;
        public bool IsInitialized => _initialized;

        // =====================================================================
        // Initialize
        // =====================================================================
        private IntPtr _presentHwnd; // HWND for swapchain surface (0 = readback mode)

        public bool Initialize(IntPtr negotiationPtr, IntPtr hwnd = default)
        {
            if (_initialized) return true;
            _presentHwnd = hwnd;

            try
            {
                if (!IsVulkanAvailable())
                {
                    System.Diagnostics.Trace.WriteLine("[Vulkan] vulkan-1.dll not found");
                    return false;
                }

                // Read the negotiation interface if the core provided one
                if (negotiationPtr != IntPtr.Zero)
                {
                    uint ifaceType = (uint)Marshal.ReadInt32(negotiationPtr, 0);
                    uint ifaceVersion = (uint)Marshal.ReadInt32(negotiationPtr, 4);
                    System.Diagnostics.Trace.WriteLine(
                        $"[Vulkan] Negotiation interface type={ifaceType} version={ifaceVersion}");

                    IntPtr getAppInfoPtr = Marshal.ReadIntPtr(negotiationPtr, 8);
                    IntPtr createDevicePtr = Marshal.ReadIntPtr(negotiationPtr, 8 + IntPtr.Size);
                    IntPtr destroyDevicePtr = Marshal.ReadIntPtr(negotiationPtr, 8 + IntPtr.Size * 2);

                    System.Diagnostics.Trace.WriteLine(
                        $"[Vulkan] Negotiation ptrs: appInfo=0x{getAppInfoPtr:X} createDev=0x{createDevicePtr:X} destroyDev=0x{destroyDevicePtr:X}");

                    _negotiation = new retro_hw_render_context_negotiation_interface_vulkan
                    {
                        interface_type = ifaceType,
                        interface_version = ifaceVersion,
                        get_application_info = getAppInfoPtr,
                    };

                    if (createDevicePtr.ToInt64() > 0x10000)
                        _coreCreateDevice = Marshal.GetDelegateForFunctionPointer<retro_vulkan_create_device_t>(createDevicePtr);
                    if (destroyDevicePtr.ToInt64() > 0x10000)
                        _coreDestroyDevice = Marshal.GetDelegateForFunctionPointer<retro_vulkan_destroy_device_t>(destroyDevicePtr);
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine("[Vulkan] No negotiation interface — frontend will create device");
                }

                // 1. Create VkInstance
                System.Diagnostics.Trace.WriteLine("[Vulkan] Creating VkInstance...");
                if (!CreateInstance()) return false;
                System.Diagnostics.Trace.WriteLine("[Vulkan] VkInstance OK");

                // 2. Pick physical device (discrete GPU preferred)
                System.Diagnostics.Trace.WriteLine("[Vulkan] Selecting physical device...");
                if (!SelectPhysicalDevice()) { Cleanup(); return false; }
                System.Diagnostics.Trace.WriteLine("[Vulkan] Physical device OK");

                // 3. Create surface for swapchain presentation (if HWND provided)
                if (_presentHwnd != IntPtr.Zero && _vkCreateWin32SurfaceKHR != null)
                {
                    var surfCI = new VkWin32SurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR,
                        hwnd = _presentHwnd,
                        hinstance = System.Diagnostics.Process.GetCurrentProcess().Handle,
                    };
                    var sr = _vkCreateWin32SurfaceKHR(_instance, ref surfCI, IntPtr.Zero, out _surface);
                    if (sr == VkResult.VK_SUCCESS)
                        System.Diagnostics.Trace.WriteLine($"[Vulkan] Surface created for HWND=0x{_presentHwnd:X}");
                    else
                    {
                        System.Diagnostics.Trace.WriteLine($"[Vulkan] Surface creation failed: {VkResultToString(sr)}");
                        _presentHwnd = IntPtr.Zero; // fall back to readback
                    }
                }

                // 4. Let the core create the VkDevice via create_device callback.
                // create_device ptr must be a valid code address (> 0x10000).
                if (_coreCreateDevice != null)
                {
                    IntPtr createDevPtr = Marshal.ReadIntPtr(negotiationPtr, 8 + IntPtr.Size);
                    bool createDevValid = createDevPtr.ToInt64() > 0x10000;
                    System.Diagnostics.Trace.WriteLine($"[Vulkan] create_device ptr=0x{createDevPtr:X} valid={createDevValid}");

                    if (createDevValid)
                    {
                        // Get the real vkGetInstanceProcAddr from the Vulkan loader DLL
                        IntPtr vulkanDll = GetModuleHandle("vulkan-1.dll");
                        IntPtr realGipa = GetProcAddress(vulkanDll, "vkGetInstanceProcAddr");
                        System.Diagnostics.Trace.WriteLine($"[Vulkan] gipa=0x{realGipa:X} (from vulkan-1.dll=0x{vulkanDll:X})");

                        // VkImage is now read directly from retro_vulkan_image.create_info.image
                        // so no gipa/gdpa interception is needed

                        _vulkanContextPtr = Marshal.AllocHGlobal(Marshal.SizeOf<retro_vulkan_context>());
                        Marshal.StructureToPtr(new retro_vulkan_context { gpu = _physicalDevice.Handle },
                            _vulkanContextPtr, false);

                        // Pass a zeroed VkPhysicalDeviceFeatures — core may dereference without null check
                        const int VkPhysicalDeviceFeaturesSize = 220; // 55 VkBool32s
                        IntPtr featuresPtr = Marshal.AllocHGlobal(VkPhysicalDeviceFeaturesSize);
                        for (int i = 0; i < VkPhysicalDeviceFeaturesSize / 8; i++)
                            Marshal.WriteInt64(featuresPtr, i * 8, 0);
                        Marshal.WriteInt32(featuresPtr, 216, 0); // last 4 bytes

                        // Pass VK_KHR_swapchain as required device extension for direct presentation
                        IntPtr swapExtName = Marshal.StringToHGlobalAnsi("VK_KHR_swapchain");
                        IntPtr reqExtArray = Marshal.AllocHGlobal(IntPtr.Size);
                        Marshal.WriteIntPtr(reqExtArray, swapExtName);

                        System.Diagnostics.Trace.WriteLine("[Vulkan] Calling core create_device...");
                        bool ok = _coreCreateDevice(
                            _vulkanContextPtr,
                            _instance.Handle, _physicalDevice.Handle,
                            _surface.Handle, realGipa,  // Pass surface so core can verify present support
                            reqExtArray, 1, IntPtr.Zero, 0, featuresPtr);
                        Marshal.FreeHGlobal(featuresPtr);
                        Marshal.FreeHGlobal(swapExtName);
                        Marshal.FreeHGlobal(reqExtArray);

                        if (ok)
                        {
                            var resultCtx = Marshal.PtrToStructure<retro_vulkan_context>(_vulkanContextPtr);
                            _device = new VkDevice { Handle = resultCtx.device };
                            _queue = new VkQueue { Handle = resultCtx.queue };
                            _queueFamilyIndex = resultCtx.queue_family_index;
                            _coreOwnsDevice = true;
                            System.Diagnostics.Trace.WriteLine(
                                $"[Vulkan] Core created device=0x{resultCtx.device:X} queue=0x{resultCtx.queue:X} queueFamily={resultCtx.queue_family_index}");
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine("[Vulkan] Core create_device returned false, creating ourselves");
                        }
                    }
                    else
                    {
                        _coreCreateDevice = null; // invalid pointer, don't use
                    }
                }

                // Create device ourselves with ALL supported features enabled
                if (_device.Handle == IntPtr.Zero)
                {
                    System.Diagnostics.Trace.WriteLine("[Vulkan] Creating device ourselves with full feature set");
                    if (!CreateLogicalDevice()) { Cleanup(); return false; }
                }

                // 5. Create readback resources (kept as fallback)
                if (!CreateReadbackResources()) { Cleanup(); return false; }

                // 6. Create swapchain for direct presentation (if surface exists)
                if (_surface.Handle != IntPtr.Zero)
                {
                    if (!CreateSwapchain())
                        System.Diagnostics.Trace.WriteLine("[Vulkan] Swapchain creation failed — falling back to readback");
                }

                _initialized = true;
                System.Diagnostics.Trace.WriteLine("[Vulkan] Context initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Vulkan] Init exception: {ex.Message}\n{ex.StackTrace}");
                Cleanup();
                return false;
            }
        }

        // =====================================================================
        // Build the retro_hw_render_interface_vulkan and return a pinned pointer
        // =====================================================================
        public IntPtr BuildHwRenderInterface()
        {
            if (_hwIfacePtr != IntPtr.Zero)
                return _hwIfacePtr;

            // Pin ourselves so the handle pointer stays valid
            _handlePin = GCHandle.Alloc(this);

            // Create managed delegates and prevent GC
            _setImageDelegate = OnSetImage;
            _getSyncIndexDelegate = OnGetSyncIndex;
            _getSyncIndexMaskDelegate = OnGetSyncIndexMask;
            _setCommandBuffersDelegate = OnSetCommandBuffers;
            _waitSyncIndexDelegate = OnWaitSyncIndex;
            _lockQueueDelegate = OnLockQueue;
            _unlockQueueDelegate = OnUnlockQueue;
            _setSignalSemaphoreDelegate = OnSetSignalSemaphore;

            var iface = new retro_hw_render_interface_vulkan
            {
                interface_type = 0,    // RETRO_HW_RENDER_INTERFACE_VULKAN
                interface_version = 5,
                handle = GCHandle.ToIntPtr(_handlePin),
                instance = _instance.Handle,
                gpu = _physicalDevice.Handle,
                device = _device.Handle,
                get_device_proc_addr = vkGetInstanceProcAddr(_instance, "vkGetDeviceProcAddr"),
                get_instance_proc_addr = vkGetInstanceProcAddr(_instance, "vkGetInstanceProcAddr"),
                queue = _queue.Handle,
                queue_index = _queueFamilyIndex,
                set_image = Marshal.GetFunctionPointerForDelegate(_setImageDelegate),
                get_sync_index = Marshal.GetFunctionPointerForDelegate(_getSyncIndexDelegate),
                get_sync_index_mask = Marshal.GetFunctionPointerForDelegate(_getSyncIndexMaskDelegate),
                set_command_buffers = Marshal.GetFunctionPointerForDelegate(_setCommandBuffersDelegate),
                wait_sync_index = Marshal.GetFunctionPointerForDelegate(_waitSyncIndexDelegate),
                lock_queue = Marshal.GetFunctionPointerForDelegate(_lockQueueDelegate),
                unlock_queue = Marshal.GetFunctionPointerForDelegate(_unlockQueueDelegate),
                set_signal_semaphore = Marshal.GetFunctionPointerForDelegate(_setSignalSemaphoreDelegate),
            };

            _hwIfacePtr = Marshal.AllocHGlobal(Marshal.SizeOf<retro_hw_render_interface_vulkan>());
            Marshal.StructureToPtr(iface, _hwIfacePtr, false);

            System.Diagnostics.Trace.WriteLine("[Vulkan] HW render interface built");
            return _hwIfacePtr;
        }

        // =====================================================================
        // get_proc_address for retro_hw_render_callback — intercepts vkCreateImageView
        // =====================================================================
        public IntPtr GetProcAddress(string name)
        {
            // Try device-level first, then instance-level
            if (_device.Handle != IntPtr.Zero)
            {
                IntPtr p = vkGetDeviceProcAddr(_device, name);
                if (p != IntPtr.Zero) return p;
            }
            return vkGetInstanceProcAddr(_instance, name);
        }

        // =====================================================================
        // Synchronous readback: record copy, submit, wait, read pixels.
        // =====================================================================
        private int _readbackLogCount;

        public (byte[]? pixels, int width, int height) ReadbackFrame(uint frameWidth, uint frameHeight)
        {
            lock (_imageLock)
            {
                if (!_hasImage || _currentVkImage == IntPtr.Zero)
                    return (null, 0, 0);

                uint w = frameWidth;
                uint h = frameHeight;

                // Skip tiny/invalid frames (1x1 = VI not configured yet during N64 boot)
                if (w < 8 || h < 8 || w > 4096 || h > 4096)
                    return (null, 0, 0);

                bool verbose = _readbackLogCount < 3;
                if (verbose) _readbackLogCount++;

                try
                {
                    EnsureStagingBuffers(w, h);
                    if (_stagingBufs[0].Handle == IntPtr.Zero)
                        return (null, 0, 0);

                    int idx = (int)(_syncIndex % SyncCount);
                    var fence = _fences[idx];

                    // Wait for any prior use of this slot
                    var wr = vkWaitForFences(_device, 1, ref fence, 1, 100_000_000UL);
                    if (wr != VkResult.VK_SUCCESS)
                        return (null, 0, 0);
                    vkResetFences(_device, 1, ref fence);

                    var cmd = _cmdBufs[idx];
                    vkResetCommandBuffer(cmd, 0);
                    var beginInfo = new VkCommandBufferBeginInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                        flags = 1, // ONE_TIME_SUBMIT
                    };
                    var r = vkBeginCommandBuffer(cmd, ref beginInfo);
                    if (r != VkResult.VK_SUCCESS)
                        return (null, 0, 0);

                    var image = new VkImage { Handle = _currentVkImage };
                    var oldLayout = (VkImageLayout)_currentImage.image_layout;

                    // Transition core's image to TRANSFER_SRC
                    var toTransfer = new VkImageMemoryBarrier
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                        srcAccessMask = VkAccessFlags.VK_ACCESS_NONE,
                        dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                        oldLayout = oldLayout,
                        newLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        image = image,
                        subresourceRange = new VkImageSubresourceRange
                        {
                            aspectMask = (uint)VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                            baseMipLevel = 0, levelCount = 1, baseArrayLayer = 0, layerCount = 1,
                        },
                    };
                    Marshal.StructureToPtr(toTransfer, _barrierPtr, false);
                    vkCmdPipelineBarrier(cmd, (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, _barrierPtr);

                    // Copy image to staging buffer
                    var region = new VkBufferImageCopy
                    {
                        bufferOffset = 0, bufferRowLength = 0, bufferImageHeight = 0,
                        imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, mipLevel = 0, baseArrayLayer = 0, layerCount = 1 },
                        imageOffset = new VkOffset3D { x = 0, y = 0, z = 0 },
                        imageExtent = new VkExtent3D { width = w, height = h, depth = 1 },
                    };
                    Marshal.StructureToPtr(region, _regionPtr, false);
                    vkCmdCopyImageToBuffer(cmd, image, (uint)VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, _stagingBufs[idx], 1, _regionPtr);

                    // Transition back to original layout
                    var toOriginal = toTransfer;
                    toOriginal.srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT;
                    toOriginal.dstAccessMask = VkAccessFlags.VK_ACCESS_NONE;
                    toOriginal.oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
                    toOriginal.newLayout = oldLayout;
                    Marshal.StructureToPtr(toOriginal, _barrierPtr, false);
                    vkCmdPipelineBarrier(cmd, (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, _barrierPtr);

                    // Submit and wait synchronously
                    vkEndCommandBuffer(cmd);
                    Marshal.WriteIntPtr(_cmdSubmitPtr, cmd.Handle);
                    var submitInfo = new VkSubmitInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO,
                        commandBufferCount = 1,
                        pCommandBuffers = _cmdSubmitPtr,
                    };
                    VkResult submitResult;
                    lock (_queueLock) { submitResult = vkQueueSubmit(_queue, 1, ref submitInfo, _fences[idx]); }
                    if (submitResult != VkResult.VK_SUCCESS)
                        return (null, 0, 0);

                    // Wait for copy to complete
                    fence = _fences[idx];
                    var fenceResult = vkWaitForFences(_device, 1, ref fence, 1, 1_000_000_000UL);
                    if (fenceResult != VkResult.VK_SUCCESS)
                        return (null, 0, 0);

                    // Read pixels and swizzle RGBA → BGRA + force alpha=0xFF
                    int needed = (int)(w * h * 4);
                    byte[] pixels = new byte[needed];
                    Marshal.Copy(_stagingMapped[idx], pixels, 0, needed);

                    var pixSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
                    for (int p = 0; p < pixSpan.Length; p++)
                    {
                        uint px = pixSpan[p];
                        pixSpan[p] = 0xFF000000u | ((px & 0xFFu) << 16) | (px & 0xFF00u) | ((px >> 16) & 0xFFu);
                    }

                    _syncIndex++;
                    if (verbose) VkLog($"ReadbackFrame OK: {w}x{h}");
                    return (pixels, (int)w, (int)h);
                }
                catch (Exception ex)
                {
                    VkLog($"ReadbackFrame exception: {ex.Message}\n{ex.StackTrace}");
                    return (null, 0, 0);
                }
            }
        }

        // =====================================================================
        // Async recording readback — zero-stall, reads frame N-1
        // =====================================================================
        private int  _recSlot = -1;       // slot with in-flight copy (-1 = none)
        private uint _recWidth, _recHeight;
        private byte[]? _recPixels;       // reusable buffer (avoids GC pressure)

        /// <summary>
        /// Non-blocking recording readback. Kicks off a GPU copy for this frame,
        /// returns the PREVIOUS frame's pixels (already completed). Returns null
        /// on the first call (no previous data yet) and when not recording.
        /// </summary>
        public (byte[]? pixels, int width, int height) ReadbackFrameForRecording(uint frameWidth, uint frameHeight)
        {
            lock (_imageLock)
            {
                if (!_hasImage || _currentVkImage == IntPtr.Zero)
                    return (null, 0, 0);

                uint w = frameWidth;
                uint h = frameHeight;
                if (w < 8 || h < 8 || w > 4096 || h > 4096)
                    return (null, 0, 0);

                try
                {
                    EnsureStagingBuffers(w, h);
                    if (_stagingBufs[0].Handle == IntPtr.Zero)
                        return (null, 0, 0);

                    byte[]? result = null;
                    int resultW = 0, resultH = 0;

                    // Step 1: Read the PREVIOUS frame's data (fence should already be signaled)
                    if (_recSlot >= 0)
                    {
                        var prevFence = _fences[_recSlot];
                        var wr = vkWaitForFences(_device, 1, ref prevFence, 1, 0); // non-blocking check
                        if (wr == VkResult.VK_SUCCESS)
                        {
                            int needed = (int)(_recWidth * _recHeight * 4);
                            if (_recPixels == null || _recPixels.Length < needed)
                                _recPixels = new byte[needed];
                            Marshal.Copy(_stagingMapped[_recSlot], _recPixels, 0, needed);
                            result = _recPixels;
                            resultW = (int)_recWidth;
                            resultH = (int)_recHeight;
                        }
                        // else: previous copy not done yet, skip this frame
                    }

                    // Step 2: Kick off async copy for THIS frame (non-blocking)
                    int idx = (int)(_syncIndex % SyncCount);
                    var fence = _fences[idx];

                    // Wait for this slot to be free (from 2+ frames ago — should be instant)
                    vkWaitForFences(_device, 1, ref fence, 1, 100_000_000UL);
                    vkResetFences(_device, 1, ref fence);

                    var cmd = _cmdBufs[idx];
                    vkResetCommandBuffer(cmd, 0);
                    var beginInfo = new VkCommandBufferBeginInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                        flags = 1,
                    };
                    if (vkBeginCommandBuffer(cmd, ref beginInfo) != VkResult.VK_SUCCESS)
                        return (result, resultW, resultH);

                    var image = new VkImage { Handle = _currentVkImage };
                    var oldLayout = (VkImageLayout)_currentImage.image_layout;

                    var toTransfer = new VkImageMemoryBarrier
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                        srcAccessMask = VkAccessFlags.VK_ACCESS_NONE,
                        dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                        oldLayout = oldLayout,
                        newLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        image = image,
                        subresourceRange = new VkImageSubresourceRange
                        {
                            aspectMask = (uint)VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                            baseMipLevel = 0, levelCount = 1, baseArrayLayer = 0, layerCount = 1,
                        },
                    };
                    Marshal.StructureToPtr(toTransfer, _barrierPtr, false);
                    vkCmdPipelineBarrier(cmd, (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, _barrierPtr);

                    var region = new VkBufferImageCopy
                    {
                        bufferOffset = 0, bufferRowLength = 0, bufferImageHeight = 0,
                        imageSubresource = new VkImageSubresourceLayers { aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT, mipLevel = 0, baseArrayLayer = 0, layerCount = 1 },
                        imageOffset = new VkOffset3D { x = 0, y = 0, z = 0 },
                        imageExtent = new VkExtent3D { width = w, height = h, depth = 1 },
                    };
                    Marshal.StructureToPtr(region, _regionPtr, false);
                    vkCmdCopyImageToBuffer(cmd, image, (uint)VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, _stagingBufs[idx], 1, _regionPtr);

                    var toOriginal = toTransfer;
                    toOriginal.srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT;
                    toOriginal.dstAccessMask = VkAccessFlags.VK_ACCESS_NONE;
                    toOriginal.oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
                    toOriginal.newLayout = oldLayout;
                    Marshal.StructureToPtr(toOriginal, _barrierPtr, false);
                    vkCmdPipelineBarrier(cmd, (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, _barrierPtr);

                    vkEndCommandBuffer(cmd);
                    Marshal.WriteIntPtr(_cmdSubmitPtr, cmd.Handle);
                    var submitInfo = new VkSubmitInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO,
                        commandBufferCount = 1,
                        pCommandBuffers = _cmdSubmitPtr,
                    };

                    // Submit but DON'T wait — the fence signals when the copy is done
                    lock (_queueLock) { vkQueueSubmit(_queue, 1, ref submitInfo, _fences[idx]); }

                    _recSlot = idx;
                    _recWidth = w;
                    _recHeight = h;
                    _syncIndex++;

                    return (result, resultW, resultH);
                }
                catch (Exception ex)
                {
                    VkLog($"ReadbackFrameForRecording exception: {ex.Message}");
                    return (null, 0, 0);
                }
            }
        }

        public void StopRecordingReadback()
        {
            _recSlot = -1;
            _recPixels = null;
        }

        // =====================================================================
        // Callback implementations for retro_hw_render_interface_vulkan
        // =====================================================================

        private int _setImageLogCount;
        private static readonly string _vkLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "vulkan_debug.txt");
        private static void VkLog(string msg)
        {
            try { System.IO.File.AppendAllText(_vkLogPath, msg + "\n"); } catch { }
        }

        private void OnSetImage(IntPtr handle, IntPtr imagePtr, uint numSemaphores, IntPtr semaphores, uint srcQueueFamily)
        {
            if (imagePtr == IntPtr.Zero) return;

            lock (_imageLock)
            {
                _currentImage = Marshal.PtrToStructure<retro_vulkan_image>(imagePtr);

                // VkImage is embedded in the VkImageViewCreateInfo within retro_vulkan_image
                _currentVkImage = _currentImage.ci_image;
                _hasImage = _currentVkImage != IntPtr.Zero;

                if (_setImageLogCount < 3)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[Vulkan] set_image #{_setImageLogCount}: view=0x{_currentImage.image_view:X} layout={_currentImage.image_layout} image=0x{_currentVkImage:X} format={_currentImage.ci_format}");
                    _setImageLogCount++;
                }
            }
        }

        private uint OnGetSyncIndex(IntPtr handle) => _syncIndex % SyncCount;
        private uint OnGetSyncIndexMask(IntPtr handle) => SyncCount - 1;

        private void OnSetCommandBuffers(IntPtr handle, uint numCmd, IntPtr cmd)
        {
            // Core submits its own command buffers — we don't need to track them
        }

        private void OnWaitSyncIndex(IntPtr handle)
        {
            // Wait for the current sync slot's fence to complete
            int idx = (int)(_syncIndex % SyncCount);
            var fence = _fences[idx];
            if (fence.Handle != IntPtr.Zero)
                vkWaitForFences(_device, 1, ref fence, 1, ulong.MaxValue);
        }

        private readonly object _queueLock = new();
        private void OnLockQueue(IntPtr handle)
        {
            System.Threading.Monitor.Enter(_queueLock);
        }

        private void OnUnlockQueue(IntPtr handle)
        {
            System.Threading.Monitor.Exit(_queueLock);
        }

        private void OnSetSignalSemaphore(IntPtr handle, IntPtr semaphore)
        {
            // We don't present to a swapchain, so signal semaphores are unused
        }

        // =====================================================================
        // Private: Vulkan init helpers
        // =====================================================================

        private bool CreateInstance()
        {
            // Check if core has get_application_info and get its required extensions
            IntPtr appInfoPtr = IntPtr.Zero;
            if (_negotiation.get_application_info != IntPtr.Zero)
            {
                var getAppInfo = Marshal.GetDelegateForFunctionPointer<retro_vulkan_get_application_info_t>(
                    _negotiation.get_application_info);
                appInfoPtr = getAppInfo();
            }

            uint apiVersion = MakeVersion(1, 1, 0); // Vulkan 1.1 minimum
            if (appInfoPtr != IntPtr.Zero)
            {
                var coreAppInfo = Marshal.PtrToStructure<VkApplicationInfo>(appInfoPtr);
                if (coreAppInfo.apiVersion > apiVersion)
                    apiVersion = coreAppInfo.apiVersion;
            }

            var appNamePtr = Marshal.StringToHGlobalAnsi("Emutastic");
            var engineNamePtr = Marshal.StringToHGlobalAnsi("Emutastic");

            var appInfo = new VkApplicationInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pApplicationName = appNamePtr,
                applicationVersion = MakeVersion(1, 0, 0),
                pEngineName = engineNamePtr,
                engineVersion = MakeVersion(1, 0, 0),
                apiVersion = apiVersion,
            };

            IntPtr appInfoNative = Marshal.AllocHGlobal(Marshal.SizeOf<VkApplicationInfo>());
            Marshal.StructureToPtr(appInfo, appInfoNative, false);

            // Enable surface extensions for swapchain presentation
            var extList = new[] { "VK_KHR_surface", "VK_KHR_win32_surface" };
            var extPtrs = new IntPtr[extList.Length];
            for (int i = 0; i < extList.Length; i++)
                extPtrs[i] = Marshal.StringToHGlobalAnsi(extList[i]);
            IntPtr extArray = Marshal.AllocHGlobal(IntPtr.Size * extList.Length);
            for (int i = 0; i < extList.Length; i++)
                Marshal.WriteIntPtr(extArray, i * IntPtr.Size, extPtrs[i]);

            var createInfo = new VkInstanceCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = appInfoNative,
                enabledExtensionCount = (uint)extList.Length,
                ppEnabledExtensionNames = extArray,
            };

            var result = vkCreateInstance(ref createInfo, IntPtr.Zero, out _instance);

            foreach (var p in extPtrs) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(extArray);
            Marshal.FreeHGlobal(appNamePtr);
            Marshal.FreeHGlobal(engineNamePtr);
            Marshal.FreeHGlobal(appInfoNative);

            if (result != VkResult.VK_SUCCESS)
            {
                System.Diagnostics.Trace.WriteLine($"[Vulkan] vkCreateInstance: {VkResultToString(result)}");
                return false;
            }

            System.Diagnostics.Trace.WriteLine("[Vulkan] Instance created");

            // Load surface extension function pointers
            var p1 = vkGetInstanceProcAddr(_instance, "vkCreateWin32SurfaceKHR");
            var p2 = vkGetInstanceProcAddr(_instance, "vkDestroySurfaceKHR");
            var p3 = vkGetInstanceProcAddr(_instance, "vkGetPhysicalDeviceSurfaceSupportKHR");
            var p4 = vkGetInstanceProcAddr(_instance, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var p5 = vkGetInstanceProcAddr(_instance, "vkGetPhysicalDeviceSurfaceFormatsKHR");
            if (p1 != IntPtr.Zero) _vkCreateWin32SurfaceKHR = Marshal.GetDelegateForFunctionPointer<PFN_vkCreateWin32SurfaceKHR>(p1);
            if (p2 != IntPtr.Zero) _vkDestroySurfaceKHR = Marshal.GetDelegateForFunctionPointer<PFN_vkDestroySurfaceKHR>(p2);
            if (p3 != IntPtr.Zero) _vkGetPhysicalDeviceSurfaceSupportKHR = Marshal.GetDelegateForFunctionPointer<PFN_vkGetPhysicalDeviceSurfaceSupportKHR>(p3);
            if (p4 != IntPtr.Zero) _vkGetPhysicalDeviceSurfaceCapabilitiesKHR = Marshal.GetDelegateForFunctionPointer<PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR>(p4);
            if (p5 != IntPtr.Zero) _vkGetPhysicalDeviceSurfaceFormatsKHR = Marshal.GetDelegateForFunctionPointer<PFN_vkGetPhysicalDeviceSurfaceFormatsKHR>(p5);

            return true;
        }

        private bool SelectPhysicalDevice()
        {
            uint count = 0;
            vkEnumeratePhysicalDevices(_instance, ref count, IntPtr.Zero);
            if (count == 0) { System.Diagnostics.Trace.WriteLine("[Vulkan] No GPUs found"); return false; }

            IntPtr buf = Marshal.AllocHGlobal((int)(count * IntPtr.Size));
            vkEnumeratePhysicalDevices(_instance, ref count, buf);

            VkPhysicalDevice best = default;
            bool discrete = false;

            // VkPhysicalDeviceProperties is ~824 bytes (our managed struct is incomplete —
            // missing VkPhysicalDeviceLimits and VkPhysicalDeviceSparseProperties).
            // Allocate a native buffer to avoid stack corruption.
            const int PropsFullSize = 840; // generous — actual is ~824
            IntPtr propsPtr = Marshal.AllocHGlobal(PropsFullSize);

            for (int i = 0; i < count; i++)
            {
                var dev = new VkPhysicalDevice { Handle = Marshal.ReadIntPtr(buf, i * IntPtr.Size) };

                // Call into native buffer to avoid overflow
                vkGetPhysicalDevicePropertiesRaw(dev.Handle, propsPtr);

                // Read fields manually: apiVersion(0), driverVersion(4), vendorID(8),
                // deviceID(12), deviceType(16), deviceName[256](20)
                var deviceType = (VkPhysicalDeviceType)(uint)Marshal.ReadInt32(propsPtr, 16);
                byte[] nameBytes = new byte[256];
                Marshal.Copy(propsPtr + 20, nameBytes, 0, 256);
                string name = System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                System.Diagnostics.Trace.WriteLine($"[Vulkan] GPU {i}: {name} ({deviceType})");

                if (!discrete && (deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU || best.Handle == IntPtr.Zero))
                {
                    best = dev;
                    discrete = deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU;
                }
            }

            Marshal.FreeHGlobal(propsPtr);
            Marshal.FreeHGlobal(buf);
            if (best.Handle == IntPtr.Zero) return false;

            _physicalDevice = best;
            return true;
        }

        private bool CreateLogicalDevice()
        {
            // Find graphics+compute queue family
            uint qfCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref qfCount, IntPtr.Zero);
            IntPtr qfBuf = Marshal.AllocHGlobal((int)(qfCount * Marshal.SizeOf<VkQueueFamilyProperties>()));
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref qfCount, qfBuf);

            bool found = false;
            for (uint i = 0; i < qfCount; i++)
            {
                var props = Marshal.PtrToStructure<VkQueueFamilyProperties>(
                    qfBuf + (int)(i * Marshal.SizeOf<VkQueueFamilyProperties>()));
                if ((props.queueFlags & VkQueueFlags.VK_QUEUE_GRAPHICS_BIT) != 0 &&
                    (props.queueFlags & VkQueueFlags.VK_QUEUE_COMPUTE_BIT) != 0)
                {
                    _queueFamilyIndex = i;
                    found = true;
                    break;
                }
            }
            Marshal.FreeHGlobal(qfBuf);

            if (!found) { System.Diagnostics.Trace.WriteLine("[Vulkan] No graphics+compute queue"); return false; }

            float priority = 1.0f;
            IntPtr prioPtr = Marshal.AllocHGlobal(sizeof(float));
            Marshal.Copy(new[] { priority }, 0, prioPtr, 1);

            var queueCI = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = _queueFamilyIndex,
                queueCount = 1,
                pQueuePriorities = prioPtr,
            };

            IntPtr queueCIPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkDeviceQueueCreateInfo>());
            Marshal.StructureToPtr(queueCI, queueCIPtr, false);

            // ── Query ALL available device extensions and enable them ────────
            uint extCount = 0;
            vkEnumerateDeviceExtensionProperties(_physicalDevice, IntPtr.Zero, ref extCount, IntPtr.Zero);
            IntPtr extPropsBuf = Marshal.AllocHGlobal((int)(extCount * VK_EXTENSION_PROPERTIES_SIZE));
            vkEnumerateDeviceExtensionProperties(_physicalDevice, IntPtr.Zero, ref extCount, extPropsBuf);

            var extNames = new List<string>();
            var extNamePtrs = new List<IntPtr>();
            for (int i = 0; i < extCount; i++)
            {
                byte[] nameBytes = new byte[VK_MAX_EXTENSION_NAME_SIZE];
                Marshal.Copy(extPropsBuf + i * VK_EXTENSION_PROPERTIES_SIZE, nameBytes, 0, VK_MAX_EXTENSION_NAME_SIZE);
                string name = System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                extNames.Add(name);
                extNamePtrs.Add(Marshal.StringToHGlobalAnsi(name));
            }
            Marshal.FreeHGlobal(extPropsBuf);

            IntPtr extArray = Marshal.AllocHGlobal(IntPtr.Size * extNamePtrs.Count);
            for (int i = 0; i < extNamePtrs.Count; i++)
                Marshal.WriteIntPtr(extArray, i * IntPtr.Size, extNamePtrs[i]);

            System.Diagnostics.Trace.WriteLine($"[Vulkan] Enabling {extNames.Count} device extensions");

            // ── Query ALL supported features and enable them ────────────────
            // VkPhysicalDeviceFeatures = 55 × VkBool32 = 220 bytes
            const int FeaturesSize = 220;
            IntPtr featuresPtr = Marshal.AllocHGlobal(FeaturesSize);
            vkGetPhysicalDeviceFeaturesRaw(_physicalDevice.Handle, featuresPtr);

            var devCI = new VkDeviceCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                queueCreateInfoCount = 1,
                pQueueCreateInfos = queueCIPtr,
                enabledExtensionCount = (uint)extNamePtrs.Count,
                ppEnabledExtensionNames = extArray,
                pEnabledFeatures = featuresPtr,
            };

            var result = vkCreateDevice(_physicalDevice, ref devCI, IntPtr.Zero, out _device);

            if (result != VkResult.VK_SUCCESS)
            {
                // Retry without extensions (features only)
                System.Diagnostics.Trace.WriteLine($"[Vulkan] vkCreateDevice with all extensions: {VkResultToString(result)}, retrying with fewer...");
                devCI.enabledExtensionCount = 0;
                devCI.ppEnabledExtensionNames = IntPtr.Zero;
                result = vkCreateDevice(_physicalDevice, ref devCI, IntPtr.Zero, out _device);
            }

            Marshal.FreeHGlobal(prioPtr);
            Marshal.FreeHGlobal(queueCIPtr);
            Marshal.FreeHGlobal(featuresPtr);
            foreach (var p in extNamePtrs) Marshal.FreeHGlobal(p);
            Marshal.FreeHGlobal(extArray);

            if (result != VkResult.VK_SUCCESS)
            {
                System.Diagnostics.Trace.WriteLine($"[Vulkan] vkCreateDevice: {VkResultToString(result)}");
                return false;
            }

            vkGetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            System.Diagnostics.Trace.WriteLine($"[Vulkan] Device created, queue family {_queueFamilyIndex}");
            return true;
        }

        private bool CreateReadbackResources()
        {
            // Command pool
            var poolCI = new VkCommandPoolCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO,
                flags = VkCommandPoolCreateFlags.VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT,
                queueFamilyIndex = _queueFamilyIndex,
            };
            var r = vkCreateCommandPool(_device, ref poolCI, IntPtr.Zero, out _cmdPool);
            if (r != VkResult.VK_SUCCESS)
            {
                System.Diagnostics.Trace.WriteLine($"[Vulkan] Command pool: {VkResultToString(r)}");
                return false;
            }

            // Allocate command buffers and fences for each sync slot
            for (int i = 0; i < SyncCount; i++)
            {
                var cbAI = new VkCommandBufferAllocateInfo
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                    commandPool = _cmdPool,
                    level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                    commandBufferCount = 1,
                };
                r = vkAllocateCommandBuffers(_device, ref cbAI, out _cmdBufs[i]);
                if (r != VkResult.VK_SUCCESS) return false;

                var fenceCI = new VkFenceCreateInfo
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
                    flags = (VkFenceCreateFlags)1, // SIGNALED so first WaitForFences doesn't hang
                };
                r = vkCreateFence(_device, ref fenceCI, IntPtr.Zero, out _fences[i]);
                if (r != VkResult.VK_SUCCESS) return false;
            }

            // Pre-allocate native memory for per-frame readback structs
            _barrierPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkImageMemoryBarrier>());
            _regionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkBufferImageCopy>());
            _cmdSubmitPtr = Marshal.AllocHGlobal(IntPtr.Size);

            System.Diagnostics.Trace.WriteLine("[Vulkan] Readback resources created");
            return true;
        }

        private void EnsureStagingBuffers(uint width, uint height)
        {
            if (width == _stagingWidth && height == _stagingHeight)
                return;

            // Destroy old staging buffers
            for (int i = 0; i < SyncCount; i++)
            {
                if (_stagingMapped[i] != IntPtr.Zero)
                {
                    vkUnmapMemory(_device, _stagingMem[i]);
                    _stagingMapped[i] = IntPtr.Zero;
                }
                if (_stagingBufs[i].Handle != IntPtr.Zero)
                {
                    vkDestroyBuffer(_device, _stagingBufs[i], IntPtr.Zero);
                    _stagingBufs[i] = default;
                }
                if (_stagingMem[i].Handle != IntPtr.Zero)
                {
                    vkFreeMemory(_device, _stagingMem[i], IntPtr.Zero);
                    _stagingMem[i] = default;
                }
            }

            ulong bufSize = (ulong)(width * height * 4);
            vkGetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProps);

            for (int i = 0; i < SyncCount; i++)
            {
                var bufCI = new VkBufferCreateInfo
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO,
                    size = bufSize,
                    usage = VkBufferUsageFlags.VK_BUFFER_USAGE_TRANSFER_DST_BIT,
                };
                var r = vkCreateBuffer(_device, ref bufCI, IntPtr.Zero, out _stagingBufs[i]);
                if (r != VkResult.VK_SUCCESS) return;

                vkGetBufferMemoryRequirements(_device, _stagingBufs[i], out var memReq);

                uint memTypeIdx = FindMemoryType(memProps, memReq.memoryTypeBits,
                    VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                    VkMemoryPropertyFlags.VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);

                var allocInfo = new VkMemoryAllocateInfo
                {
                    sType = VkStructureType.VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO,
                    allocationSize = memReq.size,
                    memoryTypeIndex = memTypeIdx,
                };
                r = vkAllocateMemory(_device, ref allocInfo, IntPtr.Zero, out _stagingMem[i]);
                if (r != VkResult.VK_SUCCESS) return;

                vkBindBufferMemory(_device, _stagingBufs[i], _stagingMem[i], 0);
                vkMapMemory(_device, _stagingMem[i], 0, bufSize, 0, out _stagingMapped[i]);
            }

            _stagingWidth = width;
            _stagingHeight = height;
            _stagingSize = bufSize;
            System.Diagnostics.Trace.WriteLine($"[Vulkan] Staging buffers: {width}×{height} mapped[0]=0x{_stagingMapped[0]:X}");
            System.Diagnostics.Trace.Flush();
        }

        // (VkImage tracking via gipa/gdpa interception removed — VkImage is now read
        //  directly from retro_vulkan_image.ci_image (the embedded VkImageViewCreateInfo))

        // =====================================================================
        // Swapchain presentation — blit core's image to screen, no CPU readback
        // =====================================================================

        private bool CreateSwapchain()
        {
            if (_vkGetPhysicalDeviceSurfaceCapabilitiesKHR == null ||
                _vkGetPhysicalDeviceSurfaceFormatsKHR == null)
                return false;

            // Check surface support
            if (_vkGetPhysicalDeviceSurfaceSupportKHR != null)
            {
                _vkGetPhysicalDeviceSurfaceSupportKHR(_physicalDevice, _queueFamilyIndex, _surface, out uint supported);
                if (supported == 0)
                {
                    System.Diagnostics.Trace.WriteLine("[Vulkan] Queue family doesn't support present to surface");
                    return false;
                }
            }

            // Get surface capabilities
            var r = _vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, out var caps);
            if (r != VkResult.VK_SUCCESS) return false;

            _swapWidth = caps.currentExtent.width;
            _swapHeight = caps.currentExtent.height;

            // If currentExtent is 0xFFFFFFFF, the surface size is undefined — use a default
            if (_swapWidth == 0xFFFFFFFF || _swapWidth == 0)
            {
                _swapWidth = 1280;
                _swapHeight = 960;
            }

            // Pick format: prefer B8G8R8A8_UNORM (matches WPF/Windows), fallback to first available
            uint fmtCount = 0;
            _vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref fmtCount, IntPtr.Zero);
            var fmtBuf = Marshal.AllocHGlobal((int)(fmtCount * Marshal.SizeOf<VkSurfaceFormatKHR>()));
            _vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, ref fmtCount, fmtBuf);

            var chosenFormat = VkFormat.VK_FORMAT_B8G8R8A8_UNORM;
            var chosenColorSpace = VkColorSpaceKHR.VK_COLOR_SPACE_SRGB_NONLINEAR_KHR;
            for (int i = 0; i < fmtCount; i++)
            {
                var fmt = Marshal.PtrToStructure<VkSurfaceFormatKHR>(fmtBuf + i * Marshal.SizeOf<VkSurfaceFormatKHR>());
                if (fmt.format == VkFormat.VK_FORMAT_B8G8R8A8_UNORM || fmt.format == VkFormat.VK_FORMAT_B8G8R8A8_SRGB)
                {
                    chosenFormat = fmt.format;
                    chosenColorSpace = fmt.colorSpace;
                    break;
                }
                if (i == 0)
                {
                    chosenFormat = fmt.format;
                    chosenColorSpace = fmt.colorSpace;
                }
            }
            Marshal.FreeHGlobal(fmtBuf);

            uint imageCount = caps.minImageCount + 1;
            if (caps.maxImageCount > 0 && imageCount > caps.maxImageCount)
                imageCount = caps.maxImageCount;

            var swapCI = new VkSwapchainCreateInfoKHR
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR,
                surface = _surface,
                minImageCount = imageCount,
                imageFormat = chosenFormat,
                imageColorSpace = chosenColorSpace,
                imageExtent = new VkExtent2D { width = _swapWidth, height = _swapHeight },
                imageArrayLayers = 1,
                imageUsage = VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT | VkImageUsageFlags.VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT,
                imageSharingMode = 0, // EXCLUSIVE
                preTransform = caps.currentTransform,
                compositeAlpha = VkCompositeAlphaFlagsKHR.VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR,
                presentMode = VkPresentModeKHR.VK_PRESENT_MODE_IMMEDIATE_KHR, // no vsync — emu loop handles timing
                clipped = 1,
            };

            r = vkCreateSwapchainKHR(_device, ref swapCI, IntPtr.Zero, out _swapchain);
            if (r != VkResult.VK_SUCCESS)
            {
                System.Diagnostics.Trace.WriteLine($"[Vulkan] vkCreateSwapchainKHR: {VkResultToString(r)}");
                return false;
            }

            // Get swapchain images
            uint imgCount = 0;
            vkGetSwapchainImagesKHR(_device, _swapchain, ref imgCount, IntPtr.Zero);
            IntPtr imgBuf = Marshal.AllocHGlobal((int)(imgCount * IntPtr.Size));
            vkGetSwapchainImagesKHR(_device, _swapchain, ref imgCount, imgBuf);
            _swapImages = new VkImage[imgCount];
            for (int i = 0; i < imgCount; i++)
                _swapImages[i] = new VkImage { Handle = Marshal.ReadIntPtr(imgBuf, i * IntPtr.Size) };
            Marshal.FreeHGlobal(imgBuf);

            // Create semaphores and fence for presentation
            var semCI = new VkSemaphoreCreateInfo { sType = VkStructureType.VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO };
            vkCreateSemaphore(_device, ref semCI, IntPtr.Zero, out _imageAvailableSemaphore);
            vkCreateSemaphore(_device, ref semCI, IntPtr.Zero, out _renderFinishedSemaphore);

            var fenceCI = new VkFenceCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_FENCE_CREATE_INFO,
                flags = (VkFenceCreateFlags)1, // SIGNALED
            };
            vkCreateFence(_device, ref fenceCI, IntPtr.Zero, out _presentFence);

            // Allocate command buffer for presentation
            var cbAI = new VkCommandBufferAllocateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO,
                commandPool = _cmdPool,
                level = VkCommandBufferLevel.VK_COMMAND_BUFFER_LEVEL_PRIMARY,
                commandBufferCount = 1,
            };
            vkAllocateCommandBuffers(_device, ref cbAI, out _presentCmd);

            // Pre-allocate blit region
            _blitRegionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkImageBlit>());

            _swapchainActive = true;
            System.Diagnostics.Trace.WriteLine($"[Vulkan] Swapchain created: {_swapWidth}x{_swapHeight} format={chosenFormat} images={imgCount}");
            return true;
        }

        private int _presentLogCount;

        /// <summary>
        /// Blit the core's rendered image directly to the swapchain and present.
        /// No CPU readback — everything stays on the GPU.
        /// </summary>
        public bool PresentFrame(uint srcWidth, uint srcHeight)
        {
            lock (_imageLock)
            {
                if (!_swapchainActive || !_hasImage || _currentVkImage == IntPtr.Zero)
                    return false;

                if (srcWidth < 8 || srcHeight < 8)
                    return false;

                bool verbose = _presentLogCount < 3;
                if (verbose) _presentLogCount++;

                try
                {
                    // Wait for previous present to complete
                    vkWaitForFences(_device, 1, ref _presentFence, 1, 1_000_000_000UL);
                    vkResetFences(_device, 1, ref _presentFence);

                    // Acquire next swapchain image
                    uint imageIndex;
                    var acqResult = vkAcquireNextImageKHR(_device, _swapchain, ulong.MaxValue,
                        _imageAvailableSemaphore, default, out imageIndex);
                    if (acqResult != VkResult.VK_SUCCESS && acqResult != (VkResult)1000001003) // SUBOPTIMAL_KHR
                        return false;

                    // Record blit command
                    vkResetCommandBuffer(_presentCmd, 0);
                    var beginInfo = new VkCommandBufferBeginInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO,
                        flags = 1, // ONE_TIME_SUBMIT
                    };
                    vkBeginCommandBuffer(_presentCmd, ref beginInfo);

                    var srcImage = new VkImage { Handle = _currentVkImage };
                    var dstImage = _swapImages[imageIndex];
                    var oldLayout = (VkImageLayout)_currentImage.image_layout;

                    var colorSubresource = new VkImageSubresourceRange
                    {
                        aspectMask = (uint)VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                        baseMipLevel = 0, levelCount = 1, baseArrayLayer = 0, layerCount = 1,
                    };

                    // Barrier: src image → TRANSFER_SRC
                    var srcBarrier = new VkImageMemoryBarrier
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                        srcAccessMask = VkAccessFlags.VK_ACCESS_NONE,
                        dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT,
                        oldLayout = oldLayout,
                        newLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        image = srcImage,
                        subresourceRange = colorSubresource,
                    };

                    // Barrier: dst (swapchain) image → TRANSFER_DST
                    var dstBarrier = new VkImageMemoryBarrier
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER,
                        srcAccessMask = VkAccessFlags.VK_ACCESS_NONE,
                        dstAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT,
                        oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_UNDEFINED,
                        newLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                        srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
                        image = dstImage,
                        subresourceRange = colorSubresource,
                    };

                    // Write both barriers (contiguous in memory)
                    IntPtr barriersPtr = Marshal.AllocHGlobal(Marshal.SizeOf<VkImageMemoryBarrier>() * 2);
                    Marshal.StructureToPtr(srcBarrier, barriersPtr, false);
                    Marshal.StructureToPtr(dstBarrier, barriersPtr + Marshal.SizeOf<VkImageMemoryBarrier>(), false);
                    vkCmdPipelineBarrier(_presentCmd,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                        0, 0, IntPtr.Zero, 0, IntPtr.Zero, 2, barriersPtr);

                    // Blit: core image → swapchain image (scales to fit)
                    var colorLayer = new VkImageSubresourceLayers
                    {
                        aspectMask = VkImageAspectFlags.VK_IMAGE_ASPECT_COLOR_BIT,
                        mipLevel = 0, baseArrayLayer = 0, layerCount = 1,
                    };
                    var blit = new VkImageBlit
                    {
                        srcSubresource = colorLayer,
                        srcOffset0_x = 0, srcOffset0_y = 0, srcOffset0_z = 0,
                        srcOffset1_x = (int)srcWidth, srcOffset1_y = (int)srcHeight, srcOffset1_z = 1,
                        dstSubresource = colorLayer,
                        dstOffset0_x = 0, dstOffset0_y = 0, dstOffset0_z = 0,
                        dstOffset1_x = (int)_swapWidth, dstOffset1_y = (int)_swapHeight, dstOffset1_z = 1,
                    };
                    Marshal.StructureToPtr(blit, _blitRegionPtr, false);
                    vkCmdBlitImage(_presentCmd,
                        srcImage, (uint)VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                        dstImage, (uint)VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                        1, _blitRegionPtr, 1); // VK_FILTER_LINEAR = 1

                    // Barrier: src back to original layout
                    srcBarrier.srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_READ_BIT;
                    srcBarrier.dstAccessMask = VkAccessFlags.VK_ACCESS_NONE;
                    srcBarrier.oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
                    srcBarrier.newLayout = oldLayout;

                    // Barrier: dst → PRESENT_SRC
                    dstBarrier.srcAccessMask = VkAccessFlags.VK_ACCESS_TRANSFER_WRITE_BIT;
                    dstBarrier.dstAccessMask = VkAccessFlags.VK_ACCESS_NONE;
                    dstBarrier.oldLayout = VkImageLayout.VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL;
                    dstBarrier.newLayout = VkImageLayout.VK_IMAGE_LAYOUT_PRESENT_SRC_KHR;

                    Marshal.StructureToPtr(srcBarrier, barriersPtr, false);
                    Marshal.StructureToPtr(dstBarrier, barriersPtr + Marshal.SizeOf<VkImageMemoryBarrier>(), false);
                    vkCmdPipelineBarrier(_presentCmd,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT,
                        (uint)VkPipelineStageFlags.VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                        0, 0, IntPtr.Zero, 0, IntPtr.Zero, 2, barriersPtr);

                    vkEndCommandBuffer(_presentCmd);
                    Marshal.FreeHGlobal(barriersPtr);

                    // Submit with semaphore sync: wait for image available, signal render finished
                    IntPtr waitSemPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(waitSemPtr, _imageAvailableSemaphore.Handle);
                    IntPtr signalSemPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(signalSemPtr, _renderFinishedSemaphore.Handle);
                    IntPtr cmdPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(cmdPtr, _presentCmd.Handle);
                    IntPtr waitStagePtr = Marshal.AllocHGlobal(sizeof(uint));
                    Marshal.WriteInt32(waitStagePtr, (int)VkPipelineStageFlags.VK_PIPELINE_STAGE_TRANSFER_BIT);

                    var submitInfo = new VkSubmitInfo
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_SUBMIT_INFO,
                        waitSemaphoreCount = 1,
                        pWaitSemaphores = waitSemPtr,
                        pWaitDstStageMask = waitStagePtr,
                        commandBufferCount = 1,
                        pCommandBuffers = cmdPtr,
                        signalSemaphoreCount = 1,
                        pSignalSemaphores = signalSemPtr,
                    };

                    VkResult submitResult;
                    lock (_queueLock) { submitResult = vkQueueSubmit(_queue, 1, ref submitInfo, _presentFence); }

                    Marshal.FreeHGlobal(waitSemPtr);
                    Marshal.FreeHGlobal(signalSemPtr);
                    Marshal.FreeHGlobal(cmdPtr);
                    Marshal.FreeHGlobal(waitStagePtr);

                    if (submitResult != VkResult.VK_SUCCESS)
                        return false;

                    // Present
                    IntPtr swapPtr = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(swapPtr, _swapchain.Handle);
                    IntPtr idxPtr = Marshal.AllocHGlobal(sizeof(uint));
                    Marshal.WriteInt32(idxPtr, (int)imageIndex);
                    IntPtr presentWaitSem = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(presentWaitSem, _renderFinishedSemaphore.Handle);

                    var presentInfo = new VkPresentInfoKHR
                    {
                        sType = VkStructureType.VK_STRUCTURE_TYPE_PRESENT_INFO_KHR,
                        waitSemaphoreCount = 1,
                        pWaitSemaphores = presentWaitSem,
                        swapchainCount = 1,
                        pSwapchains = swapPtr,
                        pImageIndices = idxPtr,
                    };

                    VkResult presentResult;
                    lock (_queueLock) { presentResult = vkQueuePresentKHR(_queue, ref presentInfo); }

                    Marshal.FreeHGlobal(swapPtr);
                    Marshal.FreeHGlobal(idxPtr);
                    Marshal.FreeHGlobal(presentWaitSem);

                    if (verbose)
                        System.Diagnostics.Trace.WriteLine($"[Vulkan] PresentFrame: {srcWidth}x{srcHeight} → {_swapWidth}x{_swapHeight} idx={imageIndex}");

                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"[Vulkan] PresentFrame exception: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// Recreate swapchain after window resize.
        /// </summary>
        public void RecreateSwapchain(uint width, uint height)
        {
            lock (_imageLock)
            {
                if (!_swapchainActive || _surface.Handle == IntPtr.Zero) return;
                if (width == _swapWidth && height == _swapHeight) return;
                if (width == 0 || height == 0) return;

                vkDeviceWaitIdle(_device);
                DestroySwapchainResources();
                CreateSwapchain();
            }
        }

        private void DestroySwapchainResources()
        {
            if (_imageAvailableSemaphore.Handle != IntPtr.Zero)
            { vkDestroySemaphore(_device, _imageAvailableSemaphore, IntPtr.Zero); _imageAvailableSemaphore = default; }
            if (_renderFinishedSemaphore.Handle != IntPtr.Zero)
            { vkDestroySemaphore(_device, _renderFinishedSemaphore, IntPtr.Zero); _renderFinishedSemaphore = default; }
            if (_presentFence.Handle != IntPtr.Zero)
            { vkDestroyFence(_device, _presentFence, IntPtr.Zero); _presentFence = default; }
            if (_swapchain.Handle != IntPtr.Zero)
            { vkDestroySwapchainKHR(_device, _swapchain, IntPtr.Zero); _swapchain = default; }
            if (_blitRegionPtr != IntPtr.Zero)
            { Marshal.FreeHGlobal(_blitRegionPtr); _blitRegionPtr = IntPtr.Zero; }
            _swapImages = Array.Empty<VkImage>();
            _swapchainActive = false;
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static uint MakeVersion(uint major, uint minor, uint patch)
            => (major << 22) | (minor << 12) | patch;

        private static uint FindMemoryType(VkPhysicalDeviceMemoryProperties memProps, uint typeBits, VkMemoryPropertyFlags required)
        {
            for (int i = 0; i < memProps.memoryTypeCount; i++)
            {
                if ((typeBits & (1u << i)) != 0)
                {
                    var mt = memProps.GetMemoryType(i);
                    if ((mt.propertyFlags & required) == required)
                        return (uint)i;
                }
            }
            return 0; // fallback to first type
        }

        /// <summary>
        /// Sets the device and queue from external source (core's create_device).
        /// Called when the core creates the device and we need to adopt it.
        /// </summary>
        public void SetDevice(IntPtr device, IntPtr queue, uint queueFamilyIndex)
        {
            _device = new VkDevice { Handle = device };
            _queue = new VkQueue { Handle = queue };
            _queueFamilyIndex = queueFamilyIndex;
        }

        // =====================================================================
        // Cleanup
        // =====================================================================

        private void Cleanup()
        {
            if (_device.Handle != IntPtr.Zero)
                vkDeviceWaitIdle(_device);

            DestroySwapchainResources();

            if (_surface.Handle != IntPtr.Zero && _vkDestroySurfaceKHR != null)
            {
                _vkDestroySurfaceKHR(_instance, _surface, IntPtr.Zero);
                _surface = default;
            }

            for (int i = 0; i < SyncCount; i++)
            {
                if (_stagingMapped[i] != IntPtr.Zero)
                {
                    vkUnmapMemory(_device, _stagingMem[i]);
                    _stagingMapped[i] = IntPtr.Zero;
                }
                if (_stagingBufs[i].Handle != IntPtr.Zero)
                    vkDestroyBuffer(_device, _stagingBufs[i], IntPtr.Zero);
                if (_stagingMem[i].Handle != IntPtr.Zero)
                    vkFreeMemory(_device, _stagingMem[i], IntPtr.Zero);
                if (_fences[i].Handle != IntPtr.Zero)
                    vkDestroyFence(_device, _fences[i], IntPtr.Zero);
            }

            if (_cmdPool.Handle != IntPtr.Zero)
                vkDestroyCommandPool(_device, _cmdPool, IntPtr.Zero);

            if (_barrierPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_barrierPtr); _barrierPtr = IntPtr.Zero; }
            if (_regionPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_regionPtr); _regionPtr = IntPtr.Zero; }
            if (_cmdSubmitPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_cmdSubmitPtr); _cmdSubmitPtr = IntPtr.Zero; }

            // Defer device/instance destruction to the next session.
            // nvoglv64.dll driver threads may still reference VkDevice memory
            // after teardown.  Keeping it alive prevents AVs; the next session's
            // DestroyDeferredResources() call will clean up once threads are dead.
            if (_device.Handle != IntPtr.Zero || _instance.Handle != IntPtr.Zero)
            {
                // Destroy any previously deferred resources first (shouldn't happen,
                // but defensive).
                DestroyDeferredResources();

                _deferredDevice = _device;
                _deferredInstance = _instance;
                _deferredCoreDestroyDevice = _coreDestroyDevice;
                _deferredCoreOwned = _coreOwnsDevice;
                System.Diagnostics.Trace.WriteLine($"[Vulkan] Deferred device=0x{_device.Handle:X} instance=0x{_instance.Handle:X} for next-session cleanup");
            }

            if (_hwIfacePtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_hwIfacePtr);
                _hwIfacePtr = IntPtr.Zero;
            }

            if (_vulkanContextPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_vulkanContextPtr);
                _vulkanContextPtr = IntPtr.Zero;
            }

            if (_handlePin.IsAllocated)
                _handlePin.Free();

            _initialized = false;
        }

        public void Dispose()
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }
    }
}
