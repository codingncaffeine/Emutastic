using System;
using System.Runtime.InteropServices;
using static Emutastic.Services.VulkanInterop;

namespace Emutastic.Services
{
    /// <summary>
    /// Manages Vulkan rendering context for libretro cores (ParaLLEl N64).
    /// </summary>
    public class VulkanRenderer : IDisposable
    {
        private VkInstance _instance;
        private VkPhysicalDevice _physicalDevice;
        private VkDevice _device;
        private VkQueue _graphicsQueue;
        private uint _graphicsQueueFamilyIndex;
        private VkSwapchainKHR _swapchain;
        private VkImage[] _swapchainImages = Array.Empty<VkImage>();
        private VkImageView[] _swapchainImageViews = Array.Empty<VkImageView>();
        private VkRenderPass _renderPass;
        private VkFramebuffer[] _framebuffers = Array.Empty<VkFramebuffer>();
        private VkCommandPool _commandPool;
        private VkCommandBuffer _commandBuffer;
        private VkSemaphore _imageAvailableSemaphore;
        private VkSemaphore _renderFinishedSemaphore;
        private VkFence _inFlightFence;
        
        private uint _width = 640;
        private uint _height = 480;
        private bool _isInitialized = false;

        public bool IsInitialized => _isInitialized;
        public VkDevice Device => _device;
        public VkPhysicalDevice PhysicalDevice => _physicalDevice;
        public uint GraphicsQueueFamilyIndex => _graphicsQueueFamilyIndex;

        /// <summary>
        /// Initialize Vulkan for libretro hardware rendering (no surface/display).
        /// ParaLLEl N64 uses headless rendering with readback.
        /// </summary>
        public bool Initialize()
        {
            if (_isInitialized) return true;

            try
            {
                // Check if Vulkan is available
                if (!IsVulkanAvailable())
                {
                    System.Diagnostics.Debug.WriteLine("Vulkan not available - vulkan-1.dll not found");
                    return false;
                }

                // Create Vulkan instance
                if (!CreateInstance())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create Vulkan instance");
                    return false;
                }

                // Select physical device (GPU)
                if (!SelectPhysicalDevice())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to select Vulkan physical device");
                    Cleanup();
                    return false;
                }

                // Create logical device
                if (!CreateLogicalDevice())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create Vulkan logical device");
                    Cleanup();
                    return false;
                }

