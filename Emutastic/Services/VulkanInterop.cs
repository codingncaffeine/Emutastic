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
            VK_STRUCTURE_TYPE_SUBMIT_INFO = 4,
            VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO = 5,
            VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 46,
            VK_STRUCTURE_TYPE_FENCE_CREATE_INFO = 8,
            VK_STRUCTURE_TYPE_SEMAPHORE_CREATE_INFO = 9,
            VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO = 12,
            VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO = 15,
            VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39,
            VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40,
            VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42,
            VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER = 45,
            VK_STRUCTURE_TYPE_PRESENT_INFO_KHR = 1000001001,
            VK_STRUCTURE_TYPE_SWAPCHAIN_CREATE_INFO_KHR = 1000001000,
            VK_STRUCTURE_TYPE_WIN32_SURFACE_CREATE_INFO_KHR = 1000009000,
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
            VK_FORMAT_R8G8B8A8_UNORM = 37,
            VK_FORMAT_R8G8B8A8_SRGB = 43,
            VK_FORMAT_B8G8R8A8_UNORM = 44,
            VK_FORMAT_B8G8R8A8_SRGB = 50,
        }

        [Flags]
        public enum VkImageUsageFlags : uint
        {
            VK_IMAGE_USAGE_TRANSFER_SRC_BIT = 0x00000001,
            VK_IMAGE_USAGE_TRANSFER_DST_BIT = 0x00000004,
            VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT = 0x00000010,
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

        public enum VkImageLayout : uint
        {
            VK_IMAGE_LAYOUT_UNDEFINED = 0,
            VK_IMAGE_LAYOUT_GENERAL = 1,
            VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL = 2,
            VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL = 5,
            VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL = 6,
            VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL = 7,
            VK_IMAGE_LAYOUT_PRESENT_SRC_KHR = 1000001002,
        }

        [Flags]
        public enum VkBufferUsageFlags : uint
        {
            VK_BUFFER_USAGE_TRANSFER_SRC_BIT = 0x00000001,
            VK_BUFFER_USAGE_TRANSFER_DST_BIT = 0x00000002,
        }

        [Flags]
        public enum VkMemoryPropertyFlags : uint
        {
            VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT = 0x00000001,
            VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT = 0x00000002,
            VK_MEMORY_PROPERTY_HOST_COHERENT_BIT = 0x00000004,
        }

        [Flags]
        public enum VkCommandPoolCreateFlags : uint
        {
            VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT = 0x00000002,
        }

        public enum VkCommandBufferLevel : uint
        {
            VK_COMMAND_BUFFER_LEVEL_PRIMARY = 0,
        }

        [Flags]
        public enum VkPipelineStageFlags : uint
        {
            VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT = 0x00000001,
            VK_PIPELINE_STAGE_TRANSFER_BIT = 0x00001000,
            VK_PIPELINE_STAGE_HOST_BIT = 0x00004000,
            VK_PIPELINE_STAGE_ALL_COMMANDS_BIT = 0x00010000,
        }

        [Flags]
        public enum VkAccessFlags : uint
        {
            VK_ACCESS_NONE = 0,
            VK_ACCESS_TRANSFER_READ_BIT = 0x00000800,
            VK_ACCESS_TRANSFER_WRITE_BIT = 0x00001000,
            VK_ACCESS_HOST_READ_BIT = 0x00002000,
        }

        [Flags]
        public enum VkImageAspectFlags : uint
        {
            VK_IMAGE_ASPECT_COLOR_BIT = 0x00000001,
        }

        [Flags]
        public enum VkFenceCreateFlags : uint
        {
            None = 0,
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

        public struct VkBuffer : IEquatable<VkBuffer>
        {
            public IntPtr Handle;
            public bool Equals(VkBuffer other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkBuffer other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkBuffer left, VkBuffer right) => left.Equals(right);
            public static bool operator !=(VkBuffer left, VkBuffer right) => !left.Equals(right);
        }

        public struct VkDeviceMemory : IEquatable<VkDeviceMemory>
        {
            public IntPtr Handle;
            public bool Equals(VkDeviceMemory other) => Handle == other.Handle;
            public override bool Equals(object? obj) => obj is VkDeviceMemory other && Equals(other);
            public override int GetHashCode() => Handle.GetHashCode();
            public static bool operator ==(VkDeviceMemory left, VkDeviceMemory right) => left.Equals(right);
            public static bool operator !=(VkDeviceMemory left, VkDeviceMemory right) => !left.Equals(right);
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
        public struct VkWin32SurfaceCreateInfoKHR
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
            public IntPtr hinstance;
            public IntPtr hwnd;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSurfaceCapabilitiesKHR
        {
            public uint minImageCount;
            public uint maxImageCount;
            public VkExtent2D currentExtent;
            public VkExtent2D minImageExtent;
            public VkExtent2D maxImageExtent;
            public uint maxImageArrayLayers;
            public uint supportedTransforms;
            public uint currentTransform;
            public uint supportedCompositeAlpha;
            public uint supportedUsageFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSurfaceFormatKHR
        {
            public VkFormat format;
            public VkColorSpaceKHR colorSpace;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSemaphoreCreateInfo
        {
            public VkStructureType sType;
            public IntPtr pNext;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageBlit
        {
            public VkImageSubresourceLayers srcSubresource;
            public int srcOffset0_x, srcOffset0_y, srcOffset0_z;
            public int srcOffset1_x, srcOffset1_y, srcOffset1_z;
            public VkImageSubresourceLayers dstSubresource;
            public int dstOffset0_x, dstOffset0_y, dstOffset0_z;
            public int dstOffset1_x, dstOffset1_y, dstOffset1_z;
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
        // Buffer / Memory / Command structs
        // =========================================================================
        [StructLayout(LayoutKind.Sequential)]
        public struct VkBufferCreateInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO = 12
            public IntPtr pNext;
            public uint flags;
            public ulong size;
            public VkBufferUsageFlags usage;
            public uint sharingMode; // VK_SHARING_MODE_EXCLUSIVE = 0
            public uint queueFamilyIndexCount;
            public IntPtr pQueueFamilyIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryRequirements
        {
            public ulong size;
            public ulong alignment;
            public uint memoryTypeBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryAllocateInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO = 5
            public IntPtr pNext;
            public ulong allocationSize;
            public uint memoryTypeIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandPoolCreateInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO = 39
            public IntPtr pNext;
            public VkCommandPoolCreateFlags flags;
            public uint queueFamilyIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandBufferAllocateInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO = 40
            public IntPtr pNext;
            public VkCommandPool commandPool;
            public VkCommandBufferLevel level;
            public uint commandBufferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkCommandBufferBeginInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO = 42
            public IntPtr pNext;
            public uint flags; // VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT = 1
            public IntPtr pInheritanceInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageMemoryBarrier
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER = 45
            public IntPtr pNext;
            public VkAccessFlags srcAccessMask;
            public VkAccessFlags dstAccessMask;
            public VkImageLayout oldLayout;
            public VkImageLayout newLayout;
            public uint srcQueueFamilyIndex;
            public uint dstQueueFamilyIndex;
            public VkImage image;
            public VkImageSubresourceRange subresourceRange;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkBufferImageCopy
        {
            public ulong bufferOffset;
            public uint bufferRowLength;
            public uint bufferImageHeight;
            public VkImageSubresourceLayers imageSubresource;
            public VkOffset3D imageOffset;
            public VkExtent3D imageExtent;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkImageSubresourceLayers
        {
            public VkImageAspectFlags aspectMask;
            public uint mipLevel;
            public uint baseArrayLayer;
            public uint layerCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkOffset3D
        {
            public int x, y, z;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkSubmitInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_SUBMIT_INFO = 4
            public IntPtr pNext;
            public uint waitSemaphoreCount;
            public IntPtr pWaitSemaphores;
            public IntPtr pWaitDstStageMask;
            public uint commandBufferCount;
            public IntPtr pCommandBuffers;
            public uint signalSemaphoreCount;
            public IntPtr pSignalSemaphores;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkFenceCreateInfo
        {
            public VkStructureType sType; // VK_STRUCTURE_TYPE_FENCE_CREATE_INFO = 8
            public IntPtr pNext;
            public VkFenceCreateFlags flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryType
        {
            public VkMemoryPropertyFlags propertyFlags;
            public uint heapIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct VkMemoryHeap
        {
            public ulong size;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct VkPhysicalDeviceMemoryProperties
        {
            public uint memoryTypeCount;
            public fixed byte memoryTypes[32 * 8]; // 32 × VkMemoryType (8 bytes each)
            public uint memoryHeapCount;
            public fixed byte memoryHeaps[16 * 16]; // 16 × VkMemoryHeap (16 bytes each: 8 size + 4 flags + 4 pad)

            public VkMemoryType GetMemoryType(int index)
            {
                fixed (byte* ptr = memoryTypes)
                {
                    return ((VkMemoryType*)ptr)[index];
                }
            }
        }

        // =========================================================================
        // Libretro Vulkan Interface Structs
        // =========================================================================

        [StructLayout(LayoutKind.Sequential)]
        public struct retro_hw_render_context_negotiation_interface_vulkan
        {
            public uint interface_type;    // RETRO_HW_RENDER_CONTEXT_NEGOTIATION_INTERFACE_VULKAN = 0
            public uint interface_version;
            public IntPtr get_application_info;  // () → VkApplicationInfo*
            public IntPtr create_device;
            public IntPtr destroy_device;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct retro_hw_render_interface_vulkan
        {
            public uint interface_type;    // RETRO_HW_RENDER_INTERFACE_VULKAN = 0
            public uint interface_version; // 5
            public IntPtr handle;
            public IntPtr instance;        // VkInstance
            public IntPtr gpu;             // VkPhysicalDevice
            public IntPtr device;          // VkDevice
            public IntPtr get_device_proc_addr;   // PFN_vkGetDeviceProcAddr
            public IntPtr get_instance_proc_addr; // PFN_vkGetInstanceProcAddr
            public IntPtr queue;           // VkQueue
            public uint queue_index;
            public IntPtr set_image;
            public IntPtr get_sync_index;
            public IntPtr get_sync_index_mask;
            public IntPtr set_command_buffers;
            public IntPtr wait_sync_index;
            public IntPtr lock_queue;
            public IntPtr unlock_queue;
            public IntPtr set_signal_semaphore;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct retro_vulkan_context
        {
            public IntPtr gpu;                          // VkPhysicalDevice       offset 0
            public IntPtr device;                       // VkDevice               offset 8
            public IntPtr queue;                        // VkQueue                offset 16
            public uint queue_family_index;             //                        offset 24
            private uint _pad0;                         // alignment padding      offset 28
            public IntPtr presentation_queue;           // VkQueue (optional)     offset 32
            public uint presentation_queue_family_index;//                        offset 40
            private uint _pad1;                         // alignment padding      offset 44
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct retro_vulkan_image
        {
            public IntPtr image_view;      // VkImageView (8 bytes)
            public uint image_layout;      // VkImageLayout (4 bytes)
            // VkImageViewCreateInfo create_info follows (aligned to 8):
            private uint _pad0;
            public uint ci_sType;          // VkStructureType
            private uint _pad1;
            public IntPtr ci_pNext;        // const void*
            public uint ci_flags;          // VkImageViewCreateFlags
            private uint _pad2;
            public IntPtr ci_image;        // VkImage — the backing image handle!
            public int ci_viewType;        // VkImageViewType
            public int ci_format;          // VkFormat
            public uint ci_comp_r, ci_comp_g, ci_comp_b, ci_comp_a; // VkComponentMapping
            public uint ci_sr_aspectMask, ci_sr_baseMipLevel, ci_sr_levelCount;
            public uint ci_sr_baseArrayLayer, ci_sr_layerCount; // VkImageSubresourceRange
        }

        // Delegate types for libretro Vulkan callbacks
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr retro_vulkan_get_application_info_t();

        // create_device(IntPtr context, VkInstance instance, VkPhysicalDevice gpu,
        //   VkSurfaceKHR surface, IntPtr get_instance_proc_addr,
        //   IntPtr required_device_extensions, uint num_required_device_extensions,
        //   IntPtr required_device_layers, uint num_required_device_layers,
        //   IntPtr required_features) → bool
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate bool retro_vulkan_create_device_t(
            IntPtr context, IntPtr instance, IntPtr gpu, IntPtr surface,
            IntPtr get_instance_proc_addr,
            IntPtr required_device_extensions, uint num_required_device_extensions,
            IntPtr required_device_layers, uint num_required_device_layers,
            IntPtr required_features);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_destroy_device_t();

        // set_image(handle, &retro_vulkan_image, num_semaphores, semaphores, src_queue_family)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_set_image_t(
            IntPtr handle, IntPtr image, uint num_semaphores, IntPtr semaphores, uint src_queue_family);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint retro_vulkan_get_sync_index_t(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate uint retro_vulkan_get_sync_index_mask_t(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_set_command_buffers_t(IntPtr handle, uint num_cmd, IntPtr cmd);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_wait_sync_index_t(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_lock_queue_t(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_unlock_queue_t(IntPtr handle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void retro_vulkan_set_signal_semaphore_t(IntPtr handle, IntPtr semaphore);

        public const uint VK_QUEUE_FAMILY_IGNORED = 0xFFFFFFFF;

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

        // Raw version: writes to a pre-allocated native buffer (avoids stack overflow from
        // incomplete VkPhysicalDeviceProperties struct missing Limits/SparseProperties).
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vkGetPhysicalDeviceProperties")]
        public static extern void vkGetPhysicalDevicePropertiesRaw(IntPtr physicalDevice, IntPtr pProperties);

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

        // Buffer & memory
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateBuffer(VkDevice device, ref VkBufferCreateInfo pCreateInfo, IntPtr pAllocator, out VkBuffer pBuffer);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyBuffer(VkDevice device, VkBuffer buffer, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkGetBufferMemoryRequirements(VkDevice device, VkBuffer buffer, out VkMemoryRequirements pMemoryRequirements);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkAllocateMemory(VkDevice device, ref VkMemoryAllocateInfo pAllocateInfo, IntPtr pAllocator, out VkDeviceMemory pMemory);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkFreeMemory(VkDevice device, VkDeviceMemory memory, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkBindBufferMemory(VkDevice device, VkBuffer buffer, VkDeviceMemory memory, ulong memoryOffset);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkMapMemory(VkDevice device, VkDeviceMemory memory, ulong offset, ulong size, uint flags, out IntPtr ppData);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkUnmapMemory(VkDevice device, VkDeviceMemory memory);

        // Command pool & buffer
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateCommandPool(VkDevice device, ref VkCommandPoolCreateInfo pCreateInfo, IntPtr pAllocator, out VkCommandPool pCommandPool);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyCommandPool(VkDevice device, VkCommandPool commandPool, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkAllocateCommandBuffers(VkDevice device, ref VkCommandBufferAllocateInfo pAllocateInfo, out VkCommandBuffer pCommandBuffers);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkBeginCommandBuffer(VkCommandBuffer commandBuffer, ref VkCommandBufferBeginInfo pBeginInfo);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkEndCommandBuffer(VkCommandBuffer commandBuffer);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkResetCommandBuffer(VkCommandBuffer commandBuffer, uint flags);

        // Pipeline barrier & copy
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkCmdPipelineBarrier(
            VkCommandBuffer commandBuffer,
            uint srcStageMask, uint dstStageMask,
            uint dependencyFlags,
            uint memoryBarrierCount, IntPtr pMemoryBarriers,
            uint bufferMemoryBarrierCount, IntPtr pBufferMemoryBarriers,
            uint imageMemoryBarrierCount, IntPtr pImageMemoryBarriers);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkCmdCopyImageToBuffer(
            VkCommandBuffer commandBuffer,
            VkImage srcImage, uint srcImageLayout,
            VkBuffer dstBuffer,
            uint regionCount, IntPtr pRegions);

        // Submit & sync
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkQueueSubmit(VkQueue queue, uint submitCount, ref VkSubmitInfo pSubmits, VkFence fence);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkQueueWaitIdle(VkQueue queue);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateFence(VkDevice device, ref VkFenceCreateInfo pCreateInfo, IntPtr pAllocator, out VkFence pFence);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroyFence(VkDevice device, VkFence fence, IntPtr pAllocator);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkWaitForFences(VkDevice device, uint fenceCount, ref VkFence pFences, uint waitAll, ulong timeout);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkResetFences(VkDevice device, uint fenceCount, ref VkFence pFences);

        // Physical device memory
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkGetPhysicalDeviceMemoryProperties(VkPhysicalDevice physicalDevice, out VkPhysicalDeviceMemoryProperties pMemoryProperties);

        // Device-level proc addr
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr vkGetDeviceProcAddr(VkDevice device, [MarshalAs(UnmanagedType.LPStr)] string pName);

        // Semaphore
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkCreateSemaphore(VkDevice device, ref VkSemaphoreCreateInfo pCreateInfo, IntPtr pAllocator, out VkSemaphore pSemaphore);

        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkDestroySemaphore(VkDevice device, VkSemaphore semaphore, IntPtr pAllocator);

        // Surface (KHR) — loaded via GetInstanceProcAddr at runtime
        // vkCreateWin32SurfaceKHR, vkDestroySurfaceKHR, vkGetPhysicalDeviceSurfaceSupportKHR,
        // vkGetPhysicalDeviceSurfaceCapabilitiesKHR, vkGetPhysicalDeviceSurfaceFormatsKHR,
        // vkGetPhysicalDeviceSurfacePresentModesKHR are loaded as function pointers.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate VkResult PFN_vkCreateWin32SurfaceKHR(VkInstance instance, ref VkWin32SurfaceCreateInfoKHR pCreateInfo, IntPtr pAllocator, out VkSurfaceKHR pSurface);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PFN_vkDestroySurfaceKHR(VkInstance instance, VkSurfaceKHR surface, IntPtr pAllocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate VkResult PFN_vkGetPhysicalDeviceSurfaceSupportKHR(VkPhysicalDevice physicalDevice, uint queueFamilyIndex, VkSurfaceKHR surface, out uint pSupported);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate VkResult PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR(VkPhysicalDevice physicalDevice, VkSurfaceKHR surface, out VkSurfaceCapabilitiesKHR pSurfaceCapabilities);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate VkResult PFN_vkGetPhysicalDeviceSurfaceFormatsKHR(VkPhysicalDevice physicalDevice, VkSurfaceKHR surface, ref uint pSurfaceFormatCount, IntPtr pSurfaceFormats);

        // Blit
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void vkCmdBlitImage(
            VkCommandBuffer commandBuffer,
            VkImage srcImage, uint srcImageLayout,
            VkImage dstImage, uint dstImageLayout,
            uint regionCount, IntPtr pRegions,
            uint filter); // VK_FILTER_LINEAR = 1, VK_FILTER_NEAREST = 0

        // Physical device features (VkPhysicalDeviceFeatures = 55 VkBool32s = 220 bytes)
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "vkGetPhysicalDeviceFeatures")]
        public static extern void vkGetPhysicalDeviceFeaturesRaw(IntPtr physicalDevice, IntPtr pFeatures);

        // Device extensions
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern VkResult vkEnumerateDeviceExtensionProperties(
            VkPhysicalDevice physicalDevice,
            IntPtr pLayerName, // null
            ref uint pPropertyCount,
            IntPtr pProperties);

        public const int VK_MAX_EXTENSION_NAME_SIZE = 256;
        public const int VK_EXTENSION_PROPERTIES_SIZE = 260; // 256 + uint

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
