using System;
using System.Runtime.InteropServices;

namespace Emutastic.Services
{
    /// <summary>
    /// Vulkan P/Invoke bindings for ParaLLEl N64 hardware rendering support.
    /// Minimal subset needed for libretro frontend implementation.
    /// </summary>
    public static class VulkanInterop
    {
        private const string VulkanLib = "vulkan-1.dll";

        // =========================================================================
        // Vulkan Types
        // =========================================================================
        public enum VkResult : int
        {
            VK_SUCCESS = 0,
            VK_NOT_READY = 1,
            VK_TIMEOUT = 2,
            VK_EVENT_SET = 3,
            VK_EVENT_RESET = 4,
            VK_INCOMPLETE = 5,
            VK_ERROR_OUT_OF_HOST_MEMORY = -1,
            VK_ERROR_OUT_OF_DEVICE_MEMORY = -2,
            VK_ERROR_INITIALIZATION_FAILED = -3,
            VK_ERROR_DEVICE_LOST = -4,
            VK_ERROR_MEMORY_MAP_FAILED = -5,
            VK_ERROR_LAYER_NOT_PRESENT = -6,
            VK_ERROR_EXTENSION_NOT_PRESENT = -7,
            VK_ERROR_FEATURE_NOT_PRESENT = -8,
            VK_ERROR_INCOMPATIBLE_DRIVER = -9,
            VK_ERROR_TOO_MANY_OBJECTS = -10,
            VK_ERROR_FORMAT_NOT_SUPPORTED = -11,
        }

        public enum VkStructureType : uint
        {
            VK_STRUCTURE_TYPE_APPLICATION_INFO = 0,
            VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1,
            VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO = 2,
            VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 5,
            VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR = 1000001000,
            VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CREATE_INFO_EXT = 1000128004,
        }

        public enum VkPhysicalDeviceType : uint
        {
            VK_PHYSICAL_DEVICE_TYPE_OTHER = 0,
            VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU = 1,
            VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU = 2,
            VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU = 3,
            VK_PHYSICAL_DEVICE_TYPE_CPU = 4,
        }

        [Flags]
        public enum VkInstanceCreateFlags : uint
        {
            None = 0,
        }

        [Flags]
        public enum VkDeviceCreateFlags : uint
        {
            None = 0,
        }

        [Flags]
        public enum VkQueueFlags : uint
        {
            VK_QUEUE_GRAPHICS_BIT = 0x00000001,
            VK_QUEUE_COMPUTE_BIT = 0x00000002,
            VK_QUEUE_TRANSFER_BIT = 0x00000004,
            VK_QUEUE_SPARSE_BINDING_BIT = 0x00000008,
        }

        public enum VkPresentModeKHR : int
        {
            VK_PRESENT_MODE_IMMEDIATE_KHR = 0,
            VK_PRESENT_MODE_MAILBOX_KHR = 1,
            VK_PRESENT_MODE_FIFO_KHR = 2,
            VK_PRESENT_MODE_FIFO_RELAXED_KHR = 3,
        }

        public enum VkColorSpaceKHR : int
        {
            VK_COLOR_SPACE_SRGB_NONLINEAR_KHR = 0,
        }

        public enum VkFormat : int
        {
            VK_FORMAT_UNDEFINED = 0,
            VK_FORMAT_B8G8R8A8_UNORM = 44,
            VK_FORMAT_R8G8B8A8_UNORM = 37,
        }

        public enum VkImageUsageFlags : uint
        {
            VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x00000010,
            VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x00000004,
        }

        public enum VkCompositeAlphaFlagsKHR : uint
        {
            VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR = 0x00000001,
        }

        public enum VkImageViewType : uint
        {
            VK_IMAGE_VIEW_TYPE_2D = 1,
        }

        public enum VkComponentSwizzle : uint
        {
            VK_COMPONENT_SWIZZLE_IDENTITY = 0,
            VK_COMPONENT_SWIZZLE_R = 1,
            VK_COMPONENT_SWIZZLE_G = 2,
            VK_COMPONENT_SWIZZLE_B = 3,
            VK_COMPONENT_SWIZZLE_A = 4,
        }