                // Create render pass
                if (!CreateRenderPass())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create Vulkan render pass");
                    Cleanup();
                    return false;
                }

                // Create command pool and buffer
                if (!CreateCommandPool())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create Vulkan command pool");
                    Cleanup();
                    return false;
                }

                // Create sync objects
                if (!CreateSyncObjects())
                {
                    System.Diagnostics.Debug.WriteLine("Failed to create Vulkan sync objects");
                    Cleanup();
                    return false;
                }

                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine("Vulkan renderer initialized successfully");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Vulkan initialization error: {ex.Message}");
                Cleanup();
                return false;
            }
        }

        private bool CreateInstance()
        {
            // Application info
            var appInfo = new VkApplicationInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_APPLICATION_INFO,
                pApplicationName = Marshal.StringToHGlobalAnsi("OpenEmu for Windows"),
                applicationVersion = MakeVersion(1, 0, 0),
                pEngineName = Marshal.StringToHGlobalAnsi("OpenEmu"),
                engineVersion = MakeVersion(1, 0, 0),
                apiVersion = MakeVersion(1, 2, 0) // Vulkan 1.2
            };

            // Instance create info
            var createInfo = new VkInstanceCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO,
                pApplicationInfo = GetPtr(appInfo),
                enabledLayerCount = 0,
                enabledExtensionCount = 0
            };

            try
            {
                var result = vkCreateInstance(ref createInfo, IntPtr.Zero, out _instance);
                
                Marshal.FreeHGlobal(appInfo.pApplicationName);
                Marshal.FreeHGlobal(appInfo.pEngineName);
                
                if (result != VkResult.VK_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"vkCreateInstance failed: {VkResultToString(result)}");
                    return false;
                }

                System.Diagnostics.Debug.WriteLine("Vulkan instance created");
                return true;
            }
            catch (Exception ex)
            {
                Marshal.FreeHGlobal(appInfo.pApplicationName);
                Marshal.FreeHGlobal(appInfo.pEngineName);
                System.Diagnostics.Debug.WriteLine($"vkCreateInstance exception: {ex.Message}");
                return false;
            }
        }

        private bool SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            var result = vkEnumeratePhysicalDevices(_instance, ref deviceCount, IntPtr.Zero);
            if (result != VkResult.VK_SUCCESS || deviceCount == 0)
            {
                System.Diagnostics.Debug.WriteLine("No Vulkan physical devices found");
                return false;
            }

            IntPtr devicesPtr = Marshal.AllocHGlobal((int)(deviceCount * IntPtr.Size));
            result = vkEnumeratePhysicalDevices(_instance, ref deviceCount, devicesPtr);
            if (result != VkResult.VK_SUCCESS)
            {
                Marshal.FreeHGlobal(devicesPtr);
                return false;
            }

            // Select first discrete GPU, or fallback to first available
            VkPhysicalDevice selectedDevice = new VkPhysicalDevice { Handle = IntPtr.Zero };
            bool foundDiscrete = false;

            for (int i = 0; i < deviceCount; i++)
            {
                IntPtr deviceHandle = Marshal.ReadIntPtr(devicesPtr, i * IntPtr.Size);
                var device = new VkPhysicalDevice { Handle = deviceHandle };

                vkGetPhysicalDeviceProperties(device, out var props);
                string deviceName = System.Text.Encoding.UTF8.GetString(props.deviceName).TrimEnd('\0');
                System.Diagnostics.Debug.WriteLine($"Found GPU {i}: {deviceName} ({props.deviceType})");

                if (!foundDiscrete)
                {
                    if (props.deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU ||
                        selectedDevice.Handle == IntPtr.Zero)
                    {
                        selectedDevice = device;
                        if (props.deviceType == VkPhysicalDeviceType.VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)
                        {
                            foundDiscrete = true;
                            System.Diagnostics.Debug.WriteLine($"Selected discrete GPU: {deviceName}");
                        }
                    }
                }
            }

            Marshal.FreeHGlobal(devicesPtr);

            if (selectedDevice.Handle == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("No suitable Vulkan device found");
                return false;
            }

            _physicalDevice = selectedDevice;
            return true;
        }

        private bool CreateLogicalDevice()
        {
            // Find graphics queue family
            uint queueFamilyCount = 0;
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, IntPtr.Zero);
            if (queueFamilyCount == 0)
            {
                System.Diagnostics.Debug.WriteLine("No queue families found");
                return false;
            }

            IntPtr queueFamiliesPtr = Marshal.AllocHGlobal((int)(queueFamilyCount * Marshal.SizeOf<VkQueueFamilyProperties>()));
            vkGetPhysicalDeviceQueueFamilyProperties(_physicalDevice, ref queueFamilyCount, queueFamiliesPtr);

            bool foundGraphicsQueue = false;
            for (uint i = 0; i < queueFamilyCount; i++)
            {
                var props = Marshal.PtrToStructure<VkQueueFamilyProperties>(queueFamiliesPtr + (int)(i * Marshal.SizeOf<VkQueueFamilyProperties>()));
                if ((props.queueFlags & VkQueueFlags.VK_QUEUE_GRAPHICS_BIT) != 0)
                {
                    _graphicsQueueFamilyIndex = i;
                    foundGraphicsQueue = true;
                    System.Diagnostics.Debug.WriteLine($"Using graphics queue family {i} with {props.queueCount} queues");
                    break;
                }
            }

            Marshal.FreeHGlobal(queueFamiliesPtr);

            if (!foundGraphicsQueue)
            {
                System.Diagnostics.Debug.WriteLine("No graphics queue family found");
                return false;
            }

            // Create device queue create info
            float queuePriority = 1.0f;
            var queueCreateInfo = new VkDeviceQueueCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO,
                queueFamilyIndex = _graphicsQueueFamilyIndex,
                queueCount = 1,
                pQueuePriorities = GetPtr(queuePriority)
            };

            // Device create info
            var deviceCreateInfo = new VkDeviceCreateInfo
            {
                sType = VkStructureType.VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO,
                queueCreateInfoCount = 1,
                pQueueCreateInfos = GetPtr(queueCreateInfo),
                enabledLayerCount = 0,
                enabledExtensionCount = 0
            };

            try
            {
                var result = vkCreateDevice(_physicalDevice, ref deviceCreateInfo, IntPtr.Zero, out _device);
                
                // Free the allocated priority pointer
                Marshal.FreeHGlobal(queueCreateInfo.pQueuePriorities);

                if (result != VkResult.VK_SUCCESS)
                {
                    System.Diagnostics.Debug.WriteLine($"vkCreateDevice failed: {VkResultToString(result)}");
                    return false;
                }

                // Get the graphics queue
                vkGetDeviceQueue(_device, _graphicsQueueFamilyIndex, 0, out _graphicsQueue);
                System.Diagnostics.Debug.WriteLine("Vulkan logical device created");
                return true;
            }
            catch (Exception ex)
            {
                Marshal.FreeHGlobal(queueCreateInfo.pQueuePriorities);
                System.Diagnostics.Debug.WriteLine($"vkCreateDevice exception: {ex.Message}");
                return false;
            }
        }

        private bool CreateRenderPass()
        {
            // Simplified render pass - for libretro we mainly need the device
            // ParaLLEl N64 manages its own rendering
            return true;
        }

        private bool CreateCommandPool()
        {
            // Command pool is optional for headless libretro cores
            // ParaLLEl N64 manages its own command buffers
            return true;
        }

        private bool CreateSyncObjects()
        {
            // Sync objects are optional for headless libretro cores
            return true;
        }

        /// <summary>
        /// Get a Vulkan function pointer by name.
        /// Used by libretro cores to get extension function pointers.
        /// </summary>
        public IntPtr GetProcAddress(string name)
        {
            if (!_isInitialized) return IntPtr.Zero;
            
            // First try instance-level function
            IntPtr ptr = vkGetInstanceProcAddr(_instance, name);
            if (ptr != IntPtr.Zero) return ptr;

            // If not found, return null (core may handle device-level functions separately)
            return IntPtr.Zero;
        }

        private void Cleanup()
        {
            if (_device.Handle != IntPtr.Zero)
            {
                vkDeviceWaitIdle(_device);
                vkDestroyDevice(_device, IntPtr.Zero);
                _device = new VkDevice { Handle = IntPtr.Zero };
            }

            if (_instance.Handle != IntPtr.Zero)
            {
                vkDestroyInstance(_instance, IntPtr.Zero);
                _instance = new VkInstance { Handle = IntPtr.Zero };
            }

            _isInitialized = false;
        }

        public void Dispose()
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }

        // Helper methods
        private static uint MakeVersion(uint major, uint minor, uint patch)
        {
            return (major << 22) | (minor << 12) | patch;
        }

        private static IntPtr GetPtr<T>(T structure) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(structure, ptr, false);
            return ptr;
        }

        private static IntPtr GetPtr<T>(T[] array) where T : struct
        {
            if (array == null || array.Length == 0) return IntPtr.Zero;
            int size = Marshal.SizeOf<T>() * array.Length;
            IntPtr ptr = Marshal.AllocHGlobal(size);
            for (int i = 0; i < array.Length; i++)
            {
                Marshal.StructureToPtr(array[i], ptr + i * Marshal.SizeOf<T>(), false);
            }
            return ptr;
        }

        private static IntPtr GetPtr(float value)
        {
            IntPtr ptr = Marshal.AllocHGlobal(sizeof(float));
            Marshal.StructureToPtr(value, ptr, false);
            return ptr;
        }
    }
}
