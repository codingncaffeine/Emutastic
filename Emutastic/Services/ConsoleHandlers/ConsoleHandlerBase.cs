using System.Collections.Generic;

namespace Emutastic.Services.ConsoleHandlers
{
    /// <summary>
    /// Default implementation. Suitable for any console that doesn't need special handling.
    /// Override only the methods that differ from these generic defaults.
    /// </summary>
    public abstract class ConsoleHandlerBase : IConsoleHandler
    {
        private const uint RETRO_DEVICE_JOYPAD = 1;

        public abstract string ConsoleName { get; }
        public virtual bool UsesAnalogStick => false;

        public virtual Dictionary<string, string> GetDefaultCoreOptions()
            => new Dictionary<string, string>();

        public virtual void ConfigureControllerPorts(LibretroCore core)
        {
            core.SetControllerPortDevice(0, RETRO_DEVICE_JOYPAD);
        }

        public virtual void OnVariableAnnounced(string key, string[] validValues,
            Dictionary<string, string> coreOptions)
        { }

        public virtual double GetDisplayAspectRatio(uint baseWidth, uint baseHeight, float coreAspectRatio)
        {
            if (coreAspectRatio > 0.01f) return coreAspectRatio;
            if (baseHeight > 0) return (double)baseWidth / baseHeight;
            return 0;
        }

        public virtual double HardwareTargetFps => -1;

        public virtual void OnBeforeContextReset() { }
        public virtual void OnAfterContextReset() { }

        public virtual int PreferredHwContext => -1;
        public virtual bool AllowHwSharedContext => false;
        public virtual bool UseEmbeddedWindow => false;

        public virtual string ResolveSystemDirectory(string defaultDir, string coreDllDir)
            => defaultDir;

        public virtual void PrepareSaveDirectory(string saveDir) { }
        public virtual bool UseFullFboReadback => false;
    }
}