        // =========================================================================
        // Vulkan Handles
        // =========================================================================
        public struct VkInstance : IEquatable<VkInstance>
        {
            public IntPtr Handle;
            public bool Equals(VkInstance other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkInstance other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkInstance left, VkInstance right) => left.Equals(right);
            public static bool operator !=(VkInstance left, VkInstance right) => !left.Equals(right);
        }

        public struct VkPhysicalDevice : IEquatable<VkPhysicalDevice>
        {
            public IntPtr Handle;
            public bool Equals(VkPhysicalDevice other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkPhysicalDevice other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkPhysicalDevice left, VkPhysicalDevice right) => left.Equals(right);
            public static bool operator !=(VkPhysicalDevice left, VkPhysicalDevice right) => !left.Equals(right);
        }

        public struct VkDevice : IEquatable<VkDevice>
        {
            public IntPtr Handle;
            public bool Equals(VkDevice other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkDevice other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkDevice left, VkDevice right) => left.Equals(right);
            public static bool operator !=(VkDevice left, VkDevice right) => !left.Equals(right);
        }

        public struct VkQueue : IEquatable<VkQueue>
        {
            public IntPtr Handle;
            public bool Equals(VkQueue other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkQueue other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkQueue left, VkQueue right) => left.Equals(right);
            public static bool operator !=(VkQueue left, VkQueue right) => !left.Equals(right);
        }

        public struct VkSwapchainKHR : IEquatable<VkSwapchainKHR>
        {
            public IntPtr Handle;
            public bool Equals(VkSwapchainKHR other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkSwapchainKHR other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkSwapchainKHR left, VkSwapchainKHR right) => left.Equals(right);
            public static bool operator !=(VkSwapchainKHR left, VkSwapchainKHR right) => !left.Equals(right);
        }

        public struct VkImage : IEquatable<VkImage>
        {
            public IntPtr Handle;
            public bool Equals(VkImage other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkImage other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkImage left, VkImage right) => left.Equals(right);
            public static bool operator !=(VkImage left, VkImage right) => !left.Equals(right);
        }

        public struct VkImageView : IEquatable<VkImageView>
        {
            public IntPtr Handle;
            public bool Equals(VkImageView other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkImageView other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkImageView left, VkImageView right) => left.Equals(right);
            public static bool operator !=(VkImageView left, VkImageView right) => !left.Equals(right);
        }

        public struct VkFramebuffer : IEquatable<VkFramebuffer>
        {
            public IntPtr Handle;
            public bool Equals(VkFramebuffer other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkFramebuffer other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkFramebuffer left, VkFramebuffer right) => left.Equals(right);
            public static bool operator !=(VkFramebuffer left, VkFramebuffer right) => !left.Equals(right);
        }

        public struct VkRenderPass : IEquatable<VkRenderPass>
        {
            public IntPtr Handle;
            public bool Equals(VkRenderPass other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkRenderPass other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkRenderPass left, VkRenderPass right) => left.Equals(right);
            public static bool operator !=(VkRenderPass left, VkRenderPass right) => !left.Equals(right);
        }

        public struct VkCommandPool : IEquatable<VkCommandPool>
        {
            public IntPtr Handle;
            public bool Equals(VkCommandPool other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkCommandPool other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkCommandPool left, VkCommandPool right) => left.Equals(right);
            public static bool operator !=(VkCommandPool left, VkCommandPool right) => !left.Equals(right);
        }

        public struct VkCommandBuffer : IEquatable<VkCommandBuffer>
        {
            public IntPtr Handle;
            public bool Equals(VkCommandBuffer other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkCommandBuffer other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkCommandBuffer left, VkCommandBuffer right) => left.Equals(right);
            public static bool operator !=(VkCommandBuffer left, VkCommandBuffer right) => !left.Equals(right);
        }

        public struct VkSurfaceKHR : IEquatable<VkSurfaceKHR>
        {
            public IntPtr Handle;
            public bool Equals(VkSurfaceKHR other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkSurfaceKHR other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkSurfaceKHR left, VkSurfaceKHR right) => left.Equals(right);
            public static bool operator !=(VkSurfaceKHR left, VkSurfaceKHR right) => !left.Equals(right);
        }

        public struct VkSemaphore : IEquatable<VkSemaphore>
        {
            public IntPtr Handle;
            public bool Equals(VkSemaphore other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkSemaphore other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkSemaphore left, VkSemaphore right) => left.Equals(right);
            public static bool operator !=(VkSemaphore left, VkSemaphore right) => !left.Equals(right);
        }

        public struct VkFence : IEquatable<VkFence>
        {
            public IntPtr Handle;
            public bool Equals(VkFence other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkFence other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkFence left, VkFence right) => left.Equals(right);
            public static bool operator !=(VkFence left, VkFence right) => !left.Equals(right);
        }

        // =========================================================================
        // Vulkan Structs
        // =========================================================================
        [StructLayout(LayoutKind.Sequential)]
        public struct VkApplicationInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public IntPtr pApplicationName;
            public uint applicationVersion;
            public IntPtr pEngineName;
            public uint engineVersion;
            public uint apiVersion;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkInstanceCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public VkInstanceCreateFlags flags;
            public IntPtr pApplicationInfo;
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkDeviceQueueCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
            public uint queueFamilyIndex;
            public uint queueCount;
            public IntPtr pQueuePriorities;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkDeviceCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public VkDeviceCreateFlags flags;
            public uint queueCreateInfoCount;
            public IntPtr pQueueCreateInfos;
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
            public IntPtr pEnabledFeatures;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkPhysicalDeviceProperties
        {
            public uint apiVersion;
            public uint driverVersion;
            public uint vendorID;
            public uint deviceID;
            public VkPhysicalDeviceType deviceType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] deviceName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] pipelineCacheUUID;
            // VkPhysicalDeviceLimits and VkPhysicalDeviceSparseProperties omitted for brevity
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSwapchainCreateInfoKHR
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
            public VkSurfaceKHR surface;
            public uint minImageCount;
            public VkFormat imageFormat;
            public VkColorSpaceKHR imageColorSpace;
            public VkExtent2D imageExtent;
            public uint imageArrayLayers;
            public VkImageUsageFlags imageUsage;
            public uint imageSharingMode;
            public uint queueFamilyIndexCount;
            public IntPtr pQueueFamilyIndices;
            public uint preTransform;
            public VkCompositeAlphaFlagsKHR compositeAlpha;
            public VkPresentModeKHR presentMode;
            public uint clipped;
            public VkSwapchainKHR oldSwapchain;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkExtent2D
        {
            public uint width;
            public uint height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageViewCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
            public VkImage image;
            public VkImageViewType viewType;
            public VkFormat format;
            public VkComponentMapping components;
            public VkImageSubresourceRange subresourceRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkComponentMapping
        {
            public VkComponentSwizzle r;
            public VkComponentSwizzle g;
            public VkComponentSwizzle b;
            public VkComponentSwizzle a;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageSubresourceRange
        {
            public uint aspectMask;
            public uint baseMipLevel;
            public uint levelCount;
            public uint baseArrayLayer;
            public uint layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkFramebufferCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
            public VkRenderPass renderPass;
            public uint attachmentCount;
            public IntPtr pAttachments;
            public uint width;
            public uint height;
            public uint layers;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkQueueFamilyProperties
        {
            public VkQueueFlags queueFlags;
            public uint queueCount;
            public uint timestampValidBits;
            public VkExtent3D minImageTransferGranularity;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkExtent3D
        {
            public uint width;
            public uint height;
            public uint depth;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkPresentInfoKHR
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint waitSemaphoreCount;
            public IntPtr pWaitSemaphores;
            public uint swapchainCount;
            public IntPtr pSwapchains;
            public IntPtr pImageIndices;
            public IntPtr pResults;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkAcquireNextImageInfoKHR
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public VkSwapchainKHR swapchain;
            public ulong timeout;
            public VkSemaphore semaphore;
            public VkFence fence;
            public uint deviceMask;
        }

        // =========================================================================
        // Vulkan Functions
        // =========================================================================
        
        // Instance level
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateInstance(ref VkInstanceCreateInfo pCreateInfo, IntPtr pAllocator, out VkInstance pInstance);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyInstance(VkInstance instance, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vkGetInstanceProcAddr(VkInstance instance, [MarshalAs(UnmanagedType.LPStr)] string pName);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkEnumeratePhysicalDevices(VkInstance instance, ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkGetPhysicalDeviceProperties(VkPhysicalDevice physicalDevice, out VkPhysicalDeviceProperties pProperties);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkGetPhysicalDeviceQueueFamilyProperties(VkPhysicalDevice physicalDevice, ref uint pQueueFamilyPropertyCount, IntPtr pQueueFamilyProperties);

        // Device level
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateDevice(VkPhysicalDevice physicalDevice, ref VkDeviceCreateInfo pCreateInfo, IntPtr pAllocator, out VkDevice pDevice);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyDevice(VkDevice device, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkGetDeviceQueue(VkDevice device, uint queueFamilyIndex, uint queueIndex, out VkQueue pQueue);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkDeviceWaitIdle(VkDevice device);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateSwapchainKHR(VkDevice device, ref VkSwapchainCreateInfoKHR pCreateInfo, IntPtr pAllocator, out VkSwapchainKHR pSwapchain);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkGetSwapchainImagesKHR(VkDevice device, VkSwapchainKHR swapchain, ref uint pSwapchainImageCount, IntPtr pSwapchainImages);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkAcquireNextImageKHR(VkDevice device, VkSwapchainKHR swapchain, ulong timeout, VkSemaphore semaphore, VkFence fence, out uint pImageIndex);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkQueuePresentKHR(VkQueue queue, ref VkPresentInfoKHR pPresentInfo);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateImageView(VkDevice device, ref VkImageViewCreateInfo pCreateInfo, IntPtr pAllocator, out VkImageView pView);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyImageView(VkDevice device, VkImageView imageView, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateFramebuffer(VkDevice device, ref VkFramebufferCreateInfo pCreateInfo, IntPtr pAllocator, out VkFramebuffer pFramebuffer);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyFramebuffer(VkDevice device, VkFramebuffer framebuffer, IntPtr pAllocator);

        // =========================================================================
        // Helper Methods
        // =========================================================================
        public static bool IsVulkanAvailable()
        {
            try
            {
                // Try to load vulkan-1.dll
                var handle = NativeMethods.LoadLibrary(VulkanLib);
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.FreeLibrary(handle);
                    return true;
                }
            }
            catch { }
            return false;
        }

        public static string VkResultToString(VkResult result)
        {
            return result switch
            {
                VkResult.VK_SUCCESS => "SUCCESS",
                VkResult.VK_NOT_READY => "NOT_READY",
                VkResult.VK_TIMEOUT => "TIMEOUT",
                VkResult.VK_EVENT_SET => "EVENT_SET",
                VkResult.VK_EVENT_RESET => "EVENT_RESET",
                VkResult.VK_INCOMPLETE => "INCOMPLETE",
                VkResult.VK_ERROR_OUT_OF_HOST_MEMORY => "ERROR_OUT_OF_HOST_MEMORY",
                VkResult.VK_ERROR_OUT_OF_DEVICE_MEMORY => "ERROR_OUT_OF_DEVICE_MEMORY",
                VkResult.VK_ERROR_INITIALIZATION_FAILED => "ERROR_INITIALIZATION_FAILED",
                VkResult.VK_ERROR_DEVICE_LOST => "ERROR_DEVICE_LOST",
                VkResult.VK_ERROR_MEMORY_MAP_FAILED => "ERROR_MEMORY_MAP_FAILED",
                VkResult.VK_ERROR_LAYER_NOT_PRESENT => "ERROR_LAYER_NOT_PRESENT",
                VkResult.VK_ERROR_EXTENSION_NOT_PRESENT => "ERROR_EXTENSION_NOT_PRESENT",
                VkResult.VK_ERROR_FEATURE_NOT_PRESENT => "ERROR_FEATURE_NOT_PRESENT",
                VkResult.VK_ERROR_INCOMPATIBLE_DRIVER => "ERROR_INCOMPATIBLE_DRIVER",
                VkResult.VK_ERROR_TOO_MANY_OBJECTS => "ERROR_TOO_MANY_OBJECTS",
                VkResult.VK_ERROR_FORMAT_NOT_SUPPORTED => "ERROR_FORMAT_NOT_SUPPORTED",
                _ => $"UNKNOWN({result})"
            };
        }
    }
}
